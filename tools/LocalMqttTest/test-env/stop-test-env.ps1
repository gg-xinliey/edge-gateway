# Stops what start-test-env.ps1 and the test senders started:
#   1. the gateway with Ctrl+C, like pressing it in its pane, so it still sends
#      what it holds (sendEveryMs queues) and finishes writing the outbox.
#      If it has not stopped after 15 s, it is stopped hard.
#   2. local MQTT broker, MES emulator, MachineSim, AOI adapter
#   3. their PowerShell panes (an old Windows Terminal window then shows them as exited)
#
#   powershell -ExecutionPolicy Bypass -File stop-test-env.ps1

# Ctrl+C means joining the gateway's console. A child PowerShell does that,
# so this window keeps its own console (and does not get the Ctrl+C itself).
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

foreach ($p in @(Get-Process EdgeGateway -ErrorAction SilentlyContinue)) {
    Write-Host "gateway ($($p.Id)): Ctrl+C, waiting for it to send what it holds..."
    Send-CtrlC $p.Id
    if ($p.WaitForExit(15000)) {
        Write-Host "gateway stopped" -ForegroundColor Green
    }
    else {
        Write-Host "gateway did not stop within 15 s, stopping it hard" -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}

# AoiAdapter: hard stop is safe, its bookmark only moves past rows the gateway answered.
foreach ($p in @(Get-Process LocalMqttTest, MES_EMULATOR, MachineSim, AoiAdapter -ErrorAction SilentlyContinue)) {
    Write-Host "stopping $($p.ProcessName) ($($p.Id))"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

# The panes left at their prompt, and the Source / MachineSim windows (never this script).
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match 'test-env\\(broker|mes|gateway|machines|aoi-adapter)\.ps1|LocalMqttTest\\(source|aoi-feed)\.ps1|MachineSim' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Write-Host "all stopped" -ForegroundColor Green
