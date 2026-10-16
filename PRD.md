# Product requirements (PRD)

What E-mre Control Center must do, for whom, and how we know it does it. This document describes the shipped product (version 2.0.2,
released 2026-10-12). Background and key decisions: [CONTEXT.md](CONTEXT.md). Architecture: [ARCHITECTURE.md](ARCHITECTURE.md).

## Vision

One trustworthy window for keeping a Windows 11 PC up to date, clean and healthy, which never changes anything without asking and
never shows a result it cannot prove.

## Problem

Maintenance on Windows is spread across many places (Windows Update, Microsoft Store, winget, the NVIDIA app, Defender, Disk Cleanup,
SFC / DISM on the command line). "Optimizer" tools that combine them often show invented scores or make changes silently. Users who are
not experts cannot tell what really happened.

## Target users

- Everyday Windows 11 users who want maintenance in one place, in Turkish or English.
- Gamers with NVIDIA RTX graphics cards (driver updates); PCs without RTX are supported with the NVIDIA card disabled.
- More technical users who want to see the real commands, exit codes and output behind each result.

## Requirements

Status legend: **Verified** = covered by the maintainer's automated local test harnesses (UI state, services, setup / uninstall in a
test location); **Not exercised** = implemented, but the real system change is deliberately not run in automated tests; **Not verified**
= no systematic test yet.

### Entry and requirements

| ID | Requirement | Status |
|---|---|---|
| FR-1 | Windows 11 (build 22000+) is required; on other systems the app cannot be entered. | Verified |
| FR-2 | An internet connection is required to enter; if it is lost while running, a lock screen waits for it (a running operation is not interrupted). | Verified (simulated offline) |
| FR-3 | Without administrator rights the app opens but all 10 maintenance cards are unavailable and a "Restart as administrator" action (UAC) is offered. | Verified |
| FR-4 | Without an NVIDIA RTX card the user continues "without RTX"; only the NVIDIA driver card is unavailable. | Verified (simulated GPUs) |
| FR-5 | The user accepts the requirements, Terms of Use and Privacy Policy once per document version; acceptance is stored only locally. | Verified |
| FR-6 | Language (Turkish / English) and theme (dark / light) are chosen at first start and can be changed later. | Verified (changing the language restarts the app; the restart itself is not exercised) |

### Maintenance

| ID | Requirement | Status |
|---|---|---|
| FR-10 | "Check all" inspects every available card without changing the system; each card shows its real status and the Detailed Result shows commands, exit codes and output. | Verified (real read-only checks for winget, Edge, temporary files, Defender version lookup) |
| FR-11 | "Update all" / per-card actions apply only what a check found, after a confirmation that lists the actions. | Verified for the UI flow; real installs Not exercised |
| FR-12 | Supported components: winget apps, Windows Update, Microsoft Store, NVIDIA driver (official NVIDIA source, signature checked), Microsoft Defender definitions, Microsoft Edge / WebView2 (through Microsoft Edge Update). | Checks Verified; installs Not exercised |
| FR-13 | Cleanup of temporary file categories (only files older than 24 hours, not in use, not system / read-only) and the Recycle Bin, with measurement before and after. | Classification Verified in test folders; real cleanup Not exercised |
| FR-14 | Repair tools: SFC (`/verifyonly` to check, `/scannow` after confirmation), DISM (`/CheckHealth`, `/RestoreHealth` only after confirmation), MRT quick scan. | Result parsing Verified; real repairs Not exercised |
| FR-15 | If running apps block an update, they are detected (Restart Manager) and closed only after confirmation; Windows, services and critical processes are never closed. | Verified with test processes |
| FR-16 | Installs, repairs and deletions are never interrupted halfway (also when the user exits during them). | Verified |

### Diagnostics, device and network

