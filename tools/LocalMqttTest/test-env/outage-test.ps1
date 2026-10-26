# Outage test with a live sender (the data team's adapter):
#   1. baseline         -BaselineMinutes, nothing changed
#   2. MES down         the fake MES refuses connections for -MesDownMinutes
#                       (the gateway keeps acking, rows wait in the outbox), then up
#                       again; waits until the backlog is 0
#   3. gateway down     the gateway is stopped with Ctrl+C for -GatewayDownMinutes
#                       (-Kill: stopped hard, like a crash or power cut), then started
#                       again; the adapter must keep its lines and send them again
#   4. settle           -SettleMinutes so the last lines get through
# Every step and a snapshot (backlog, machines, connections) goes to
# outage-<time>.txt next to this script. Afterwards: outage-check.ps1 -Since <start>.
#
#   powershell -ExecutionPolicy Bypass -File outage-test.ps1 [-MesDownMinutes 3] [-GatewayDownMinutes 3] [-Kill] [-SkipMes] [-SkipGateway]
param(
    [double]$BaselineMinutes = 2,
    [double]$MesDownMinutes = 3,
    [double]$GatewayDownMinutes = 3,
    [double]$SettleMinutes = 2,
    [switch]$Kill,
    [switch]$SkipMes,
    [switch]$SkipGateway
)

$gatewayPs1 = Join-Path $PSScriptRoot 'gateway.ps1'
$out = Join-Path $PSScriptRoot ("outage-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))
function Log([string]$text) { $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $text; Write-Host $line; Add-Content -Path $out -Value $line -Encoding UTF8 }

function Snapshot([string]$label) {
    try {
        $s = Invoke-RestMethod http://localhost:5100/api/status -TimeoutSec 3
        $m = Invoke-RestMethod http://localhost:5100/api/machines -TimeoutSec 3
        Log "[$label] gateway $($s.state), backlog $($s.stats.backlog), held $($s.held), unmapped $($s.unmapped), failed $($s.failed)"
        foreach ($x in $m.machines) { Log ("[$label]   {0}: {1}, new messages {2}, to MES {3}, repeats {4}, held {5}" -f $x.machineId, $x.status, $x.total, $x.mes, $x.duplicates, $x.held) }
        foreach ($c in ($m.connections | Where-Object open)) { Log "[$label]   open connection $($c.endpoint): lines $($c.lines), machines $($c.machines -join ',')" }
    }
    catch { Log "[$label] gateway not answering" }
}

function Wait-Minutes([double]$minutes, [string]$label) {
    $end = (Get-Date).AddMinutes($minutes)
    while ((Get-Date) -lt $end) { Start-Sleep -Seconds ([Math]::Min(30, [Math]::Max(1, ($end - (Get-Date)).TotalSeconds))); Snapshot $label }
}

function Wait-Backlog-Zero([int]$maxMinutes) {
    $start = Get-Date
    while (((Get-Date) - $start).TotalMinutes -lt $maxMinutes) {
        try { $b = (Invoke-RestMethod http://localhost:5100/api/status -TimeoutSec 3).stats.backlog; if ($b -eq 0) { Log "backlog 0 after $([int]((Get-Date) - $start).TotalSeconds) s"; return } } catch {}
        Start-Sleep -Seconds 5
    }
    Log "backlog still not 0 after $maxMinutes min"
}

# Ctrl+C to the gateway's console (same as stop-test-env.ps1), so it finishes writing its outbox.
function Send-CtrlC([int]$ProcessId) {
    $script = @"
`$ProgressPreference = 'SilentlyContinue'
Add-Type -Namespace Native -Name Con -MemberDefinition '
[DllImport("kernel32.dll")] public static extern bool FreeConsole();
[DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
[DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(System.IntPtr handler, bool add);
[DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint groupId);'
[Native.Con]::FreeConsole() | Out-Null
if ([Native.Con]::AttachConsole($ProcessId)) {
    [Native.Con]::SetConsoleCtrlHandler([IntPtr]::Zero, `$true) | Out-Null
    [Native.Con]::GenerateConsoleCtrlEvent(0, 0) | Out-Null
    Start-Sleep -Milliseconds 500
}
"@
    powershell -NoProfile -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script)))
}

$started = Get-Date
Log "outage test started; since for outage-check.ps1: $($started.ToString('yyyy-MM-ddTHH:mm:ss'))"
Snapshot 'start'
Log "1. baseline, $BaselineMinutes min"
Wait-Minutes $BaselineMinutes 'baseline'

if (-not $SkipMes) {
    Log "2. MES down (connection refused) for $MesDownMinutes min"
    Invoke-RestMethod "http://localhost:5001/mode?set=unreachable" | Out-Null
    Wait-Minutes $MesDownMinutes 'MES down'
    Invoke-RestMethod "http://localhost:5001/mode?set=up" | Out-Null
    Log "2. MES up again, waiting for the backlog to drain"
    Wait-Backlog-Zero 5
    Snapshot 'MES back'
}

if (-not $SkipGateway) {
    $gw = Get-Process EdgeGateway -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $gw) { Log "3. no gateway running, skipped" }
    else {
        if ($Kill) { Log "3. gateway stopped HARD (pid $($gw.Id)) for $GatewayDownMinutes min"; Stop-Process -Id $gw.Id -Force }
        else {
            Log "3. gateway Ctrl+C (pid $($gw.Id)) for $GatewayDownMinutes min"; Send-CtrlC $gw.Id
            if (-not $gw.WaitForExit(15000)) { Log "   did not stop in 15 s, stopped hard"; Stop-Process -Id $gw.Id -Force }
        }
        Wait-Minutes $GatewayDownMinutes 'gateway down'
        Log "3. starting the gateway again (its own window)"
        Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gatewayPs1
        $deadline = (Get-Date).AddMinutes(2)
        while ((Get-Date) -lt $deadline) { try { if ((Invoke-RestMethod http://localhost:5100/api/status -TimeoutSec 3).state -eq 'running') { break } } catch {}; Start-Sleep 2 }
        Log "3. gateway running again"
        Snapshot 'gateway back'
    }
}

Log "4. settle, $SettleMinutes min"
Wait-Minutes $SettleMinutes 'settle'
Wait-Backlog-Zero 5
Log "done. Check: powershell -File outage-check.ps1 -Since $($started.ToString('yyyy-MM-ddTHH:mm:ss'))"
