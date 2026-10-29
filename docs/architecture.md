# papertrail — architecture plan (POC)

papertrail lets an HTTPS web app start a document scan on the user's Windows PC (TWAIN scanner) and get the pages back in JavaScript. It is proprietary software of Pursuant Solutions (all rights reserved; Pursuant may distribute it free or paid).

## 1. Decisions

| Area | Decision | Why |
|---|---|---|
| Scope | **Windows 10/11 workstations only.** No terminal servers / multi-user RDS hosts, no macOS/Linux, no WIA. | Agreed scope for the POC. The core and API stay cross-platform only so they build and test on Linux CI. |
| Runtime | .NET 10 (LTS), C#. Windows app targets `net10.0-windows`; core + API are plain `net10.0` so they build and test on Linux/CI. | Current LTS (supported to Nov 2028). |
| Process model | **Per-user tray app** (WinForms `NotifyIcon`), auto-start at sign-in through `HKCU\…\Run`. **Not** a Windows service. One instance per signed-in user. | Services run in session 0 with no interactive desktop. TWAIN drivers need a message loop and window, and often show their own UI. The pairing dialog must appear on the user's desktop. Per-user also means no admin rights are needed. |
| TWAIN library | **NTwain 3.7.6 (MIT)**, pinned. It uses its internal message-loop thread (a WPF `Dispatcher`). DIB is converted to PNG with `System.Drawing`. | NTwain is stable and MIT since 3.6/3.7; 4.0 is still beta (re-evaluate later). NAPS2.Sdk (LGPL) and Saraff.Twain (LGPL) are excluded. |
| Bitness | **win-x86 only** (`RuntimeIdentifier` fixed in `Papertrail.App.csproj`). No x64 build, no out-of-process workers. | A process only sees TWAIN sources of its own bitness. 32-bit drivers are the common case, and many scanners ship nothing else. |
| Publish | **Framework-dependent, single file**: one `Papertrail.exe` of about 0.55 MB. Needs the x86 **.NET Desktop Runtime 10** and **ASP.NET Core Runtime 10**, which the MSI checks for. | The self-contained build was 186 MB in 410 files. The two runtimes are free Microsoft downloads (about 56 MB + 11 MB installers), installed once per machine and serviced by Microsoft Update. |
| DSM | Use the `TWAINDSM.dll` (or legacy `twain_32.dll`) that scanner drivers install. **We do not redistribute the DSM.** | The TWAIN DSM is LGPL-2.1. Calling the copy already installed on the system via P/Invoke does not ship LGPL code. |
| Result transport | **Local HTTP API on `http://127.0.0.1:47115`** (Kestrel minimal API) with JSON and polling. Pages are returned as image downloads. | Browsers treat `127.0.0.1` as potentially trustworthy, so it is not blocked as mixed content. HTTP is simpler than WebSocket and is covered by the Local Network Access permission everywhere. |
| Port | **Always 47115.** Not configurable in settings or policy, and no fallback ports. If the port is taken, the app says so and the client reports `not_running`. | One well-known endpoint keeps the client simple. Without terminal-server support, there is one instance per machine in practice. |
| Output format | **PNG only** for the POC. `ScanOptions.Format` (`OutputFormat` enum, `format` in the API and `papertrail://`) is already plumbed through; unknown values are rejected as `invalid_options`. | Adding PDF later means: add `OutputFormat.Pdf`, accept it in `OutputFormats.TryParse`, and add a branch to the encoders in `MockScannerBackend` and `TwainScannerBackend.ToPage`. A multipage PDF would be assembled once all pages are in (one result instead of N pages), using an MIT/Apache writer such as PdfSharp 6 (MIT). |
| `papertrail://` role | **Launch + start scan only; it never carries results.** `papertrail://launch` starts the app. `papertrail://scan?v=1&origin=…&request=<id>&dpi=…` starts the app and queues a scan under a client-chosen random id. The page then collects the result over the HTTP API, where the `Origin` header *is* verifiable. | A protocol launch has no trustworthy caller identity, so any page or email could fire it. Results are released only to a page whose real `Origin` matches and which knows the 128-bit request id. A spoofed launch therefore yields nothing. |
| Protocol registration | `HKCU\Software\Classes\papertrail` (`URL Protocol`, `shell\open\command = "…\Papertrail.exe" "%1"`), written by the MSI. `Papertrail.exe --register` / `--unregister` and a first-run self-check write the same keys for portable use. Single instance: a second launch forwards its URL to the running instance over a current-user-only named pipe. | Per-user, no admin rights needed. |
| Installer | **Per-user MSI built with WiX Toolset 5.0.2** (`installer/Papertrail.wxs`, `scripts/build-msi.ps1`). Installs to `%LOCALAPPDATA%\Programs\Papertrail` with no elevation prompt and a progress bar only. Registers the protocol handler and sign-in auto-start in HKCU, adds a Start menu shortcut, and starts the app. Blocks with download links if either x86 runtime is missing. Upgrades are major upgrades that stop the running app first (`Papertrail.exe --exit`). Signing: `signtool` signs `Papertrail.exe` and the MSI when a certificate is given (thumbprint or PFX via parameters or `PAPERTRAIL_SIGN_*` variables). | WiX 5 is MS-RL with no Open Source Maintenance Fee (v6+ requires the fee from revenue-generating users). MS-RL applies to WiX's own source, not to MSIs built with it. We use no WiX extensions or custom-action DLLs, so the MSI contains no WiX code. Inno Setup (no MSI, commercial license requested) and Velopack (own `Setup.exe` and updater) don't fit an MSI requirement. |
| Policy | `%ProgramData%\Papertrail\policy.json` only. **No GPO/ADMX.** | Agreed. Admins can push the file with any deployment tool. |