| ID | Requirement | Status |
|---|---|---|
| FR-20 | One-click diagnosis and read-only sections: drivers, apps, storage health and analysis, event log, crash analysis, network, DNS, privacy, battery, security status, startup apps, services, processes. | Verified (UI and real reads) |
| FR-21 | System report (TXT / HTML / JSON) and support package (ZIP) are created only on request; computer name, user name, profile path, IP and MAC addresses are masked. | Verified |
| FR-22 | Device information and live performance (only while the section is open); values that cannot be read are shown as unavailable with the reason, never estimated. | Verified |
| FR-23 | Internet speed test with Cloudflare (default) or Speedtest by Ookla (optional, installed and used only after the user accepts Ookla's terms). | Verified with real tests during development; skipped in the latest offline-safe run |

### App lifecycle

| ID | Requirement | Status |
|---|---|---|
| FR-30 | Setup installs to Program Files with shortcuts and a Windows uninstall entry; every step is verified and rolled back on failure. | Verified in a test location; real Program Files install Not exercised |
| FR-31 | Uninstall removes only the app's own files and entries, optionally the user data, and offers optional feedback (Google Forms). | Verified in a test location |
| FR-32 | New versions are detected at start and every 5 minutes; the update is mandatory and the installer is verified (size, SHA-256, product name, version) before it runs. | Verified (fake server and the real GitHub release, read-only) |
| FR-33 | Closing the window keeps the app in the notification area; double-click reopens it where it was. | Verified |

### Non-functional requirements

| ID | Requirement | Status |
|---|---|---|
| NFR-1 | Truthful results: no fake progress, no invented scores; failures are shown with the real reason. | Design rule; checked in reviews and tests |
| NFR-2 | Consent: nothing changes the system without confirmation; no silent installs, deletions, forced restarts, Defender or firewall changes. | Design rule; Verified for the flows above |
| NFR-3 | Security: UAC is never bypassed; nothing user-writable runs as administrator; downloads are verified (see [SECURITY.md](SECURITY.md)). | Verified for the implemented checks |
| NFR-4 | Privacy: no account, telemetry, analytics, ads or cookies; data stays on the computer (see the legal documents). | Verified by code review on 2026-10-12 / 2026-10-16 |
| NFR-5 | Accessibility: keyboard use, visible keyboard focus on every focusable control, status not by color alone (see [ACCESSIBILITY.md](ACCESSIBILITY.md)). | Focus Verified (automated audit); screen readers Not verified; contrast partly below WCAG AA (measured) |
| NFR-6 | Internationalization: every visible string in Turkish and English. | Verified (automated scan of English screens) |
| NFR-7 | Performance: no continuous animation while idle. On the maintainer's PC (2026-09-24) the UI used about 0 % CPU idle and roughly 3-13 % of one core during operations. | Measured once; not monitored continuously |

## Key scenarios

1. **Monthly check-up.** The user opens the app, clicks "Check all", reviews the found updates and cleanable space, clicks "Update all",
   confirms, and reads the real result per card.
2. **Something is wrong.** The user runs one-click diagnosis, opens the failing section, and creates a support package to share.
3. **No RTX, no admin.** On a PC with integrated graphics the user continues without RTX; without administrator rights the cards are
   unavailable but speed test, device information and diagnostics that do not need rights still work.
4. **New version.** While the app is open a new release is published; within five minutes the update window appears and the update
   installs after verification.

## Current scope and candidates

All requirements above are implemented in version 2.0.2. The following are candidates; none is scheduled:

- Raise the contrast of the primary button text, the light theme accent / status colors and the log timestamps to WCAG AA (measured
  gaps in [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md#contrast-measured)).
- Honor the Windows "Animation effects" setting.
- Systematic testing with Windows Narrator and NVDA; support for Windows high contrast themes.
- Code signing of the EXE (needs a certificate; see [SECURITY.md](SECURITY.md)).

## Out of scope

"Speed boosters", registry cleaners or anything whose effect cannot be measured honestly; drivers or software from unofficial
sources; automatic repairs or deletions without confirmation; full MRT scans; deleting files that are in use; collecting user data.

## Open questions

Tracked in [CONTEXT.md](CONTEXT.md#open-decisions) (legal entity and data controller details, legal review of the documents, code
signing).
