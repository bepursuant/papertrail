# papertrail — architecture plan (v0.1 slice)

papertrail lets an HTTPS web app start a document scan on the user's Windows PC (TWAIN scanner) and get the pages back in JavaScript.

## 1. Decisions

| Area | Decision | Why |
|---|---|---|
| Runtime | .NET 10 (LTS), C#. Windows app targets `net10.0-windows`; core + API are plain `net10.0` so they build and test on Linux/CI. | Current LTS (supported to Nov 2028). |
| Process model | **Per-user tray app** (WinForms `NotifyIcon`), auto-start at sign-in through `HKCU\…\Run`. **Not** a Windows service. | Services run in session 0 with no interactive desktop. TWAIN drivers need a message loop and window, and often show their own UI. Consent dialogs must appear on the user's desktop. Per-user also means no admin rights are needed. |
| TWAIN library | **NTwain 3.7.6 (MIT)**, pinned. It uses its internal message-loop thread (a WPF `Dispatcher`). DIB is converted to PNG with `System.Drawing`. | NTwain is stable and MIT since 3.6/3.7; 4.0 is still beta (re-evaluate later). NAPS2.Sdk (LGPL) and Saraff.Twain (LGPL) are excluded. |
| Bitness | v0.1: publish **win-x86** by default (win-x64 also builds). Planned: x64 host plus out-of-process TWAIN workers per bitness (stdin/stdout JSON). | A process only sees TWAIN sources of its own bitness, and many drivers are still 32-bit only. Out-of-process workers also isolate driver crashes. |
| DSM | Use the `TWAINDSM.dll` (or legacy `twain_32.dll`) that scanner drivers install. **We do not redistribute the DSM.** | The TWAIN DSM is LGPL-2.1. Calling the copy already installed on the system via P/Invoke does not ship LGPL code. |
| Result transport | **Local HTTP API on `http://127.0.0.1:47115`** (Kestrel minimal API) with JSON and polling. Pages are returned as PNG downloads. | Browsers treat `127.0.0.1` as potentially trustworthy, so it is not blocked as mixed content. HTTP is simpler than WebSocket and is covered by the Local Network Access permission everywhere. |
| `papertrail://` role | **Launch + start scan only; it never carries results.** `papertrail://launch` starts the app. `papertrail://scan?v=1&origin=…&request=<id>&dpi=…` starts the app and queues a scan under a client-chosen random id. The page then collects the result over the HTTP API, where the `Origin` header *is* verifiable. | A protocol launch has no trustworthy caller identity, so any page or email could fire it. Results are released only to a page whose real `Origin` matches and which knows the 128-bit request id. A spoofed launch therefore yields nothing. |
| Protocol registration | `HKCU\Software\Classes\papertrail` (`URL Protocol`, `shell\open\command = "…\Papertrail.exe" "%1"`), written by `Papertrail.exe --register` (and later by the installer hook). Single instance: a second launch forwards its URL to the running instance over a current-user-only named pipe. | Per-user, no admin rights needed, and removable with `--unregister`. |
| Installer | v0.1: `scripts/install.ps1` copies the self-contained publish output to `%LOCALAPPDATA%\Programs\Papertrail` and runs `--register --autostart`. Next: **Velopack (MIT)** for per-user install and auto-update, with registration done in its install/uninstall hooks. | WiX v6+ binaries need the Open Source Maintenance Fee for revenue-generating users. Inno Setup asks commercial users to buy a license. Velopack is MIT and .NET-native. |

## 2. Security model

1. **Origin allowlist ("pairing").** Every API request must carry a browser `Origin`. Allowed origins are `https://…`, plus `http://localhost` and `http://127.0.0.1` for development (this can be turned off by policy). `null`, `file://` and requests with no Origin are rejected.
   - The first time a site calls `POST /v1/pair`, the tray app shows "Allow *https://app.example.com* to use your scanners?".
   - A yes is saved to `%APPDATA%\Papertrail\settings.json`.
   - A no is remembered for 10 minutes so a site can't spam prompts. Only one prompt is shown at a time.
   - Admins can pre-approve origins, or turn pairing off, in `%ProgramData%\Papertrail\policy.json` (planned: GPO under `HKLM\Software\Policies\Papertrail`).
2. **Per-scan consent.** Each scan shows a "*origin* wants to scan from *scanner*" dialog with Scan/Cancel and an optional "don't ask again for this site". Scans started via `papertrail://` from unpaired origins are rejected; they never trigger pairing, because their origin is unverifiable.
3. **Job binding.** A scan job belongs to the origin that created (or claimed) it. Other origins get 404. Jobs and pages live only in memory and expire after 10 minutes or on `DELETE`.
4. **Transport hardening.** The server binds to loopback only. It checks the `Host` header (`127.0.0.1|localhost|[::1]:port`) to block DNS rebinding. CORS is reflected per request for valid origins (no `*`), with no credentials. It answers legacy PNA preflights harmlessly. One scan runs at a time.
5. **Out of scope.** Malicious local processes: they can already reach the scanner directly. Fingerprinting: a non-paired *valid* origin can learn that papertrail is installed via `/v1/status`, but only after the browser's Local Network Access prompt is granted.

## 3. Browser constraints (verified Oct 2026)

