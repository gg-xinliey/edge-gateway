possible change and could be update according to environment change 

1. every incoming data will be convert to envelope and being send over eventho it's a duplicated data
2. the time stamp recorded is the time data arrived gateway not the time event happened in machine, see if incoming data has time , use that 
3. update normalizer to be more flexible with unknown data
4. connect ui with api for easier config
5. added mqtt feature 

potential issue
1. UNIDENTIFIED isnt considered clean and safe yet
2. UI still isnt to my ideal
3. need to add more test cases for normalizer and mqtt


==========================================================================
update 2026-10-09 : MQTT in SpotWelding format, new config page, restart
==========================================================================

changes
1. MQTT twin output now matches the SpotWelding format
   - the machine sends {"topic":"...","payload":"{...}"} ; the gateway unwraps it
     and publishes ONLY the inner payload:
       {"timestamp":"2026-10-09 14:04:06.961","totalbits":96,"value":"E77B..."}
   - timestamp rewritten to "yyyy-MM-dd HH:mm:ss.fff", local time (same as SpotWelding)
   - published on the line's OWN topic. the front is free, but it must END in one of 4:
       IO/input   IO/output   motion/position   motion/status
     wrong case is fixed (io/INPUT -> IO/input)
   - ending IO/<other> or motion/<other> -> dropped, 1 warning per topic, never goes to MES
   - "routes" and "publishTopicTemplate" removed from mqtt-config.json, the rule is
     fixed in code (Services/MqttRelay.cs)
   - topics the gateway builds itself (TwinForwarder):
       svi/<area>/<machine_id>/IO/input|output , motion/position|status
     keepalive kept separate: svi/<area>/<machine_id>/keepalive
