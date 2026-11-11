@echo off
setlocal
REM Global Agent Notch shim — copy/symlink to %USERPROFILE%\.local\bin\notch.cmd
REM   notch          start (dotnet run in this terminal)
REM   notch stop     kill the running overlay
REM   notch --scan   pass-through flags to the app

if /I "%~1"=="stop" goto :stop

dotnet run --project "%~dp0AgentNotch\AgentNotch.csproj" -- %*
exit /b %ERRORLEVEL%

:stop
taskkill /IM AgentNotch.exe /F >nul 2>&1
if errorlevel 1 (
  echo Agent Notch is not running.
  exit /b 1
)
echo Agent Notch stopped.
exit /b 0
