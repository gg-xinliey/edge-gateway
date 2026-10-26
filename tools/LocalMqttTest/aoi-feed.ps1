# Plays the AOI program for tests: appends one result row every -EverySeconds to a
# weekly AOI file (12 tab-separated columns, CRLF, like P'Somporn's program), so
# AoiAdapter has new rows to send. Alternates AOI TOP / AOI BOTTOM; serials count up
# (T-0001 ...), results mostly P. Writes with the file shared, like the real program.
#
#   powershell -ExecutionPolicy Bypass -File aoi-feed.ps1 [-File <week file>] [-EverySeconds 5] [-Minutes 30]
param(
    [string]$File = (Get-ChildItem "$PSScriptRoot\..\..\..\test_data\aoi-share\Line1\BackUp" -Filter 'Line*_Y*W*.TXT' | Sort-Object Name | Select-Object -Last 1).FullName,
    [double]$EverySeconds = 5,
    [double]$Minutes = 30
)

$end = (Get-Date).AddMinutes($Minutes)
$i = 0
Write-Host "appending to $File every $EverySeconds s until $($end.ToString('HH:mm'))"
while ((Get-Date) -lt $end) {
    $side = if ($i % 2 -eq 0) { 'AOI TOP' } else { 'AOI BOTTOM' }
    $serial = 'T-{0:D4}' -f ([int][math]::Floor($i / 2) + 1)
    $result = if ($i % 7 -eq 6) { 'F' } else { 'P' }
    $row = @('Line1', 'BIP', 'BIP01E-221192A1LF', $serial, (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $result, '', '', '458066', '', '44406', $side) -join "`t"
    $fs = [System.IO.File]::Open($File, 'Append', 'Write', 'ReadWrite')
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($row + "`r`n")
    $fs.Write($bytes, 0, $bytes.Length); $fs.Close()
    $i++
    Start-Sleep -Milliseconds ([int]($EverySeconds * 1000))
}
Write-Host "done: $i rows appended"
