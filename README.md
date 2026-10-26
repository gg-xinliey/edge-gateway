# Edge Gateway (TCP)

Machine adapters send one JSON line per message to the gateway over TCP. The gateway:

- relays IO and motion lines to the MQTT broker (live, no reply);
- maps every other line with the machine's tab, saves it to disk (the outbox), answers the adapter with `{"ack":id}`, then sends it to MES;
- sends only changes, and only the columns of the MES tables;
- keeps the data when MES is down and sends it later, in order.

Diagrams (open in a browser): [one line, start to end](docs/gateway-workflow.html) ·
[replies and MES answers](docs/receive-and-send.html) · [config page](docs/config-page-architecture.html).
Full guide (message format, every reply, every setting, troubleshooting): [USE_CASE.md](USE_CASE.md).

## Run it

| What | How |
|---|---|
| Test setup: local broker, fake MES, gateway | `..\Start Gateway.bat` (stop: `..\Stop Gateway.bat`) |
| Real MES test: gateway, broker, AOI adapter | `..\Start Real MES Test.bat` (set the real `mesUrl` first) |
| Gateway only | `dotnet run --project EdgeGateway.csproj` |
| Config page | http://localhost:5100/config/ (on the gateway PC only) |

Machines connect to `listenAddress:tcpPort` in `machine-config.json` (now `192.168.250.78:9000`).
A failed start is retried by itself (5 s, 10 s ... every 60 s).

## Where things are

Everything below is next to the running exe (e.g. `bin\Debug\net8.0\`). The config page edits these
files, not the project's `Config\` folder.

| Path | What |
|---|---|
| `Config\machine-config.json` | gateway id, listen address + port, MES URL, rules for unknown machines and tags (restart to apply) |
| `Config\mqtt-config.json` | broker, area, keepalive (restart to apply) |
| `Config\machines\<machine_id>\` | each machine tab: `point-map.json` (tag to MES field), `timestamp-rules.json` (applied on save) |
| `Config\point-map.json`, `timestamp-rules.json` | the Template, copied into new machine tabs |
| `Config\mes-schema.json` | the MES columns per data group (applied on save) |
| `outbox\<machine_id>\outbox-yyyy-MM-dd.db` | saved records (SQLite); a day file is deleted after `outboxKeepSentDays` once all of it was sent |
| `logs\gateway-yyyy-MM-dd.log` | the log, kept 14 days (also on the config page) |
| `state\message-ids.txt` | message_ids of the last 10 min, kept over a clean stop (Ctrl+C) |

## Replies to the adapter

| Reply | When |
|---|---|
| `{"ack":id}` | saved to disk, or nothing changed. A repeated `message_id` gets the first copy's answer |
| `{"nack":id,"reason":...}` | never accepted as sent: no `machine_id`, or not valid JSON |
| none | no `message_id`, machine has no tab (held), IO / motion line. The adapter sends it again later |

## Project layout

| Folder | What |
|---|---|
| `Services\` | the gateway: TCP in, mapping, outbox, MES sender, MQTT relay |
| `Admin\`, `wwwroot\config\` | the config page and its API |
| `Config\` | default config files (copied next to the exe on build) |
| `docs\` | diagrams |
| `tests\EdgeGateway.Checks\` | regression checks, one per fixed GitHub issue |
| `tools\` | `LocalMqttTest` (local broker, load test, test-env scripts), `MES_EMULATOR` (fake MES), `MachineSim` (machine simulator + end-to-end check) |

## Before you commit

```
dotnet run --project tests\EdgeGateway.Checks                                all checks, about 1 min
dotnet run --project tests\EdgeGateway.Checks -- cfg bin\Debug\net8.0\Config  would this config load?
```

They run while the gateway runs. End-to-end check with MachineSim and the fake MES: see
"Local MES tests" in [USE_CASE.md](USE_CASE.md).

## Known limits

- Runs as a console window, not a Windows service, and its data and config live under `bin\`. Fix before production.
- Port 9000 has no login: keep it on the machine network (firewall rule by IT).
- The real MES API is not confirmed yet (repeat `event_id`, 409, timeout, login). After a gateway crash a row
  can reach MES twice with the same `event_id`; MES must drop the repeat.
- Disk: a day file with a failed row is kept until the row is re-sent or discarded; there is no free-space alarm.
- Adapters on one PC share one worker: about 10,000 lines/s (Debug build).
- NXT alarm event names (`MCALARMON` / `MCALARMOFF`) are fixed in the code.
- Open work: [GitHub issues](https://github.com/svi-automation/edge-gateway/issues).

## Git

- Push to both remotes: `origin` (github.com/gg-xinliey/edge-gateway) and `svi` (github.com/svi-automation/edge-gateway).
- git is not on PATH: `C:\Users\Atm_Theppanadda\Desktop\Claude Cowork\PortableGit\cmd\git.exe`.
- Older notes and test logs: the history of `README.txt` (before 2026-10-26).
