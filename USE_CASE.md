# Edge Gateway (TCP) — Use Case Report

## 1. What it does

The gateway is a shared server. Many machines (sized for 50, tested at 5,000 messages/s) each keep a TCP connection to it and send one JSON line per message.
- **IO and motion lines** go to MQTT (the digital twin), in SpotWelding format.
- **Everything else** goes to MES through a local outbox, so nothing is lost if MES is down. MES data is sent per data group on an interval, and only when something changed.
- Every MES message must say which machine it came from (`machine_id`) and carry its own id (`message_id`). The same `message_id` again from the same machine and `data_source` within 10 minutes is ignored.
- What goes to MES follows the MES data dictionary: every column of the group's MES table is in the payload, `null` when the machine didn't send it.

## 2. How to use

1. Start `bin\Release\net8.0\EdgeGateway.exe`. The console shows:
   `Configuration UI: http://localhost:5100/config/`
2. Point every machine at **this server, TCP port 9000**, one JSON message per line.
3. Check that it works:
   - The console shows `[gateway] running: ...`, then one `[stats]` line every 10 s while data flows:
     `[stats] in 5003/s · mqtt 2001/s · mes queued 3337/s, delivered 2600/s · backlog 25,014`
   - MQTT Explorer (connected to the broker in `mqtt-config.json`) shows your topics.
   - The bottom left of the config page shows a green dot with **Gateway running**, and the same rates.

To test without the real broker, use `tools\LocalMqttTest`. It has its own broker, and the gateway copy uses TCP port 19000 and page port 15100.
- `dotnet run -- --load 5000 60 50` simulates 50 machines at 5,000 messages/s for 60 s, with a fake MES. Results are saved in `load-results\`.

## 3. Input → output

**IO** (machine → gateway):
```
{"topic":"svi/TEST/ASSY-L01-FCT-01/IO/input","payload":"{\"timestamp\":\"2026-10-09T13:42:52.635\",\"totalbits\":96,\"value\":\"E77B300F8F0DF02A671D5819\"}"}
```
Gateway → MQTT, topic `svi/TEST/ASSY-L01-FCT-01/IO/input`:
```
{"timestamp":"2026-10-09 13:42:52.635","totalbits":96,"value":"E77B300F8F0DF02A671D5819"}
```

**Motion**: if `world`, `joint` or `pulse` arrive as JSON text, they are published as real objects:
```
{"name":"XY_axis","timestamp":"2026-10-12 09:56:55.620","world":{"X":9.3,"Y":0,...},"joint":{"J1":0,...},"pulse":{"X":9.3,"Y":0,...}}
```

**Rules for MQTT lines**
- The topic can start with anything, but it must **end** in one of:
  `IO/input` · `IO/output` · `motion/position` · `motion/status`
- The message is published on **the same topic it arrived with**.
- Only the inner `payload` is published. `timestamp` becomes `yyyy-MM-dd HH:mm:ss.fff`, in local time.

**Heartbeat** (the gateway's own, not a machine's): `svi/<area>/<gatewayId>/keepalive`, e.g. `svi/TEST/TEST/keepalive` = `{"alive":true}`, every 30 s.

**MES line** (anything without an IO/motion topic), for example:
```
{"message_id":"3ef0d28b-200d-5d6d-826a-594d891eaa2a","machine_id":"NXT102","timestamp":"2026-10-20T08:10:32.000+00:00","data_source":"MCSTATECHANGE",
 "data":{"event_name":"MCSTATECHANGE","event_time":"20261020081032","line_name":"LINE01","sequence_id":"38","module_no":"1","previous_status_code":"4","current_status_code":"5","source_ref":"326"}}
