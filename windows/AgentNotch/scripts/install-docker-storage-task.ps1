# Registers weekly Docker prune for AgentNotch (every 7 days, Tue 12:30 local).
# Status + live probe: ~/.config/agent-notch/storage.json + Systems panel in the notch.

[CmdletBinding()]
param(
    [string]$TaskName = 'AgentNotchDockerPrune',
    [string]$Day = 'TUE',
    [string]$Time = '12:30'
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'docker-storage.ps1'
if (-not (Test-Path $script)) { throw "Missing $script" }

$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$tr = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$script`" -Prune"
schtasks /Create /TN $TaskName /TR $tr /SC WEEKLY /D $Day /ST $Time /F | Out-Host

# Seed a status snapshot so the notch has numbers immediately
& $ps -NoProfile -ExecutionPolicy Bypass -File $script -Snapshot | Out-Host

Write-Host "Installed scheduled task '$TaskName' ($Day $Time weekly)."
schtasks /Query /TN $TaskName /FO LIST /V | Select-String -Pattern 'Task Name|Status|Last Run|Next Run|Task To Run' | ForEach-Object { $_.Line }
