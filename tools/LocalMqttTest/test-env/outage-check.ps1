# After outage-test.ps1: did everything the gateway accepted reach MES, exactly once?
# Compares, per machine, the outbox rows written since -Since with what the fake MES
# stored (received.jsonl), by event_id:
#   missing   in the outbox (acked to the adapter) but not at MES
#   unknown   at MES but not in the outbox
#   waiting   outbox rows not sent yet; failed = rejected by MES
# Plus the fake MES's repeats (same event_id sent again; MES keeps one).
# The adapter side (lines sent vs lines acked) comes from the data team's own log.
#
#   powershell -ExecutionPolicy Bypass -File outage-check.ps1 -Since 2026-10-21T15:00:00 [-Machine NAME]
param(
    [Parameter(Mandatory)][datetime]$Since,
    [string]$Machine
)

$mesFile = Join-Path $PSScriptRoot '..\..\MES_EMULATOR\received.jsonl'
$sinceUtc = $Since.ToUniversalTime()
# Data may still be flowing: only records made before the check starts count, on both
# sides. The outbox is read first and MES last, so a record saved just before
# $untilUtc has been delivered by the time MES is read (if not, it shows as waiting).
$untilUtc = (Get-Date).ToUniversalTime()
$day = $Since.ToString('yyyy-MM-dd')
$machines = if ($Machine) { @($Machine) } else { (Invoke-RestMethod http://localhost:5100/api/machine-configs) | ForEach-Object machineId }

# outbox rows since the start, per machine (newest first, 500 per page)
$outbox = @{}
foreach ($m in $machines) {
    $rows = @(); $before = $null
    while ($true) {
        $url = "http://localhost:5100/api/outbox?scope=$m&day=$day&limit=500" + $(if ($before) { "&before=$before" } else { '' })
        try { $page = @((Invoke-RestMethod $url).rows) } catch { break }
        $rows += @($page | Where-Object { $t = [datetimeoffset]::Parse($_.createdAt).UtcDateTime; $t -ge $sinceUtc -and $t -lt $untilUtc })
        if ($page.Count -lt 500 -or [datetimeoffset]::Parse($page[-1].createdAt).UtcDateTime -lt $sinceUtc) { break }
        $before = $page[-1].id
    }
    $inOutbox = @{}; $waiting = 0; $failed = 0
    foreach ($r in $rows) {
        if ($r.failedAt) { $failed++ } elseif (-not $r.sent) { $waiting++ }
        $env = Invoke-RestMethod "http://localhost:5100/api/outbox/row?scope=$m&day=$day&id=$($r.id)"
        $inOutbox[$env.event_id] = $r.group
    }
    $outbox[$m] = @{ ids = $inOutbox; waiting = $waiting; failed = $failed }
}

# fake MES: event_ids per machine, published in the same window
$atMes = @{}
foreach ($l in Get-Content $mesFile) {
    $e = $l | ConvertFrom-Json
    $t = [datetimeoffset]::Parse($e.publish_timestamp).UtcDateTime
    if ($t -lt $sinceUtc -or $t -ge $untilUtc) { continue }
    if (-not $atMes[$e.machine_id]) { $atMes[$e.machine_id] = @{} }
    $atMes[$e.machine_id][$e.event_id] = $e.data_group
}
$stats = (Invoke-RestMethod http://localhost:5001/stats).machines

$allGood = $true
foreach ($m in $machines) {
    $inOutbox = $outbox[$m].ids; $waiting = $outbox[$m].waiting; $failed = $outbox[$m].failed
    if (-not $inOutbox.Count -and -not $atMes[$m]) { continue }
    $mesIds = if ($atMes[$m]) { $atMes[$m] } else { @{} }
    $missing = @($inOutbox.Keys | Where-Object { -not $mesIds.ContainsKey($_) })
    $unknown = @($mesIds.Keys | Where-Object { -not $inOutbox.ContainsKey($_) })
    $repeats = 0; if ($stats.$m) { foreach ($g in $stats.$m.PSObject.Properties) { $repeats += $g.Value.repeats } }

    $ok = $missing.Count -eq 0 -and $unknown.Count -eq 0 -and $waiting -eq 0 -and $failed -eq 0
    if (-not $ok) { $allGood = $false }
    "{0}  {1}: outbox {2} rows, at MES {3}, missing {4}, unknown {5}, waiting {6}, failed {7}, MES repeats {8} (all time)" -f `
        $(if ($ok) { 'OK  ' } else { 'FAIL' }), $m, $inOutbox.Count, $mesIds.Count, $missing.Count, $unknown.Count, $waiting, $failed, $repeats
    $missing | Select-Object -First 5 | ForEach-Object { "      missing at MES: $_ ($($inOutbox[$_]))" }
}

$traffic = Invoke-RestMethod http://localhost:5100/api/machines
""
"gateway counts since its last start (compare 'new messages' with what the adapter says it sent):"
foreach ($x in $traffic.machines) { if (-not $Machine -or $x.machineId -eq $Machine) { "  {0}: new messages {1}, to MES {2}, repeats {3}, held now {4}" -f $x.machineId, $x.total, $x.mes, $x.duplicates, $x.held } }
""
$window = "$Since to $($untilUtc.ToLocalTime().ToString('HH:mm:ss'))"
if ($allGood) { "PASS: every record the gateway saved from $window reached MES exactly once." } else { "FAIL ($window): see above." }
