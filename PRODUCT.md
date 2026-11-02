# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Automation engineers at SVI (the gateway's own team). They work on the gateway PC or the Docker PC's own screen, at a desktop monitor (full HD or wider), to connect machines, map incoming tags to MES columns, watch machine and MES status, and fix records MES refused. Phones and tablets are rare.

## Product Purpose

The Edge Gateway takes machine data over TCP (JSON or CSV lines), maps it to the MES format (nine data groups), stores it safely in an outbox until MES confirms it, and publishes real-time IO / motion data on MQTT (optionally TLS). The config page at http://localhost:5100/config/ is where engineers set it up and watch it. Success: a new machine is sending correct MES records within minutes, and problems are seen before MES or the line notices.

## Positioning

One gateway for every machine: a new machine needs a mapping tab, not code. Tabs are created automatically for new machines, unknown tags are mapped by name matching, by mappings other tabs already have, and by learned synonyms, and every record is kept on disk until MES confirms it.

## Operating Context

- The page runs on the gateway PC itself (Windows) or a Docker PC; it is only reachable on that PC (localhost).
- Columns: Machines (tabs, with connection dots and "auto" chips) on the left; the selected tab's views (PointMap, TimeStamp, Outbox); the working view in the middle; General (MQTTConfig, GatewayConfig, Machines, MES failures, Log, gateway status) on the right. Columns are resizable.
- Typical jobs: map Unidentified tags (with suggestions), confirm or cancel automatic tabs, change a tab's mapping, test the MQTT connection, re-send or discard MES failures, read the log, search the message log.
- Saving a mapping asks: this machine, similar machines, or all.

## Capabilities and Constraints

- Plain static HTML / CSS / JavaScript in wwwroot/config (index.html, app.css, app.js), served by the gateway; no build step, no framework, no external fonts or CDNs (the gateway PC may have no internet).
- The page polls the gateway every few seconds; the machine list redraws.
- Terminology to keep: PointMap, TimeStamp, Outbox, Unidentified, MES failures, data group, tag, MES field / column, auto, confirm.

## Brand Commitments

None required (no SVI logo or colors). The user supplied a reference image (a dark navy, dense game-stats dashboard) as the visual direction for a redesign.

## Evidence on Hand

Real data on the local test gateway: machine tabs (LDM_Robot2, Auto_Screw_Contactor, ROBOT-01/02, OVEN-01, GLUE-01 and others), live fake machines, the MES format in Config/mes-schema.json.

## Product Principles

- Never lose a record: whatever the page does, data waits safely until MES has it.
- Show the problem where the engineer looks, with the reason in plain words.
- Automatic is fine, silent is not: anything the gateway decides by itself is marked and can be confirmed or undone.
- One place per job: each task has one obvious screen and one obvious button.
