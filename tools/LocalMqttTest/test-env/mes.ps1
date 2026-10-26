# Pane: fake MES on http://localhost:5000/, prints every envelope it receives.
$Host.UI.RawUI.WindowTitle = 'MES emulator'
dotnet run --project "$PSScriptRoot\..\..\MES_EMULATOR\MES_EMULATOR.csproj" --no-build
