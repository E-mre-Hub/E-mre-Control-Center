# Code style

Conventions used in this repository. "Observed" rules were measured on the current code on 2026-10-16 and are followed consistently;
"recommended" rules are suggestions that are not enforced by any tool. Project rules that matter more than style (real results,
consent, safety, two languages, themes) are in [AGENTS.md](AGENTS.md).

## Tooling

- There is no `.editorconfig`, no analyzer rule set and no automatic formatter in the build or CI. Match the surrounding code.
- The build must finish with **0 warnings and 0 errors**: `dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release`.
- Line endings are set by [.gitattributes](.gitattributes): C#, XAML, csproj and PowerShell files are CRLF in the working copy; YAML is
  LF. Save `.ps1` files as UTF-8 **with BOM** (Windows PowerShell 5.1 needs it for Turkish characters).

## C#

Observed:

- File-scoped namespaces (`namespace RtxWindowsUpdater.Services;`), four-space indentation, no tabs.
- Nullable reference types and implicit usings are enabled; handle `null` explicitly instead of suppressing warnings.
- Naming: `PascalCase` for types, methods, properties and constants; `_camelCase` for private fields; `camelCase` for locals and
  parameters; asynchronous methods end with `Async`.
- Types are `sealed` unless they are designed for inheritance; small immutable data types are `record`s.
- `async void` only for event handlers and `ICommand.Execute`; everything else returns `Task` / `Task<T>`.
- `internal` is used for members that are tested but not part of the public surface.
- XML documentation comments (`/// <summary>`) explain *why*, not only *what*, and record decisions and evidence (for example a log
  that showed a bug). Comments and documentation comments in the code are written in Turkish; keep that when editing existing files.
- Long lines are common for two-language strings (`L.T("…", "…")` stays on one line so both languages can be compared); otherwise wrap
  around 140 characters.

Recommended:

- Keep `Services` and `Core` free of WPF types (today they are); UI logic belongs in `ViewModels`.
- Run module work on a worker thread (the orchestrator already uses `Task.Run`); never block the UI thread.

## Error handling and results

- Every result must come from a real exit code, tool output or Windows API result. A timeout, empty output or "process started" is
  not success. If output cannot be interpreted, report "failed / could not be interpreted" with the real message.
- Catch specific exceptions where a failure is expected (for example `IOException`, `UnauthorizedAccessException`) and report the
  real reason; do not swallow exceptions silently.
- Installs, repairs and deletions are started with `CancellationToken.None`; checks accept a cancellation token.
- Write user-relevant steps to the session log through `Logger`; never log secrets, personal data, local IP or MAC addresses.

## Strings and localization

- Every user-visible string is written in both languages where it is used: `L.T("Türkçe", "English")` in C#, `{l:T 'Türkçe',
  'English'}` in XAML (escape `'` as `\'`; a value that starts with `{` needs `{}`).
- Do not translate strings used to parse tool output, internal keys, or folder and file names used to clean up earlier versions.
- When code compares a displayed string, wrap both sides in `L.T`.

## XAML

- Use the shared styles in `Themes/Theme.xaml` (see [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md)); colors only through `DynamicResource`
  keys that exist in both color dictionaries.
- Every new focusable control shows keyboard focus (`Focus.*` styles) and has a UI Automation name (`AutomationProperties.Name`).
- Icons are Segoe Fluent Icons glyphs; no emoji anywhere (UI, comments, documentation).
- Never put a continuous animation inside an element that has an effect (shadow, blur).

## Commands and processes

- Build command lines with `CmdCommand` (it rejects shell metacharacters); validate identifiers (for example winget package IDs)
  before passing them to a tool.
- Open URLs, settings pages and files only through `Core/ShellOpen.cs`; add new URLs to an explicit allowlist.
- While running as administrator, never execute a file that a standard user can modify.

## Reviews

Recommended review checklist (also in [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md)): build with 0 warnings,
both languages present, theme keys only, keyboard focus and UI Automation name, real results, no system change without consent, no
secrets or personal data, documentation updated.
