# IMP Weld Inspection — design instructions

The app should look like it belongs to the same family as **Kosovnice**, the IMP
BOM web app (Izometrije / Zvari / Popisi). Every value below is taken from that
app's stylesheet (`C:\VS_Projects\IMP_BOMs\IMP_Kosovnice\src\App.css`), not
invented. If something here is unclear, open Kosovnice and match what you see.

Read `..\SPEC.md` first. This file only covers how the app looks.

---

## 1. Character

- **Utilitarian, dense, calm.** It's a shop-floor tool: a table of welds, a
  button, a progress bar. No splash screens, no decorative animation.
- **One accent colour, used sparingly:** the header, the primary action, focus
  rings. Everything else is neutral grey.
- **Zvari family.** Each Kosovnice app has its own accent. This one belongs to
  welds, so it uses **Zvari green `#2E7D32`**.
- Slovenian UI text, in the same register as Kosovnice: `Uvozi`, `Prekliči`,
  `Počisti kartico`, `Odpri mapo`.

---

## 2. Colour

Ready to use as WPF resources: `Theme.Light.xaml` and `Theme.Dark.xaml` in this
folder. Same keys in both, so switching theme is swapping one dictionary.

| Role | Light | Dark | Kosovnice source |
|---|---|---|---|
| Accent (header, primary) | `#2E7D32` | `#2E7D32` | Zvari `--accent` |
| Accent hover | `#256B29` | `#256B29` | 85 % accent + black, as `.create-bom-btn:hover` |
| Accent tint (selection, chips) | `#2E7D32` @ 9 % | `#2E7D32` @ 15 % | `--accent-light` |
| Window background | `#E8E8E8` | `#1E1E1E` | `.app-container` / grid dark rows |
| Surface (dialogs, panels) | `#FFFFFF` | `#242424` | `.create-bom-modal` |
| Toolbar button | `#FFFFFF` | `#3A3A3A` | `.toolbar-btn` |
| Text | `#1F2937` | `#E5E7EB` | |
| Text, muted | `#6B7280` | `#9CA3AF` | |
| Border | `#D1D5DB` | `#3A3A3A` | |
| Table header | `#E8E8E8` | `#252525` | `.ag-theme-alpine` |
| Table row / alt row | `#F8F8F8` / `#F8F8F8` | `#252525` / `#1E1E1E` | |
| Row separator | `#FFFFFF` | `#2A2A2A` | |
| Success (Import, "new") | `#16A34A`, hover `#15803D` | `#22C55E`, hover `#16A34A` | `.create-bom-btn--primary` |
| Warning (partly new, unresolved) | `#D97706` | `#FBBF24` | repair amber |
| Danger (failed, Clear card) | `#DC2626`, hover `#B91C1C` | `#F87171` | `.create-bom-btn--danger` |

The header is the accent colour in **both** themes, exactly as in Kosovnice.

---

## 3. Typography

| Use | Font | Size | Weight | Notes |
|---|---|---|---|---|
| App title in header, dialog titles, big numbers | **Barlow Condensed** | 22 px (1.4 rem) | Bold | UPPERCASE, letter-spacing 0.05 em. Header title white; dialog title in accent. |
| Body, table, buttons | **Segoe UI** | 13 px | Regular; buttons SemiBold (500) | |
| Secondary / hints | Segoe UI | 11–12 px | Regular | muted text colour |

**About Barlow Condensed:** Kosovnice asks for it but never loads it, so on most
machines it actually falls back to Segoe UI. That's a bug on the web side.
Here, **bundle it**: download `BarlowCondensed-Bold.ttf` from Google Fonts (SIL
Open Font License, free to embed), add it as a WPF `Resource`, and reference it
as `pack://application:,,,/Assets/Fonts/#Barlow Condensed`. Keep `Segoe UI` as
the fallback in the `FontFamily` list.

---

