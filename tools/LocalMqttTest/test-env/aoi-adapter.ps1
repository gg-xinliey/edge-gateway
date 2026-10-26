# Pane: the AOI adapter (AoiAdapter\publish\AoiAdapter.exe) following the test AOI folder
# test_data\aoi-share\Line1\BackUp, sending its rows to the gateway (machines Line1_AOI_TOP /
# Line1_AOI_BOTTOM). It retries until the gateway is up, so it can start before the gateway.
$Host.UI.RawUI.WindowTitle = 'AOI adapter'
& "$PSScriptRoot\..\..\..\..\AoiAdapter\publish\AoiAdapter.exe" --folder "$PSScriptRoot\..\..\..\..\test_data\aoi-share\Line1\BackUp" --interval-s 5
