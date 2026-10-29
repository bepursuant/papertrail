import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PapertrailClient, PapertrailError, buildScanUrl } from '../papertrail.js';

const ORIGIN = 'https://app.example.com';

/** In-memory stand-in for the papertrail API. */
class FakeApp {
  constructor({ running = true, paired = false } = {}) {
    this.running = running;
    this.paired = paired;
    this.jobs = new Map();
    this.calls = [];
    this.deleted = [];
  }

  fetch = async (url, init = {}) => {
    const { pathname } = new URL(url);
    const method = init.method ?? 'GET';
    this.calls.push(`${method} ${pathname}`);
    assert.equal(init.targetAddressSpace, 'loopback');
    assert.equal(init.credentials, 'omit');
    if (!this.running) throw new TypeError('Failed to fetch');

    const json = (status, body) => new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    if (method === 'GET' && pathname === '/v1/status') return json(200, { app: 'papertrail', version: '0.1.0', apiVersion: 1, backend: 'mock', paired: this.paired, pairingAllowed: true });
    if (method === 'POST' && pathname === '/v1/pair') { this.paired = true; return json(200, { paired: true }); }
    if (!this.paired) return json(403, { error: 'origin_not_paired', message: 'Pair first.' });
    if (method === 'GET' && pathname === '/v1/scanners') return json(200, { scanners: [{ id: 'mock-feeder', name: 'Mock', backend: 'mock', isDefault: true }] });
    if (method === 'POST' && pathname === '/v1/scans') return json(202, this.addJob('job123456789abcdef', JSON.parse(init.body ?? '{}')));

    const match = pathname.match(/^\/v1\/scans\/([^/]+)(?:\/pages\/(\d+))?$/);
    const job = match && this.jobs.get(match[1]);
    if (!job) return json(404, { error: 'not_found', message: 'No such scan.' });
    if (method === 'DELETE') { this.deleted.push(job.id); return json(204, null); }
    if (match[2] !== undefined) return new Response(new Uint8Array([137, 80, 78, 71]), { status: 200, headers: { 'Content-Type': 'image/png' } });
    job.polls += 1;
    if (job.polls >= 2) job.status = job.outcome;
    return json(200, this.view(job));
  };

  addJob(id, options, outcome = 'completed') {
    const job = { id, options, polls: 0, status: 'queued', outcome };
    this.jobs.set(id, job);
    return this.view(job);
  }

  view(job) {
    const done = job.status === 'completed';
    return {
      id: job.id,
      status: job.status,
      trigger: 'api',
      format: 'png',
      createdAt: new Date().toISOString(),
      error: job.status === 'failed' ? { error: 'paper_jam', message: 'The scanner reported a paper jam.' } : undefined,
      pages: done ? [{ index: 0, mediaType: 'image/png', width: 10, height: 14, size: 4, url: `/v1/scans/${job.id}/pages/0` }] : [],
    };
  }
}

function memoryStorage() {
  const map = new Map();
  return { getItem: (k) => map.get(k) ?? null, setItem: (k, v) => map.set(k, v), removeItem: (k) => map.delete(k) };
}

function client(app, extra = {}) {
  return new PapertrailClient({ fetch: app.fetch, origin: ORIGIN, pollIntervalMs: 1, launchTimeoutMs: 50, storage: memoryStorage(), ...extra });
}

test('buildScanUrl encodes origin, request id and options', () => {
  const url = buildScanUrl(ORIGIN, 'abc123abc123abc123', { dpi: 300, colorMode: 'gray', duplex: true, scannerId: 'A & B', format: 'png' });
  const parsed = new URL(url);
  assert.equal(parsed.protocol, 'papertrail:');
  assert.equal(parsed.searchParams.get('origin'), ORIGIN);
  assert.equal(parsed.searchParams.get('request'), 'abc123abc123abc123');
  assert.equal(parsed.searchParams.get('scannerId'), 'A & B');
  assert.equal(parsed.searchParams.get('duplex'), 'true');
  assert.equal(parsed.searchParams.get('format'), 'png');
  assert.equal(parsed.searchParams.get('v'), '1');
});

