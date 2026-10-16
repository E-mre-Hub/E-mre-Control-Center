# Architecture

How E-mre Control Center is built. This document describes the current code; proposals are marked as such. Product background:
[CONTEXT.md](CONTEXT.md). Rules for changing the code: [AGENTS.md](AGENTS.md). Full technical details in Turkish (including how each
tool's output is interpreted): [docs/TEKNIK.md](docs/TEKNIK.md).

## Overview

| | |
|---|---|
| Type | Windows 11 desktop app (WPF, MVVM), single self-contained EXE (no .NET installation needed on the target PC) |
| Language / runtime | C# (latest language version), .NET 8 (`net8.0-windows10.0.19041.0`), nullable reference types enabled |
| Size | 126 C# / XAML source files under `src/RtxWindowsUpdater` |
| Dependencies | One NuGet package (`System.Management` 8.0.0, WMI). Windows SDK projection for WinRT APIs. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) |
| Storage | No database. Local JSON state file and plain-text session logs under `%LOCALAPPDATA%\E-mre Control Center` |
| Server side | None. The app is a client of public services (GitHub, Microsoft, NVIDIA, Cloudflare, optional Ookla and Google Forms) |

The C# project and namespace are still called `RtxWindowsUpdater` (the app's first name). They are kept on purpose; the EXE name and
all visible text come from `Core/AppInfo.cs`.

## Launch modes

`App.xaml.cs` decides the mode before any window opens (`Core/LaunchMode.cs`):

| Mode | How it starts | What runs |
|---|---|---|
| App | Normal start | Single-instance lock, language / theme, requirements page, Control Center |
| Setup | File name contains "Setup", or `--install` | Install / update window (`Views/SetupWindow.xaml`); the setup file is the same EXE |
| Uninstall | `--uninstall` (from the Windows uninstall entry) | Uninstall window with optional feedback; runs from a protected temporary copy so the installed EXE can be deleted |

A second app instance does not start; it asks the running one to show its window (`Core/AppSignals.cs`).

## Layers

```
Views (XAML, converters, custom controls, tray icon)
   │  data binding, commands
ViewModels (MainViewModel partial classes, tool view models, setup / update / welcome)
   │  calls services, receives IProgress<T> reports
Services (one module per card, diagnostics, update, installer, state, network)
   │  uses
Core (process / PowerShell / cmd runners, logging, localization, shell open, Win32 helpers)
```

| Folder | Responsibility |
|---|---|
| `Core/` | Infrastructure without UI: `ProcessRunner` (stdout / stderr, timeouts, process-tree kill), `CmdCommand` (safe command lines), `PowerShellRunner`, `Logger`, `Loc` (two-language strings), `ShellOpen` (open URLs and settings pages safely), `UnelevatedLauncher`, `RestartManager`, `ProtectedDirectory`, `FileProbe`, WMI and storage helpers |
| `Models/` | Shared result types: `ComponentStatus`, `ModuleResult`, `UpdateItem`, `RequirementsResult` |
| `Services/` | Card modules (`WingetManager`, `WindowsUpdateManager`, `MicrosoftStoreManager`, `NvidiaDriverManager`, `DefenderManager`, `DismManager`, `SfcManager`, `MrtManager`, `TemporaryFilesManager`, `RecycleBinManager`), `UpdateOrchestrator`, `UpdateService` (in-app update), `InstallerService`, `AppStateStore`, `InternetConnectivity`, speed test services, device monitoring |
| `Services/Diagnostics/` | Read-only diagnostics (drivers, apps, storage, events, crashes, network, DNS, privacy, battery, security, processes, startup apps, services, reports) |
| `ViewModels/` | Screen state and commands; `MainViewModel` is split into partial files by area (Device, Search, Tools, Logs, Legal, Internet, Exit, Preferences) |
| `Views/`, `Themes/` | XAML windows, styles, color dictionaries (`Colors.Dark.xaml`, `Colors.Light.xaml`), see [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) |

`Services` and `Core` contain no WPF references (verified by search on 2026-10-16); only `ViewModels` and `Views` depend on WPF.

## Card modules and the orchestrator

Every maintenance card implements `IUpdateModule` (`Services/IUpdateModule.cs`):

- `CheckAsync` inspects the system and must not change it.
- `UpdateAsync` performs only the applicable change, after the user confirms.
- Optional interfaces: `IMaintenanceModule` (card action button: SFC scan, DISM repair, MRT scan), `IInUseRetryModule` (close the apps
  that block an update, then retry), `IManualUpdateModule` (updates winget does not apply automatically), `IProgressReportingModule`,
  `IStopsBetweenItems` (cancel between packages without interrupting a running install).

`UpdateOrchestrator` runs modules in a fixed order (winget, Windows Update, Microsoft Store, NVIDIA, Defender, DISM, SFC, MRT,
temporary files, Recycle Bin). Each module runs on a worker thread (`Task.Run`) and is isolated: an exception in one module becomes a
failed result for that card and the others continue.

