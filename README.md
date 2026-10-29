# papertrail

papertrail lets a web application scan paper documents on the user's Windows PC. It is a small per-user tray app that:

- drives TWAIN scanners, through [NTwain](https://github.com/soukoku/ntwain) (MIT);
- exposes a local API on `http://127.0.0.1:47115` that only approved HTTPS websites can use;
- registers the `papertrail://` URL scheme so a page can start the app (and a scan) with a link.

The user approves each website once. After that, the site's scans start immediately, with no papertrail dialogs.

```js
import { PapertrailClient } from './papertrail.js';

const client = new PapertrailClient();
scanButton.onclick = async () => {
  const { pages } = await client.scan({ dpi: 300, colorMode: 'gray', source: 'feeder', duplex: true });
  for (const page of pages) img.src = URL.createObjectURL(page.blob); // image/png
};
```

Status: **proof of concept** for Windows 10/11 workstations (32-bit build, PNG output). Architecture, security model, browser constraints, dependency licenses and open questions are in [docs/architecture.md](docs/architecture.md).

## How it works

```
 HTTPS web app (browser)                         papertrail.exe (per-user tray app)
 ───────────────────────                         ─────────────────────────────────
 papertrail.js ── fetch http://127.0.0.1:47115 ──► Kestrel API ──► TWAIN (NTwain)
      │            (Origin checked, CORS)              ▲
      └── papertrail://launch | scan?... ──► Windows ──┘  (starts the app / forwards to the running one)
```

1. **Pairing (once per site).** The first `POST /v1/pair` from a site shows *"Allow https://site to use your scanners?"*. This is the only papertrail prompt. Approved origins are saved in `%APPDATA%\Papertrail\settings.json` and can be removed in the tray menu's **Approved websites** window. The browser supplies the `Origin` header, so pages cannot fake it.
2. **Scanning.** `POST /v1/scans` from a paired site starts the scan right away. papertrail also asks the driver to hide its progress window (`CAP_INDICATORS`) unless the page asks for the driver UI. The page polls `GET /v1/scans/{id}` and downloads each page from `/v1/scans/{id}/pages/{n}`. Results stay in memory only, are visible only to the origin that requested them, and expire after 10 minutes.
3. **`papertrail://` links** only start the app (`papertrail://launch`) or queue a scan for an *already paired* origin (`papertrail://scan?v=1&origin=…&request=<random id>&dpi=…`). The scanner does not start until that origin's page polls the job over HTTP (within 30 seconds), so a forged link from another page or an email cannot run the scanner. A protocol link never carries data back, and it can never pair a site.
4. **Browser permission.** Chrome, Edge and Firefox ask the user once to let a public site reach "apps on this device" (Local Network Access, loopback). The client sends `targetAddressSpace: 'loopback'` and should be called from a click.

### Local API (v1)

| Request | Purpose |
|---|---|
| `GET /v1/status` | `{app, version, apiVersion, backend, paired, pairingAllowed}` |
| `POST /v1/pair` | Ask the user to approve this site |
| `GET /v1/scanners` | List scanners (paired sites only) |
| `POST /v1/scans` | Body: `{scannerId?, dpi, colorMode: color\|gray\|blackWhite, source: auto\|flatbed\|feeder, duplex, showDriverUi, maxPages, format: png}`. Returns `202` with the job |
| `GET /v1/scans/{id}` | Job status: `queued`, `scanning`, `completed`, `failed` or `canceled`, plus pages |
| `GET /v1/scans/{id}/pages/{n}` | Page image (`image/png`) |
| `DELETE /v1/scans/{id}` | Discard or cancel a job |

`format` is optional and only `png` is accepted for now; the option exists so PDF can be added later without changing the API shape.

Errors are returned as `{ "error": "<code>", "message": "…" }`. Common codes are `origin_not_paired`, `pairing_denied`, `invalid_options`, `paper_jam`, `no_scanners`, `twain_unavailable` and `not_claimed` (a `papertrail://` scan that no page picked up).

## Repository layout

| Path | What |
|---|---|
| `src/Papertrail.Core` | Scan options, `papertrail://` parser, origin rules and pairing, job manager, settings and policy, mock scanner with a built-in PNG encoder (cross-platform) |
| `src/Papertrail.Server` | Loopback HTTP API: Host and Origin checks, CORS, endpoints (cross-platform) |
| `src/Papertrail.Twain` | TWAIN backend on NTwain (Windows) |
| `src/Papertrail.App` | `Papertrail.exe`: tray app, pairing dialog, approved-sites window, protocol and auto-start registration (Windows, x86) |
| `src/Papertrail.DevHost` | Headless host with the mock scanner, for Linux, macOS, CI and demos |
| `clients/js` | Browser client (`papertrail.js` plus `.d.ts`), with unit and end-to-end tests |
| `samples/web`, `samples/Papertrail.SampleSite` | Demo page and its static host on `http://127.0.0.1:47200` |
| `installer/` | WiX source for the MSI (`Papertrail.wxs`) |
| `tests/` | xUnit tests for Core and the API |
| `scripts/` | `test.sh` (everything except Windows-only checks), `build-msi.ps1` (Windows: build, sign, validate), `build-msi.sh` (Linux via Wine: unsigned dev build) |

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
dotnet run --project src/Papertrail.DevHost            # API on http://127.0.0.1:47115, mock scanners, auto-approves pairing
dotnet run --project samples/Papertrail.SampleSite     # demo page on http://127.0.0.1:47200
```

Open http://127.0.0.1:47200, click **Connect**, then **Scan**. The mock scanner offers a duplex feeder, a flatbed, and a "jammed" device for testing errors.

To simulate a `papertrail://` launch (after the sample page has been paired), pass the URL to a second dev host process. It forwards the URL to the running one, which is what Windows does when a link is clicked. The scan waits until something polls `GET /v1/scans/0123456789abcdef0123` with the sample page's `Origin`, and is dropped after 30 seconds otherwise:

```bash
dotnet run --project src/Papertrail.DevHost -- "papertrail://scan?v=1&origin=http%3A%2F%2F127.0.0.1%3A47200&request=0123456789abcdef0123&source=flatbed"
```

### From Visual Studio (F5)

The demo needs two projects running at once: a scanner host and the sample site. Open `papertrail.slnx` in Visual Studio 2022 17.11 or later, pick a multi-project launch profile from the startup dropdown (they are stored in `papertrail.slnLaunch`), and press F5:

- **Demo: dev host + sample site** runs `Papertrail.DevHost` (mock scanners, pairing auto-approved) and `Papertrail.SampleSite`, which opens the demo page in the browser.
- **Demo: tray app (mock scanner) + sample site** runs the real tray app with `--mock` instead, so you see the actual pairing dialog and tray menu. The app is 32-bit, so this needs the x86 .NET Desktop Runtime and ASP.NET Core Runtime 10 (see [Install on Windows](#install-on-windows)). Exit any installed papertrail first, because both use port 47115.

On older Visual Studio versions, right-click the solution, choose **Configure Startup Projects... > Multiple startup projects**, and set `Papertrail.DevHost` (or `Papertrail.App`, whose launch profile passes `--mock`) and `Papertrail.SampleSite` to **Start**.

## Install on Windows

Requirements:

- Windows 10 or 11 (workstations; terminal servers are not supported).
- The **32-bit (x86)** [.NET Desktop Runtime 10](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x86.exe) and [ASP.NET Core Runtime 10](https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x86.exe), both free from Microsoft ([all downloads](https://dotnet.microsoft.com/download/dotnet/10.0)). The x86 versions are needed even on 64-bit Windows. The MSI checks for both and stops with these links if one is missing.
- A scanner with a **32-bit TWAIN** driver from the manufacturer. The driver installs `TWAINDSM.dll`; papertrail does not ship it. Without a scanner, you can install the TWAIN Working Group's *TWAIN2 FreeImage Software Scanner* sample source for testing.

Install `papertrail-<version>-x86.msi` by double-clicking it, or silently:

```powershell
msiexec /i papertrail-0.1.0-x86.msi /qn                 # AUTOSTART=0 / LAUNCHAPP=0 to opt out of those
msiexec /x papertrail-0.1.0-x86.msi /qn                 # uninstall (or Settings > Apps)
```

The MSI is per-user: no administrator rights, no UAC prompt, and a progress bar as the only UI. It:

- installs `Papertrail.exe` (about 0.55 MB) to `%LOCALAPPDATA%\Programs\Papertrail`;
- registers the URL handler: `HKCU\Software\Classes\papertrail\shell\open\command` = `"…\Papertrail.exe" "%1"`;
- starts papertrail at sign-in: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Papertrail` = `"…\Papertrail.exe" --background` (also toggled from the tray menu);
- adds a Start menu shortcut and starts the app.

Upgrades install over the old version: the MSI asks the running app to exit (`Papertrail.exe --exit`), replaces it and starts the new one. Uninstalling removes the files and registry entries, but keeps the approved sites and logs in `%APPDATA%\Papertrail` and `%LOCALAPPDATA%\Papertrail`.

### Build the MSI

On Windows, with the .NET 10 SDK (WiX is restored as a local .NET tool, see `.config/dotnet-tools.json`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1                     # unsigned
$env:PAPERTRAIL_SIGN_CERT_THUMBPRINT = '<sha1>'                                     # or PAPERTRAIL_SIGN_PFX + PAPERTRAIL_SIGN_PFX_PASSWORD
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1 -RequireSigning     # release
```

The script publishes `Papertrail.exe`, signs it with `signtool` (SHA-256, RFC 3161 timestamp from `PAPERTRAIL_SIGN_TIMESTAMP_URL`, default DigiCert), builds `artifacts/msi/papertrail-<version>-x86.msi`, runs the ICE validation (`wix msi validate`) and signs the MSI. `signtool` is found in the Windows SDK, or set `SIGNTOOL`. `-RequireSigning` fails the build when no certificate is configured; use it in release CI.

On Linux, `scripts/build-msi.sh` builds an unsigned MSI by running WiX under Wine. It is for development checks only: it skips ICE validation (which does not work under Wine), and Wine's cabinet library doesn't compress, so the file is larger.

For CI, run `scripts/build-msi.ps1 -RequireSigning` on a `windows-latest` runner, with the certificate supplied through the `PAPERTRAIL_SIGN_*` secrets.

### Run without installing

`dotnet publish src/Papertrail.App -c Release -o artifacts/publish/win-x86` produces a single `Papertrail.exe` (the x86 runtimes above are still needed). Started from any folder, it registers the URL handler for itself on first run.

Command line: `Papertrail.exe [--mock] [--background]`, `--register [--autostart] [--quiet]`, `--unregister`, `--exit` (ask the running instance to quit; used by the MSI). When Windows passes a `papertrail:` URL, all switches are ignored.

Files:

| Path | Contents |
|---|---|
| `%APPDATA%\Papertrail\settings.json` | Approved sites |
| `%LOCALAPPDATA%\Papertrail\logs\papertrail.log` | Log |
| `%ProgramData%\Papertrail\policy.json` | Optional admin policy (see below) |

### Admin policy (optional)

```json
{
  "allowedOrigins": ["https://app.example.com"],
  "allowPairing": false,
  "allowInsecureLoopbackOrigins": false
}
```

`allowedOrigins` are approved without a prompt. `allowPairing: false` stops users from approving other sites. The policy is read from this file only (no Group Policy templates); push it with your usual deployment tool. The port is always 47115.

To skip browser prompts on managed machines, use the browsers' own policies: the Local Network Access allowlist policies in Chrome and Edge, `LocalNetworkAccess.SkipDomains` in Firefox, and `AutoLaunchProtocolsFromOrigins` for the `papertrail://` prompt.

## Using the JS client

Copy `clients/js/papertrail.js` (and `papertrail.d.ts` for TypeScript) into your app. Then:

- `await client.connect()` makes sure the app is running (launching it via `papertrail://launch` if needed) and that the site is paired.
- `await client.listScanners()` lists scanners.
- `await client.scan(options, { onProgress, signal })` returns `{ id, pages: [{ index, width, height, mediaType, blob }] }`. If the app isn't running, it starts it via a `papertrail://` link.
- Errors are `PapertrailError` objects with a `code`, such as `not_running`, `loopback_blocked`, `pairing_denied` or `paper_jam`.
- Call these from a click handler, and serve your site over HTTPS. Browsers deny loopback access to insecure public pages.
- If your page runs in an iframe, the embedding page must delegate `allow="loopback-network"`.

## Known limitations (POC)

- **Not yet tested on Windows with real scanners.** The code was built and tested on Linux. The MSI was installed, upgraded and uninstalled under Wine, where the tray app ran with the mock scanner. The TWAIN backend and tray UI have not been run on real Windows.
- PNG output only (PDF is planned). Large color scans at 600 dpi produce big PNGs, and everything is held in memory.
- 32-bit only, so only 32-bit TWAIN drivers are visible.
- Canceling a scan stops at the next page boundary. Scans waiting in the driver's own dialog cannot be aborted from the web page.
- One instance per Windows session on the fixed port 47115; terminal servers are not supported.

## License

Proprietary. Copyright (c) 2026 Pursuant Solutions, all rights reserved; see [LICENSE](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); everything shipped is MIT, and the MSI is built with WiX Toolset 5 (MS-RL, no fee).
