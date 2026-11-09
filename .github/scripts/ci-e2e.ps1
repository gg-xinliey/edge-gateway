# End-to-end test of the gateway, all in one PowerShell process (CI's e2e job, or by hand):
#   a real gateway and the fake MES on 127.0.0.1, MachineSim sends 2 machines' data for 1 min,
#   then every record the fake MES stored must match the line a machine sent (MachineSim --check).
# The post-deploy check script also runs against this gateway (MQTT skipped: no broker here).
#
# One process on purpose: on Windows the Actions runner ends every process a step started when
# that step ends, so the gateway and fake MES must live in the same step as the tests.
#
# Build first (Release): EdgeGateway.csproj, tools/MES_EMULATOR, tools/MachineSim. Then, from anywhere:
#   powershell -ExecutionPolicy Bypass -File .github/scripts/ci-e2e.ps1 [-Root <folder>] [-MachineId CI-E2E]
# Uses ports 127.0.0.1:19000 (machines), 15100 (config page), 15000/15001 (fake MES), not the
# usual 5000/5001/9000/5100: the PC may run the gateway test setup or Docker next to it.
# Works in Windows PowerShell 5.1 and PowerShell 7. Exit code 1 if anything fails.
param(
    [string]$Root = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'e2e' } else { Join-Path ([IO.Path]::GetTempPath()) 'edge-gateway-e2e' }),
    [int]$TcpPort = 19000,
    [int]$AdminPort = 15100,
    [int]$MesPort = 15000,
    [int]$MesControlPort = 15001,
    [string]$MachineId = 'CI-E2E',
    [double]$Minutes = 1
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '../..')   # the repo root
$mes = $null; $gw = $null; $code = 0

function Wait-Url([string]$url, [string]$what) {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5 | Out-Null; Write-Host "up: $what ($url)"; return }
        catch {
            Assert-Running
            if ((Get-Date) -gt $deadline) { throw "$what not answering after 60 s ($url)" }
            Start-Sleep -Seconds 2
        }
    }
}

# Stop with the end of a crashed process's error log, so a crash is named as such.
function Assert-Running {
    foreach ($p in @(@{ P = $mes; Name = 'fake MES'; Log = 'mes' }, @{ P = $gw; Name = 'gateway'; Log = 'gateway' })) {
        if ($p.P -and $p.P.HasExited) {
            $err = Get-Content "$Root/$($p.Log).err.log" -Tail 5 -ErrorAction SilentlyContinue
            throw "$($p.Name) exited (code $($p.P.ExitCode)): $($err -join ' ')"
        }
    }
}

try {
    # 0. the ports must be free: else an answer could come from another program on this PC
    foreach ($port in $TcpPort, $AdminPort, $MesPort, $MesControlPort) {
        $client = New-Object System.Net.Sockets.TcpClient
        try { $client.Connect('127.0.0.1', $port); $inUse = $true } catch { $inUse = $false } finally { $client.Close() }
        if ($inUse) { throw "port $port is already in use on this PC (stop what uses it, or pick other ports)" }
    }

    # 1. data folders: the repo's Config, but on loopback and with the fake MES. Machines without a
    #    tab get one from the Template (auto tab), the mapping MachineSim --check expects.
    Write-Host "== data folder $Root"
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    $config = New-Item -ItemType Directory -Force (Join-Path $Root 'gw/Config')
    New-Item -ItemType Directory -Force (Join-Path $Root 'mes') | Out-Null
    Copy-Item Config/*.json $config
    $file = Join-Path $config 'machine-config.json'
    $c = Get-Content $file -Raw | ConvertFrom-Json
    $c.listenAddress = '127.0.0.1'
    $c.tcpPort = $TcpPort
    $c.mesUrl = "http://localhost:$MesPort/api/v1/machine-events"
    [IO.File]::WriteAllText($file, ($c | ConvertTo-Json -Depth 10))

    # 2. fake MES and gateway in the background, logs in $Root
    Write-Host "== start fake MES and gateway"
    $mes = Start-Process dotnet -PassThru -NoNewWindow `
        -ArgumentList 'tools/MES_EMULATOR/bin/Release/net10.0/MES_EMULATOR.dll', '--data', "`"$Root/mes`"", '--schema', 'Config/mes-schema.json', '--port', $MesPort, '--control-port', $MesControlPort `
        -RedirectStandardOutput "$Root/mes.out.log" -RedirectStandardError "$Root/mes.err.log"
    $gw = Start-Process dotnet -PassThru -NoNewWindow `
        -ArgumentList 'bin/Release/net8.0/EdgeGateway.dll', '--data', "`"$Root/gw`"", '--admin-port', $AdminPort `
        -RedirectStandardOutput "$Root/gateway.out.log" -RedirectStandardError "$Root/gateway.err.log"
    Wait-Url "http://localhost:$MesControlPort/stats" 'fake MES'
    Wait-Url "http://localhost:$AdminPort/api/status" 'gateway config page'
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Select-String -Path "$Root/gateway.out.log" -SimpleMatch "listening on 127.0.0.1:$TcpPort" -Quiet)) {
        if ($gw.HasExited) { throw "gateway exited (see $Root/gateway.out.log)" }
        if ((Get-Date) -gt $deadline) { throw "gateway is not listening on 127.0.0.1:$TcpPort" }
        Start-Sleep -Seconds 2
    }
    Write-Host "up: gateway TCP 127.0.0.1:$TcpPort"

    # 3. the deploy's read-only check, against this gateway (in this process: its exit code
    #    comes back in $LASTEXITCODE)
    Write-Host "== post-deploy check script"
    & ./.github/scripts/post-deploy-check.ps1 -BaseUrl "http://localhost:$AdminPort" -WaitSeconds 30 -SkipMqtt
    if ($LASTEXITCODE -ne 0) { throw 'post-deploy check failed' }

    # 4. machines send MES data (no IO / motion: no MQTT broker here)
    Write-Host "== MachineSim: 2 machines, $Minutes min"
    dotnet tools/MachineSim/bin/Release/net10.0/MachineSim.dll `
        --machines 2 --id $MachineId --minutes $Minutes `
        --data-s 0.25 --motion-ms 0 --io-s 0 --port $TcpPort --drain-s 60
    if ($LASTEXITCODE -ne 0) { throw 'MachineSim failed' }
    Assert-Running

    # 5. every MES record matches its source line. Some groups are sent at most every 10 s
    #    (sendEveryMs), so MES can lag the acks a little.
    Write-Host '== MES records vs. machine lines'
    $passed = $false
    for ($try = 1; $try -le 6 -and -not $passed; $try++) {
        dotnet tools/MachineSim/bin/Release/net10.0/MachineSim.dll --check `
            --run tools/MachineSim/run-data.jsonl --mes "$Root/mes/received.jsonl"
        if ($LASTEXITCODE -eq 0) { $passed = $true; continue }
        Assert-Running   # a crash is not worth waiting for
        if ($try -lt 6) { Write-Host "--- not yet (try $try of 6), again in 10 s"; Start-Sleep -Seconds 10 }
    }
    if (-not $passed) { throw 'end-to-end check failed: MES records do not match the machine lines' }
    Write-Host 'end-to-end: PASS'
}
catch {
    $code = 1
    Write-Host "end-to-end FAILED: $($_.Exception.Message)"
    if ($env:GITHUB_ACTIONS) { Write-Host "::error title=End-to-end::$($_.Exception.Message)" }
}
finally {
    foreach ($p in $gw, $mes) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}
exit $code
