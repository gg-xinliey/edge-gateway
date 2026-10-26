# Release build of the gateway for the Windows service, into bin\publish\EdgeGateway\
# (not in git). Self-contained: the gateway PC needs no .NET installed, so this folder
# can be copied to another PC. Next: install-service.ps1 (first time) or
# update-service.ps1 (new version), as administrator.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\EdgeGateway.csproj'
$out = Join-Path $PSScriptRoot '..\bin\publish\EdgeGateway'
dotnet publish $project -c Release -r win-x64 --self-contained -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Write-Host "published to $((Resolve-Path $out).Path)"