### Data flow of an operation

1. A command in `MainViewModel` calls `RunBusyAsync`, which locks the UI state (`IsBusy`).
2. The orchestrator runs the selected modules; modules run real tools (winget, PowerShell, DISM, SFC, MRT, COM, WMI) through
   `ProcessRunner` / `PowerShellRunner` and parse the real output and exit codes.
3. Progress is reported through `IProgress<T>` and throttled to one UI update per 100 ms.
4. Each module returns a `ModuleResult`; the card shows the real status, the Detailed Result panel shows the commands, exit codes
   and output (`Core/ExecutionTrace.cs`).
5. One final transition (`CompleteOperation`) stores the history, updates the cards and releases the UI.

Installs, repairs and deletions are started with `CancellationToken.None` (never interrupted halfway); checks can be cancelled.

## State, logging and configuration

- **State:** `Services/AppStateStore.cs` keeps settings and history in `%LOCALAPPDATA%\E-mre Control Center\state.json` (writes are
  batched for 300 ms and flushed on exit). Contents: preferences (language, theme, notifications, tray, log setting), operation
  history, speed test history (no IP addresses), legal acceptance record, Ookla terms acceptance date.
- **Logs:** one session log per start in `%LOCALAPPDATA%\E-mre Control Center\Logs\`; logs stay on the computer.
- **Configuration:** the app reads no configuration from environment variables or `.env` files; there is nothing to configure
  before running it. The only environment access is reading `PATH` to locate the Ookla tool, expanding variables in startup entries
  it displays, and setting `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for the protected uninstaller copy. Constants (name, repository, legal
  document URLs) live in `Core/AppInfo.cs`. Build configuration: `RtxWindowsUpdater.csproj` and `global.json`.

## Localization and themes

- Every user-visible string is written in Turkish and English where it is used: `L.T("Türkçe", "English")` in C#,
  `{l:T 'Türkçe', 'English'}` in XAML (`Views/LocExtension.cs`). The language is chosen before the first window opens; changing it
  restarts the app.
- Colors are defined twice with the same keys (`Themes/Colors.Dark.xaml`, `Themes/Colors.Light.xaml`) and used through
  `DynamicResource`; `Views/ThemeManager.cs` switches them at runtime.

## External services

The app connects to GitHub (update check and download), Microsoft (connectivity test, Windows Update, winget, Store, Defender, Edge
Update), app publishers (installers downloaded by winget), NVIDIA (driver lookup and download), Cloudflare (speed test, network
diagnostics) and, only when the user opts in, Speedtest by Ookla and Google Forms (uninstall feedback). The complete list with the data
sent is in the privacy policy ([TR](legal/tr/gizlilik-politikasi.md) / [EN](legal/en/privacy-policy.md)); keep it in sync when a
connection is added.

## Security boundaries

- **Elevation:** the manifest is `asInvoker`; administrator rights are requested only through the Windows UAC prompt (`runas`). UAC is
  never bypassed. Without administrator rights the app opens but all maintenance cards are unavailable.
- **While elevated:** nothing a standard user can modify is executed as administrator. winget is resolved from the protected
  WindowsApps folder (not `PATH`); downloaded installers are stored in folders writable only by Administrators and SYSTEM; the Ookla
  tool, which lives in the user profile, is started without elevation (`Core/UnelevatedLauncher.cs`).
- **Opening links:** URLs, settings pages and files are opened only through `Core/ShellOpen.cs` (via Explorer, so the browser does not
  inherit administrator rights); the app opens only URLs on its allowlist (`AppInfo.LegalLinks`, the Ookla site).
- **Command lines:** built with `CmdCommand`; arguments with shell metacharacters are rejected; winget package IDs are validated.
- **Downloads:** app updates are verified by size, SHA-256 digest published by GitHub, product name and version; NVIDIA drivers by
  Authenticode signature (NVIDIA Corporation) and download host.
- More detail: [SECURITY.md](SECURITY.md).

## Build and release

- Local build: `dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release` (0 warnings expected) and
  `tools\Build-Exe.ps1` for the single-file EXE plus the TR / EN setup copies.
- Release: pushing a `vX.Y.Z` tag runs `.github/workflows/release.yml`, which builds the EXE on GitHub Actions, publishes the TR, EN
  and compatibility setup files and a ZIP, and uses the matching `### vX.Y.Z` section of [CHANGELOG.md](CHANGELOG.md) as release notes.
  The in-app updater reads that release.
- The EXE is not code-signed (see [SECURITY.md](SECURITY.md), known gaps).

## Constraints

- Windows 11 (build 22000 or later) only; an internet connection is required to enter the app.
- Single-file self-contained publishing: a running single-file EXE cannot be moved safely, which is why the uninstaller works from a
  temporary copy.
- No automated test project in this repository; see [TESTING.md](TESTING.md).
