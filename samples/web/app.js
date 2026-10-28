import { PapertrailClient, PapertrailError } from './lib/papertrail.js';

const params = new URLSearchParams(location.search);
const client = new PapertrailClient({ port: Number(params.get('port')) || undefined });

const $ = (id) => document.getElementById(id);
const form = $('scan-form');
const scanButton = $('scan');
const connectButton = $('connect');
const scannerSelect = $('scanner');
const progress = $('progress');
const errorBox = $('error');
const pagesList = $('pages');
const empty = $('empty');
const clearButton = $('clear');
const connection = $('connection');

const STATUS_TEXT = {
  queued: 'Waiting for the scanner…',
  awaitingConsent: 'Confirm the scan in the papertrail window…',
  scanning: 'Scanning…',
};

const objectUrls = [];

function setConnection(kind, text) {
  connection.className = `pill pill-${kind}`;
  connection.textContent = text;
}

function showError(err) {
  const message = err instanceof PapertrailError ? err.message : `Unexpected error: ${err?.message ?? err}`;
  errorBox.textContent = message;
  errorBox.hidden = false;
}

function clearError() {
  errorBox.hidden = true;
  errorBox.textContent = '';
}

async function refreshStatus() {
  try {
    const status = await client.status();
    if (!status) setConnection('off', 'papertrail not running');
    else if (!status.paired) setConnection('warn', `Connected (${status.backend}) · not approved`);
    else setConnection('ok', `Ready · ${status.backend} · v${status.version}`);
    return status;
  } catch (err) {
    setConnection('off', 'Unavailable');
    return null;
  }
}

async function loadScanners() {
  const scanners = await client.listScanners();
  const current = scannerSelect.value;
  scannerSelect.replaceChildren(new Option('Default scanner', ''));
  for (const s of scanners) {
    scannerSelect.add(new Option(`${s.name}${s.isDefault ? ' (default)' : ''}`, s.id));
  }
  scannerSelect.value = scanners.some((s) => s.id === current) ? current : '';
  scannerSelect.disabled = false;
}

function readOptions() {
  const data = new FormData(form);
  return {
    scannerId: data.get('scannerId') || undefined,
    dpi: Number(data.get('dpi')),
    colorMode: data.get('colorMode'),
    source: data.get('source'),
    duplex: data.get('duplex') === 'on',
    showDriverUi: data.get('showDriverUi') === 'on',
  };
}

function renderPages(pages) {
  for (const page of pages) {
    const url = URL.createObjectURL(page.blob);
    objectUrls.push(url);
    const number = pagesList.children.length + 1;
    const item = document.createElement('li');
    item.className = 'page';
    item.innerHTML = `
      <a class="thumb" target="_blank" rel="noopener"><img alt="" loading="lazy" /></a>
      <div class="meta">
        <strong>Page ${number}</strong>
        <span>${page.width}×${page.height} · ${(page.size / 1024).toFixed(0)} KB</span>
        <a class="download">Download</a>
      </div>`;
    item.querySelector('img').src = url;
    item.querySelector('img').alt = `Scanned page ${number}`;
    item.querySelector('.thumb').href = url;
    const download = item.querySelector('.download');
    download.href = url;
    download.download = `scan-page-${number}.png`;
    pagesList.append(item);
  }
  empty.hidden = pagesList.children.length > 0;
  clearButton.hidden = pagesList.children.length === 0;
}

connectButton.addEventListener('click', async () => {
  clearError();
  connectButton.disabled = true;
  connectButton.textContent = 'Connecting…';
  try {
    await client.connect();
    await loadScanners();
    connectButton.textContent = 'Refresh';
  } catch (err) {
    showError(err);
    connectButton.textContent = 'Connect';
  } finally {
    connectButton.disabled = false;
    refreshStatus();
  }
});

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearError();
  scanButton.disabled = true;
  scanButton.classList.add('busy');
  progress.textContent = 'Contacting papertrail…';
  try {
    const result = await client.scan(readOptions(), {
      onProgress: (job) => {
        progress.textContent = STATUS_TEXT[job.status] ?? '';
        if (job.status === 'scanning' && job.pages.length) {
          progress.textContent = `Scanning… ${job.pages.length} page(s) so far`;
        }
      },
    });
    renderPages(result.pages);
    progress.textContent = `Done · ${result.pages.length} page(s)`;
    if (scannerSelect.disabled) loadScanners().catch(() => {});
  } catch (err) {
    progress.textContent = '';
    showError(err);
  } finally {
    scanButton.disabled = false;
    scanButton.classList.remove('busy');
    refreshStatus();
  }
});

clearButton.addEventListener('click', () => {
  objectUrls.splice(0).forEach((u) => URL.revokeObjectURL(u));
  pagesList.replaceChildren();
  empty.hidden = false;
  clearButton.hidden = true;
});

// Don't trigger the browser's loopback permission prompt on page load; wait for a click.
const permission = await client.loopbackPermission();
if (permission === 'prompt') {
  setConnection('unknown', 'Click Connect to link papertrail');
} else if (permission === 'denied') {
  setConnection('off', 'Blocked by browser settings');
} else {
  const status = await refreshStatus();
  if (status?.paired) loadScanners().catch(() => {});
}
