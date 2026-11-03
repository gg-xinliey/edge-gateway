---
name: Edge Gateway Config
description: A machine's tag mapping read like a character loadout, on a dark navy operator console.
colors:
  ground-navy: "#131924"
  bar-navy: "#1a2230"
  panel-navy: "#1e2838"
  panel-head-navy: "#243044"
  inset-navy: "#161e2b"
  line-slate: "#2d394d"
  line-soft: "rgba(45, 57, 77, .6)"
  line-hover: "#3c4b63"
  quiet-fill: "#2c3a50"
  track-slate: "#2a3546"
  text-frost: "#e4e9f1"
  text-muted: "#a3aec0"
  text-faint: "#8a95a9"
  action-cyan: "#3db4d6"
  action-cyan-hover: "#57c4e2"
  action-ink: "#062330"
  switch-on-teal: "#1f6f88"
  select-gold: "#e2c26c"
  ok-green: "#5cc97b"
  ok-text: "#bfeccc"
  filter-on-green: "#2b6a3f"
  warn-amber: "#e9a23b"
  warn-text: "#f3d3a0"
  warn-ink: "#1f1606"
  bad-coral: "#f47c6e"
  bad-fill: "#c4423a"
  bad-text: "#f5b0a6"
  group-machine-status: "#4fd1c5"
  group-production: "#e8b04a"
  group-process: "#a78bfa"
  group-quality: "#7ccf6a"
  group-traceability: "#5aa9f0"
  group-alarm: "#f47c6e"
  group-operator: "#f08fc0"
  group-material: "#f0a35e"
  group-maintenance: "#9fb6cc"
  group-motion: "#7dd3fc"
  group-io: "#c4b5fd"
  group-other: "#a3aec0"
typography:
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "18px"
    fontWeight: 650
    lineHeight: 1.35
  dialog-title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 700
    lineHeight: 1.35
  navigation:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.35
  status:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.35
    fontFeature: "tnum"
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.35
    fontFeature: "tnum"
  body-strong:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "13px"
    fontWeight: 600
    lineHeight: 1.35
  small:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.35
  section-label:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 600
    letterSpacing: "0.04em"
  label:
    fontFamily: "Segoe UI Variable Text, Segoe UI, system-ui, sans-serif"
    fontSize: "11px"
    fontWeight: 600
    lineHeight: "16px"
  mono:
    fontFamily: "Cascadia Mono, Consolas, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.5
rounded:
  xs: "4px"
  sm: "5px"
  md: "6px"
  lg: "8px"
  round: "50%"
spacing:
  2xs: "2px"
  xs: "4px"
  sm: "6px"
  md: "8px"
  lg: "10px"
  xl: "12px"
  2xl: "14px"
