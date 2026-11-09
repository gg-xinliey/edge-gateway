@echo off
rem One click on the gateway PC: get the newest gateway from GitHub and restart it in Docker.
rem Double-click it in the gateway folder (the one with docker-compose.yml).
rem Settings, outbox and logs (data\, mes-data\) are not touched: git ignores them.
setlocal
cd /d "%~dp0"
title Update Edge Gateway

echo === Edge Gateway update ===
echo Folder: %CD%
echo.

where git >nul 2>&1 || (echo [X] Git is not installed or not on PATH. & goto :fail)
call docker info >nul 2>&1 || (echo [X] Docker is not running. Start Docker Desktop, wait until it is running, then try again. & goto :fail)

for /f %%i in ('git rev-parse --short HEAD') do set BEFORE=%%i
echo Running now: %BEFORE%
echo.
echo --- 1/3 Getting the newest version from GitHub ---
rem --ff-only: never merges. A file edited by hand here stops it, and nothing is changed.
call git pull --ff-only
if errorlevel 1 (
  echo.
  echo [X] git pull did not work. Nothing was changed: the gateway keeps running as it is.
  echo     Usually a file in this folder was edited by hand. Send this window to the developer.
  goto :fail
)
for /f %%i in ('git rev-parse --short HEAD') do set AFTER=%%i
if "%BEFORE%"=="%AFTER%" (echo Already the newest: %AFTER%) else (echo Updated %BEFORE% -^> %AFTER%: & call git log --oneline %BEFORE%..%AFTER%)
echo.

echo --- 2/3 Building and restarting the gateway, this can take a few minutes ---
rem Builds while the old one keeps running, then swaps it (a clean stop: up to 30 s).
call docker compose up -d --build
if errorlevel 1 (
  echo.
  echo [X] docker compose did not work. Send this window to the developer.
  goto :fail
)
echo.

echo --- 3/3 Check ---
call docker compose ps
echo.
call docker compose logs --tail 15 gateway
echo.
echo [OK] Done. The config page opens now: press Ctrl+F5 there.
start "" http://localhost:5100/config/
echo.
pause
exit /b 0

:fail
echo.
pause
exit /b 1
