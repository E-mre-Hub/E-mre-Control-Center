# Design system

The visual language of E-mre Control Center as implemented in `src/RtxWindowsUpdater/Themes/` (values below were read from
`Theme.xaml`, `Colors.Dark.xaml` and `Colors.Light.xaml` on 2026-10-16). Proposals are marked as such and are not implemented.

## Principles

1. **Dark first, calm, sharp.** Near-black and dark navy surfaces with a neon blue accent; the light theme keeps the same structure.
2. **Important things glow, everything else stays quiet.** The accent marks the primary action, the active section and key icons.
3. **Status is never color alone.** Every colored dot or icon has a text label ("Up to date", "Error", ...).
4. **Real state only.** No decorative fake progress; spinners and shimmer run only while an operation is actually running.
5. **No emoji.** Icons come from Segoe Fluent Icons (fallback Segoe MDL2 Assets).

## Color tokens

Raw colors are `Color` resources named `C.*`; brushes used by views are `B.*` (often the same value). Views must use brushes through
`DynamicResource` so the theme can switch at runtime. Never hard-code a color in a view; add a key to both color files instead.

| Token | Dark | Light | Use |
|---|---|---|---|
| `C.Bg0` | `#020307` | `#F3F6FB` | Window background |
| `C.Bg1` / `C.Bg2` | `#070C18` / `#0B1426` | `#EDF1F7` / `#E6ECF4` | Panels, deeper areas |
| `C.Surface` / `C.SurfaceHi` | `#0C1424` / `#111C33` | `#FFFFFF` / `#F5F8FC` | Surfaces, raised surfaces |
| `B.CardBg` (gradient) | `#0C1528` → `#080F1D` | `#FFFFFF` → `#FAFBFD` | Cards |
| `C.Border` / `C.BorderHi` | `#1A2742` / `#24365C` | `#D8E0EB` / `#C2CEDE` | Borders |
| `C.Neon` | `#1FB6FF` | `#0A84D6` | Accent: links, selected items, focus |
| `B.NeonBright` | `#4CCBFF` | `#0A84D6` | Strong accent: home icons, focus outlines |
| `C.Neon2` | `#3D6BFF` | `#2F5BE0` | Second gradient stop |
| `B.NeonGradient` | `#1FB6FF` → `#3D6BFF` | `#1AA6F2` → `#3460F0` | Primary button, progress, selection bar |
| `C.Text` | `#E7EEF9` | `#0D1626` | Primary text |
| `C.Text2` | `#A7B5CF` | `#3A4860` | Secondary text |
| `C.Text3` | `#7887A5` | `#5F6E87` | Tertiary text, descriptions, labels |
| `C.Green` / `C.Orange` / `C.Red` | `#2BD67B` / `#FFA63D` / `#FF4D61` | `#12A150` / `#C86A00` / `#D32F3F` | Success / attention / error status |
| `C.Gray` / `C.Blue` | `#5D6A84` / `#3DA5FF` | `#8592A8` / `#1C7FD9` | Neutral / info status |
| `B.OnAccent` | `#FFFFFF` | `#FFFFFF` | Text on accent backgrounds |

Further semantic brushes (both themes, same keys): `B.Control*`, `B.HoverFill`, `B.SelectedFill`, `B.InputBg`, `B.InputBorder`,
`B.TileBg`, `B.TileBorder`, `B.ChipBg`, `B.RowBg`, `B.RowBorder`, `B.AltRow`, `B.NavHover`, `B.NavSelected`, `B.Overlay`,
`B.PopupBg`, `B.DeepBg`, `B.Timestamp`, `B.LinkHover`, `B.CardBorderHover` and others in the color files.

## Typography

| Resource | Font | Use |
|---|---|---|
| `F.Text` | Segoe UI Variable Text, fallback Segoe UI | Body text |
| `F.Display` | Segoe UI Variable Display, fallback Segoe UI | Headings |
| `F.Mono` | Cascadia Mono, fallback Consolas | Log, command output |
| `F.Icons` | Segoe Fluent Icons, fallback Segoe MDL2 Assets | Icons |

| Style | Size / weight | Color |
|---|---|---|
| `T.H1` | 26, SemiBold, Display | `B.Text` |
| Section title (category pages) | 24 | `B.Text` |
| `T.H2` | 15, SemiBold, Display (cards often set 16) | `B.Text` |
| `T.Body` | 13 | `B.Text2` |
| `T.SettingDesc` | 12 | `B.Text3` |
| `T.Label` | 11, SemiBold | `B.Text3` |

## Shape, size and depth

- **Corner radius:** cards 14, primary and accent buttons 12, secondary buttons 10, ghost buttons 8, chips 13, icon tiles 20.
- **Sizes:** buttons 44 high by default (primary 54, accent 50, ghost 34), search box 36, caption buttons 46 x 36, icon tiles
  68 x 68 with a 30 px glyph, progress bar 8.
