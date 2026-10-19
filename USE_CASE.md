# Edge Gateway (TCP) — Use Case Report

## 1. What it does

The gateway is a shared server. Many machines (sized for 50, tested at 5,000 messages/s) each keep a TCP connection to it and send one JSON line per message.
- **IO and motion lines** go to MQTT (the digital twin), in SpotWelding format.
- **Everything else** goes to MES through a local outbox, so nothing is lost if MES is down. MES data is sent per data group on an interval, and only when something changed.
- Every MES message must say which machine it came from (`machine_id`).

## 2. How to use

1. Start `bin\Release\net8.0\EdgeGateway.exe`. The console shows:
   `Configuration UI: http://localhost:5100/config/`
2. Point every machine at **this server, TCP port 9000**, one JSON message per line.
3. Check that it works:
   - The console shows `[gateway] running: ...`, then one `[stats]` line every 10 s while data flows:
     `[stats] in 5003/s · mqtt 2001/s · mes queued 3337/s, delivered 2600/s · backlog 25,014`
   - MQTT Explorer (connected to the broker in `mqtt-config.json`) shows your topics.
   - The bottom left of the config page shows a green dot with **Gateway running**, and the same rates.

To test without the real broker, use `..\LocalMqttTest`. It has its own broker, and the gateway copy uses TCP port 19000 and page port 15100.
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

**Heartbeat**: `svi/TEST/ASSY-L01-FCT-01/keepalive` = `{"alive":true}`, every 30 s.

**MES line** (anything without an IO/motion topic), for example:
```
{"machine_id":"ASSY-L01-FCT-01","timestamp":"2026-10-12T10:00:00.123","MACHINE_STATUS":"RUN","GOOD_QTY":125}
```
- `machine_id` is required. `timestamp` is optional: it is when the event happened on the machine, and the gateway uses it for MES times (otherwise the arrival time is used). A timestamp with `Z` or an offset (`+07:00`) is taken as written. **Without one it is UTC** (setting `timestampsWithoutZone` in machine-config.json: `UTC` or `local`).
- Each `data_source` is kept apart: the same data group from two data sources (e.g. `mcstatechange` and `mcstartstate`, both with traceability tags) gives **two separate envelopes**, even when they arrive at the same time. Changes, intervals and Span times are tracked per machine + data_source + group.
- **All MES times are sent in UTC with `Z`**, e.g. `2026-10-16T08:25:28.086Z` (`record_timestamp`, start/end times, `publish_timestamp`). MQTT keeps the SpotWelding local format `yyyy-MM-dd HH:mm:ss.fff`. `data_source` is optional: it goes into the envelope's `data_source` (otherwise `dataSource` from machine-config.json is used). None of these three is a data tag.
- Any other field is a data tag. Tags not in the point map go to MES in group `UNIDENTIFIED` until you map them.
- Put many tags in one message. One tag per line costs far more work.
- Each tag is mapped through `point-map.json` into its data group.

**When MES data is sent** (per machine and data group):
- A message that changes nothing is ignored.
- Each change becomes one record: all current values of that group, with timestamps.
- Records wait for the group's next tick (`sendEveryMs`), then **every** waiting record is sent, oldest first. Example with PRODUCTION every 10 s: changes at 10:00:03 and 10:00:07 are both sent at 10:00:10.
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
| MES line without `machine_id` | **Rejected**, so data is never filed under the wrong machine | Once a minute per sender: `[ingest] line from <ip> rejected: no "machine_id" (+N more ...)` |
| CSV line `TAG,value` | **Rejected**: it can't carry a `machine_id` | Same as above |
| Same values as the last message | Nothing sent to MES (no change) | — |
| Tag not in `point-map.json` | Sent to MES in group `UNIDENTIFIED` | Console `unmapped tag 'X'` the first time only; listed under **⚠ Unidentified** on the config page |
| Line that is not valid JSON | Skipped | Once a minute per sender: `[tcp] failed to process message from <ip> ...` with a count |
| Broker down | MQTT messages are dropped, not queued; retried after 5 s | `[mqtt] cannot connect ...`; `[mqtt] N messages dropped` once a minute |
| MES down | Kept in the outbox, retried every 2 s, each machine's data in order | `[forwarder] send failed ... will retry`, once a minute; backlog grows in `[stats]`. When MES is back: one `[forwarder] MES reachable again after N s (N tries) — N waiting rows delivered` line |
| Gateway restarts (config page) | Connected machines are dropped; the gateway is back within ~50 ms. Lines a machine sends on the dead connection are lost, and so is anything it can't send until it reconnects | `[tcp] client disconnected` / `client connected`. The first message after a restart is always sent, even if unchanged (the gateway forgets last values) |

## 5. How to change the config

