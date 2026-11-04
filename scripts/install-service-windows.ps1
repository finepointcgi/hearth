# Runs Locus (web) in the background on Windows: starts at logon, restarts if it stops.
#   .\scripts\install-service-windows.ps1            install / update
#   .\scripts\install-service-windows.ps1 -Uninstall remove
param([switch]$Uninstall, [int]$Port = 5290, [string]$Password = "", [switch]$NoLocalTools)
$ErrorActionPreference = "Stop"
$TaskName = "Locus"
$AppDir = Join-Path $env:LOCALAPPDATA "Locus-app"
$Repo = Split-Path -Parent $PSScriptRoot

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "Locus task removed (data untouched)."; exit 0
}

dotnet publish "$Repo\src\Locus.Web\Locus.Web.csproj" -c Release -o $AppDir --nologo -v quiet
$bind = if ($Password) { "0.0.0.0" } else { "127.0.0.1" }   # no password = this PC only
$taskArgs = "`"$AppDir\Locus.Web.dll`" --urls http://${bind}:$Port"
if (-not $NoLocalTools) { $taskArgs += " --Locus:AllowLocalProcesses=true" }
if ($Password) { [Environment]::SetEnvironmentVariable("Locus__Password", $Password, "User") }

$action = New-ScheduledTaskAction -Execute "dotnet" -Argument $taskArgs -WorkingDirectory $AppDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
  -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "✅ Locus runs at logon: http://localhost:$Port"