```
- The message details (`message_id`, `machine_id`, `timestamp`, `data_source`) are at the top; the data tags are inside `"data"` (an object, or JSON text holding one). The older flat form, with the tags next to the details and no `"data"`, still works.
- The whole message must be **one line of valid JSON**. A line that isn't (e.g. one `}` too many) is skipped as unreadable. If its `message_id` can still be read, it gets `{"nack":"<message_id>","reason":"not valid JSON"}`; otherwise no reply.
- `message_id` is required, and is checked **first**: the same `message_id` again from the same machine and `data_source` within 10 minutes means the same line came twice (e.g. resent after a reconnect), so it is ignored. Any text works; a GUID is best. Two machines (or two sources) using the same `message_id` are two messages: both are saved. `machine_id` and `data_source` count in any case (`spi` = `SPI`). The gateway remembers the ids over a clean restart (Stop Gateway.bat / Ctrl+C), not after a crash or a closed window. IO/motion lines don't need one, but one that has a `message_id` is checked the same way.
- `machine_id` is required. `timestamp` is optional: it is when the event happened on the machine, and the gateway uses it for MES times (otherwise the arrival time is used). A timestamp with `Z` or an offset (`+07:00`) is taken as written. **Without one it is UTC** (setting `timestampsWithoutZone` in machine-config.json: `UTC` or `local`). A `timestamp` that can't be read is replaced by the arrival time. One more than 1 hour from its arrival is used as sent. Both are logged (once a minute per machine) and counted under **time warnings** on the Machines page.
- Each `data_source` is kept apart: the same data group from two data sources (e.g. `mcstatechange` and `mcstartstate`, both with traceability tags) gives **two separate envelopes**, even when they arrive at the same time. Changes, intervals and Span times are tracked per machine + data_source + group.
- NXT alarms come as two event names, `MCALARMON` and `MCALARMOFF`, with no status tag. When the machine's tab maps `alarm_status`, both become data_source `MCALARM` with `alarm_status` 1 (on) or 0 (off). Then the ALARM record of an alarm going off has the same `alarm_start_time` as its on record, plus `alarm_clear_time`. An off with no on before it gets neither time.
- **All MES times are sent in UTC with `Z`**, e.g. `2026-10-16T08:25:28.086Z` (`record_timestamp`, start/end times, `publish_timestamp`). MQTT keeps the SpotWelding local format `yyyy-MM-dd HH:mm:ss.fff`. `data_source` is optional: it goes into the envelope's `data_source` (otherwise `dataSource` from machine-config.json is used). None of these three is a data tag.
- Any other field is a data tag, mapped by the machine's tab (or set to ignore). An unknown tag never goes to MES (MES has no `UNIDENTIFIED` group): by default the rest of the message goes on without it; with `unmappedTags` = `hold` the whole message is held. See "A tag the mapping doesn't know" below.

**Reply to the adapter** (one JSON line back on the same TCP connection, per MES message):

| Reply | When | Adapter should |
|---|---|---|
| `{"ack":"<message_id>"}` | Only after the message is **saved to disk** (the outbox file, committed and flushed). Also sent at once when the message changed nothing (nothing new to save) | Forget the message |
| `{"ack":"<message_id>"}` for a repeat | The same `message_id` again: answered once the first copy is on disk (its first reply may have been lost) | Forget the message |
| `{"nack":"<message_id>","reason":"no machine_id"}` | It will never be accepted as sent | Stop resending; fix the data (it may then be sent with the same `message_id`) |
| `{"nack":"<message_id>","reason":"not valid JSON"}` | The line isn't valid JSON, but its `message_id` can be read | Stop resending; fix the line |
| no reply | No `message_id` (nothing to reply with), a broken line whose `message_id` can't be read, or an IO/motion line (live data, not saved) | — |

- **The adapter keeps every MES message until its reply arrives.** If the connection drops (gateway restart, network), it reconnects and sends every unanswered message again, with the same `message_id`.
- Replies come within milliseconds; `sendEveryMs` only delays the sending to MES, not the save.
- A message sent again that was already saved gets `ack` (as a repeat) and is not saved twice. After a gateway crash the gateway may not remember the id; then it is saved again with the **same `event_id`**, so MES (unique `event_id`) can drop it.
- Replies to a connection that is gone are dropped; the adapter's resend gets its reply.

**What MES receives** (one envelope per record):
```
{"event_id":"…","gateway_id":"TEST","machine_id":"NXT101","data_source":"PRODSTARTED","protocol":"TCP/JSON-CSV",
 "data_group":"MACHINE_STATUS","event_time":"2026-10-19T08:50:53.957Z","publish_timestamp":"2026-10-19T08:50:54.012Z",
 "data_quality_status":"VALID","schema_version":"1.0",
 "payload":{"machine_status":"RUN","previous_status":"IDLE","machine_mode":null,"status_start_time":"…Z","status_end_time":"…Z","communication_status":null}}