2. new config page, http://localhost:5100/config/ , same layout as the WinForms mockup
   - PointMap / TimeStamp / MQTTConfig / MachineConfig
   - "+ New group..." in the dropdown
   - "Unidentified (N)" = tags received but not in point-map; pick group + fieldName, save
   - every save asks "restart the gateway now?"
   - old page kept as wwwroot/config/*.bak
3. Config/machine-config.json (new): gatewayId, machineId, tcpPort, dataSource,
   protocolLabel, mesUrl. these were consts in Program.cs; file missing = old values
4. in-process restart (Services/GatewayHost.cs): reads all config again and rebuilds
   TCP / MQTT / MES parts; the config page stays up and shows the result or the error.
   "--admin-port <n>" changes the page port (default 5100)
5. offline test bench: Desktop\gateway\LocalMqttTest (see "how to test" below)

bugs fixed / found
1. topic came out as "svi/{gateway_id}/io": placeholder no longer filled after a template
   change -> template removed, the line's own topic is used
2. page saved to <working dir>\Config but runtime reloaded <exe dir>\Config. under
   "dotnet run" those are different folders, so Save looked like it did nothing
   -> page, runtime and restart all use the folder next to the exe (GatewayConfigStore.ConfigRoot)
3. page only checked JSON syntax: a wrong shape was written to disk, reload failed, and
   the next start crashed -> Admin/ConfigValidator.cs loads it like the gateway would
   BEFORE writing; bad content = HTTP 400 with a message, file untouched
4. duplicate tags (ignoring case) crash Normalizer (ToDictionary) -> rejected on save
5. Span rule without startField/endField throws while stamping -> rejected on save

discoveries / notes
- a restart drops connected machines for ~1-2 s; the machine must reconnect by itself
- the page edits the Config next to the RUNNING exe (e.g. bin\Release\net8.0\Config),
  not the project Config folder
- Unidentified list lives in memory: survives a page restart, cleared when the exe closes
- MqttOptions.MaxPublishRateMs was used as "messages per second" (20 -> 1 per 50 ms);
  since 2026-10-26 (#21) it is ms between publishes, default 50 (same speed as before)
- MqttOptions and MqttConnectionConfig both read brokerAddress with DIFFERENT defaults
  (127.0.0.1 vs 192.168.251.189); only matters if the key is missing from
  mqtt-config.json (the page always writes it)

how to test without the real broker (LocalMqttTest)
- dotnet run --project ..\LocalMqttTest              menu: 1-6 samples, c = paste a line
- dotnet run --project ..\LocalMqttTest -- --auto    send every sample once
- dotnet run --project ..\LocalMqttTest -- --hold 10 keep it up 10 min, page on :15100
- runs a copy in LocalMqttTest\gateway-run on TCP 19000 / page 15100 / broker 127.0.0.1,
  so it can run next to the live gateway; the real Config is never touched

tested 2026-10-09 (offline bench)
- real line -> svi/TEST/ASSY-L01-FCT-01/IO/input with flat payload            ok
- IO/output, motion/position, io/INPUT (case fixed), IO/foo dropped, MES line   ok
- page: unidentified SPINDLE_TEMP mapped to PROCESS                             ok
- page: MQTT keepalive 30 -> 10, restart, new value used, IO still published    ok
- page: new group, duplicate tag blocked, 4 bad configs rejected by the API     ok

status of "potential issue" above
1. UNIDENTIFIED: now visible + mappable from the page; still sent to MES as UNIDENTIFIED
2. UI: rebuilt to the mockup layout
3. tests: offline bench added; still no automated unit tests for normalizer / mqtt

remaining
- live gateway still runs the old build until it is stopped, rebuilt (Release) and started
- ..\LocalMqttTest is outside this folder, so it is not in this git repo

git
- this folder is a git repo since 2026-10-09 (first commit 411ea17, see "git log")
- git is not on PATH; it is at C:\Users\Atm_Theppanadda\Desktop\PortableGit\cmd\git.exe
- Config/*.bak (written by the page on every save), bin/, obj/, .vs/, outbox/, logs/ are ignored


==========================================================================
update 2026-10-12 .. 10-14 : shared server for many machines
==========================================================================

why
- the gateway is no longer one machine's local gateway: ~50 machines send to it.
  designed for 5,000 messages/s in. user guide: USE_CASE.md

changes
1. machine_id + timestamp in every message are message details, not tags.
   a MES line without machine_id is rejected (CSV TAG,value lines too)
2. MES data per data group on an interval: sendEveryMs in timestamp-rules.json
   (MACHINE_STATUS 100, PRODUCTION 10000, default 1000, 0 = right away), live.
   only changes are sent, every change, in order; no change = nothing sent
   (a 60 s repeat of unchanged data was removed on 2026-10-16: MES could
   count it as new events)
3. outbox: one background writer with batch transactions; one file per day
   (outbox\outbox-yyyy-MM-dd.db); a day file older than outboxKeepSentDays is
   deleted only when all its rows were delivered; old outbox.db still read
4. ingest: TCP readers only queue lines; up to 8 worker lanes, one lane per
   sender IP (each machine in order, also across a reconnect; adapters on one
   PC share a lane, ~10,000 lines/s in a Debug build); each line parsed once;
   a line over 1 MB without a line break closes its connection
5. MES forwarder: 2,000 rows per pass, 16 machines in parallel, each in order,
   no pause while there is a backlog, 15 s HTTP timeout
6. MQTT relay: one send queue + one sender (drop oldest when full)
7. logs: unmapped tags logged once; repeated problems once a minute with a count;
   [stats] line every 10 s; logs\gateway-<date>.log (14 days); Log view on the page
8. config page: sendEveryMs field, live rates in the status area, 22 px gap fix
9. MQTT client ids from clientIdSuffix (twin = <PC>_<suffix>_TwinPublisher)
10. motion: world/joint/pulse sent as JSON text are published as real objects

bugs found by the load test (all fixed)
1. one SQLite write per row on the TCP thread: capped at ~300 rows/s and
   stopped the gateway from reading machines
2. thread starvation from those writes: MQTT took 36 s to connect
3. MES forwarder sent one request at a time: ~50/s max
4. TwinPublisher disposed its client while another connect used it
   ("Cannot access a disposed object ... AsyncLockWaiter")
5. point-map maps tag machine_id into MOTION, so every machine's data was
   filed as ASSY-L01-FCT-01 (machine_id is now metadata, never a tag)
6. 'timestamp' not in point-map: "unmapped tag" logged 3,000 times/s
7. two gateways with the same client id on one broker kick each other off

load test, 50 machines, 5,000 msg/s, same laptop (..\LocalMqttTest --load 5000 60 50)
                     before        after
  accepted           1,312/s       5,000/s (all)
  MQTT latency p95   65 s          13 ms
  MES delivered      6/s           2,600/s under load, 4,000/s draining
  console lines/70 s 27,015        217
  problems           9             0
- the tester's data changes every message (worst case: 3,800 MES records/s);
  that is slightly more than one laptop delivers, so its backlog grows during
  the test and drains after. re-run on the real server

manual test session 2026-10-16 (4-pane test window: broker, MES, gateway, source)
tools (outside this repo, in ..\): LocalMqttTest\test-env\start-test-env.ps1 opens
the window; MachineSim simulates N machines (motion 20 ms, data 3 s, IO 9 s,
reconnects); OutboxDump reads the outbox read-only to compare with expectations
- passed: unmapped -> mapped tags; no change = nothing sent; fast changes; 1 and
  5 min loops (182/182 envelopes); MES down 1 and 3 machines (all delivered after);
  3 machines + motion 50/s each (245/245 MES, 18,034/18,034 MQTT, no cross-talk);
  restart under load (253/253 MES, back in 32 ms, no false alarm)
- fixed during the session:
  1. 60 s full-snapshot repeat removed (MES could count unchanged data again)
  2. envelopes written with plain "+07:00" instead of "+07:00"
  3. intentional restart no longer logged as "ERR twin connection dropped"
  4. [stats] shows 0.1/s instead of 0/s at low rates
  5. [stats] divides by the real window (after a restart it showed 179/s for ~151/s)
  6. new line when MES is back: "MES reachable again after N s (N tries) — N rows delivered"
- found, by design: a restart forgets last values (first message after it is
  always sent); a machine loses ~1 line written on the dead connection plus
  whatever falls due before it reconnects
- on hold (team to decide):
  1. PROCESS-type data: every change vs latest value per interval (volume)
  2. Span records: one record should hold the full span (start + end); today the
     raise record carries the previous state's end and the clear record has no
     original start

open
- MES batch (array in one POST): ~10x faster if MES supports it; ask the MES team
- Machines view on the page, server heartbeat + per-machine online/offline (part D)
- run as a Windows service


==========================================================================
update 2026-10-16 .. 10-19 : remote devices, UTC, data_source, Machines + Outbox views
==========================================================================

changes
1. listen address: machine-config.json "listenAddress" (now 192.168.250.78, port 9000).
   "0.0.0.0" = every address of this PC. the page refuses an IP that is not on this PC
2. machine_id AND data_source come from each message. envelope data_source = the
   message's; the configured dataSource is only used when a message has none
3. data is kept apart per machine + data_source + group: own snapshot, own changes,
   own sendEveryMs queue, own spans, own envelopes. two sources with the same group
   are never merged into one record
4. UTC everywhere for MES: record / start / end times and publish_timestamp are
   "2026-10-16T08:25:28.086Z". an incoming "timestamp" without a zone is read as UTC
   (machine-config "timestampsWithoutZone": "UTC" | "local"); with Z or +07:00 it is
   taken as written. MQTT is unchanged: SpotWelding local "yyyy-MM-dd HH:mm:ss.fff"
5. config page, new view "Machines" (refresh 4 s, Services/TrafficRegistry.cs)
   - per machine_id: green (open + message in last 60 s) / orange (open, quiet) /
     grey (disconnected), connection ip:port, last message, messages, to MES, to MQTT,
     per s, count per data_source
   - per TCP connection: lines received, lines without machine_id, unreadable lines
     (red above 0), last line, machines on it. last 200 closed connections kept
   - a connection is now named ip:port (was ip), so 2 machines behind 1 IP stay apart
   - API: GET /api/machines
6. config page, new view "Outbox" (read-only, Admin/OutboxReader.cs)
   - day list (rows, waiting, MB); filter by machine, group, data source, sent/waiting
     (no case: nxt101 = NXT101); newest 100 rows + "load older rows"; click a row =
     the whole envelope
   - files opened read-only without pooling: the writer and the daily cleanup are
     never blocked. only yyyy-MM-dd or "legacy" map to a file (no path tricks)
   - API: GET /api/outbox/days , /api/outbox?day=&machine=&group=&source=&status=&before=&limit= ,
     /api/outbox/row?day=&id=   (no file for the day / no row = 404 with a message)
7. USE_CASE.md: Machines + Outbox sections, 2 troubleshooting rows

bugs fixed / found
1. one Nexim message put a 192-item component list into unrelated events:
   cause: one snapshot per machine + group merged the fields of every event type
   -> snapshot per machine + data_source + group. if the source sends event_name as
   data_source, only that event keeps its own fields
2. timestamps without a zone were read as local time (UTC+7) -> read as UTC by default
3. Outbox view: machine filter was case-sensitive -> COLLATE NOCASE
4. Outbox view: unknown / deleted day returned an empty list -> 404 "No outbox file
   for that day"
5. Outbox view: machine suggestions did not show (input "list" is read-only as a JS
   property -> setAttribute); 4 s refresh of Machines jumped the page to the top

discoveries / notes
- listenAddress 192.168.250.78 = 127.0.0.1 is refused. local tools must send to
  192.168.250.78:9000. remote devices also need an inbound firewall rule for TCP 9000
  (IT / admin, not done by the gateway)
- Machines counts live in memory: kept on "Restart now", reset when the exe closes
- "lines" on a connection is counted when the line arrives, before parsing. if the
  source's sent count = "lines", nothing was lost on the way in
- Span memory (last trigger value + start) lives in TimeStamper, which is rebuilt on
  every config reload: restart AND saving PointMap / TimeStamp on the page. the next
  message then opens a new span with no end and no previous_<trigger>
- data_source is matched exactly (after trim): "PRODSTARTED" and "prodstarted" are
  two sources
- MachineConfig.machineId is no longer what the data is filed under; every message
  must carry machine_id (the TODO comment on it is out of date)

how to test
- ..\LocalMqttTest\test-env\start-test-env.ps1 -NoSource : broker, MES, gateway
  (builds first); devices send to 192.168.250.78:9000
- page -> Machines: compare the source's sent count with "lines" on its connection
- page -> Outbox: pick the day, type a machine, click a row

tested 2026-10-19
- build: 0 warnings, 0 errors
- Outbox view on outbox-2026-10-16.db (3,350 rows, 0 waiting):
  machine nxt101 + group traceability 100 rows, all NXT101 / TRACEABILITY   ok
  load older: next 100, all ids lower                                        ok
  data source PRODSTARTED: 146 rows, all PRODSTARTED                         ok
  status waiting: 0                                                          ok
  click a row: full envelope with UTC times                                  ok
  day "..\..\x" and 2026-10-01: 404 ; row id 999999999: 404                  ok
- Machines view: loads, empty state shown; NOT yet tested with live traffic

status of earlier items
- "Machines view ... per-machine online/offline (part D)": done (connection state,
  60 s idle, counts). a server heartbeat to MES is still not done
- on hold (PROCESS volume, full span in one record): unchanged

remaining
- Machines view with live traffic (2 machines, mixed data_source, 1 line without
  machine_id)
- line framing: one JSON per line works; one JSON spread over several lines does not
- span memory lost on config save / restart (see notes) if MES needs every span closed
- point-map edits made on the page are in bin\Debug\net8.0\Config, not in Config\
- MES batch, Windows service (see "open" above)


==========================================================================
update 2026-10-26 : regression checks and test tools in the repo
==========================================================================

test tools moved into this repo (were next to it, outside git):
  tools\LocalMqttTest   local broker, load test, test-env scripts (was ..\LocalMqttTest)
  tools\MES_EMULATOR    fake MES                                  (was ..\MES_EMULATOR)
  tools\MachineSim      machine simulator + end-to-end check      (was ..\MachineSim)
older notes above still say ..\LocalMqttTest etc. The .bat files in ..\ (Start Gateway,
Stop Gateway, Start Real MES Test, Crash Gateway) point at tools\ now. Their run data
(received.jsonl, run-data.jsonl, outage logs, gateway-run\) stays out of git.

regression checks (tests\EdgeGateway.Checks): one check per GitHub issue fixed, each
failed before its fix. Runs while the gateway runs (own build configuration).
- dotnet run --project tests\EdgeGateway.Checks             all (about 1 min)
- dotnet run --project tests\EdgeGateway.Checks -- 14       one issue
- dotnet run --project tests\EdgeGateway.Checks -- cfg bin\Debug\net8.0\Config
                                         would the gateway load this config folder?
run them before every commit that touches Services\ or Admin\.