All files are in the `Config\` folder **next to the running exe**, e.g. `bin\Release\net8.0\Config\`.

| File | Controls | Applies |
|---|---|---|
| `point-map.json` | incoming tag → data group + fieldName | live, on save |
| `timestamp-rules.json` | per data group: timestamp (SinglePoint / Span) and `sendEveryMs` (how often changes go to MES; 0 = right away, empty = 1000) | live, on save |
| `mqtt-config.json` | broker address/port, `clientIdSuffix`, area, keepalive, deadband, rate | after restart |
| `machine-config.json` | gatewayId, `listenAddress` + TCP port (`0.0.0.0` = all addresses; a specific IP = only that network card, then `127.0.0.1` is refused), MES URL, default data source, `timestampsWithoutZone` (`UTC` or `local`), `outboxKeepSentDays` | after restart |

- **Best way:** use the config page. It checks your changes before saving, so a bad config is never written.
- **By hand:** edit the file, then restart the gateway.
- **Two gateways on one broker:** give each one a different `clientIdSuffix`, or they keep kicking each other off.

**Logs (the gateway usually runs in the background)**
- Every console line is also written to `logs\gateway-yyyy-MM-dd.log` next to the exe. There's one file per day, and files are deleted after 14 days. Problem lines are marked `ERR`.
- The config page shows the same lines; see **Log** below.

**Outbox (local store for MES)**
- One SQLite file per day in `outbox\` next to the exe: `outbox-yyyy-MM-dd.db`, plain JSON, readable with DB Browser for SQLite.
- After `outboxKeepSentDays` (default 7), a day file is deleted as a whole, but only once every row in it was delivered. The check runs every hour.
- Rows not yet delivered are **never** deleted, so nothing is lost while MES is down.
- An older single `outbox.db` from before this change is still read first, and removed the same way.
- Sending: up to 16 machines in parallel, each machine's data strictly in order.
- Over 10,000 undelivered rows logs a warning: MES has been unreachable for a long time.
- Disk: about 0.5 KB per row. Plan disk space for 7 days of your real volume.

## 6. How to use the config page

Open **http://localhost:5100/config/**. It only opens on the gateway PC itself.

**PointMap**
1. Pick a data group in the dropdown.
2. Edit the **incoming tag** / **fieldName** rows. Type in a blank row to add one, and use **×** to remove one.
3. **+ New group…** adds a group. If the group has no timestamp rule, the page offers **Add rule**.
4. **⚠ Unidentified (N)** lists tags the gateway received that aren't mapped yet. Pick a group and a fieldName, then save.

**TimeStamp**
1. Pick a group, then set the mode:
   - SinglePoint: set `timestampField`.
   - Span: set `triggerField`, `startField` and `endField`.
2. Set `sendEveryMs`: how often this group's changes go to MES (empty = 1000). It applies as soon as you save.
3. Use **remove rule** to delete a rule.

**MQTTConfig / MachineConfig**
1. Edit the values.
2. These only apply after a restart.

**Machines** (read-only, updates every 4 s)
1. One row per `machine_id` the gateway has received: a dot for the state, the connection (ip:port), the last message, and the message counts.
   - Green = connection open and a message in the last 60 s. Orange = open but quiet for over 60 s. Grey = disconnected.
   - **messages** = all lines with this machine; **to MES** = lines that went into the outbox; **to MQTT** = lines relayed to the broker.
   - **data sources** = how many messages came with each `data_source`.
2. **Connections** lists each TCP connection (open first, plus the last 200 closed ones):
   - **lines**: non-empty lines received on it.
   - **without machine_id** (red if above 0): lines that were dropped.
   - **unreadable** (red if above 0): lines that weren't valid JSON.
3. To check that nothing was missed: the source's count of sent lines must equal **lines** on its connection. Every line is counted when it arrives, before it is handled.
4. Counts start when the exe starts. A **Restart now** from the page keeps them; closing the exe resets them.

**Outbox** (read-only)
1. Pick a day. Each day shows its rows, how many still wait for MES, and its size. Days are kept for 7 days after all rows were delivered.
2. Filter by machine, group, data source, or **sent to MES** / **waiting**. Machine and data source don't care about upper or lower case.
3. The list shows the newest 100 rows. **load older rows** shows the next 100.
4. Click a row to show the whole envelope exactly as it went (or will go) to MES. Click it again to close it.
5. **refresh** reloads the day list and the rows.
6. Times show in this PC's local time. The envelope itself keeps UTC (`…Z`).

**Log**
1. Shows everything the gateway printed, with problems in red. Tick **Show problems only** to filter.
2. When a new problem arrives, the page shows a red pop-up, and the status area shows **⚠ N new problems · view log**. Click it to open the Log.

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
| Tag shows up as UNIDENTIFIED | Map it in PointMap → ⚠ Unidentified. |
| A machine's data never reaches MES | Look for `[ingest] ... rejected: no "machine_id"` in the Log. Does each message have `machine_id`? |
| Not sure every line arrived | Config page → **Machines** → **Connections**: **lines** must equal what the source sent. Check **without machine_id** and **unreadable** too. |
| Need to see what MES got for a machine | Config page → **Outbox** → pick the day, type the machine, click a row. |
| A change reaches MES late | It waits for the group's `sendEveryMs` (PRODUCTION: up to 10 s). |
| Backlog keeps growing | MES delivered/s is lower than queued/s in `[stats]`. Check that MES is reachable and fast enough. |
| Page says "Gateway stopped: …" | Fix the named setting (e.g. port in use), save, then **Restart now**. |
| Machine stops sending after a restart | The machine must reconnect to TCP port 9000 by itself. |
