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
- no git on this PC: install Git for Windows to version this folder
- live gateway still runs the old build until it is stopped, rebuilt (Release) and started

commit message (for when git is set up)
--------------------------------------------------------------------------
MQTT: SpotWelding payload, fixed topic endings; new config page

- Relay unwraps {"topic","payload"} lines and publishes the inner payload,
  timestamp as "yyyy-MM-dd HH:mm:ss.fff", on the line's own topic.
- Topic must end in IO/input, IO/output, motion/position or motion/status
  (case fixed); other IO/motion endings are dropped with a warning.
  Removes routes/publishTopicTemplate from mqtt-config.json.
- New config page in the mockup layout: PointMap, TimeStamp, MQTTConfig,
  MachineConfig, new groups, unidentified-tag mapping, restart on save.
- Machine settings move to Config/machine-config.json; GatewayHost
  restarts the pipeline in-process while the page stays up.
- Config is validated before it is written; page and runtime share the
  Config folder next to the exe.
- Adds LocalMqttTest offline bench (local broker, ports 19000/15100).
--------------------------------------------------------------------------