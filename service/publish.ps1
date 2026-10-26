# Release build of the gateway for the Windows service, into bin\publish\EdgeGateway\
# (not in git). Needs the .NET 8 runtime on the gateway PC (it has it: dotnet run works).
# Next: install-service.ps1 (first time) or update-service.ps1 (new version), as administrator.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\EdgeGateway.csproj'
$out = Join-Path $PSScriptRoot '..\bin\publish\EdgeGateway'
dotnet publish $project -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Write-Host "published to $((Resolve-Path $out).Path)"
