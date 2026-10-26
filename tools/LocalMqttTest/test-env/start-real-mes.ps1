# Opens the real-MES test setup as one Windows Terminal window (no fake MES):
#
#   +----------------------+----------------------+
#   | Gateway              | MQTT broker (local)  |
#   +----------------------+----------------------+
#   | AOI data creator     | AOI adapter          |
#   +----------------------+----------------------+
#
# The gateway sends to the mesUrl in its GatewayConfig (Config\machine-config.json next to
# the exe); this stops first if that is still the fake MES (localhost:5000).
#   powershell -ExecutionPolicy Bypass -File start-real-mes.ps1 [-AoiEverySeconds 10] [-AoiMinutes 240]
param(
    [double]$AoiEverySeconds = 10,
    [double]$AoiMinutes = 240
)

$dir = $PSScriptRoot
$config = Join-Path $dir '..\..\..\bin\Debug\net8.0\Config\machine-config.json'
if (-not (Test-Path $config)) { $config = Join-Path $dir '..\..\..\Config\machine-config.json' }
$mesUrl = (Get-Content $config -Raw | ConvertFrom-Json).mesUrl
Write-Host "gateway sends MES data to: $mesUrl"
if ($mesUrl -match 'localhost:5000|127\.0\.0\.1:5000') {
    Write-Host 'That is the FAKE MES. Set the real MES URL first: config page -> General -> GatewayConfig -> mesUrl, save.' -ForegroundColor Red
    Write-Host '(Or edit mesUrl in' (Resolve-Path $config) ')' -ForegroundColor Red
    exit 1
}

$pane = 'powershell -NoExit -NoProfile -ExecutionPolicy Bypass -File'
$feed = "$pane `"$dir\..\aoi-feed.ps1`" -EverySeconds $AoiEverySeconds -Minutes $AoiMinutes"
$wtArgs = @(
    "-w new -M --title Gateway $pane `"$dir\gateway.ps1`"",
    "split-pane -V --title Broker $pane `"$dir\broker.ps1`"",
    "split-pane -H --title `"AOI adapter`" $pane `"$dir\aoi-adapter.ps1`"",
    "move-focus left",
    "split-pane -H --title `"AOI data creator`" $feed"
) -join ' ; '
Start-Process wt -ArgumentList $wtArgs

Start-Sleep -Seconds 15   # the Gateway pane builds before it starts
Start-Process 'http://localhost:5100/config/'