## 4. Layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ [imp logo] | WELD INSPECTION  [Uvoz] [Pregled]   [Posodobi] [⟳] [☀/☾] │ ← accent header, 52 px
│ ░░ faint grid lines fading out to the right ░░                        │
├──────────────────────────────────────────────────────────────────────┤
│ SD kartica E:\  ·  3-6-40-00-4501 - FAMAR …  ·  142 fotografij        │ ← context bar, 36 px, muted
├──────────────────────────────────────────────────────────────────────┤
│ Sklop │ Izometrija              │ Zvar │ Fotografij │ Status  │ Cilj  │ ← table (header #E8E8E8)
│ …     │ 2000487 - 100-EAP-…    │ W2a  │     3      │ ● nova  │ …     │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│ 12 praznih zvarov skritih          [Odpri mapo]  [Prekliči] [UVOZI]  │ ← footer, 56 px
└──────────────────────────────────────────────────────────────────────┘
```

- **Minimum window** 960 × 600. Remember size and position between runs.
- **Header** (mirrors `.app-header`):
  - Background: accent. Shadow: `0 2px 6px` black at 25 % (50 % in dark).
  - Grid pattern: 1 px white lines at 9 % opacity every 20 px, both directions,
    faded out left → right (fully visible at 0 %, gone by 75 % of the width).
    Use `patterns/header-grid.png` as a tiled `ImageBrush` with a horizontal
    `LinearGradientBrush` opacity mask, or draw it with a `DrawingBrush`.
  - Logo: `logo/imp-logo-white.png`, 30 px tall, at 85 % opacity, 12 px from the
    left edge.
  - Title: `WELD INSPECTION` behind a 2 px divider of white at 50 %, 10 px after the
    logo, as Kosovnice's `.app-brand-label`: Segoe UI 13 px bold, uppercase, 2 px
    letter-spacing, white at 90 %.
  - Right side: toolbar buttons (§5).
- **Spacing:** 4 px base unit. Panels padded 12–16 px. Table cells 8 px
  horizontally.
- **Two pages, switched by tabs in the header:** Uvoz (import) and Pregled (viewer).
  Dialogs for confirmations.

---

## 5. Components

### Toolbar button (header, footer icons)
- 34 × 34 px, corner radius 7 px, no border.
- Light: white background, text colour `#1F2937`. Dark: `#3A3A3A`, text `#E5E7EB`.
- Hover: accent background, white icon. Transition 150 ms on colour and background.
- Icon 16–18 px.

### Buttons (footer, dialogs)
- Padding 8 × 16 px, corner radius 6 px, 13 px SemiBold.
- **Primary action (`UVOZI`)**: success green `#16A34A`, hover `#15803D`, white text.
  Green rather than the accent, as Kosovnice's primary "save" buttons are.
- **Secondary (`Prekliči`, `Odpri mapo`)**: transparent background, muted text, 1 px
  border `#D1D5DB`, hover background `#F3F4F6` (dark: `#3A3A3A`).
- **Danger (`Počisti kartico`)**: `#DC2626`, hover `#B91C1C`, white text. Only shown
  after a verified import, and always behind a confirmation dialog.
- Disabled: 50 % opacity, no hover.

### Dialog (confirmations, summary)
- Mirrors `.create-bom-modal`: width 360 px (summary may go to 520 px),
  padding 24 × 26 px, corner radius 12 px, shadow `0 18px 48px` black at 35 %.
- Dims the window behind it with black at ~40 %.
- Title: Barlow Condensed 22 px Bold uppercase, accent colour. Danger dialogs
  use the danger colour.
- Buttons right-aligned, secondary first, then primary or danger.

### Table
- Styled like the Kosovnice grid (AG Grid "alpine" as themed there): 13 px Segoe
  UI, row height 28 px, header 32 px bold `#111827`, header background `#E8E8E8`,
  rows `#F8F8F8` separated by 1 px white lines. No vertical borders.
- Numbers centered. Free text (isometrija name, destination) left-aligned with
  ellipsis and a tooltip showing the full value.
- Selected row: accent tint background. No row hover highlight.
- **Status chip** in the Status column: small rounded rectangle (radius 4 px,
  padding 2 × 8 px, 11 px SemiBold) with a tinted background and coloured text:
  - `nova`: success green
  - `že uvožena`: muted grey
  - `delno nova`: warning amber
  - `nerazvrščena`: warning amber, with a tooltip explaining why
  - `napaka`: danger red, with the reason in the tooltip

### Progress
- Thin bar, 4 px high, flush under the header while importing. Accent fill on a
  track of accent at 15 %.
- Footer text: `Uvažam 38 / 142 · 2000487-W2a-1.jpg`, muted, 12 px.

### Empty state (no card inserted)
- Centered in the table area: `images/weld-duotone.jpg` as a 220 px rounded
  square at 90 % opacity, then `VSTAVITE SD KARTICO` in Barlow Condensed muted
  text, then one line in Segoe UI:
  `Aplikacija jo prepozna samodejno.`
- Plus a secondary button, `Preglej znova`.

---

## 6. Icons

- Use **Phosphor Icons**, **Bold** weight. Kosovnice uses the same set
  (`@phosphor-icons/react`), so the icons match. For WPF, use the Phosphor icon
  font or its SVG paths.
- Icons in use: `SdCard` (card detected), `ArrowSquareIn` (import), `FolderOpen`,
  `ArrowClockwise` (rescan), `Sun` / `Moon` (theme), `CheckCircle`,
  `WarningCircle`, `XCircle`, `Trash` (clear card), `Camera`.

### App icon
- `icon/IMPWeldPhotos.ico`: multi-size, 16 to 256 px. Set it as the
  `ApplicationIcon` and the window icon.
- It follows the Kosovnice app tile style: a rounded square in the app's accent
  (Kosovnice's own tile is the white "i" on blue) with a bold white glyph. Here
  the glyph is a camera, with a weld spark.
- PNGs from 16 to 1024 px are in `icon/` for the installer, taskbar and store.

---

## 7. Motion

- Colour transitions 150 ms, dialogs fade and scale in from 96 % over 180 ms.
- No blur effects (a WPF `BlurEffect` on large areas costs every frame, which is
  what made the Kosovnice home screen stutter on tablets), no looping
  animations, no parallax.
- Respect Windows "Show animations" off: no transitions at all.

---

## 8. Dark mode

- Follow the Windows app theme (`AppsUseLightTheme` in
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`) on first run.
- Sun/moon toolbar button toggles it, like Kosovnice. Remember the choice.
- Swap `Theme.Light.xaml` ↔ `Theme.Dark.xaml` at runtime. Nothing else changes.

---

## 9. Photo stamp

The visual rules for the stamp are in `SPEC.md` §4 ("Stamping").
`samples/stamp-preview.jpg` shows the intended result on a real weld photo:
white text with a soft dark shadow, centered. The text centre is now at 86 % of the
height (the preview was rendered at 72 % before it was moved down).

---

## 10. Assets in this folder

| File | Use |
|---|---|
| `Theme.Light.xaml`, `Theme.Dark.xaml` | Colour, brush, font, radius resources |
| `logo/imp-logo.png` | IMP Promont logo, full colour (About dialog) |
| `logo/imp-logo-white.png` | Same logo in white, for the accent header |
| `icon/IMPWeldPhotos.ico` | App and window icon |
| `icon/icon-{16…1024}.png` | Same icon as PNGs |
| `patterns/header-grid.png` | 20 × 20 tile for the header grid pattern |
| `images/card-zvari.jpg` | Weld photo used on the Kosovnice Zvari card |
| `images/weld-duotone.jpg` | Same photo toned to the accent, for the empty state |
| `samples/stamp-preview.jpg` | Reference render of the photo stamp |
| `samples/header-preview.png` | Reference render of the header: accent, fading grid, white logo, title. Rendered with Segoe UI Bold because Barlow Condensed isn't installed on this machine; the real app uses the bundled Barlow Condensed |
| `make_assets.py` | Regenerates everything above from the Kosovnice sources |
