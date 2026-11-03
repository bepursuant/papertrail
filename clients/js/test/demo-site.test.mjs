import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

test('the demo page can arm ADF auto-start', () => {
  const html = readFileSync(join(root, 'docs', 'index.html'), 'utf8');
  const app = readFileSync(join(root, 'docs', 'app.js'), 'utf8');
  assert.match(html, /name="adfAutoStart"/);
  assert.match(html, /Start when a document is placed in the feeder/);
  assert.match(app, /adfAutoStart: true/);
  assert.match(app, /waitingForPaper: 'Waiting for a document in the feeder…'/);
  assert.match(app, /Scan when loaded/);
});

test('the GitHub Pages copy of the client matches clients/js', () => {
  for (const name of ['papertrail.js', 'papertrail.d.ts']) {
    const source = readFileSync(join(root, 'clients', 'js', name));
    const published = readFileSync(join(root, 'docs', 'lib', name));
    assert.deepEqual(published, source, `docs/lib/${name} is stale. Copy clients/js/${name} over it.`);
  }
});
