# Installs the gateway as the Windows service "EdgeGateway". Run once, as administrator,
# after publish.ps1. It:
#   1. copies the published gateway to -AppDir (the program; replaced on every update)
#   2. makes the data folder -DataDir: Config\, outbox\, logs\, state\ (never replaced or removed).
#      -CopyDataFrom <folder of the old exe, e.g. ...\bin\Debug\net8.0> takes over its Config\
#      (machine tabs too), outbox\ (unsent rows) and state\. Only into an empty data folder, and
#      only while no gateway runs, so the outbox is copied complete. Without it: the default Config.
#   3. creates the service: starts with Windows (delayed, after the network), runs as its own
#      account NT SERVICE\EdgeGateway, restarts after 5 s, 10 s, then 60 s when it fails
#   4. gives that account the data folder (modify) and the config page address
#      (http://localhost:<AdminPort>/)
# Not done here: the inbound firewall rule for the machines' TCP port (IT).
#
#   powershell -ExecutionPolicy Bypass -File install-service.ps1 -CopyDataFrom "<repo>\bin\Debug\net8.0" -Start
param(
    [string]$From = (Join-Path $PSScriptRoot '..\bin\publish\EdgeGateway'),
    [string]$AppDir = 'C:\Program Files\EdgeGateway',
    [string]$DataDir = 'C:\ProgramData\EdgeGateway',
    [string]$CopyDataFrom,
    [int]$AdminPort = 5100,
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$name = 'EdgeGateway'
$account = "NT SERVICE\$name"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this as administrator.' }
if (Get-Service $name -ErrorAction SilentlyContinue) { throw "The service $name exists already: update-service.ps1 for a new version, or uninstall-service.ps1 first." }
if (-not (Test-Path (Join-Path $From 'EdgeGateway.exe'))) { throw "No published gateway in ${From}: run publish.ps1 first." }

# 1. the program
New-Item -ItemType Directory -Force $AppDir | Out-Null
Copy-Item (Join-Path $From '*') $AppDir -Recurse -Force
Write-Host "program: $AppDir"

# 2. the data
New-Item -ItemType Directory -Force $DataDir | Out-Null
if (Test-Path (Join-Path $DataDir 'Config')) {
    Write-Host "data: $DataDir (kept as it is)"
} elseif ($CopyDataFrom) {
    if (Get-Process EdgeGateway -ErrorAction SilentlyContinue) { throw 'A gateway is still running: stop it first (Ctrl+C or Stop Gateway.bat), so its outbox is copied complete.' }
    foreach ($part in 'Config', 'outbox', 'state') {
        $source = Join-Path $CopyDataFrom $part
        if (Test-Path $source) { Copy-Item $source (Join-Path $DataDir $part) -Recurse }
    }
    Write-Host "data: $DataDir (Config, outbox, state copied from $CopyDataFrom)"
} else {
    Copy-Item (Join-Path $AppDir 'Config') (Join-Path $DataDir 'Config') -Recurse
    Write-Host "data: $DataDir (default Config)"
}

# 3. the service. New-Service takes the path with its quotes as it is; sc.exe sets the rest.
$binary = "`"$AppDir\EdgeGateway.exe`" --service --data `"$DataDir`" --admin-port $AdminPort"
New-Service -Name $name -BinaryPathName $binary -DisplayName 'Edge Gateway (TCP)' -StartupType Automatic `
    -Description "Machine data in on TCP; IO/motion to MQTT; production data to MES. Data: $DataDir" | Out-Null
sc.exe config $name start= delayed-auto obj= $account | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'sc.exe config failed' }
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Host
sc.exe failureflag $name 1 | Out-Host   # also restart when it stops with an error code

# 4. the account's rights: the data folder, and the config page address (HttpListener)
icacls $DataDir /grant "${account}:(OI)(CI)M" /T /Q | Out-Host
netsh http add urlacl url=http://localhost:$AdminPort/ user=$account | Out-Host

Write-Host ''
Write-Host "Installed: service $name, account $account."
Write-Host "Firewall: inbound TCP for the machines (machine-config.json tcpPort) is IT's."
if ($Start) {
    Start-Service $name
    Write-Host "Started. Config page: http://localhost:$AdminPort/config/  Log: $DataDir\logs"
} else {
    Write-Host "Start it with: Start-Service $name   (stop any console gateway first: same ports)"
}
