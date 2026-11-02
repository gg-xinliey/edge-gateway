---
version: 1
slug: "wwwroot-config-index-html"
primary_target: "wwwroot/config/index.html"
related_targets: ["wwwroot/config/app.css","wwwroot/config/app.js"]
---

# Config page (wwwroot/config)

Scope: the whole Edge Gateway config page at http://localhost:5100/config/. Mode: Operate.
Audience and job: SVI automation engineers on the gateway / Docker PC's desktop monitor: map tags to MES columns, watch machines and MES, fix refused records.
Constraints: plain HTML / CSS / JS, offline (no external fonts or CDNs), polls every 4 s; every existing function stays.
Approved: the user picked the clickable mockup .impeccable/mocks/pointmap-mockup.html (machines in a left sidebar, General pages in the top bar, profile column, loadout row, group panels) on 2026-11-02, after a reference image (dark navy game-stats dashboard).
Left out of the build: the mockup's "Compare with <machine>" switch. It was drawn only, with no agreed behaviour; put to the user on 2026-11-02 (build it, or drop it). Until they answer it stays out.
Loadout count rule (user-approved): a field counts as filled when a tag fills it, or when the gateway fills it and the rule's trigger tag is mapped; gateway-filled shown lighter.

## Direction contract

THESIS: a machine's mapping read like a character loadout: nine MES groups as equipped slots with fill badges, then dense stat-style panels; refuses the grey settings-form layout of the old WinForms look.
OWN-WORLD: dark navy ground (#131924) with lighter navy panels, small header strips, one colour per MES group (teal, gold, violet, green, blue, coral, pink, orange, steel), cyan for actions, gold for selection, right-aligned values, tabular numerals, Segoe UI.
STORY: the engineer sees at once which machine is connected, how complete its mapping is per group and what needs a tag, edits rows in place, and saves.
FIRST VIEWPORT: top bar (brand, General tabs, gateway status, problem badge); left machine list with dots and auto chips; content: Mapping | Outbox bar, view tabs (PointMap, TimeStamp, Unidentified), profile column (portrait, machine stats, twin tags) beside the loadout row, filters, group panels; footer note with change mapping and save.
FORM: pinned by the user's reference image and approved mockup (no roll). seed: none (unavailable)
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
