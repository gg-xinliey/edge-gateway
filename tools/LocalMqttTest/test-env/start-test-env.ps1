# Opens the manual test setup as one Windows Terminal window, and the config
# page in the browser. Build first (dotnet build) if code changed; the Gateway
# pane builds the gateway itself.
#
#   powershell -ExecutionPolicy Bypass -File start-test-env.ps1             4 panes
#   powershell -ExecutionPolicy Bypass -File start-test-env.ps1 -NoSource   3 panes (remote devices send)
#
#   +----------------+----------------+        +----------------+----------------+
#   | MQTT broker    | MES emulator   |        | MQTT broker    | MES emulator   |
#   +----------------+----------------+        +----------------+----------------+
#   | Gateway        | Source         |        | Gateway                         |
#   +----------------+----------------+        +---------------------------------+
param([switch]$NoSource)

$dir = $PSScriptRoot
$pane = 'powershell -NoExit -NoProfile -ExecutionPolicy Bypass -File'

if ($NoSource) {
    $wtArgs = @(
        "-w new -M --title Broker $pane `"$dir\broker.ps1`"",
        "split-pane -H --title Gateway $pane `"$dir\gateway.ps1`"",
        "move-focus up",
        "split-pane -V --title MES $pane `"$dir\mes.ps1`""
    ) -join ' ; '
}
else {
    $wtArgs = @(
        "-w new -M --title Broker $pane `"$dir\broker.ps1`"",
        "split-pane -V --title MES $pane `"$dir\mes.ps1`"",
        "move-focus left",
        "split-pane -H --title Gateway $pane `"$dir\gateway.ps1`"",
        "move-focus right",
        "split-pane -H --title Source $pane `"$dir\..\source.ps1`""
    ) -join ' ; '
}

Start-Process wt -ArgumentList $wtArgs

Start-Sleep -Seconds 12   # the Gateway pane builds before it starts
Start-Process 'http://localhost:5100/config/'
