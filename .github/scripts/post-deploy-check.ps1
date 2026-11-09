# Read-only check of a running gateway, through its own config page API (after a deploy).
# Saves nothing, restarts nothing and sends no records:
#   1. the gateway is running                  GET  /api/status            (fails on "error")
#   2. the MQTT broker is connected            GET  /api/status            (mqttConnected)
#   3. every MES address answers               POST /api/mes/test          (the page's Test button:
#                                                   one GET to MES, no data; secrets stay in the gateway)
#   4. warnings only: held machines, failed messages, outbox backlog with nothing delivered
#
#   powershell -File .github\scripts\post-deploy-check.ps1 [-BaseUrl http://localhost:5100] [-WaitSeconds 90] [-SkipMqtt]
# Works in Windows PowerShell 5.1 and PowerShell 7. Exit code 1 if a check fails.
param(
    [string]$BaseUrl = 'http://localhost:5100',
    [int]$WaitSeconds = 90,
    [switch]$SkipMqtt
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]

function Get-Api([string]$path) {
    Invoke-RestMethod -Uri "$BaseUrl$path" -UseBasicParsing -TimeoutSec 20
}
function Note([string]$ok, [string]$name, [string]$detail) {
    Write-Host ("{0,-5} {1}: {2}" -f $ok, $name, $detail)
    $lines.Add("| $ok | $name | $($detail -replace '\|', '/') |")
}

# 1 + 2: running, and (unless skipped) MQTT connected. Both can take a few seconds after a start.
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ($true) {
    $status = $null
    try { $status = Get-Api '/api/status' } catch { }
    $running = $status -and $status.state -eq 'running'
    $mqttOk = $SkipMqtt -or ($status -and $status.mqttConnected -eq $true)
    if (($running -and $mqttOk) -or ($status -and $status.state -eq 'error') -or (Get-Date) -gt $deadline) { break }
    Start-Sleep -Seconds 3
}

if (-not $status) {
    Note 'FAIL' 'gateway' "no answer from $BaseUrl/api/status"
    $failures.Add('gateway')
} else {
    if ($running) {
        Note 'PASS' 'gateway' "running (gateway $($status.gatewayId), TCP port $($status.tcpPort), since $($status.startedAt))"
    } else {
        Note 'FAIL' 'gateway' "state '$($status.state)': $($status.lastError)"
        $failures.Add('gateway')
    }

    if ($SkipMqtt) { Note 'SKIP' 'MQTT broker' 'not checked (-SkipMqtt)' }
    elseif ($status.mqttConnected -eq $true) { Note 'PASS' 'MQTT broker' 'connected' }
    else {
        Note 'FAIL' 'MQTT broker' "not connected after $WaitSeconds s (broker address on the config page, MQTT tab)"
        $failures.Add('mqtt')
    }

    # 4: warnings, not failures (they can be older than this deploy)
    if ($status.held -gt 0) { $warnings.Add("$($status.held) machine(s) sending without a tab: their data is held (config page, Machines)") }
    if ($status.unmapped -gt 0) { $warnings.Add("$($status.unmapped) machine(s) held for tags not mapped yet") }
    if ($status.failed -gt 0) { $warnings.Add("$($status.failed) message(s) MES rejected, waiting for re-send or discard (config page, Failed)") }
    $s = $status.stats
    if ($s -and $s.backlog -gt 0 -and $s.mesDeliveredPerSec -eq 0) {
        $warnings.Add("outbox backlog $($s.backlog) row(s), nothing delivered to MES in the last 10 s")
    }
}

# 3: every MES address: the default one (if set) and each data group's own.
$settings = $null
if ($status) {
    try { $settings = Get-Api '/api/config/machine-config' }
    catch {
        Note 'FAIL' 'MES' "could not read the MES settings: $($_.Exception.Message)"
        $failures.Add('mes')
    }
}
if ($settings) {
    $targets = New-Object System.Collections.Generic.List[string]
    if ("$($settings.mesUrl)".Trim() -ne '') { $targets.Add('') }
    if ($settings.mesUrls) {
        foreach ($p in $settings.mesUrls.PSObject.Properties) {
            if ("$($p.Value)".Trim() -ne '') { $targets.Add($p.Name) }
        }
    }
    if ($targets.Count -eq 0) {
        Note 'FAIL' 'MES' 'no MES address set: records wait in the outbox'
        $failures.Add('mes')
    }
    foreach ($group in $targets) {
        $name = if ($group -eq '') { 'MES (default)' } else { "MES $group" }
        $body = @{ group = $group; settings = $settings } | ConvertTo-Json -Depth 20 -Compress
        try {
            $r = Invoke-RestMethod -Uri "$BaseUrl/api/mes/test" -Method Post -ContentType 'application/json; charset=utf-8' `
                -Body ([Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing -TimeoutSec 30
            if ($r.ok) { Note 'PASS' $name "$($r.url): $($r.text)" }
            else { Note 'FAIL' $name "$($r.url): $($r.text)"; $failures.Add($name) }
        } catch {
            Note 'FAIL' $name "test request failed: $($_.Exception.Message)"
            $failures.Add($name)
        }
    }
}

foreach ($w in $warnings) {
    Write-Host "WARN  $w"
    $lines.Add("| WARN | | $($w -replace '\|', '/') |")
    if ($env:GITHUB_ACTIONS) { Write-Host "::warning title=Post-deploy check::$w" }
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @('### Post-deploy check', '', '| | Check | Result |', '|---|---|---|') + $lines
    $summary | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

if ($failures.Count -gt 0) {
    Write-Host "post-deploy check FAILED: $($failures -join ', ')"
    exit 1
}
Write-Host 'post-deploy check passed'
exit 0
