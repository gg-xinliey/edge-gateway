# Puts a new version (publish.ps1) into the installed EdgeGateway service. Run as administrator.
# Stops the service (a clean stop: the outbox writes what it holds, message_ids are saved),
# replaces the program folder, starts the service again. The data folder is not touched.
param([string]$From = (Join-Path $PSScriptRoot '..\bin\publish\EdgeGateway'))
$ErrorActionPreference = 'Stop'
$name = 'EdgeGateway'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this as administrator.' }
$service = Get-CimInstance Win32_Service -Filter "Name='$name'"
if (-not $service) { throw "The service $name is not installed: install-service.ps1 first." }
if (-not (Test-Path (Join-Path $From 'EdgeGateway.exe'))) { throw "No published gateway in ${From}: run publish.ps1 first." }

# The program folder is the folder of the exe in the service's command line.
$exe = if ($service.PathName -match '^"([^"]+)"') { $Matches[1] } else { ($service.PathName -split ' ')[0] }
$appDir = Split-Path $exe

Stop-Service $name   # waits until it has stopped
Copy-Item (Join-Path $From '*') $appDir -Recurse -Force
Start-Service $name
Write-Host "Updated $appDir and started $name."