components:
  button-default:
    backgroundColor: "{colors.panel-head-navy}"
    textColor: "{colors.text-frost}"
    typography: "{typography.body-strong}"
    rounded: "{rounded.sm}"
    padding: "0 14px"
    height: "34px"
  button-primary:
    backgroundColor: "{colors.action-cyan}"
    textColor: "{colors.action-ink}"
    typography: "{typography.body-strong}"
    rounded: "{rounded.sm}"
    padding: "0 14px"
    height: "34px"
  button-primary-hover:
    backgroundColor: "{colors.action-cyan-hover}"
    textColor: "{colors.action-ink}"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.action-cyan}"
    rounded: "{rounded.sm}"
    padding: "0 14px"
    height: "34px"
  button-small:
    rounded: "{rounded.sm}"
    padding: "0 10px"
    height: "28px"
  button-footer:
    rounded: "{rounded.sm}"
    height: "40px"
    width: "132px"
  pill-auto:
    backgroundColor: "{colors.warn-amber}"
    textColor: "{colors.warn-ink}"
    typography: "{typography.label}"
    rounded: "{rounded.lg}"
    padding: "0 6px"
  pill-count:
    backgroundColor: "{colors.bad-fill}"
    textColor: "#ffffff"
    typography: "{typography.label}"
    rounded: "{rounded.lg}"
    padding: "0 6px"
  pill-quiet:
    backgroundColor: "{colors.quiet-fill}"
    textColor: "{colors.text-muted}"
    typography: "{typography.label}"
    rounded: "{rounded.lg}"
    padding: "0 6px"
  badge-rule:
    backgroundColor: "{colors.action-cyan}"
    textColor: "{colors.action-ink}"
    typography: "{typography.label}"
    rounded: "{rounded.xs}"
    padding: "1px 8px"
  badge-quiet:
    backgroundColor: "{colors.quiet-fill}"
    textColor: "{colors.text-muted}"
    typography: "{typography.label}"
    rounded: "{rounded.xs}"
    padding: "1px 8px"
  card:
    backgroundColor: "{colors.panel-navy}"
    textColor: "{colors.text-frost}"
    rounded: "{rounded.md}"
  card-head:
    backgroundColor: "{colors.panel-head-navy}"
    typography: "{typography.body-strong}"
    padding: "8px 12px"
  slot:
    backgroundColor: "{colors.panel-navy}"
    rounded: "{rounded.md}"
    padding: "10px 8px 8px"
  slot-tile:
    rounded: "{rounded.lg}"
    size: "40px"
  input-row:
    backgroundColor: "{colors.inset-navy}"
    rounded: "{rounded.xs}"
    padding: "0 8px"
    height: "28px"
  input-form:
    backgroundColor: "{colors.inset-navy}"
    textColor: "{colors.text-frost}"
    rounded: "{rounded.sm}"
    padding: "0 9px"
    height: "32px"
  search:
    backgroundColor: "{colors.inset-navy}"
    textColor: "{colors.text-faint}"
    rounded: "{rounded.sm}"
    padding: "0 8px"
    height: "32px"
  segment:
    backgroundColor: "{colors.bar-navy}"
    textColor: "{colors.text-muted}"
    padding: "0 12px"
    height: "30px"
  segment-on:
    backgroundColor: "{colors.filter-on-green}"
    textColor: "#eaf7ee"
  switch:
    backgroundColor: "{colors.quiet-fill}"
    rounded: "9px"
    width: "34px"
    height: "18px"
  switch-on:
    backgroundColor: "{colors.switch-on-teal}"
  nav-top:
    textColor: "{colors.text-muted}"
    padding: "0 14px"
    height: "50px"
  nav-top-active:
    backgroundColor: "{colors.panel-head-navy}"
    textColor: "{colors.text-frost}"
  machine-tab:
    textColor: "{colors.text-muted}"
    rounded: "{rounded.md}"
    padding: "0 6px 0 10px"
    height: "38px"
  machine-tab-active:
    backgroundColor: "{colors.panel-head-navy}"
    textColor: "{colors.select-gold}"
  chip:
    backgroundColor: "{colors.inset-navy}"
    textColor: "{colors.text-frost}"
    typography: "{typography.small}"
    rounded: "{rounded.xs}"
    padding: "2px 8px"
---

# Design System: Edge Gateway Config

## Overview

**Creative North Star: "The Loadout Console"**

The config page reads a machine's mapping the way a game reads a character: the nine MES data groups are equipped slots, each with its own colour, a fill count and a two-tone fill bar, and beneath them sit dense, stat-style panels where every row is a field and every value is right-aligned. The ground is dark navy, the panels one step lighter, the headers one step lighter again. Depth is tonal, not shadowed.

The density is that of an operator console for engineers on a full HD or wider monitor: 13px body text with tabular numerals, rows 28 to 34px tall, gutters of 10 to 12px. Colour has three jobs and never mixes them: group identity (twelve hues, one per MES group), action (cyan), and selection (gold). Status (green, amber, coral) speaks in soft tinted fills with a coloured border and a light text tone, never a loud solid block, except in the small count and auto pills.

