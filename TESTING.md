# Testing

How changes to E-mre Control Center are verified, and what is not covered. Report only what you actually ran.

## What exists in this repository

- **No automated test project is published in this repository.** The GitHub Actions workflow (`.github/workflows/release.yml`)
  builds and publishes releases; it does not run tests.
- The maintainer runs local test harnesses that are not part of the repository (described below).
- The one check everyone can run is the build: `dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release` must finish
  with **0 warnings and 0 errors** (.NET 8 SDK, version pinned in `global.json`).

## Safety rules for any test

- Never run a real system change in a test: no updates, installs, `sfc /scannow`, `DISM /RestoreHealth`, Recycle Bin emptying,
  temporary file cleanup, Defender definition updates or Edge installs. Test the logic in folders and registry keys the test creates
  itself, with fake results, or with read-only checks.
- Do not use the real settings file (`state.json`) of the person running the test; work on a copy.
- Do not open real browser windows or Settings pages from tests; test the rejection paths instead.

## The maintainer's local harnesses

| Harness | Covers |
|---|---|
| UI state | The real main window and view model, off-screen: requirements and non-admin states, all categories and sections, dialogs, update window (local fake GitHub server), offline lock screen, notification area, language and theme switching, legal links, keyboard focus audit (every focusable control, dark and light theme) |
| Services | Card modules with real read-only checks (winget, Microsoft Edge Update, Defender version lookup), output parsing, temporary file classification in test folders, process handling, offline behavior (proxy to a closed port), download verification |
| Setup | Install / update / uninstall into a test folder and a test registry key (never Program Files or HKLM), rollback, junction safety, feedback form against a local fake server, update service against a local fake GitHub server and, on request, the real GitHub release (read-only download and verification), keyboard focus in setup windows |
| No RTX / no admin | Simulated GPU lists (Intel, GTX) and a non-elevated process |
| Diagnostics | Read-only diagnostics on the real system, report masking (computer name, user name, MAC addresses must not appear) |
| Performance | CPU use of the UI while idle and during operations |

### Latest run

Run on 2026-10-16 against the version 2.0.2 code (Release build, 0 warnings), on Windows 11 with Turkish display language, not
elevated, without speed tests (`--no-network`):

| Harness | Result |
|---|---|
| UI state | 330 passed, 0 failed (keyboard focus audit: 1811 controls, 56 kinds) |
| Setup | 129 passed, 0 failed (keyboard focus audit: 62 controls) |
| No RTX, Intel GPU | 48 passed, 0 failed |
| No RTX, GTX GPU | 48 passed, 0 failed |
| Services | 152 passed, 0 failed |
| Diagnostics | 124 passed, 0 failed |

Earlier runs on 2026-10-12: English light theme scan of every section (only Windows-provided data such as service names was Turkish)
and the real GitHub release check (version 2.0.2 found, downloaded and verified). Real speed tests (Cloudflare and Ookla) were run
during development; they are skipped in offline-safe runs because they transfer 50-120 MB.

### Not covered

- Real installs, updates, repairs and deletions (by design, see the safety rules).
- Screen readers (Narrator, NVDA) and Windows high contrast themes.
- Windows with an English display language, ARM64 devices, multi-monitor DPI changes.
- Elevated runs: tests simulate administrator state in the view model instead of elevating.

## Manual checks for a change

Before opening a pull request, check what your change touches:

- **Languages:** every new visible string in Turkish and English; switch the language in General Settings → Quick Settings.
- **Themes:** dark and light theme; no hard-coded colors.
- **Keyboard:** Tab / Shift+Tab reach new controls and show the focus outline; Esc and Enter behave as before.
- **Rights and hardware:** with and without administrator rights; with and without an NVIDIA RTX card if the change is GPU related.
- **Offline:** the app shows the offline lock screen and does not report success without a connection.
- **Small screens:** 1366 x 768 and 125 % / 150 % scaling.
- **Results:** a failure is shown as a failure with the real message.

## Documentation checks

- Relative Markdown links and `#anchors` resolve (the maintainer checks them with a local script; 0 broken links on 2026-10-16).
- No emoji, no secrets, no personal data, no invented features or results.

## Reporting

In the pull request, list what you ran and the real result (for example "build: 0 warnings; checked dark and light theme manually").
Do not mark a check as passed if it was not run.
