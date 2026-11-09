# Edge Gateway: short guidebook

One block per thing. **See** = where on the config page. **Cleared** = when it goes away. **Stores** = what is in it.
Paths are inside the data folder (`data\` in Docker, `--data` otherwise).

---

## 1. Things kept on disk

### LOG
- **See:** Log tab (last 500 lines since the gateway started). The red "N new problems" pill counts lines with words like *failed, error, refused, warning*.
- **Cleared:** day files deleted after **14 days**.
- **Stores:** every gateway line with its time → `logs\gateway-<date>.log`.

### MESSAGES RECEIVED (traffic)
- **See:** Machines → Messages received (search by day, from–to time, machine).
- **Cleared:** day files deleted after **7 days** (checked every hour).
- **Stores:** time + machine of every line that arrived (MES, IO, motion, repeats, broken lines). **No content.** → `traffic\traffic-<date>.db`.

### RECEIVED (the lines themselves)
- **See:** a machine → Received (next to Sent to MES). Pick a day, **find in lines** (e.g. a `message_id`), click a row for the whole line. Template tab: machines without a tab, lines with no `machine_id` or unreadable.
- **Cleared:** day files deleted after **7 days** (`outboxKeepSentDays`), checked every hour.
- **Stores:** every line **except IO / motion**, whole and exactly as it arrived, with time and sender IP:port; repeats and broken lines too → `received\<machine>\received-<date>.db` (no tab: `received\received-<date>.db`).
- **Note:** written in the background, so it never slows an ack; a crash can lose the last moment of lines.

### OUTBOX (records for MES)
- **See:** a machine → Sent to MES (pick a day, click a row for the whole record).
- **Cleared:** a day file is deleted **7 days** after its day (`outboxKeepSentDays`), and only once every row in it was delivered or discarded. **Unsent or rejected rows are never deleted.**
- **Stores:** each MES record (envelope + `event_id`), sent or waiting, MES's answer when rejected, discard reason → `outbox\<machine>\outbox-<date>.db`.

### MES FAILURES (rejected records)
- **See:** MES failures tab (top bar badge).
- **Cleared:** only by you: **Re-send** or **Discard** (a reason is required and logged).
- **Stores:** the record + what MES answered. These are outbox rows, so they live in the same day file.

### OPEN SPANS
- **See:** not shown.
- **Cleared:** when the span closes (its value changes).
- **Stores:** the start time of a span that is still open (for example a cycle in progress), so a restart mid-cycle keeps it → `outbox\<machine>\open-spans.json`.

### MESSAGE IDS (duplicate check)
- **See:** Repeats (10 min) on the machine card.
- **Cleared:** each id is forgotten after **10 minutes**.
- **Stores:** recent `message_id`s. Saved to `state\message-ids.txt` **only on a clean stop**; a hard kill loses them.

### CONFIG
- **See:** every tab of the config page.
- **Cleared:** never. Each save keeps the previous file as `.bak` (one copy back).
- **Stores:** `Config\machine-config.json` (gateway, MES addresses, logins), `mqtt-config.json`, `mes-schema.json` (MES columns), `point-map.json` (Template), `timestamp-rules.json`, `machines\<id>\` (each machine's own mapping).

### CLOSED MACHINE TABS
- **See:** not shown.
- **Cleared:** never (delete by hand).
- **Stores:** the closed tab's files → `Config\machines-removed\<id>-<date-time>\`. Its outbox is still delivered.

---

## 2. Things kept in memory only (reset when the gateway exe closes; a restart from the page keeps them)

| Thing | See | Limit / window | Stores |
|---|---|---|---|
| Machines & Connections | Machines tab, sidebar dots | since the exe started | lines, rejected, repeats, unreadable, last line, msg/s per connection and machine |
| Repeats / Time warnings | machine card | last **10 min** | count of repeated `message_id`s / machine times more than **1 h** off |
| Held messages | Log → **Held** | max **5,000**; each leaves once it goes through (or is rejected) | every message held right now: machine, data source, message_id, **why**, first held, last sent, times sent |
| Held machines | Machines tab (Held), pop-up | max **200** machines, **300** tags each | machines sending with no tab + the tags they sent |
| Unidentified tags | machine → Unidentified | max **500** tags | tags not in the mapping, with their last value |
| MQTT twin (IO / motion) | MQTT broker tab, banner | **not kept** at all | nothing; sent live or lost |

---

## 3. Features (how it behaves)

### ACK (answer to the machine)
- `{"ack":id}` only after the record is **on disk**. `{"nack":id,"reason":...}` = never accepted. No answer = held; the adapter sends it again later.

### DUPLICATES
- Same **machine_id + data_source + message_id** within 10 min = ignored. Also blocked by the outbox (`event_id`, today + yesterday) and by MES (unique `event_id`). No `message_id` = no check.

### CHANGE-ONLY
- A message that changes no MES value makes **no record** (answered at once).

### PACING (`sendEveryMs`)
- Records are saved at once, but each data group is sent to MES at most once per `sendEveryMs` (`timestamp-rules.json`; PRODUCTION up to 10 s).

### MES: ONE ADDRESS PER DATA GROUP
- Gateway settings → MES: each group (Machine, Alarm, ...) can have its own address and login (none / token / user+password / API key). Empty = uses Default. No address at all = records wait in the outbox.
- **Test** button: a GET only, 10 s timeout, sends no record.
- https with a company CA: `mesCaFile`.

### MES SENDING & RETRY
- Up to **16** groups at the same time, each strictly in order.
- Down / timeout (**15 s**) / 5xx / 401 403 404 405 407 408 413 429 → wait and retry: **2 s, 4 s, 8 s ... max 60 s**.
- Other 4xx → moved to **MES failures** (not retried), the next rows go on.
- Same row gets 500 or 413 **5 times** (`mesStuckRowTries`) and MES took others meanwhile, or **10 min** passed (`mesStuckRowMinutes`) → moved to MES failures.

### MACHINE WITHOUT A TAB (`unregisteredMachines`)
- `hold` (default): not processed, not answered, shown on the page. The adapter keeps the data.
- **Auto tab**: a new machine gets a tab copied from a **similar machine** (same name before the first `-`, or the same name with its numbers left out), else from the Template. Max **20 per hour**, then held + alert. An auto tab nobody confirms counts as confirmed after **3 days**.
- `shared`: uses the Template mapping.

### UNKNOWN TAGS (`unmappedTags`)
- `send` (default): known tags go to MES; unknown ones are listed under Unidentified.
- `hold`: the whole message waits until every tag is mapped or ignored.
- **Name matching**: a tag that matches one MES column with score ≥ **0.9** is mapped automatically and marked "auto".

### POINTMAP TAG RULES
- `a, b` = either one (the later one in the message wins).
- `a+b` = joined with `_`, only when **every** part is present; otherwise the field keeps its last value.

### TIME
- Top-level `timestamp` = when it happened (else the arrival time). No zone = **UTC**.
- Machine time more than **1 h** from arrival → time warning.
- **SinglePoint** = one time field. **Span** = start/end, closed when the trigger value changes.
- **Cycle time**: the machine's own `cycle_time` wins, else start+end in one message, else how long the `cycle_active` span was on.

### MQTT TWIN
- IO and motion go **only** to MQTT, **never** to MES.
- Motion is sent when it moves ≥ **0.01**, IO when it changes; max one per **50 ms** per point.

### ALARM WEBHOOK (`alarmWebhookUrl`)
- Posts JSON to Teams / Power Automate / Slack when a problem starts or clears: MES down **5 min** with records waiting, backlog over **10,000**, MES failures, held machines, auto tabs, time warnings. Checked every **10 s**. Empty = off.

### RENAME (display name)
- A confirmed machine's card → **Rename**: the name shown in the machine list only (e.g. the customer first, for sorting; NXT2 sorts before NXT10). The card shows the real `machine_id` under it.
- Changes **nothing else**: data, MES, folders, Sent to MES / Received / Log and the similar-machines rule keep the `machine_id`.
- Auto tabs: confirm first. Unique, max 64 characters, empty = back to the `machine_id`. Saved in `Config\machines\<id>\display-name.json`.

### MAPPING PASSWORD (`mappingPassword`)
- Needed to change a **confirmed** tab's mapping. Empty = not asked.

### GATEWAY SELF-RESTART
- If it stops on an error, it starts again after **5, 10, 20, 40 s, then every 60 s**.

---

## 4. When something goes wrong

| Problem | Gateway does | You do |
|---|---|---|
| MES down / unreachable | keeps records, retries (up to every 60 s) | nothing; they go when MES is back |
| MES rejects a record | moves it to MES failures, alert | read MES's answer, fix, **Re-send** or **Discard** |
| Gateway crash / power off | records already on disk are safe; unanswered lines are resent by the adapter | start it again |
| MQTT broker down | yellow banner; IO/motion **lost** (not kept) | check the broker address / the broker PC |
| New machine, no tab | holds it (or makes an auto tab) | check and **confirm** the tab |
| Unknown tags | lists them under Unidentified (in `hold` mode: holds the message, see Log → Held) | map each one or set it to ignore, Save (or Change mapping): the held messages are sent again |
| Machine shows "not connected" | nothing to fix on the gateway side | the machine/adapter must reconnect to port **9000** |
| Same message sent twice | ignored (10 min window + `event_id`) | nothing |
| Time not readable (e.g. `20261106021217`) | passes it on as text | ask the data team for ISO time (`2026-11-06T02:12:17Z`) |
| Saved but nothing changed | MQTT and gateway settings need a restart | press **Restart now** |

---

## 5. Known problems (open)

- **+ Add a machine** and **Change mapping** use a browser pop-up: blocked in some browsers, so nothing happens.
- Problems in the Log are raw technical lines; nothing says if production is affected or who to call.
- Clicking **N new problems** marks them all as seen; repeats of one error raise the count.
- The top bar has no MES status (only the Log shows MES down); it is cut off below ~1100 px wide.
- No daily "sent to MES" total across all machines.
- Real MES details are still unknown (exact URLs, reply codes, login, size limits; SVI-53). 413 is retried until then.
- Check #4 "MES 409" sometimes fails when all checks run together (timing only).
- The `gg-xinliey/edge-gateway` GitHub copy is **public**.