## 2. Security model

1. **Origin allowlist ("pairing"), the only user prompt.** Every API request must carry a browser `Origin`. Allowed origins are `https://…`, plus `http://localhost` and `http://127.0.0.1` for development (this can be turned off by policy). `null`, `file://` and requests with no Origin are rejected.
   - The first time a site calls `POST /v1/pair`, the tray app shows "Allow *https://app.example.com* to use your scanners?". The dialog says that the site will then be able to scan whenever it asks.
   - A yes is saved to `%APPDATA%\Papertrail\settings.json`. Sites are removed in the tray menu's "Approved websites" window.
   - A no is remembered for 10 minutes so a site can't spam prompts. Only one prompt is shown at a time, and it times out after 2 minutes.
   - Admins can pre-approve origins (no prompt at all), or turn pairing off, in `policy.json`.
2. **No per-scan prompt.** After pairing, `POST /v1/scans` starts scanning straight away. papertrail adds no dialog, and it turns off the driver's progress indicator (`CAP_INDICATORS = false`) unless the page asks for the driver UI (`showDriverUi`). This is sized for about 1,000 scans per user per day.
3. **Protocol-launched scans need a claim.** A scan started from a `papertrail://scan` link (paired origins only; it never triggers pairing) is queued but does **not** move paper until the named origin polls `GET /v1/scans/{request id}` over HTTP, where the browser's `Origin` is verifiable. If no claim arrives within 30 seconds, the job ends as `canceled` with code `not_claimed`. Without the per-scan prompt, this is what stops a forged link from a third-party page or email from running the scanner for a paired site. The JS client claims automatically because it polls immediately.
4. **Job binding.** A scan job belongs to the origin that created (or claimed) it. Other origins get 404. Jobs and pages live only in memory and expire after 10 minutes or on `DELETE`.
5. **Transport hardening.** The server binds to loopback only. It checks the `Host` header (`127.0.0.1|localhost|[::1]:port`) to block DNS rebinding. CORS is reflected per request for valid origins (no `*`), with no credentials. It answers legacy PNA preflights harmlessly. One scan runs at a time.
6. **Out of scope.** Malicious local processes: they can already reach the scanner directly. Fingerprinting: a non-paired *valid* origin can learn that papertrail is installed via `/v1/status`, but only after the browser's Local Network Access prompt is granted.

## 3. Browser constraints (verified Oct 2026)

- **Local Network Access (Chrome/Edge 14x+, Firefox).** A public HTTPS page that fetches `127.0.0.1` gets a one-time *loopback-network* permission prompt. The JS client therefore:
  - passes `targetAddressSpace: 'loopback'` to `fetch` (this also gives the mixed-content exemption);
  - checks `navigator.permissions.query({name:'loopback-network'})` (never the legacy `'local-network-access'` name);
  - makes the first call from a user click.
  - Enterprises can pre-grant via policy (Chrome/Edge LNA allowlist policies; Firefox `LocalNetworkAccess.SkipDomains`).
  - The PNA preflight headers are deprecated; we only echo them.
- **Iframes** need `allow="loopback-network"` and a matching `Permissions-Policy` on the top-level document.
- **Protocol prompts.** Chrome/Edge show "Open Papertrail?" (with "always allow *site*"). Firefox shows a handler chooser. These must be triggered from a user gesture. If the handler isn't installed, nothing happens, so the client detects this by timing out. Enterprises can suppress the prompt with `AutoLaunchProtocolsFromOrigins` (Chrome/Edge). With auto-start at sign-in, the app is normally already running, so day-to-day scans go straight to the HTTP API and never show this prompt.
- **Sites must be HTTPS.** LNA is auto-denied on insecure public origins.