test('scan pairs an unpaired site, polls to completion, downloads pages and cleans up', async () => {
  const app = new FakeApp({ paired: false });
  const seen = [];
  const result = await client(app).scan({ dpi: 200 }, { onProgress: (job) => seen.push(job.status) });

  assert.equal(result.pages.length, 1);
  assert.equal(result.pages[0].blob.type, 'image/png');
  assert.ok(app.calls.includes('POST /v1/pair'));
  assert.deepEqual(app.jobs.get('job123456789abcdef').options, { dpi: 200 });
  assert.equal(seen.at(-1), 'completed');
  await new Promise((r) => setTimeout(r, 5));
  assert.deepEqual(app.deleted, ['job123456789abcdef']);
});

test('failed scans reject with the server error code', async () => {
  const app = new FakeApp({ paired: true });
  app.fetch = ((inner) => async (url, init) => {
    if (init?.method === 'POST' && url.endsWith('/v1/scans')) {
      return new Response(JSON.stringify(app.addJob('jammedjob1234567890', {}, 'failed')), { status: 202 });
    }
    return inner(url, init);
  })(app.fetch);

  await assert.rejects(client(app).scan(), (err) => err instanceof PapertrailError && err.code === 'paper_jam');
});

test('when the app is not running and the site was paired before, scan launches papertrail://scan', async () => {
  const app = new FakeApp({ running: false, paired: true });
  const storage = memoryStorage();
  storage.setItem('papertrail.paired', 'http://127.0.0.1:47115');
  const launched = [];
  const c = client(app, {
    storage,
    launch: (url) => {
      launched.push(url);
      const request = new URL(url).searchParams.get('request');
      setTimeout(() => { app.running = true; app.addJob(request, {}); }, 5);
    },
  });

  const result = await c.scan({ colorMode: 'blackWhite' });

  assert.equal(launched.length, 1);
  const url = new URL(launched[0]);
  assert.equal(url.host, 'scan');
  assert.equal(url.searchParams.get('origin'), ORIGIN);
  assert.equal(url.searchParams.get('colorMode'), 'blackWhite');
  assert.match(url.searchParams.get('request'), /^[0-9a-f]{32}$/);
  assert.equal(result.pages.length, 1);
});

test('when the app is not running and the site was never paired, scan launches the app then pairs over HTTP', async () => {
  const app = new FakeApp({ running: false, paired: false });
  const launched = [];
  const c = client(app, { launch: (url) => { launched.push(url); app.running = true; } });

  const result = await c.scan();

  assert.deepEqual(launched, ['papertrail://launch']);
  assert.ok(app.calls.includes('POST /v1/pair'));
  assert.equal(result.pages.length, 1);
});

test('reports not_running when the app never comes up', async () => {
  const app = new FakeApp({ running: false });
  await assert.rejects(client(app, { launch: () => {} }).connect(), (err) => err.code === 'not_running');
});

test('retries without targetAddressSpace when the browser rejects the value', async () => {
  const app = new FakeApp({ paired: true });
  const seen = [];
  const fetchImpl = async (url, init) => {
    seen.push(init.targetAddressSpace);
    if (init.targetAddressSpace === 'loopback') {
      throw new TypeError("Failed to read the 'targetAddressSpace' property from 'RequestInit': The provided value 'loopback' is not a valid enum value");
    }
    return app.fetch(url, { ...init, targetAddressSpace: 'loopback' });
  };
  const c = client(app, { fetch: fetchImpl });

  assert.equal((await c.status()).paired, true);
  await c.status();
  assert.deepEqual(seen, ['loopback', undefined, undefined]);
});

test('status() returns null when unreachable', async () => {
  assert.equal(await client(new FakeApp({ running: false })).status(), null);
});