The world refuses the grey settings-form look of the old WinForms UI it replaced. Everything is offline: system fonts (Segoe UI), an inline SVG sprite for icons, no CDNs.

**Key Characteristics:**
- Dark navy tonal layering: ground, bar, panel, panel header, inset field.
- One colour per MES group, carried from slot to panel to value.
- Cyan acts, gold selects, amber marks what the gateway did by itself or what is unsaved.
- Right-aligned values in tabular numerals; dense zebra rows.
- Stroke icons from one inline sprite (16px, 1.6 stroke, round caps).
- Flat surfaces; shadows only on floating layers.

## Colors

A dark navy ground with a cyan action voice, a gold selection voice, and a twelve-hue group palette that does the identifying.

### Primary
- **Action Cyan** (action-cyan): primary buttons, ghost button text and border, focus rings, the active view tab's underline, the search field's focus border, link buttons, histogram bars, the Span rule badge, the resize grip while dragged. Hover lifts to Action Cyan Hover; text on cyan is Action Ink.

### Secondary
- **Selection Gold** (select-gold): what is selected right now: the active top-bar page's underline, the selected machine's name, the selected loadout slot's border, the active Mapping / Outbox split, the flash ring when a slot jumps to its panel, the machine list's title icon.

### Tertiary
- **The Group Palette** (group-machine-status through group-other): one hue per MES group: Machine Status teal, Production gold, Process violet, Quality green, Traceability blue, Alarm coral, Operator pink, Material orange, Maintenance steel, Motion sky, IO lavender, other muted grey. Set per element as a local colour variable from the group name, then used for the slot tile, panel icon, header tint, group name, row mark, mapped value text and the group tag in tables.

### Neutral
- **Ground Navy** (ground-navy): page background; also the scrollbar thumb's border.
- **Bar Navy** (bar-navy): top bar, machine list, footer, view-tab strip, unselected segments.
- **Panel Navy** (panel-navy): cards, slots, stats, tables, log, dialogs.
- **Panel Head Navy** (panel-head-navy): card headers, table headers, default buttons, the active machine and active top-bar page.
- **Inset Navy** (inset-navy): every input, search field, chip and code block sits one step darker than its panel.
- **Slate Line** (line-slate) and **Soft Line** (line-soft): 1px borders and dividers; soft line between form rows and table rows. **Hover Line** (line-hover) is the border a default button or slot gains on hover.
- **Quiet Fill** (quiet-fill): quiet badges and pills, the off switch, empty slot count.
- **Track Slate** (track-slate): the empty part of fill bars, score bars and zero histogram bars.
- **Frost** (text-frost), **Muted** (text-muted), **Faint** (text-faint): primary text, labels and inactive navigation, placeholders and timestamps.

### Status
- **OK Green** (ok-green / ok-text): connected dots (with a 3px soft ring), the running gateway, the green status bar, the OK button.
- **Warn Amber** (warn-amber / warn-text / warn-ink): auto pills, the dirty dot, idle and restarting dots, warning alerts and stats.
- **Bad Coral** (bad-coral / bad-fill / bad-text): errors, flagged rows, the problem button, count pills (solid bad-fill), the close-hover tint. Group Alarm shares this coral on purpose.

### Named Rules
**The One Colour Per Group Rule.** A group's hue appears everywhere that group appears: slot tile (14% fill, 40% border), panel header (12% mixed into the header navy), header icon, group name, row mark, mapped value text, group tag in tables. Group hues never mean anything else, and no two groups share one.

**The Two-Tone Fill Rule.** Solid group colour means a tag fills it; the same colour at 45% means the gateway fills it. The rule holds on the slot's fill bar and on each row's square mark. An empty field is an outlined faint mark.

**The Three Voices Rule.** Cyan acts, gold selects, amber flags the automatic and the unsaved. A new control takes the voice of its job, never a group hue.

