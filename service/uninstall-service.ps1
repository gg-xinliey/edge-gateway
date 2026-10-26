# Removes the EdgeGateway service. Run as administrator.
# Stops it cleanly, removes the service and its config page address right. Leaves the
# program and the data folders (Config, outbox with any unsent rows, logs) as they are.
param([int]$AdminPort = 5100)
$ErrorActionPreference = 'Stop'
$name = 'EdgeGateway'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this as administrator.' }
$service = Get-CimInstance Win32_Service -Filter "Name='$name'"
if (-not $service) { throw "The service $name is not installed." }

Stop-Service $name
sc.exe delete $name | Out-Host
netsh http delete urlacl url=http://localhost:$AdminPort/ | Out-Host
Write-Host "Removed the service $name. Not removed: the program ($($service.PathName)) and the data folder."
