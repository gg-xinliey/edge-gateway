# Feeds source.ps1 (the adapter) with NXT-style lines from one machine, one every
# -EverySeconds, by appending them to outgoing.txt. Cycles through:
#   PRODSTARTED, PRODCOMPLETED (+ panel_status), MCSTATECHANGE, MCALARMON, MCALARMOFF
# sequence_id / source_ref / panel_no count up, so every line carries new values.
# Timestamps in UTC with Z, like the data team's adapter.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File new-machine-feed.ps1 [-MachineId LINE02_NXT201] [-EverySeconds 4] [-Minutes 30]
param(
    [string]$MachineId = 'LINE02_NXT201',
    [double]$EverySeconds = 4,
    [double]$Minutes = 30
)

$queue = Join-Path $PSScriptRoot 'outgoing.txt'
$end = (Get-Date).AddMinutes($Minutes)
$i = 0
$status = @('RUN', 'IDLE')

while ((Get-Date) -lt $end) {
    $now = (Get-Date).ToUniversalTime()
    $ts = $now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    $compact = $now.ToString('yyyyMMddHHmmss')
    $n = [int][math]::Floor($i / 5)
    $seq = 200 + $n
    $ref = 5000 + $i
    $base = [ordered]@{ event_time = $compact; line_name = 'LINE02'; module_no = '1'; sequence_id = "$seq"; source_ref = "$ref" }

    switch ($i % 5) {
        0 { $source = 'PRODSTARTED';   $data = $base + [ordered]@{ event_name = $source; lane_no = '1'; product_mode = '0'; program = 'JOB_A_R1'; panel_no = "$($n + 1)" } }
        1 { $source = 'PRODCOMPLETED'; $data = $base + [ordered]@{ event_name = $source; lane_no = '1'; product_mode = '0'; program = 'JOB_A_R1'; panel_no = "$($n + 1)"; panel_status = 'OK' } }
        2 { $source = 'MCSTATECHANGE'; $data = $base + [ordered]@{ event_name = $source; current_status_code = $status[$n % 2]; previous_status_code = $status[($n + 1) % 2] } }
        3 { $source = 'MCALARMON';     $data = $base + [ordered]@{ event_name = $source; alarm_code = ('{0:X8}' -f (10 + $n)); sub_alarm_code = '00000001' } }
        4 { $source = 'MCALARMOFF';    $data = $base + [ordered]@{ event_name = $source; alarm_code = ('{0:X8}' -f (10 + $n)); sub_alarm_code = '00000001' } }
    }

    $line = [ordered]@{ message_id = [guid]::NewGuid().ToString(); machine_id = $MachineId; timestamp = $ts; data_source = $source; data = $data } |
        ConvertTo-Json -Compress -Depth 4
    [System.IO.File]::AppendAllText($queue, $line + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $i++
    Start-Sleep -Milliseconds ([int]($EverySeconds * 1000))
}
