<#
.SYNOPSIS
Stops processes listening on the common Skoruba dev ports.

.DESCRIPTION
Looks up listeners on ports 5001, 5004, 5100, and 7127, prints the owning
process info, and stops those processes. Use -WhatIf to preview.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [int[]] $Ports = @(5001, 5004, 5100, 7127, 7128)
)

$ErrorActionPreference = 'Stop'

if (-not $Ports -or $Ports.Count -eq 0) {
    Write-Host 'No ports provided. Nothing to do.'
    exit 0
}

$listeners = @()
foreach ($port in $Ports) {
    $listeners += Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
}

if (-not $listeners -or $listeners.Count -eq 0) {
    Write-Host ('No listeners found on ports: {0}' -f ($Ports -join ', '))
    exit 0
}

$processIds = $listeners | Select-Object -ExpandProperty OwningProcess -Unique
$processes = @()
foreach ($processId in $processIds) {
    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($proc) {
        $processes += $proc
    }
}

if (-not $processes -or $processes.Count -eq 0) {
    Write-Host 'Listeners found, but no processes could be resolved.'
    exit 0
}

Write-Host 'Processes currently holding the dev ports:'
$processes | Select-Object Id, ProcessName, Path, StartTime | Format-Table -AutoSize

foreach ($proc in $processes) {
    if ($PSCmdlet.ShouldProcess(($proc.ProcessName + ' (PID ' + $proc.Id + ')'), 'Stop-Process')) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Done.'
