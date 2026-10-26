# Tab: machine simulator (MachineSim). Arguments pass straight through, e.g.
#   machines.ps1 --machines 3 --minutes 2 --motion-ms 20 --data-s 3 --io-s 9
$Host.UI.RawUI.WindowTitle = 'Machines'
dotnet run --project "$PSScriptRoot\..\..\MachineSim\MachineSim.csproj" --no-build -- @args
