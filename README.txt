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
- MqttOptions.MaxPublishRateMs is used as "messages per second" (20 -> 1 per 50 ms),
  the name is misleading
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
   only changes are sent, every change, in order; full snapshot after 60 s silence
3. outbox: one background writer with batch transactions; one file per day
   (outbox\outbox-yyyy-MM-dd.db); a day file older than outboxKeepSentDays is
   deleted only when all its rows were delivered; old outbox.db still read
4. ingest: TCP readers only queue lines; up to 8 worker lanes, one lane per
   connection (each machine in order); each line parsed once
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

open
- MES batch (array in one POST): ~10x faster if MES supports it; ask the MES team
- Machines view on the page, server heartbeat + per-machine online/offline (part D)
- run as a Windows service