- **Cards:** `Card.Flat` (padding 20, border 1) and `Card` (= `Card.Flat` + `Fx.CardShadow`). Cards whose content changes or animates
  use `Card.Flat` plus a separate `CardShadowLayer`, so the shadow is not recalculated every frame.
- **Effects:** `Fx.CardShadow` (blur 28, depth 6, 55 %), `Fx.IconGlow` (blur 20, 85 %), `Fx.NeonGlow` (blur 22), `Fx.SoftGlow`
  (blur 14). Glow colors follow the accent.

## Layout

- Window: 1340 x 900 by default, minimum 1024 x 640; fits a 1366 x 768 screen.
- Home: logo and name, search box, eight category items in two rows of four, quick action bar.
- Category pages: left column (Home, category title, section list, progress) and a right content area; content width is capped at
  1120 px. List rows use a 96 px icon column (icon tile) and a wide card.
- Text wraps instead of being cut; avoid fixed widths for labels because English and Turkish strings differ in length.

## Components

| Component | Styles | Notes |
|---|---|---|
| Buttons | `Btn.Primary`, `Btn.Accent`, `Btn.Secondary`, `Btn.Ghost`, `Btn.Link`, `Btn.Row`, `Btn.Caption`, `Btn.CaptionClose` | One primary action per area |
| Choice chips | `Chip` (RadioButton) | Filters, language / theme selection |
| Check boxes | implicit `CheckBox`, `SelectCheck` (card selection), `ToggleSwitch` (settings) | |
| Inputs | `Input.Search` (Tag = placeholder), `Input.ReadOnly` | |
| Cards and tiles | `Card`, `Card.Flat`, `CardShadowLayer`, `CardLift`, `IconTile`, `T.TileIcon` | |
| Tables | implicit `DataGrid`, `Grid.Tool`, `T.Cell` | No horizontal scrolling; cells trim with a tooltip |
| Progress | `Progress.Neon` (shimmer only when `Tag=True`) | |
| Tray menu | `Tray.Menu`, `Tray.MenuItem`, `Tray.Separator` | |

## Icons

Segoe Fluent Icons glyphs in use include (verified by rendering): CPU `E950`, GPU `E964`, RAM `EEA0`, disk `EDA2`, OS `E770`,
compatibility `EA86`, performance `E9D2`, about `E946`, speed test `EC4A`, privacy `E72E`, terms `E8A5`, services / web `E774`,
cookie policy (no tracking) `ED1A`. Check a new glyph by rendering it before use.

## Keyboard focus

Every focusable control shows a 2 px `B.NeonBright` outline when focused with the keyboard (never on mouse click). Outline styles:
`Focus.Ring`, `Focus.Pill`, `Focus.Round`, `Focus.Circle`, `Focus.Box`, `Focus.Default` (outside, follow the corner radius) and
`Focus.Inset`, `Focus.Row` (inside, for controls at the edge of a window or scroll area). Details: [AGENTS.md](AGENTS.md#keyboard-focus).

## Motion

- Short, one-time transitions only: page entrance (fade and 18 px slide, 0.35-0.45 s), card hover lift (3 px, 0.18-0.25 s).
- Continuous animation (spinner, progress shimmer, 30 fps) runs only while an operation is running and stops immediately after.
- Never put a continuous animation inside an element that has an effect (shadow, blur): WPF would recompute the effect every frame.
- The Windows "Animation effects" setting is not yet honored (see [ACCESSIBILITY.md](ACCESSIBILITY.md)).

## Contrast (measured)

WCAG 2.x contrast ratios computed from the color values above (relative luminance formula) on 2026-10-16. AA requires 4.5:1 for
normal text and 3:1 for large text (at least 18.66 px bold or 24 px).

| Pair | Dark | Light |
|---|---|---|
| Primary text on card | 15.60:1 | 18.11:1 |
| Secondary text on card | 8.80:1 | 9.23:1 |
| Tertiary text on card | 5.03:1 | 5.16:1 |
| Accent / link on card | 7.97:1 | 3.97:1 (below AA for small text) |
| Green / orange / red status on card | 9.54 / 9.34 / 5.62:1 | 3.37 / 3.81 / 4.94:1 (green, orange below AA for small text) |
| White text on primary button, gradient start | 2.28:1 (below AA) | 2.70:1 (below AA) |
| White text on primary button, gradient end | 4.43:1 | 5.15:1 |
| Log timestamp on log background | 2.47:1 (below AA) | 3.31:1 (below AA for small text) |

**Proposal (not implemented, needs a design decision and a release):** darken the start of the primary button gradient (or use dark
text on it), darken the light theme accent, green and orange for text use, and lighten the dark theme log timestamp, so that all pairs
reach 4.5:1 while keeping the neon look.
