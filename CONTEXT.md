# CONTEXT.md

Product background for contributors and AI agents: what the app is, why it is built this way, and the decisions that shape it.
How to work in the code: [AGENTS.md](AGENTS.md). Technical details (Turkish): [docs/TEKNIK.md](docs/TEKNIK.md).

## What it is

E-mre Control Center is a free Windows 11 maintenance app. In one window it checks and applies updates (Windows Update, winget apps,
Microsoft Store, NVIDIA driver, Microsoft Defender, Microsoft Edge), cleans temporary files and the Recycle Bin, runs Windows repair
tools (SFC, DISM, MRT quick scan), and offers diagnostics (drivers, network, DNS, storage health, event log, crash analysis, startup
apps, services, processes, security status, system report, support package), device information and an internet speed test.

**For whom:** everyday Windows 11 users who want one trustworthy place for maintenance, especially gamers with NVIDIA RTX cards.
The interface is available in Turkish and English, with dark and light themes.

## Principles

1. **Ask first.** Anything that changes the system runs only after the user confirms.
2. **Show the truth.** Every result comes from Windows' own tools and APIs. A failure is shown as a failure, with the real reason.
   No "your PC is now 80% faster" style numbers, no fake progress.
3. **Stay safe.** Never bypass UAC, never touch personal files, never stop critical Windows components, verify every download.
4. **Respect privacy.** No account, telemetry, analytics, ads or cookies. Data stays on the user's computer.

## Key decisions

| Decision | Reason |
|---|---|
| Windows 11 is required | The app relies on Windows 11 components and APIs; other systems cannot enter the app. |
| NVIDIA RTX is optional | Without an RTX card the user continues "without RTX"; only the NVIDIA driver card is disabled. |
| Administrator rights for card operations | Updates and repairs need them. Without them the app opens, but all 10 cards are unavailable. |
| Internet is required | Checks and updates are online; offline results would be misleading. |
| Mandatory in-app updates from GitHub Releases | Every user gets fixes, including security fixes. Downloads are verified (SHA-256, size, product, version). |
| Setup is the same EXE | One file for app and installer; the installed file is byte-for-byte the downloaded one. TR and EN setup files differ only in name. |
| Language and theme chosen at first start | Turkish / English and dark / light; language changes restart the app so every text is consistent. |
| Source-available, not open source | The code is public for transparency; all rights are reserved ([LICENSE](LICENSE)). |
| Feedback only on uninstall, only if the user chooses | Optional reasons and message via Google Forms; no names, e-mail or computer names. |

## Non-goals

- "Speed boosters", registry cleaners or anything whose effect cannot be measured honestly.
- Drivers or software from unofficial sources (NVIDIA drivers come only from NVIDIA; apps through winget / Microsoft Store).
- Automatic repairs or deletions without confirmation, full MRT scans, deleting files that are in use.
- Collecting user data.

## Glossary

- **Card:** one maintenance component (e.g. Winget, SFC) with its own check and action.
- **Category / section:** home screen tiles (Updates, Cleanup, Device Health, Speed Test, General Settings, Summary, Device Info,
  System Tools) and the sections in their left menu.
- **Check:** read-only inspection. **Update / action:** the change that follows, only after confirmation.

## Status

The current version and its changes are in [CHANGELOG.md](CHANGELOG.md). Security reporting: [SECURITY.md](SECURITY.md).
