# Third-party notices

papertrail is meant to be sold commercially and offered free to open-source projects, so every
dependency must be under a permissive license (MIT, Apache-2.0, BSD or similar). GPL, LGPL, AGPL
and "non-commercial" components are not allowed. Check this table before adding a package to
`Directory.Packages.props`.

## Shipped with the Windows app

| Component | Version | License | Source |
|---|---|---|---|
| .NET runtime, ASP.NET Core (Kestrel), Windows Desktop (WinForms, WPF, System.Drawing) | 10.0 | MIT | https://github.com/dotnet |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| NTwain | 3.7.6 | MIT | https://github.com/soukoku/ntwain |

## Used at runtime but not distributed

| Component | License | Notes |
|---|---|---|
| TWAIN Data Source Manager (`TWAINDSM.dll` / `twain_32.dll`) | LGPL-2.1 | Installed by scanner drivers into the Windows system directory and loaded from there. papertrail must **not** bundle it. |

## Build and test only (not shipped)

| Component | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.AspNetCore.TestHost | 10.0.14 | MIT |
| coverlet.collector | 6.0.4 | MIT |

The JS client (`clients/js`) and the sample page have no third-party dependencies.

## Considered and rejected

| Component | Reason |
|---|---|
| NAPS2.Sdk | LGPL |
| Saraff.Twain.NET | LGPL-3.0 |
| Dynamic Web TWAIN / Dynamsoft .NET TWAIN | Commercial license |
| NTwain 3.5.x and earlier | NuGet metadata points to an external license file rather than an SPDX MIT expression. Pin 3.7+ |
| WiX Toolset v6+ (prebuilt binaries) | Revenue-generating users must pay the Open Source Maintenance Fee |
| Inno Setup | Commercial users are asked to buy a license |

---

## NTwain license

```
The MIT License (MIT)
Copyright (c) 2012 Yin-Chun Wang

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished
to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
```