- **Local Network Access (Chrome/Edge 14x+, Firefox).** A public HTTPS page that fetches `127.0.0.1` gets a one-time *loopback-network* permission prompt. The JS client therefore:
  - passes `targetAddressSpace: 'loopback'` to `fetch` (this also gives the mixed-content exemption);
  - checks `navigator.permissions.query({name:'loopback-network'})` (never the legacy `'local-network-access'` name);
  - makes the first call from a user click.
  - Enterprises can pre-grant via policy (Chrome/Edge LNA allowlist policies; Firefox `LocalNetworkAccess.SkipDomains`).
  - The PNA preflight headers are deprecated; we only echo them.
- **Iframes** need `allow="loopback-network"` and a matching `Permissions-Policy` on the top-level document.
- **Protocol prompts.** Chrome/Edge show "Open Papertrail?" (with "always allow *site*"). Firefox shows a handler chooser. These must be triggered from a user gesture. If the handler isn't installed, nothing happens, so the client detects this by timing out. Enterprises can suppress the prompt with `AutoLaunchProtocolsFromOrigins` (Chrome/Edge).
- **Sites must be HTTPS.** LNA is auto-denied on insecure public origins.

## 4. Local API (v1)

| Method & path | Who | Purpose |
|---|---|---|
| `GET /v1/status` | any valid origin | `{app, version, apiVersion, backend, paired}` |
| `POST /v1/pair` | any valid origin | Shows the pairing prompt; returns `{paired}` |
| `GET /v1/scanners` | paired | List sources |
| `POST /v1/scans` | paired | `{scannerId?, dpi, colorMode, duplex, source, showDriverUi, maxPages}` → `202 {id,status}` |
| `GET /v1/scans/{id}` | job owner | Status (`awaitingConsent`, `scanning`, `completed`, `denied`, `failed`) plus page list |
| `GET /v1/scans/{id}/pages/{n}` | job owner | `image/png` |
| `DELETE /v1/scans/{id}` | job owner | Discard |

JS client (`clients/js/papertrail.js`, an ES module with no dependencies): `status()`, `pair()`, `listScanners()`, and `scan(options)`. If the app is not reachable, `scan` falls back to `papertrail://scan` and polls by request id.

## 5. Repository layout

`src/Papertrail.Core` (models, URI parser, origin policy, jobs, settings, mock scanner with built-in PNG encoder) · `src/Papertrail.Server` (Kestrel API) · `src/Papertrail.Twain` (NTwain backend) · `src/Papertrail.App` (Windows tray app, dialogs, registry) · `src/Papertrail.DevHost` (cross-platform headless host with the mock scanner) · `samples/Papertrail.SampleSite` + `samples/web` (demo page) · `clients/js` · `tests/*`.

## 6. Dependencies and licenses

| Dependency | Used by | License | Notes |
|---|---|---|---|
| .NET 10 runtime, ASP.NET Core, Windows Desktop (WinForms/WPF, System.Drawing) | all | MIT | Self-contained publish bundles these. |
| NTwain 3.7.6 | Papertrail.Twain | MIT | Pin ≥3.7. Earlier (≤3.5.x) NuGet metadata pointed to an external license file; do not use them. |
| TWAIN DSM (`TWAINDSM.dll`) | runtime, system | LGPL-2.1 | **Not redistributed**; installed by scanner drivers. |
| xunit 2.9.x | tests only | Apache-2.0 | |
| xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | tests only | MIT/Apache-2.0/MIT | |
| Microsoft.AspNetCore.TestHost | tests only | MIT | |
| Velopack (planned) | installer | MIT | |
| **Excluded** | | | NAPS2.Sdk (LGPL), Saraff.Twain (LGPL), Dynamsoft (commercial), WiX v6+ binaries (OSMF fee), Inno Setup (commercial license requested). |

The JS client and sample page have no third-party dependencies.

## 7. Open decisions (need your input)

1. **License text.** Which model: PolyForm/BUSL-style source-available, or a true dual license (for example AGPL plus commercial)? "Free for open-source projects" needs a precise definition (OSI license? public repo?). `LICENSE` is a placeholder; **have counsel review it**. A contributor CLA/DCO is needed if you intend to relicense contributions commercially.
2. **Default bitness.** Ship x86 only (simplest, broadest driver coverage), or invest now in the x64 host plus x86/x64 worker processes?
3. **Output formats.** v0.1 returns PNG per page. Do you need JPEG, multipage PDF and/or TIFF? A pure-.NET PDF writer under MIT (for example PdfSharp 6, MIT) is available. Should we also offer a callback-upload mode (the app POSTs to an allowlisted HTTPS URL) for sites where LNA is blocked?
4. **Consent UX.** Should per-scan consent stay on by default, with an opt-in "don't ask again"? Should enterprises be able to pre-approve scans via policy?
5. **Port.** Fixed `47115` (configurable), or a small list of fallback ports that the client probes?
6. **Code signing and distribution.** An Authenticode certificate (EV/OV or Azure Trusted Signing) is needed to avoid SmartScreen warnings. Velopack and GitHub Releases, or MSI for enterprise deployment (which would need WiX + OSMF or another tool)?
7. **Policy delivery.** `policy.json` only, or ADMX/GPO registry policies too?
8. **Platforms.** Is macOS/Linux (ICA/SANE) or WIA (for WIA-only devices) in scope later? The core is already cross-platform.
9. **Install size.** The self-contained build is about 190 MB (WPF, WinForms and ASP.NET Core). Options: accept it; ship framework-dependent (about 5 MB, but needs the .NET Desktop and ASP.NET Core runtimes, which a bootstrapper could install); or later replace the WPF-dependent NTwain v3 message loop.
10. **Terminal servers.** One instance per session on a fixed port collides on multi-user RDS hosts. Is that in scope? If so, we need per-session ports plus a discovery mechanism.
