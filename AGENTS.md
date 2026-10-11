# AGENTS.md

Guidance for AI coding agents and contributors working in this repository. Product background: [CONTEXT.md](CONTEXT.md).
Full technical documentation (Turkish): [docs/TEKNIK.md](docs/TEKNIK.md).

## Project

E-mre Control Center: a Windows 11 update, cleanup, repair and diagnostics app. C# / .NET 8 / WPF (MVVM), published as one
self-contained single-file EXE. The C# namespace and project name are `RtxWindowsUpdater` for historical reasons; do not rename them.

| Path | Contents |
|---|---|
| `src/RtxWindowsUpdater/Core` | Process runner, command / PowerShell helpers, logging, localization (`Loc.cs`), shell open, Win32 helpers |
| `src/RtxWindowsUpdater/Services` | One module per card (`IUpdateModule`): winget, Windows Update, Store, NVIDIA, Defender, DISM, SFC, MRT, temp files, Recycle Bin; setup, in-app update |
| `src/RtxWindowsUpdater/Services/Diagnostics` | System tools: drivers, network, DNS, storage, events, crashes, services, processes, reports |
| `src/RtxWindowsUpdater/ViewModels` | `MainViewModel` (partial files), tools, setup / uninstall view models |
| `src/RtxWindowsUpdater/Views`, `Themes` | XAML windows, converters, `Theme.xaml`, `Colors.Dark.xaml`, `Colors.Light.xaml` |
| `legal/` | Privacy policy and terms of use (Turkish and English) |
| `tools/Build-Exe.ps1` | Local single-file build (also creates the TR and EN setup copies) |
| `.github/workflows/release.yml` | Builds and publishes a release when a `vX.Y.Z` tag is pushed |

## Build

```powershell
dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release   # must stay at 0 warnings
powershell -ExecutionPolicy Bypass -File .\tools\Build-Exe.ps1           # single-file EXE + setup copies
```

The SDK version is pinned in `global.json` (.NET 8). Save `.ps1` files as UTF-8 **with BOM** (Windows PowerShell 5.1).

## Non-negotiable rules

- **Real results only.** Every status comes from a real command output, exit code or Windows API result. Never show a failed,
  timed-out or uninterpretable operation as successful; "process started" is not success. No fake or estimated progress percentages.
- **Nothing changes without consent.** No silent installs, deletions, forced restarts, or Defender / firewall changes. Installs,
  repairs and deletions start only after the user confirms and are not cancelled halfway (`CancellationToken.None`); checks are
  read-only and cancellable. DISM `/RestoreHealth` only after explicit approval.
- **Never bypass UAC.** Elevation only via `runas` (`AdminPrivilegeManager`).
- **Elevated safety.** The app usually runs as administrator: never run a file a standard user can modify as administrator
  (winget is resolved from the protected WindowsApps path; downloads go to protected folders), never `ShellExecute` user file
  associations — open URLs, settings pages and files only through `Core/ShellOpen`. Build command lines with `CmdCommand`
  (unsafe characters are rejected).
- **Never close** Windows services, Explorer, critical or system processes, or processes in other sessions.
- **Temp cleanup** never touches personal folders (Downloads etc.), Windows.old, files changed in the last 24 hours or files in use.
- **Design:** keep the existing design language (black / navy, neon blue, shadowed cards). No emoji; icons are Segoe Fluent Icons /
  Segoe MDL2 glyphs.

## Localization (Turkish / English)

- Every user-visible string is written in **both languages** where it is used: C# `L.T("Türkçe", "English")`, XAML
  `{l:T 'Türkçe', 'English'}` (escape `'` as `\'`; a value starting with `{` needs `{}`), PowerShell log lines `«Türkçe|English»`.
- Turkish is the default. The language is chosen once before the first window opens; changing it restarts the app.
- Do **not** translate strings used to parse tool output (SFC, DISM `/English`, winget, WMI / registry names), internal keys, or
  folder and file names used for cleanup of earlier versions.
- If code compares a displayed string, wrap both sides in `L.T` so the comparison works in both languages.
- Percent format: Turkish `%50`, English `50%`. Dates in English: `yyyy-MM-dd`. Upper-casing: `L.Upper`.

## Themes (dark / light)

All colors come from `Themes/Colors.Dark.xaml` and `Themes/Colors.Light.xaml` (same keys) via `DynamicResource`. Never hard-code a
color in a view; add a key to both dictionaries instead. Dark is the default.

## Privacy and legal

No telemetry, analytics, ads or cookies. If you add a new external service or change what is sent, update both privacy policies,
the in-app "Legal and Privacy" text and `AppInfo.LegalVersion`. Never commit secrets, tokens or personal data.

## Releases

1. Bump `<Version>` in `RtxWindowsUpdater.csproj`.
2. Add a `### vX.Y.Z` section at the **top** of `CHANGELOG.md`: Turkish first, then a line containing only `**English**`, then
   English (the in-app update window shows the user's language).
3. The maintainer commits, pushes and tags `vX.Y.Z`. Never reuse or move a published tag; every release gets a new version.

The release publishes `E-mre-Control-Center-Setup-TR-vX.Y.Z.exe`, `E-mre-Control-Center-Setup-EN-vX.Y.Z.exe`, the ZIP and a
compatibility copy `E-mre-Control-Center-Setup-vX.Y.Z.exe`. Keep the compatibility copy: v1.9.3 and earlier only look for that name
when updating.

## Testing

Tests must never perform real system changes (updates, `sfc /scannow`, Recycle Bin, temp cleanup, installs). Deletion logic is tested
only in folders the test created. The maintainer runs the UI and service test harnesses locally (they are not part of this
repository).

## Documentation

Keep `README.md` short and user-facing; put technical details in `docs/TEKNIK.md`. Security reporting: [SECURITY.md](SECURITY.md).
Community rules: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
License: [LICENSE](LICENSE) (source-available, all rights reserved — not open source).
