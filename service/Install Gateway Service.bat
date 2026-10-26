@echo off
rem Installs the gateway as the Windows service "EdgeGateway". Double-click it on the
rem gateway PC. Works from wherever this folder is copied to.
rem   1. asks where to install (Enter = the default shown in [ ]):
rem        program folder, data folder (Config with machine tabs, outbox, logs),
rem        and an old gateway folder to take its data over from (or none)
rem   2. stops a running console gateway (asks first), so its outbox is copied complete
rem   3. builds the release version if the .NET SDK is on this PC; otherwise uses the
rem      build already in bin\publish\EdgeGateway (copy it along from the build PC)
rem   4. asks Windows for administrator rights and runs install-service.ps1. Its window
rem      stays open to show the result.
rem Undo: as administrator, uninstall-service.ps1 (program and data are kept).
setlocal
for %%I in ("%~dp0..") do set "REPO=%%~fI"
set "PUBLISH=%REPO%\bin\publish\EdgeGateway"

set "APPDIR=C:\Program Files\EdgeGateway"
set /p "APPDIR=Program folder [%APPDIR%]: "
set "DATADIR=C:\ProgramData\EdgeGateway"
set /p "DATADIR=Data folder [%DATADIR%]: "
set "FROM=none"
if exist "%REPO%\bin\Debug\net8.0\Config\machine-config.json" set "FROM=%REPO%\bin\Debug\net8.0"
echo Take over Config, outbox and state from an old gateway folder (type none for a fresh start).
set /p "FROM=Old gateway folder [%FROM%]: "

set "COPYARG="
if /i not "%FROM%"=="none" (
    if not exist "%FROM%\Config" (
        echo No Config folder in "%FROM%" - nothing installed.
        pause
        exit /b 1
    )
    set "COPYARG=,'-CopyDataFrom','\"%FROM%\"'"
)

powershell -NoProfile -Command "if (Get-Process EdgeGateway -ErrorAction SilentlyContinue) { exit 1 }"
if errorlevel 1 (
    echo A gateway is running. It must stop first, so its outbox is copied complete.
    choice /c YN /m "Stop it now"
    if errorlevel 2 exit /b
    powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\tools\LocalMqttTest\test-env\stop-test-env.ps1"
)

dotnet --list-sdks 2>nul | findstr /r "." >nul
if not errorlevel 1 (
    echo Building the release version...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\service\publish.ps1"
    if errorlevel 1 (
        echo Build failed - nothing installed.
        pause
        exit /b 1
    )
) else (
    echo No .NET SDK on this PC: using the build in "%PUBLISH%".
)
if not exist "%PUBLISH%\EdgeGateway.exe" (
    echo No build in "%PUBLISH%". Run service\publish.ps1 on a PC with the .NET SDK and copy that folder along.
    pause
    exit /b 1
)

echo Windows will now ask for administrator rights.
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-NoExit','-ExecutionPolicy','Bypass','-File','\"%REPO%\service\install-service.ps1\"','-From','\"%PUBLISH%\"','-AppDir','\"%APPDIR%\"','-DataDir','\"%DATADIR%\"'%COPYARG%,'-Start'"
if errorlevel 1 (
    echo Administrator rights were not given - nothing installed.
    pause
)
