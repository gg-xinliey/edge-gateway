# Pane: local MQTT broker on 127.0.0.1:1883, prints every message it receives.
$Host.UI.RawUI.WindowTitle = 'MQTT broker'
dotnet run --project "$PSScriptRoot\..\LocalMqttTest.csproj" --no-build -- --broker