Steady state for a paired site on a machine where the LNA permission is granted: one click on the page, scan, no dialogs.

## 4. Local API (v1)

| Method & path | Who | Purpose |
|---|---|---|
| `GET /v1/status` | any valid origin | `{app, version, apiVersion, backend, paired, pairingAllowed}` |
| `POST /v1/pair` | any valid origin | Shows the pairing prompt; returns `{paired}` |
| `GET /v1/scanners` | paired | List sources |
| `POST /v1/scans` | paired | `{scannerId?, dpi, colorMode, duplex, source, showDriverUi, maxPages, format: "png"}` → `202 {id, status, format, …}` |
| `GET /v1/scans/{id}` | job owner | Status (`queued`, `scanning`, `completed`, `failed`, `canceled`) plus page list. Also claims a protocol-launched job |
| `GET /v1/scans/{id}/pages/{n}` | job owner | `image/png` |
| `DELETE /v1/scans/{id}` | job owner | Discard |

JS client (`clients/js/papertrail.js`, an ES module with no dependencies): `status()`, `pair()`, `listScanners()`, and `scan(options)`. If the app is not reachable, `scan` falls back to `papertrail://scan` and polls by request id (which claims the job).

## 5. Repository layout

`src/Papertrail.Core` (models, URI parser, origin policy, jobs, settings, mock scanner with built-in PNG encoder) · `src/Papertrail.Server` (Kestrel API) · `src/Papertrail.Twain` (NTwain backend) · `src/Papertrail.App` (Windows tray app, dialogs, registry) · `src/Papertrail.DevHost` (cross-platform headless host with the mock scanner) · `samples/Papertrail.SampleSite` + `samples/web` (demo page) · `clients/js` · `installer/` (WiX source) · `scripts/` (tests, MSI build) · `tests/*`.

## 6. Dependencies and licenses

Rule: nothing shipped may be GPL, LGPL, AGPL or "non-commercial", and no build tool may charge commercial users.

| Dependency | Used by | License | Notes |
|---|---|---|---|
| .NET 10 runtime, ASP.NET Core, Windows Desktop (WinForms/WPF, System.Drawing) | all | MIT | Not bundled: the user installs Microsoft's x86 runtimes (framework-dependent publish). |
| NTwain 3.7.6 | Papertrail.Twain | MIT | Pin ≥3.7. Earlier (≤3.5.x) NuGet metadata pointed to an external license file; do not use them. |
| TWAIN DSM (`TWAINDSM.dll`) | runtime, system | LGPL-2.1 | **Not redistributed**; installed by scanner drivers. |
| WiX Toolset 5.0.2 (`wix` local .NET tool) | MSI build only | MS-RL | Not shipped: no WiX extensions or custom-action DLLs in the MSI. Do not move to v6+ (OSMF fee). |
| xunit 2.9.x | tests only | Apache-2.0 | |
| xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | tests only | MIT/Apache-2.0/MIT | |
| Microsoft.AspNetCore.TestHost | tests only | MIT | |
| **Excluded** | | | NAPS2.Sdk (LGPL), Saraff.Twain (LGPL), Dynamsoft (commercial), WiX v6+ binaries (OSMF fee), Inno Setup (commercial license requested; no MSI). |

The JS client and sample page have no third-party dependencies.

## 7. Open questions

1. **Runtime prerequisite UX.** The MSI currently blocks with two download links when a runtime is missing. Machines are set up once, but if that's too much friction, the alternative is a WiX Burn bootstrapper (`.exe` that downloads and installs both runtimes, then the MSI). It stays fee-free on WiX 5, but it is an EXE wrapper around the MSI and needs elevation for the runtimes. Wanted?
2. **Per-user vs per-machine MSI.** The MSI is per-user (no admin, installs into the user's profile). That suits self-service installs. For deployment by IT to every user of a machine (Intune, SCCM, GPO software install), a per-machine variant under `Program Files (x86)` with HKLM protocol registration is the usual choice. Is per-user right, or do you need per-machine (or both)?
3. **EULA screen.** The MSI has no license dialog (basic UI only). Should the installer show a EULA? We'd need the text.
4. **Auto-start on upgrade.** An upgrade reinstalls the sign-in Run entry even if the user turned "Start when I sign in" off in the tray menu, unless the upgrade is run with `AUTOSTART=0`. Acceptable for the POC?
5. **WiX 5 support window.** WiX 5 no longer gets fixes from the WiX team. That's fine for the current feature set. If a future Windows change breaks it, the options are paying the OSMF for v6+ or building WiX from source (the fee applies to the prebuilt binaries). OK to revisit only if needed?
