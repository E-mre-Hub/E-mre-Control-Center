# CONTEXT.md

Product background for contributors and AI agents: what the app is, why it is built this way, and the decisions that shape it.
How to work in the code: [AGENTS.md](AGENTS.md). Requirements: [PRD.md](PRD.md). Architecture: [ARCHITECTURE.md](ARCHITECTURE.md).
Technical details (Turkish): [docs/TEKNIK.md](docs/TEKNIK.md).

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

## Document map

| Document | Purpose |
|---|---|
| [README.md](README.md) | User-facing overview, install, requirements, FAQ (Turkish and English) |
| [CHANGELOG.md](CHANGELOG.md) | Release notes per version; the release workflow and the in-app update window read it |
| [PRD.md](PRD.md) | Requirements with their verification status |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Layers, data flow, state, security boundaries, build and release |
| [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) | Colors, typography, components, focus, motion, measured contrast |
| [CODE_STYLE.md](CODE_STYLE.md) | Coding conventions |
| [TESTING.md](TESTING.md) | How changes are verified, latest results, what is not covered |
| [AGENTS.md](AGENTS.md) | Rules for contributors and AI agents |
| [SECURITY.md](SECURITY.md), [ACCESSIBILITY.md](ACCESSIBILITY.md) | Security policy and measures; accessibility statement |
| [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | Contribution guide and community rules |
| [LICENSE](LICENSE), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | License of this project; licenses of bundled components |
| [legal/](legal/README.md) | Privacy Policy, Terms of Use, Cookie Policy (Turkish and English) |
| [docs/TEKNIK.md](docs/TEKNIK.md) | Full technical documentation in Turkish |

`CLAUDE.md` is the maintainer's local working notes for Claude Code; it is intentionally not published (listed in `.gitignore`).
The shared rules for every agent are in [AGENTS.md](AGENTS.md).

## Current status

Facts as of 2026-10-16, with how they were checked.

- **Release:** version 2.0.2 (tag `v2.0.2`, 2026-10-12) on GitHub Releases. The release workflow succeeded; the in-app updater found
  the release on the real GitHub API, downloaded the Turkish setup file and verified size, SHA-256, product name and version.
- **Technology:** C# / .NET 8 / WPF (MVVM), single self-contained EXE; one NuGet package (System.Management 8.0.0); Windows SDK
  projection for WinRT APIs (checked in the project file and the build output).
- **Configuration:** the app reads no settings from environment variables or `.env` files, so no `.env` / `.env.example` exists by
  design. Settings are stored in `state.json`; build settings in the project file and `global.json` (checked by searching the source).
- **Build and tests:** Release build with 0 warnings and 0 errors; the maintainer's local harnesses passed (see
  [TESTING.md](TESTING.md#latest-run)).

### Git and security review (2026-10-16)

| Check | Method | Result |
|---|---|---|
| Secrets in the current files | `git grep` for key, token, password and private key patterns | None found |
| Secrets in the history | The same patterns on every commit (33 commits) | None found |
| Sensitive files ever tracked | File names in the whole history (`.env`, certificates, keys, `state.json`, logs) | None |
| `.gitignore` | Read | Ignores build outputs, logs, `.env*`, certificates and keys, local tool settings, local notes and local tests; does not ignore source, documentation or `global.json` |
| GitHub secret scanning and push protection | API | Enabled, 0 open alerts |
| Dependabot alerts | API | Enabled, 0 open alerts |
| Vulnerable NuGet packages | `dotnet list package --vulnerable --include-transitive` | None |
| Release workflow | Read | Least privilege (`contents: write`); third-party release action pinned to a commit since 2026-10-16 |
| Personal data in published files | Search for e-mail addresses and local user paths | None |

### Legal links in the app (audit 2026-10-16)

| Item | In the app | Result |
|---|---|---|
| About | Device Info → About: version, installation status, update status with "Check now", language, data folder, log file, source code link | Present; checked by the UI test harness |
| Privacy Policy | General Settings → Legal and Privacy (Turkish and English links), the acceptance box on the requirements page, the setup welcome note | All links are on the app's allowlist, point to files that exist in the repository (automated check) and resolve on GitHub (HTTP 200). Tests do not open a real browser |
| Terms of Use ("Terms of Service") | Same places as the Privacy Policy | Same result |
| Cookie Policy | General Settings → Legal and Privacy (version 2.0.2 and later) | Same result |
| Privacy notice | No separate item. The short notice is the Privacy card text and the setup note; the details are in the Privacy Policy | Not duplicated on purpose |
| KVKK | No separate item or document. KVKK and GDPR rights are in section 7 of the Privacy Policy | Separate notice not created; see open decisions |

## Open decisions

These need a decision by the maintainer; nothing below has been decided by guessing.

1. **Data controller identity.** The legal documents name "an independent developer publishing under the name E-mre Control Center"
   and GitHub Issues as the contact. KVKK and GDPR notices normally identify the controller by name and a contact address. Decide
   whether to publish them.
2. **Separate KVKK notice (aydınlatma metni).** Not created: the Privacy Policy already covers the purposes, legal basis, retention and
   rights, but not the controller identity (item 1). Decide after item 1; a legal review is recommended.
3. **Cross-border transfer.** Optional uninstall feedback is stored in Google Forms / Google Sheets, whose servers may be outside
   Türkiye; this is disclosed in the Privacy Policy. A legal review should confirm that the consent given when sending feedback is
   sufficient under KVKK.
4. **Legal review.** The Privacy Policy, Terms of Use and Cookie Policy are drafts that have not been reviewed by a lawyer. The
   governing law in the Terms of Use is Türkiye.
5. **Code signing.** The EXE is unsigned (SmartScreen warning, "Unknown publisher"); a certificate is needed to change that.
6. **Contrast and motion.** Measured contrast gaps and the unhonored Windows "Animation effects" setting need a design decision and a
   release ([DESIGN_SYSTEM.md](DESIGN_SYSTEM.md#contrast-measured)).
7. **Notices inside the app.** Third-party notices are in the repository and, from the next release, in the ZIP; the app's About
   section does not link to them yet.

## Next steps

- Decide items 1-4 with legal advice, then update the legal documents and `AppInfo.LegalVersion` if the content changes materially.
- Implement the contrast proposal and the animation setting in the next version, after the design decision.
- If an agent's task is interrupted, record what was done and what remains in this section.