```
- The envelope has every `machine_events` column the gateway can know. MES fills `id` and `received_at` itself.
- `event_time` is the machine's `timestamp` (the arrival time when it sent none). `event_id` is made from the `machine_id`, `data_source` (both in upper case, as the line sent them), `message_id` and data group, so it is the same every time that message is sent or resent; MES can drop repeats by `event_id`.
- Numbers are sent as the machine wrote them; whole numbers above 2^53 (9,007,199,254,740,992) too, digit for digit.
- The payload has **every column of the group's MES table, in the table's order, `null` when not sent**. The columns per group are in `Config\mes-schema.json` (from the MES data dictionary). Edit that file when MES changes a table, then restart.
- Fields that have no MES column (extra fields, e.g. Nexim's `components`, `cycle_active`) are **not sent to MES**. To send one, add it to the group's MES format (**+ MES field**) once MES has the column.
- A tag only fills a MES column when its **fieldName in PointMap is the column name** (e.g. `work_order_no`, not `work_order`). Otherwise the column is `null` and the field is not sent.
- Put many tags in one message. One tag per line costs far more work.
- Each tag is mapped through `point-map.json` into its data group.

**When MES data is sent** (per machine and data group):
- A message that changes nothing is ignored. So is a message that only changes fields with no MES column (extras such as `components`). MES would get a row that looks the same as the last one.
- Each change becomes one record: all current values of that group, with timestamps. It is **saved to the outbox file right away**.
- `sendEveryMs` paces MES per group: the first change after a quiet interval is sent at once; changes within the next `sendEveryMs` wait until it is over, then **all** of them are sent, oldest first. Example with PRODUCTION every 10 s: a change at 10:00:00 goes at once; changes at 10:00:03 and 10:00:07 both go at 10:00:10.
- No change, nothing is sent: there are no periodic repeats.
- Current settings: MACHINE_STATUS every 100 ms, PRODUCTION every 10 s, other groups every 1 s (the default).

## 4. Wrong or different input

| You send | What happens | What you see |
|---|---|---|
| Different prefix, e.g. `plant2/line3/M01/IO/output` | Published as-is, on that topic | Topic appears in MQTT |
| Wrong case, e.g. `.../io/INPUT` | Published with the case fixed: `.../IO/input` | Topic with `IO/input` |
| Wrong ending, e.g. `.../IO/foo`, `.../motion/speed` | **Dropped.** Not sent to MQTT or MES | One console warning per topic: `[mqtt] dropped '...': topic must end in ...` |
| Correct topic, but `payload` missing or not JSON | **Dropped** | `[mqtt] dropped '...': "payload" is missing or not a JSON object` |
| Topic not ending in `IO/…` or `motion/…`, e.g. `M01/PRODUCTION/x` | Treated as MES data, not MQTT | Counted in `[stats]` as MES |
| Same `message_id` again (same machine and `data_source`) within 10 min | **Ignored**, before anything else is checked | Once a minute per sender: `[ingest] line from <ip:port> ignored: message_id … already received`; **repeats** on the Machines page |
| MES line from a `machine_id` without a tab | **Held** (with `unregisteredMachines` = `hold`): not processed, not answered; the adapter keeps it | Log line once a minute: `[ingest] NXT103 is not registered (no tab): its data cannot be used until it has a tab ...`; red "⚠ N not registered" on the tab bar; General → Machines → **register** |
| MES line without `message_id` | **Rejected** | Once a minute per sender: `[ingest] line from <ip:port> rejected: no message_id`; **rejected** on the Machines page |
| MES line without `machine_id` | **Rejected**, so data is never filed under the wrong machine | Once a minute per sender: `[ingest] line from <ip> rejected: no machine_id (+N more ...)`. Its `message_id` is forgotten, so the corrected line can be sent with the same id |
| CSV line `TAG,value` | **Rejected**: it can't carry a `machine_id` | Same as above |
| Same values as the last message | Nothing sent to MES (no change) | — |
| Tag the machine's mapping doesn't know | `unmappedTags` = `send` (default): the known tags go to MES, the unknown ones are left out until mapped. `hold`: the whole message is **held** (not answered, the adapter keeps it) until the tag is mapped or set to ignore | Log line once a minute: `[ingest] NXT102 sent tags its mapping doesn't know (source_ref): ...`; the tags are listed under the tab's ⚠ Unidentified (`hold`: also ⚠ on the tab and a red bar) |
| Line that is not valid JSON | Skipped. `nack` with reason `not valid JSON` if its `message_id` can be read | Once a minute per sender: `[tcp] failed to process message from <ip> ...` with a count; **unreadable** on the Machines page |
| More than 1 MB without a line break (e.g. binary data, wrong framing) | The gateway closes that connection, so it can't fill the memory. Lines before it were handled; other connections go on | `[tcp] connection from <ip:port> failed: a line passed 1,048,576 characters without a line break, connection closed` |
| `timestamp` that can't be read, or more than 1 h from its arrival | Not rejected. An unreadable one is replaced by the arrival time; a far-off one is used as sent | Once a minute per machine: `[ingest] NXT102: timestamp ... can't be read ...` or `... is 3.0 h ahead of its arrival ...`; **time warnings** on the Machines page |
| Broker down | MQTT messages are dropped, not queued; retried after 5 s | `[mqtt] cannot connect ...`; `[mqtt] N messages dropped` once a minute |
| MES down, unreachable or too slow (no answer in 15 s), or answers 5xx / 401 / 403 / 404 / 405 / 407 / 408 / 413 / 429 | Kept in the outbox and retried, each machine's data in order (404 / 405: check `mesUrl`, or MES is being redeployed; 413: check the MES / proxy size limit, the machine's next records wait behind that record until it moves to failed messages, see below). A backlog of one machine does not hold up the others. The wait between tries grows: 2 s, 4 s, 8 s … up to 60 s, back to 2 s once MES takes something. So after MES is back it can take up to 60 s before the backlog starts to drain | `[forwarder] send failed ... will retry in N s or more`, once a minute; backlog grows in `[stats]`. When MES is back: one `[forwarder] MES reachable again after N s (N tries) — N waiting rows delivered` line |
| MES rejects a record as invalid (any other 4xx, e.g. 400), or answers 500 or 413 for the same record 5 times in a row while it takes other records (or for 10 min, when nothing else is sent; both are General settings `mesStuckRowTries` / `mesStuckRowMinutes`, applied on Restart Gateway) | **Not retried, not lost**: moved to **failed messages** with MES's answer. That machine's next records go on | ERR log line `[forwarder] MES rejected <event_id> (<machine>/<group>) as invalid, moved to failed messages: MES 400 …`; red pop-up; red "⚠ N rejected by MES" on the tab bar; General → **MES failures** |
| Gateway restarts (config page) or crashes | Connected machines are dropped; the gateway is back within ~50 ms. MES lines the adapter sent but got no reply for are sent again by the adapter after it reconnects (lines already saved are answered as repeats). IO/motion lines sent on the dead connection are lost | `[tcp] client disconnected` / `client connected`. The last values of each machine + group are saved with every record, so after a restart or crash unchanged data is **not** sent again, and an open Span keeps its start time |

## 5. How to change the config

All files are in the `Config\` folder **next to the running exe**, e.g. `bin\Release\net8.0\Config\`.

| File | Controls | Applies |
|---|---|---|
| `point-map.json` | incoming tag → data group + fieldName | live, on save |
| `timestamp-rules.json` | per data group: timestamp (SinglePoint / Span) and `sendEveryMs` (how often changes go to MES; 0 = right away, empty = 1000). Span: `previousField` = where the value before the change goes (MACHINE_STATUS: `previous_status`; empty = `previous_<triggerField>`) | live, on save |
| `mes-schema.json` | the MES format: per data group, the columns of its MES table, all sent (`null` when missing). Edited on the page with **+ MES field** / **×** in PointMap (all machines) | live, on save |
| `mqtt-config.json` | broker address/port, `clientIdSuffix`, area, keepalive, deadband, rate | after restart |
| `machine-config.json` | gatewayId, `listenAddress` + TCP port (`0.0.0.0` = all addresses; a specific IP = only that network card, then `127.0.0.1` is refused), MES URL, default data source, `timestampsWithoutZone` (`UTC` or `local`), `outboxKeepSentDays` | after restart |

- **Best way:** use the config page. It checks your changes before saving, so a bad config is never written.
- **By hand:** edit the file, then restart the gateway.
- **Two gateways on one broker:** give each one a different `clientIdSuffix`, or they keep kicking each other off.

**Every machine has its own tab** (so two machines can use the same field name for different things)
- All machines still send to the same TCP port (9000). The gateway looks at each line's `machine_id`.
- A machine with a tab (folder `Config\machines\<machine_id>\`: its own `point-map.json` + `timestamp-rules.json`) is handled completely apart from the others: its own mapping, own change check and Span times, own `sendEveryMs` pacing, own Unidentified list, own outbox folder `outbox\<machine_id>\` and its own MES sender. It still goes to the same MES and broker.
- The mapping in `Config\` itself is the **Template**: what **+** copies into a new machine tab.
- `mes-schema.json` (the MES format), `machine-config.json` and `mqtt-config.json` are shared by all machines.

**A new machine** (a `machine_id` without a tab) — GatewayConfig `unregisteredMachines`:

| Setting | What happens to its MES lines |
|---|---|
| `hold` (default) | **Held**: not processed, not answered. The adapter keeps them. A red "⚠ N not registered" shows on the tab bar, the log has a line about it, and General → Machines lists the machine with the tags it sent and a **register** button |
| `shared` | Processed with the Template mapping and the shared outbox (no notice) |

- **Best:** register a machine before it starts sending. Same model as an existing machine (NXT103 next to NXT101): open that machine's tab and press **+**. New model: get the field list, press **+**, fill in the incoming tags.
- **Register a held machine:** General → Machines → pick whose mapping to copy (a machine with a similar name is suggested) → **register**. The machine gets its tab, the gateway closes its connection so the adapter reconnects and sends the held data again, and that data is processed with the new tab. The tags it sent are listed under its PointMap → ⚠ Unidentified, to map.
- IO/motion lines are never held: they don't use the mapping.
- The **×** on a machine's tab removes its tab (with `hold`, its data is held again). Its files are kept in `Config\machines-removed\`, and whatever is in its outbox is still delivered (visible under Template → Outbox).
- If a machine's own files can't be read, or break a rule the config page checks (edited by hand, e.g. a Span rule without `endField`), its lines are held the same way until the files are fixed. The tab is red with the file and the problem. The same mistake in the shared files stops the gateway (it retries every 60 s) until fixed.

**A tag the mapping doesn't know** (e.g. the data team adds `source_ref`) — GatewayConfig `unmappedTags`:

| Setting | What happens to such a message |
|---|---|
| `send` (default) | The tags the mapping knows go to MES as usual and the message is acked: one new tag does not stop the machine's data. The unknown tags are **left out of MES** (never as `UNIDENTIFIED`) and listed under the tab's PointMap → **⚠ Unidentified** with their last value. Log line once a minute per machine: `[ingest] NXT102 sent tags its mapping doesn't know (source_ref): the other tags were sent, these are left out ...`. Values of a tag before it is mapped are not sent later. |
| `hold` | The whole message is **held**: not processed, not answered, the adapter keeps it. A log line, **⚠** on the machine's tab and a red bar listing the tags. When the machine's last unknown tag is handled, the gateway closes its connection; the adapter sends the held messages again, and they go through complete. Until then that machine's MES data waits. |

