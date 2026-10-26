# Test data source: acts as ONE machine with ONE TCP connection to the gateway,
# and behaves like an adapter: every MES line with a message_id is kept until
# the gateway replies {"ack":"<id>"} (saved) or {"nack":"<id>",...} (never
# accepted). After a reconnect, every line still without a reply is sent again.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File source.ps1 [-GatewayHost 127.0.0.1] [-Port 9000]
#
# It sends every new line appended to outgoing.txt (next to this script):
#   {now}        in a line is replaced with the current time, yyyy-MM-ddTHH:mm:ss.fff
#   {uuid}       in a line is replaced with a new id (e.g. "message_id":"{uuid}")
#   #reconnect   closes and reopens the connection (then resends the unanswered lines)
#   #resend      sends every unanswered line again now
#   #pending     lists the unanswered message_ids
# Each line is printed when sent, each reply when it arrives, and the same
# lines go to source-log.txt next to this script (for checking a test). Ctrl+C to stop.
param(
    [string]$GatewayHost = '127.0.0.1',
    [int]$Port = 9000
)

$queue = Join-Path $PSScriptRoot 'outgoing.txt'
$log = Join-Path $PSScriptRoot 'source-log.txt'
[System.IO.File]::WriteAllText($queue, '')   # start empty (no BOM)
[System.IO.File]::WriteAllText($log, '')

function Write-Host {
    param([Parameter(Position = 0)][string]$Object, [string]$ForegroundColor = 'Gray')
    Microsoft.PowerShell.Utility\Write-Host $Object -ForegroundColor $ForegroundColor
    [System.IO.File]::AppendAllText($script:log, $Object + "`n")
}
$sentLines = 0
$client = $null
$writer = $null
$replyText = ''
$pending = New-Object System.Collections.Specialized.OrderedDictionary   # message_id -> line, until replied

function Open-Connection {
    if ($script:writer) { try { $script:writer.Dispose() } catch {} }
    if ($script:client) { try { $script:client.Dispose() } catch {} }
    $script:replyText = ''
    while ($true) {
        try {
            $script:client = New-Object System.Net.Sockets.TcpClient
            $script:client.Connect($GatewayHost, $Port)
            $script:writer = New-Object System.IO.StreamWriter($script:client.GetStream(), (New-Object System.Text.UTF8Encoding($false)))
            $script:writer.AutoFlush = $true
            $script:writer.NewLine = "`n"
            Write-Host ("connected to {0}:{1} from {2}" -f $GatewayHost, $Port, $script:client.Client.LocalEndPoint) -ForegroundColor Green
            break
        }
        catch {
            Write-Host ("cannot connect to {0}:{1} ({2}), retrying in 2 s" -f $GatewayHost, $Port, $_.Exception.Message) -ForegroundColor Red
            Start-Sleep -Seconds 2
        }
    }
    Send-Pending 'after reconnect'
}

# Writes one line; a MES line with a message_id is kept until its reply.
function Send-Line([string]$line, [string]$note = '') {
    $script:writer.WriteLine($line)
    Write-Host ("{0}  sent{1}: {2}" -f (Get-Date).ToString('HH:mm:ss.fff'), $note, $line) -ForegroundColor Yellow
    if ($line -notmatch '"topic"\s*:' -and $line -match '"message_id"\s*:\s*"([^"]+)"') {
        $script:pending[$Matches[1]] = $line
    }
}

function Send-Pending([string]$why) {
    if ($script:pending.Count -eq 0) { return }
    Write-Host ("{0} line(s) without a reply, sending again ({1})" -f $script:pending.Count, $why) -ForegroundColor Magenta
    foreach ($line in @($script:pending.Values)) { Send-Line $line ' again' }
}

# Prints the gateway's replies and forgets the answered lines. Returns $false
# when the gateway closed the connection.
function Read-Replies {
    $socket = $script:client.Client
    if ($socket.Poll(0, [System.Net.Sockets.SelectMode]::SelectRead) -and $script:client.Available -eq 0) { return $false }
    $stream = $script:client.GetStream()
    $buffer = New-Object byte[] 65536
    while ($script:client.Available -gt 0) {
        $n = $stream.Read($buffer, 0, $buffer.Length)
        if ($n -le 0) { return $false }
        $script:replyText += [System.Text.Encoding]::UTF8.GetString($buffer, 0, $n)
    }
    while (($i = $script:replyText.IndexOf("`n")) -ge 0) {
        $reply = $script:replyText.Substring(0, $i).Trim()
        $script:replyText = $script:replyText.Substring($i + 1)
        $color = 'Cyan'
        if ($reply -match '"(ack|nack)"\s*:\s*"([^"]+)"') {
            $script:pending.Remove($Matches[2])
            if ($Matches[1] -eq 'nack') { $color = 'Red' }
        }
        Write-Host ("{0}  reply: {1}   (waiting for {2})" -f (Get-Date).ToString('HH:mm:ss.fff'), $reply, $script:pending.Count) -ForegroundColor $color
    }
    return $true
}

Write-Host "data source: one machine connection. Waiting for lines in $queue" -ForegroundColor Cyan
Open-Connection

while ($true) {
    if (-not (Read-Replies)) {
        Write-Host 'connection closed by the gateway, reconnecting' -ForegroundColor Red
        Open-Connection
    }

    # Read without locking the file (ReadAllText blocks writers: lines got lost).
    $stream = New-Object System.IO.FileStream($queue, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { $text = (New-Object System.IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }

    # Only lines that end with a newline are complete (one may be half written).
    $parts = $text -split "`n"
    $lines = @($parts | Select-Object -First ($parts.Count - 1))
    if ($lines.Count -lt $sentLines) { $sentLines = 0 }   # file was emptied

    for ($i = $sentLines; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line -eq '') { continue }
        if ($line -eq '#reconnect') { Open-Connection; continue }
        if ($line -eq '#resend') { Send-Pending 'asked'; continue }
        if ($line -eq '#pending') { Write-Host ("waiting for a reply: {0}" -f (@($pending.Keys) -join ', ')) -ForegroundColor Magenta; continue }

        $line = $line.Replace('{now}', (Get-Date).ToString("yyyy-MM-dd'T'HH:mm:ss.fff")).Replace('{uuid}', [guid]::NewGuid().ToString())
        try {
            Send-Line $line
        }
        catch {
            Write-Host ("send failed ({0}), reconnecting" -f $_.Exception.Message) -ForegroundColor Red
            Open-Connection   # resends what is unanswered
            if (-not ($line -match '"message_id"\s*:\s*"([^"]+)"' -and $pending.Contains($Matches[1]))) { Send-Line $line }
        }
    }

    $sentLines = $lines.Count
    Start-Sleep -Milliseconds 200
}
