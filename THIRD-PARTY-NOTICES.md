# Third-party notices / Üçüncü taraf bildirimleri

E-mre Control Center is published as a single, self-contained Windows executable. The executable (and the setup files, which are the
same executable) includes the third-party components listed below. Each component remains subject to its own license; the
[LICENSE](LICENSE) file of this repository covers only the code of E-mre Control Center itself.

E-mre Control Center tek, kendi içinde çalışan bir Windows programı olarak yayınlanır. Program dosyası (ve aynı dosya olan kurulum
dosyaları) aşağıdaki üçüncü taraf bileşenleri içerir. Her bileşen kendi lisansına tabidir; bu deponun [LICENSE](LICENSE) dosyası
yalnızca E-mre Control Center'ın kendi kodunu kapsar.

## Components included in the executable

| Component | Source | License | Copyright |
|---|---|---|---|
| .NET runtime and base class libraries (.NET 8) | <https://github.com/dotnet/runtime> | MIT | Copyright (c) .NET Foundation and Contributors |
| Windows Presentation Foundation (WPF) | <https://github.com/dotnet/wpf> | MIT | Copyright (c) .NET Foundation and Contributors |
| System.Management 8.0.0 (NuGet package; source in dotnet/runtime) | <https://www.nuget.org/packages/System.Management/8.0.0> | MIT | © Microsoft Corporation |
| WinRT.Runtime (C#/WinRT) | <https://github.com/microsoft/CsWinRT> | MIT | Copyright (c) Microsoft Corporation |
| Microsoft.Windows.SDK.NET (Windows SDK projection for .NET, package Microsoft.Windows.SDK.NET.Ref 10.0.19041.56) | <https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref> | Microsoft Windows SDK license: <https://aka.ms/WinSDKLicenseURL> | © Microsoft Corporation |

The .NET runtime and WPF themselves include further third-party components. Their notices are published by the .NET Foundation:

- <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- <https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT>

## MIT License

The components marked "MIT" above are licensed under the following terms (with the copyright line shown in the table):

```
The MIT License (MIT)

Copyright (c) <copyright holders shown in the table above>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Not included in the executable

These are used by the app but are not distributed with it:

- **Windows components** (winget, Windows Update, Microsoft Store, Microsoft Defender, PowerShell, SFC, DISM, MRT): part of Windows
  and licensed to you with Windows.
- **Fonts** (Segoe UI Variable, Segoe Fluent Icons, Cascadia Mono; fallbacks Segoe UI, Segoe MDL2 Assets, Consolas): not embedded;
  the app uses them from the fonts installed on the computer.
- **Speedtest by Ookla CLI**: installed separately from Ookla's server through winget, only if you choose the Ookla engine and
  approve the installation; subject to Ookla's own license and terms.
- **Apps and drivers updated through the app** (winget packages, Microsoft Store apps, NVIDIA drivers): licensed by their owners.
- **The E-mre Control Center logo** (`assets/`) belongs to the project and is covered by [LICENSE](LICENSE).

This list was compiled on 2026-10-16 from the build output (`bin/Release/net8.0-windows10.0.19041.0/win-x64`) and the NuGet package
metadata. If a dependency is added or changed, update this file.
