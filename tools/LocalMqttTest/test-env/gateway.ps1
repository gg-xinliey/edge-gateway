# Pane: the real gateway (Debug build), TCP 9000, config page http://localhost:5100/config/.
# Builds first, so this pane always runs the latest code, then waits a moment
# so the broker and MES panes are up first.
$Host.UI.RawUI.WindowTitle = 'Gateway'
$project = "$PSScriptRoot\..\..\..\EdgeGateway.csproj"
dotnet build $project -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'build failed - gateway not started' -ForegroundColor Red; return }
Start-Sleep -Seconds 3
dotnet run --project $project --no-build
