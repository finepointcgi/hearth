# Runs Hearth (web) in the background on Windows: starts at logon, restarts if it stops.
#   .\scripts\install-service-windows.ps1            install / update
#   .\scripts\install-service-windows.ps1 -Uninstall remove
param([switch]$Uninstall, [int]$Port = 5290, [string]$Password = "", [switch]$NoLocalTools)
$ErrorActionPreference = "Stop"
$TaskName = "Hearth"
$AppDir = Join-Path $env:LOCALAPPDATA "Hearth-app"
$Repo = Split-Path -Parent $PSScriptRoot

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "Hearth task removed (data untouched)."; exit 0
}

dotnet publish "$Repo\src\Hearth.Web\Hearth.Web.csproj" -c Release -o $AppDir --nologo -v quiet
$bind = if ($Password) { "0.0.0.0" } else { "127.0.0.1" }   # no password = this PC only
$taskArgs = "`"$AppDir\Hearth.Web.dll`" --urls http://${bind}:$Port"
if (-not $NoLocalTools) { $taskArgs += " --Hearth:AllowLocalProcesses=true" }
if ($Password) { [Environment]::SetEnvironmentVariable("Hearth__Password", $Password, "User") }

$action = New-ScheduledTaskAction -Execute "dotnet" -Argument $taskArgs -WorkingDirectory $AppDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
  -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "✅ Hearth runs at logon: http://localhost:$Port"
