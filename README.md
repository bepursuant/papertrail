# papertrail

papertrail lets a web application scan paper documents on the user's Windows PC. It is a small per-user tray app that:

- drives TWAIN scanners, through [NTwain](https://github.com/soukoku/ntwain) (MIT);
- exposes a local API on `http://127.0.0.1:47115` that only approved HTTPS websites can use;
- registers the `papertrail://` URL scheme so a page can start the app (and a scan) with a link.

The user approves each website once, and confirms every scan in a papertrail dialog before the scanner starts.

```js
import { PapertrailClient } from './papertrail.js';

const client = new PapertrailClient();
scanButton.onclick = async () => {
  const { pages } = await client.scan({ dpi: 300, colorMode: 'gray', source: 'feeder', duplex: true });
  for (const page of pages) img.src = URL.createObjectURL(page.blob); // image/png
};
```

Status: **v0.1, first slice.** Architecture, security model, browser constraints, dependency licenses and open decisions are in [docs/architecture.md](docs/architecture.md).

## How it works

```
 HTTPS web app (browser)                         papertrail.exe (per-user tray app)
 ───────────────────────                         ─────────────────────────────────
 papertrail.js ── fetch http://127.0.0.1:47115 ──► Kestrel API ──► consent dialog ──► TWAIN (NTwain)
      │            (Origin checked, CORS)              ▲
      └── papertrail://launch | scan?... ──► Windows ──┘  (starts the app / forwards to the running one)
```

1. **Pairing.** The first `POST /v1/pair` from a site shows *"Allow https://site to use your scanners?"*. Approved origins are saved in `%APPDATA%\Papertrail\settings.json`. The browser supplies the `Origin` header, so pages cannot fake it.
2. **Scanning.** `POST /v1/scans` queues a job. papertrail asks the user to confirm, then scans. The page polls `GET /v1/scans/{id}` and downloads each page from `/v1/scans/{id}/pages/{n}`. Results stay in memory only, are visible only to the origin that requested them, and expire after 10 minutes.
3. **`papertrail://` links** only start the app (`papertrail://launch`) or start a scan for an *already paired* origin (`papertrail://scan?v=1&origin=…&request=<random id>&dpi=…`). The result is still collected over HTTP by the page that knows the random request id. A protocol link never carries data back, and it can never pair a site.
4. **Browser permission.** Chrome, Edge and Firefox ask the user once to let a public site reach "apps on this device" (Local Network Access, loopback). The client sends `targetAddressSpace: 'loopback'` and should be called from a click.

### Local API (v1)

| Request | Purpose |
|---|---|
| `GET /v1/status` | `{app, version, apiVersion, backend, paired, pairingAllowed}` |
| `POST /v1/pair` | Ask the user to approve this site |
| `GET /v1/scanners` | List scanners (paired sites only) |
| `POST /v1/scans` | Body: `{scannerId?, dpi, colorMode: color\|gray\|blackWhite, source: auto\|flatbed\|feeder, duplex, showDriverUi, maxPages}`. Returns `202` with the job |
| `GET /v1/scans/{id}` | Job status: `queued`, `awaitingConsent`, `scanning`, `completed`, `denied`, `failed` or `canceled`, plus pages |
| `GET /v1/scans/{id}/pages/{n}` | Page image (`image/png`) |
| `DELETE /v1/scans/{id}` | Discard or cancel a job |

Errors are returned as `{ "error": "<code>", "message": "…" }`. Common codes are `origin_not_paired`, `pairing_denied`, `consent_denied`, `paper_jam`, `no_scanners` and `twain_unavailable`.

## Repository layout

| Path | What |
|---|---|
| `src/Papertrail.Core` | Scan options, `papertrail://` parser, origin rules and pairing, job manager, settings and policy, mock scanner with a built-in PNG encoder (cross-platform) |
| `src/Papertrail.Server` | Loopback HTTP API: Host and Origin checks, CORS, endpoints (cross-platform) |
| `src/Papertrail.Twain` | TWAIN backend on NTwain (Windows) |
| `src/Papertrail.App` | `Papertrail.exe`: tray app, consent dialogs, approved-sites window, protocol and auto-start registration (Windows) |
| `src/Papertrail.DevHost` | Headless host with the mock scanner, for Linux, macOS, CI and demos |
| `clients/js` | Browser client (`papertrail.js` plus `.d.ts`), with unit and end-to-end tests |
| `samples/web`, `samples/Papertrail.SampleSite` | Demo page and its static host on `http://127.0.0.1:47200` |
| `tests/` | xUnit tests for Core and the API |
| `scripts/` | `test.sh` (everything except Windows-only checks), `install.ps1` and `uninstall.ps1` |

## Build and test (any OS)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Node.js 20 or later (for the JS tests only).

```bash
./scripts/test.sh          # builds everything (Windows projects cross-compile) and runs all tests
```

Or step by step:

```bash
dotnet build papertrail.slnx
dotnet test papertrail.slnx
PAPERTRAIL_DEVHOST_DLL=$PWD/src/Papertrail.DevHost/bin/Debug/net10.0/Papertrail.DevHost.dll \
  node --test "clients/js/test/*.test.mjs"
```

## Try it without a scanner (mock)

```bash
dotnet run --project src/Papertrail.DevHost            # API on http://127.0.0.1:47115, mock scanners, auto-approves prompts
dotnet run --project samples/Papertrail.SampleSite     # demo page on http://127.0.0.1:47200
```

Open http://127.0.0.1:47200, click **Connect**, then **Scan**. The mock scanner offers a duplex feeder, a flatbed, and a "jammed" device for testing errors.

To simulate a `papertrail://` launch (after the sample page has been paired), pass the URL to a second dev host process. It forwards the URL to the running one, which is what Windows does when a link is clicked:

```bash
dotnet run --project src/Papertrail.DevHost -- "papertrail://scan?v=1&origin=http%3A%2F%2F127.0.0.1%3A47200&request=0123456789abcdef0123&source=flatbed"
```

On Windows, the real tray app can also use the mock scanner: `Papertrail.exe --mock`.

## Run and install on Windows

Requirements: Windows 10 or 11, and a scanner with a **TWAIN** driver from the manufacturer. The driver installs `TWAINDSM.dll`; papertrail does not ship it. Without a scanner, you can install the TWAIN Working Group's *TWAIN2 FreeImage Software Scanner* sample source for testing.

```powershell
# 32-bit build: sees 32-bit TWAIN drivers, which many scanners still ship (recommended for now)
dotnet publish src/Papertrail.App -c Release -r win-x86 --self-contained -o artifacts/publish/win-x86
# or 64-bit: only sees 64-bit TWAIN drivers
dotnet publish src/Papertrail.App -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64

powershell -ExecutionPolicy Bypass -File scripts/install.ps1                # per-user, no admin
powershell -ExecutionPolicy Bypass -File scripts/uninstall.ps1 [-RemoveSettings]
```

`install.ps1` copies the build to `%LOCALAPPDATA%\Programs\Papertrail` and runs `Papertrail.exe --register --autostart`, which writes:

- `HKCU\Software\Classes\papertrail\shell\open\command` = `"…\Papertrail.exe" "%1"` (the URL handler);
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Papertrail` = `"…\Papertrail.exe" --background` (start at sign-in).

It then adds a Start menu shortcut and starts the app. To run from a build folder without installing, start `Papertrail.exe`; it registers the URL handler for itself on first run.

Command line: `Papertrail.exe [--mock] [--background]`, `--register [--autostart] [--quiet]`, `--unregister`. When Windows passes a `papertrail:` URL, all switches are ignored.

Files:

| Path | Contents |
|---|---|
| `%APPDATA%\Papertrail\settings.json` | Approved sites and port |
| `%LOCALAPPDATA%\Papertrail\logs\papertrail.log` | Log |
| `%ProgramData%\Papertrail\policy.json` | Optional admin policy (see below) |

### Admin policy (optional)

```json
{
  "allowedOrigins": ["https://app.example.com"],
  "allowPairing": false,
  "allowInsecureLoopbackOrigins": false,
  "alwaysConfirmScans": true,
  "port": 47115
}
```

To skip browser prompts on managed machines, use the browsers' own policies: the Local Network Access allowlist policies in Chrome and Edge, `LocalNetworkAccess.SkipDomains` in Firefox, and `AutoLaunchProtocolsFromOrigins` for the `papertrail://` prompt.

## Using the JS client

Copy `clients/js/papertrail.js` (and `papertrail.d.ts` for TypeScript) into your app. Then:

- `await client.connect()` makes sure the app is running (launching it via `papertrail://launch` if needed) and that the site is paired.
- `await client.listScanners()` lists scanners.
- `await client.scan(options, { onProgress, signal })` returns `{ id, pages: [{ index, width, height, mediaType, blob }] }`. If the app isn't running, it starts it via a `papertrail://` link.
- Errors are `PapertrailError` objects with a `code`, such as `not_running`, `loopback_blocked`, `pairing_denied`, `consent_denied` or `paper_jam`.
- Call these from a click handler, and serve your site over HTTPS. Browsers deny loopback access to insecure public pages.
- If your page runs in an iframe, the embedding page must delegate `allow="loopback-network"`.

## Known limitations (v0.1)

- **Not yet tested on Windows with real scanners.** Everything was built and tested on Linux. The TWAIN backend, tray UI and registry code compile, but have not been run.
- PNG output only. Large color scans at 600 dpi produce big PNGs, and everything is held in memory.
- A process only sees TWAIN drivers of its own bitness. Out-of-process x86/x64 workers are planned.
- Canceling a scan stops at the next page boundary. Scans waiting in the driver's own dialog cannot be aborted from the web page.
- The self-contained build is about 190 MB (WPF, WinForms and ASP.NET Core). A framework-dependent build is much smaller, but needs the .NET Desktop and ASP.NET Core runtimes installed.
- One instance per Windows session on a fixed port. Multi-user terminal servers need per-session ports (open decision).

## License

Dual-licensed: free for open-source projects, paid for commercial use. **`LICENSE` is a placeholder and must be reviewed by counsel.** Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); all are MIT or Apache-2.0.