- Fix it on that tab: PointMap → **⚠ Unidentified** → for each tag pick a group and field (a MES column, or an extra field), or **ignore (never sent)** for a tag MES doesn't need. Then **save**.
- Ignored tags are kept in PointMap under the group `IGNORED`; remove a row there to stop ignoring it.

**Logs (the gateway usually runs in the background)**
- Every console line is also written to `logs\gateway-yyyy-MM-dd.log` next to the exe. There's one file per day, and files are deleted after 14 days. Problem lines are marked `ERR`.
- The config page shows the same lines; see **Log** below.

**Outbox (local store for MES)**
- One SQLite file per day in `outbox\` next to the exe: `outbox-yyyy-MM-dd.db`, plain JSON, readable with DB Browser for SQLite. A machine with its own mapping has its own folder `outbox\<machine_id>\` with the same files.
- After `outboxKeepSentDays` (default 7), a day file is deleted as a whole, but only once every row in it was delivered. The check runs every hour.
- Rows not yet delivered are **never** deleted, so nothing is lost while MES is down. Failed messages (rejected by MES) count as not delivered and keep their day file too, until they are re-sent and delivered or discarded.
- An older single `outbox.db` from before this change is still read first, and removed the same way.
- Sending: up to 16 machines in parallel, each machine's data strictly in order.
- Over 10,000 undelivered rows logs a warning: MES has been unreachable for a long time.
- Disk: about 0.5 KB per row. Plan disk space for 7 days of your real volume.

## 6. How to use the config page

Open **http://localhost:5100/config/**. It only opens on the gateway PC itself.

**Tabs at the top** (like browser tabs)

| Tab | What's in it |
|---|---|
| **General** (opens first) | **MQTTConfig**, **GatewayConfig** (the gateway's own settings), **Machines**, **MES failures**, **Log**: everything that is the same for all machines |
| **Template** | **PointMap**, **TimeStamp**, **Outbox** of the template mapping (what **+** copies). Called **Other machines** when `unregisteredMachines` is `shared` (then every machine without a tab uses it) |
| **One tab per machine** (e.g. `NXT101`) | That machine's own **PointMap**, **TimeStamp** and **Outbox**. Only that machine uses them |
| **+** | Gives a machine its own tab, copied from the mapping of the tab you are on |
| **⚠ N not registered** (red, only when it happens) | Machines sending without a tab: their data is held. Click to see them in General → Machines and register them |
| **⚠ N rejected by MES** (red, only when it happens) | Records MES refused as invalid. Click to see them in General → MES failures |

- **×** on a machine's tab closes it (asks first). Unsaved changes are asked about before switching tabs.
- A machine tab in red (⚠) means its files can't be read; its lines are held until they are fixed.
- In **Machines**, **own tab** opens that machine's tab; **held: no tab** marks a machine whose data is held.

**PointMap** — per data group, two lists:
1. **MES fields:** every column of that group's MES table (the MES format), already filled in. Type only the **incoming tag** next to each one; several tags for one field are comma-separated (`MACHINE_STATUS, current_status_code`). A field with no tag is sent to MES as `null`. "set by TimeStamp rule" marks fields the gateway fills itself (start/end/record times).
   - **+ MES field** adds a column to the MES format, **×** removes one. This changes the MES format **for every machine** (when MES changes a table) and is saved with **save**.
2. **Extra fields:** tags with no MES column (e.g. Nexim `event_name`): incoming tag + a field name of your choice, for this tab only. They are **not sent to MES**: the payload has only the MES fields. Time fields the TimeStamp rule adds without a MES column (`record_timestamp` …) are not sent either. Type in the blank row to add one, **×** removes one. Groups that are no MES table (MOTION, IO, new groups) only have this list.
   - Several tags on one MES field (`line_name, module_no`) are alternatives: if a message carries both, the later one in the message wins.
   - **Join with `+`:** `line_no+process_id` puts both values in the field, joined with `_` (`001` + `xxxx` = `001_xxxx`). Only when every part is in the message; otherwise the field keeps its last value (never half a value). The parts count as mapped and can still be mapped on their own too.
3. **+ New group…** adds a group. If the group has no timestamp rule, the page offers **Add rule**.
4. **⚠ Unidentified (N)** lists tags this machine sent that aren't mapped yet (their messages are held). Pick a group and a field (the box suggests that group's MES columns), or **ignore (never sent)**, then save. The held messages are then sent again.

**TimeStamp**
1. Pick a group, then set the mode:
   - SinglePoint: set `timestampField`.
   - Span: set `triggerField`, `startField` and `endField`.
2. Set `sendEveryMs`: how often this group's changes go to MES (empty = 1000). It applies as soon as you save.
3. Use **remove rule** to delete a rule.

**MQTTConfig / GatewayConfig** (General tab)
1. Edit the values. Each setting has a plain name, its name in the file underneath (e.g. `mesUrl`), and a help line. The settings with fixed choices (`timestampsWithoutZone`, `unregisteredMachines`, `unmappedTags`) are drop-downs. GatewayConfig is `Config\machine-config.json`: gatewayId, listen address + port, MES URL, default data source, timestamps without a zone, outbox keep days, `unregisteredMachines` (`hold` or `shared`, see "A new machine" above). There is no machineId: which machine a line is from comes from the line's `machine_id`.
2. These only apply after a restart.
3. The gateway's own MQTT heartbeat is `svi/<area>/<gatewayId>/keepalive`.

**Machines** (read-only, updates every 4 s)
1. One row per `machine_id` the gateway has received: a dot for the state before its name, its tab, the connection (ip:port), the last message, and the message counts.
   - Green = connection open and a message in the last 60 s. Orange = open but quiet for over 60 s. Grey = disconnected. A machine that lost power or its cable shows grey after about 90 s (TCP keepalive).
   - **messages** = all lines with this machine; **to MES / MQTT** = lines that went into the outbox / were relayed to the broker.
   - **issues** shows only the counters that are not 0 (hover for what each means):
     - **held** (red) = messages waiting right now (no tab, or tags not mapped);
     - **repeats** (orange) = lines ignored because their `message_id` was already received in the last 10 min;
     - **time warnings** (orange) = messages whose `timestamp` couldn't be read (arrival time used) or was more than 1 h from arrival (wrong machine clock, wrong `timestampsWithoutZone`, or an old message resent). The log says which.
   - **data sources** = how many messages came with each `data_source`.
2. **Connections** lists each TCP connection (open first, plus the last 200 closed ones):
   - **lines**: non-empty lines received on it.
   - **issues**, only when not 0: **rejected** (red: no `machine_id` or `message_id`, not used), **unreadable** (red: not valid JSON), **repeats** (orange: repeated `message_id`, ignored).
3. To check that nothing was missed: the source's count of sent lines must equal **lines** on its connection. Every line is counted when it arrives, before it is handled.
4. Counts start when the exe starts. A **Restart now** from the page keeps them; closing the exe resets them.

**MES failures** (General tab) — records MES rejected as invalid, all machines
1. Each row: machine, group, data source, event time, when MES rejected it, and **what MES said** (status + its answer). Click a row for the whole envelope.
2. Fix the cause first: the mapping (wrong field, value too long), the MES format (**+ MES field**), or on the MES side.
3. **re-send** (one row, the ticked rows, or all): back in the send queue. If MES rejects it again it comes back here.
4. **discard**: only with a reason. The reason is written to the log (`[failed] discarded N … reason: …`) and kept with the rows in the outbox (status **discarded**); they are never sent.
5. MES unreachable or slow is **not** a failure: those records wait in the outbox and are retried by themselves.

**Outbox** (read-only)
1. Pick a day. Each day shows its rows, how many still wait for MES, how many MES rejected, and its size. Days are kept for 7 days after all rows were delivered.
2. Filter by machine, group, data source, or **sent to MES** / **waiting** / **rejected by MES** / **discarded**. Machine and data source don't care about upper or lower case.
3. The list shows the newest 100 rows. **load older rows** shows the next 100.
4. Click a row to show the whole envelope exactly as it went (or will go) to MES. Click it again to close it.
5. **refresh** reloads the day list and the rows.
6. Times show in this PC's local time. The envelope itself keeps UTC (`…Z`).

**Log**
1. Shows everything the gateway printed, with problems in red. Tick **Show problems only** to filter.
2. New problem lines are counted in the status area: **⚠ N new problems · view log**. Click it to open the Log. There is no pop-up per line.
3. A problem that lasts shows as one banner at the top of every view until it is solved: **MES not reachable since …** (records wait in the outbox, with how many), **MQTT broker not reachable since …** (IO / motion data and the keepalive are not published), **Gateway stopped** (it tries again by itself), or the page lost its connection to the gateway. Pop-ups are only for what you just did (save, register, restart) and for records MES rejects.

**Every view**
- Bottom left: gateway state, plus the last 10 s per second: lines in, MQTT out, MES delivered, and the backlog (rows waiting for MES).
- **Search** filters what's shown.
- A **•** on a button means unsaved changes.
- **save** checks the file first. If something is wrong, a red message explains it and nothing is written.
- After saving, the page asks **Restart the gateway now?** If you choose **Restart now**, the page stays open and reports **Gateway restarted and running**, or the error.
- During a restart, connected machines are dropped for about 1–2 s and must reconnect.

## 7. Quick troubleshooting

| Symptom | Check |
|---|---|
| Topic missing in MQTT | Does it end in one of the 4 endings? Look for a `dropped` line in the page's **Log** or in `logs\`. |
| Something went wrong while nobody watched | Config page → **Log** (last 500 lines), or `logs\gateway-<date>.log` (14 days). |
| Saved on the page, but nothing changed | MQTT and Machine settings need **Restart now**. Is the right exe running? |
| MQTT keeps disconnecting and reconnecting | Two gateways share a `clientIdSuffix` on the same broker. |
| A machine tab shows ⚠ / its data stopped | Its messages are held for unknown tags: open the tab, **show them**, map each tag or set it to ignore, save. |
| A machine's data never reaches MES | Look for `[ingest] ... rejected: no machine_id` in the Log. Does each message have `machine_id`? |
| Not sure every line arrived | Config page → **Machines** → **Connections**: **lines** must equal what the source sent. Check **rejected**, **repeats** and **unreadable** too. |
| Need to see what MES got for a machine | Config page → **Outbox** → pick the day, type the machine, click a row. |
| Red "⚠ N rejected by MES" | General → **MES failures**: read what MES said, fix, **re-send** (or **discard** with a reason). |
| Backlog grows, nothing delivered | MES down or unreachable: Log shows `[forwarder] send failed ...`. Rows wait; tries slow down to once a minute. Nothing to do on the gateway. |

**Local MES tests** (fake MES, `MES_EMULATOR`; not the real MES)
- The fake MES checks envelopes like we expect MES to (known data group, every column present, `machine_status` ≤ 30 characters), stores each `event_id` once (`received.jsonl`), and can be switched for tests: `http://localhost:5001/mode?set=up|down|unreachable|slow|reject` (`&rejectGroup=ALARM` rejects one group), `/stats`, `/reset`.
- End-to-end: run MachineSim (e.g. `--machines 2 --id E2E --minutes 1 --data-s 0.25 --motion-ms 0 --io-s 0 --host 192.168.250.78`; tabs `E2E-M01`/`E2E-M02` exist for it), then `dotnet run -- --check` in `MachineSim`: every MES record must match its source line; prints PASS/FAIL with lost / extra / wrong counts.
- MachineSim keeps each data line until the gateway answers and sends unanswered ones again after a reconnect, like the data team's adapter.
| A change reaches MES late | It waits for the group's `sendEveryMs` (PRODUCTION: up to 10 s). |
| Backlog keeps growing | MES delivered/s is lower than queued/s in `[stats]`. Check that MES is reachable and fast enough. |
| Page says "Gateway stopped: …" | It starts again by itself after 5 s, 10 s, 20 s, 40 s, then every 60 s, so a passing problem (e.g. the network not up yet) needs nothing. If it keeps failing, fix the named setting (e.g. port in use), save, then **Restart now** (no wait). |
| Machine stops sending after a restart | The machine must reconnect to TCP port 9000 by itself. |