**The Soft Status Rule.** Status surfaces are a 6 to 10% tint of the status colour with a 40 to 50% border and the light text tone. Solid status fills are kept for pills of 16px height and dots.

## Typography

**Body Font:** Segoe UI Variable Text (with Segoe UI, system-ui, sans-serif)
**Mono Font:** Cascadia Mono (with Consolas, monospace)

**Character:** one system sans at console density, carried by weight (400 / 500 / 600 / 650 / 700) rather than size; tabular numerals everywhere so counts and rates line up. Mono is for codes, MES column keys, log lines, JSON and raw previews.

### Hierarchy
- **Title** (650, 18px): the machine's name in the portrait card; big stat values.
- **Dialog title** (700, 16px): dialog headings only.
- **Navigation** (400, 15px, 18px icons; brand 16px with a 20px icon): top bar pages, machine list, Mapping / Outbox split, view tabs. A size up from the panels so the way around reads first.
- **Status** (400, 14px, tabular numerals): the gateway status and stats in the top bar.
- **Body** (400, 13px, 1.35, tabular numerals): rows, inputs, buttons (600).
- **Small** (400 to 600, 12px): help text, notes, table headers, alerts in the list, sub-headings.
- **Section label** (600, 12px, 0.04em, uppercase): the machine list's title only, with its count in normal case beside it.
- **Label** (600, 11px, 16px line): pills, badges, slot counts and slot names.
- **Mono** (12px, 1.5): log, JSON, error text, column codes under form labels.

### Named Rules
**The Right-Aligned Value Rule.** Mapped values sit right-aligned, in the group's colour, at 600. When a field takes focus it turns left-aligned for editing and shows the cyan border.

**The No Web Fonts Rule.** The page must work on a gateway PC without internet: system and installed fonts only.

## Layout

Full-height app grid: a 50px top bar, then a window of three columns: machine list (260px by default, resizable through a 6px grip, keyboard arrows and double-click to reset), the grip, and the work area. The machine list is a column with a pinned title and search at the top, a scrolling list, and a pinned footer (Add a machine, list alerts).

The work area stacks the Mapping / Outbox split, the view-tab strip, alerts, the work grid and a footer. On a machine's PointMap the work grid holds a 250px profile column beside the content. The content opens with a toolbar (segmented filter, actions, search pushed right), the loadout row (slots in an auto-fit grid, 84px minimum, 8px gap), and group panels in CSS columns (up to 3, 380px minimum, 10px gap). Forms use 2 columns of 420px minimum.

Spacing rhythm: 8 / 10 / 12px for gutters and panel padding, 4 to 6px inside rows, 2px between list items and top-bar tabs. Rows alternate with a 1.8% white zebra.

Responsive:
- **Container, 560px:** form and key-value rows collapse to one column; message results stack.
- **1100px:** the profile column moves above the content as an auto-fit grid of 220px cards.
- **760px (phone):** the page scrolls instead of the app grid; the top bar wraps (brand, General pages, then status full width); the grip hides and the machine list stacks above the work area, capped at 340px; search goes full width; footer buttons share the row equally; the toast spans the screen.

## Elevation & Depth

Flat at rest. Depth comes from tonal steps of navy (ground, bar, panel, panel head; inset fields go darker), outlined by 1px slate lines. Shadows exist only for things that float above the page or for a brief signal.

### Shadow Vocabulary
- **Dialog** (`box-shadow: 0 12px 40px rgba(0, 0, 0, .45)`): modal dialogs, over a 60% navy-black backdrop.
- **Busy / toast / suggestion list** (`box-shadow: 0 8px 30px rgba(0, 0, 0, .4)` and `0 8px 24px rgba(0, 0, 0, .4)`): the busy box, toasts, and the suggestion list under a text box (`.combo-list`: panel-head ground, rows in the group colour, hint on the right).
- **Live ring** (`box-shadow: 0 0 0 3px rgba(92, 201, 123, .15)`): a connected dot and the running gateway dot.
- **Inset selection** (`box-shadow: inset 0 0 0 1px` gold): the selected slot; `inset 0 -2px 0` cyan underlines the active view tab.
- **Flash** (`0 0 0 2px` gold fading out over 1.2s): a group panel when its slot is clicked.

