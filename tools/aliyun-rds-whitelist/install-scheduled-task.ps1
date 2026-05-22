<#
.SYNOPSIS
  Register a Windows Scheduled Task that runs update-whitelist.ps1 every 10
  minutes (and once at user logon) so the Aliyun RDS whitelist follows your
  rotating public IP without manual action.

.DESCRIPTION
  - Runs as the current interactive user (no admin elevation needed).
  - Triggers: at logon + every 10 minutes thereafter.
  - Logs each run to `update-whitelist.log` next to this script.

.PARAMETER Uninstall
  Remove a previously installed task instead of creating one.
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$TaskName = 'Skoruba.AliyunRdsWhitelistUpdater',
    [int]$IntervalMinutes = 10
)

$ErrorActionPreference = 'Stop'

if ($Uninstall) {
    & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Removed scheduled task '$TaskName'."
    }
    else {
        Write-Host "No scheduled task named '$TaskName' found."
    }
    return
}

$scriptPath = Join-Path $PSScriptRoot 'update-whitelist.ps1'
if (-not (Test-Path $scriptPath)) {
    throw "update-whitelist.ps1 not found next to this installer."
}

$envPath = Join-Path $PSScriptRoot '.env'
if (-not (Test-Path $envPath)) {
    throw ".env not found next to this installer. Copy .env.example to .env and fill in values first."
}

$logPath = Join-Path $PSScriptRoot 'update-whitelist.log'

# schtasks.exe (legacy CLI) doesn't require admin for tasks owned by the
# current interactive user, while ScheduledTasks PowerShell cmdlets often
# trigger UAC. Use schtasks for the install path so the user does not need
# to right-click "Run as administrator".

# Use a tiny .cmd wrapper so we don't have to encode shell redirection inside
# the scheduled-task XML, which Task Scheduler tends to garble on save.
$wrapperPath = Join-Path $PSScriptRoot 'run-and-log.cmd'
if (-not (Test-Path $wrapperPath)) {
    throw "Wrapper not found: $wrapperPath. Re-pull the tools folder."
}
$cmdPath = $wrapperPath
$argLine = ''  # the wrapper has all args baked in

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Updates Aliyun RDS / KVStore whitelist with the current public IP every $IntervalMinutes minutes.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
    </LogonTrigger>
    <TimeTrigger>
      <Repetition>
        <Interval>PT${IntervalMinutes}M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>$([DateTime]::Now.ToString('s'))</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$cmdPath</Command>
      <Arguments>$argLine</Arguments>
    </Exec>
  </Actions>
</Task>
"@

# Write to a temp xml file and import via schtasks /Create /XML.
$xmlPath = Join-Path $env:TEMP "$TaskName.xml"
Set-Content -Path $xmlPath -Value $xml -Encoding Unicode

# Remove any existing task with the same name (ignore failure).
# Wrap in try/catch + cmd.exe redirect to fully swallow stderr noise when
# the task does not exist (which is the common first-install case).
try {
    & cmd.exe /c "schtasks /Delete /TN $TaskName /F >nul 2>&1"
} catch { }

& schtasks.exe /Create /TN $TaskName /XML $xmlPath /F | Out-Null
$rc = $LASTEXITCODE

Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue

if ($rc -ne 0) {
    throw "schtasks /Create failed with exit code $rc."
}

Write-Host "Registered scheduled task '$TaskName' (per-user, no admin required)."
Write-Host "Logs: $logPath"
Write-Host "Run once now to test:"
Write-Host "  schtasks /Run /TN '$TaskName'"
Write-Host "Or manually: powershell -ExecutionPolicy Bypass -File '$scriptPath'"