### Named Rules
**The Float-Only Shadow Rule.** Cards, slots, panels and buttons never cast a shadow. Only dialogs, the busy box, toasts and the suggestion list float.

## Shapes

Small, even corners: 4px for inline things (row inputs, chips, badges, row buttons), 5px for buttons, search and segmented controls, 6px for cards, slots, tabs, alerts and tables, 8px for slot tiles, pills and dialogs. Pills and the switch are fully rounded; dots are circles; row marks are 8px squares with 2px corners. The split and segmented controls clip their children in one outline. Dashed borders mean absence or a parked state: an empty slot's tile, and the remembered machine while a General page is open.

## Components

### Buttons
Compact and solid; the label carries the weight (600).
- **Shape:** gently rounded (5px), 34px tall, 14px side padding, 8px gap to an icon.
- **Primary:** Action Cyan with Action Ink text; hover lifts to Action Cyan Hover.
- **Default:** Panel Head Navy with Frost text; hover adds the Hover Line border.
- **Ghost:** transparent, cyan text, a dark cyan border (#2c6c82); hover adds an 8% cyan wash.
- **Warn outline:** transparent with warn text and a 60% amber border, for undoing an automatic decision.
- **Small:** 28px tall, 12px text. **Footer:** 40px tall, at least 132px wide.
- **Focus:** 2px cyan outline, 2px offset. **Disabled:** 45% opacity.
- **Link button:** cyan underlined text, 3px underline offset.

### Pills, Badges and Dots
- **Pills** (16px tall, fully rounded, 11px / 600): auto (amber), count (solid coral), quiet, ok (dark green with ok text).
- **Time rule strip** under each MES group's header: a 5% group-colour band, clock icon, a mode chip (Span / Single point, 22% group colour), the trigger or time field in bold, "sent every …", and a cyan Edit that opens the rule's rows in place (dashed top line). No rule: "No time rule: each record gets record_timestamp" with an Add link. Rows the rule fills say "set by the time rule" with a clock; the trigger row has a "starts / ends the span" chip; a Span group's slot has a small clock top right. MQTT-only groups keep a quiet "MQTT · live only" badge.
- **Dots** (8px): connected green with its live ring, idle amber, disconnected slate, disconnected-with-problem coral. Every dot carries an accessible label and title.
- **Dirty dot** (7px amber circle): beside a top-bar page with unsaved changes.

### Chips
- **Style:** inset navy, 1px slate border, 4px corners, 12px text. Clickable chips take a cyan border on hover. Used for twin (MQTT) tag lists.

### Cards / Containers
- **Corner Style:** 6px.
- **Background:** Panel Navy with a 1px slate border; the header strip is Panel Head Navy with a bottom line, 8px 12px padding, 600 weight, and a badge pushed right.
- **Shadow Strategy:** none (see Elevation).
- **Internal Padding:** 12px horizontally; rows 4 to 5px vertically.
- **Portrait plate:** 120px, a soft radial navy gradient, a 60px stroke icon, and a state badge top-right.
- **Status bar:** soft green, coral or quiet strip above a form.

### Inputs / Fields
- **Style:** Inset Navy with a slate border, 32px tall, 5px corners in forms; in mapping rows the border is transparent until hover (28px, 4px corners) and the value is right-aligned in the group colour. Tag lists use an auto-sizing textarea so the whole list wraps into view.
- **Focus:** the border turns cyan; mapping-row values turn left-aligned.
- **Error:** a flagged row gets a 1px coral outline, a 6% coral wash and a coral note below.
- **Search:** inset field with a faint search icon, 32px, 260px in toolbars; the cyan border shows on focus-within.

### Segmented Control
- One outline, 5px corners, 30px segments on Bar Navy with 1px dividers. The on segment is filled deep green (filter-on-green) with near-white text. Used for show filters (All fields / Filled / Empty / Extras; All / Problems).

### Switch
- 34 by 18px track, 14px knob. Off: Quiet Fill with a muted knob. On: dark teal (switch-on-teal) with a pale cyan knob that slides 0.15s on the expo-out curve. Exposed as a button with aria-checked.

### Navigation
- **Top bar:** brand (cyan icon, 650 weight) behind a divider; General pages as 50px tabs, muted, Frost on hover with a 3% white wash; the active page sits on Panel Head Navy with a 2px gold underline inset 10px each side. Gateway status is pushed right: a status dot and state text, then MQTT / MES / backlog stats behind a divider (bold values, green or coral where it matters), then the problem button (coral outline, 6% coral wash).
- **Machine list:** 38px rows with a dot, name, auto pill, unidentified count and a close button that appears on hover or selection. The active machine sits on Panel Head Navy with a slate border and a gold name; while a General page is open it is parked: transparent with a dashed border. The Template row is pinned first with a copy icon and a bottom line. Broken machines show a coral name and info icon.
- **Mapping / Outbox split:** two equal halves, 42px; the on half is Panel Head Navy with gold text.
- **View tabs:** a 40px strip under the split; the active tab gets Panel Navy and a 2px cyan inset underline.
- **Mobile:** see Layout, 760px.

### Loadout Slot (signature)
A button per MES group in an auto-fit row. A 40px tile with the group icon in the group colour on a 14% wash with a 40% border; a count badge (filled / total, solid group colour, dark text) at top-left; the short group name in 11px muted; a 3px two-tone fill bar (solid for tags, 45% for gateway-filled, Track Slate for the rest). Empty: dashed faint tile and a quiet count. Hover: Hover Line border and a lifted navy. Selected: a gold border plus gold inset ring. Clicking selects the group and flashes its panel gold.

### Group Panel (signature)
A card with its header tinted 12% by the group colour, a group icon and name in the group colour, the fill count in muted 12px, and below it the time rule strip. Rows are a four-column grid: the 8px two-tone mark, the MES field name (with a cyan "set by the gateway" note where it applies), the value input, and a 26px remove button that tints coral on hover. Below, a sub-header introduces extras not sent to MES, and a footer holds the add action.

### Log
A mono panel with three-column lines (time, module, message), zebra rows, and error lines tinted coral with a coral inset outline.

## Do's and Don'ts

### Do:
- **Do** set a group's colour once on its element and derive every tint from it (14% tile wash, 40% tile border, 12% header mix, 45% gateway fill).
- **Do** keep values right-aligned with tabular numerals, and flip to left-aligned only while editing.
- **Do** step depth through the navy tones (ground, bar, panel, panel head, inset) and 1px slate lines.
- **Do** mark everything the gateway decided by itself in amber (auto pill, auto mark) and keep it confirmable.
- **Do** draw every icon from the inline SVG sprite: 16px, 1.6 stroke, round caps and joins, current colour.
- **Do** give every interactive element the 2px cyan focus outline with 2px offset.
- **Do** honour reduced motion: the panel flash, the toast slide and the switch knob stop moving under prefers-reduced-motion.

### Don't:
- **Don't** use a group hue for actions, selection or status; cyan, gold and the status colours own those jobs.
- **Don't** cast shadows from cards, slots or buttons; only dialogs, the busy box, toasts and the suggestion list float.
- **Don't** load web fonts, icon fonts or anything from a CDN; the page runs offline.
- **Don't** use text glyphs (arrows, warning signs, triangles) as icons; use sprite symbols.
- **Don't** put a small uppercase label above a heading; the machine list's title is the one uppercase label, and it is the heading itself.
- **Don't** shout status with solid blocks on large surfaces; use the soft tint, border and text tone.
