<#
.SYNOPSIS
Stops dev ports and starts the Skoruba local stack.

.DESCRIPTION
1) Clears dev ports by calling stop-dev-ports.ps1
2) Starts STS, Admin API, Admin host, and Vite dev server
3) Prints which ports are listening

Use -NoVite to skip the SPA dev server.
#>

[CmdletBinding()]
param(
    [switch] $NoVite
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$stopScript = Join-Path $PSScriptRoot 'stop-dev-ports.ps1'
$logDir = Join-Path $root '.codex-run'

if (-not (Test-Path $stopScript)) {
    throw "Missing $stopScript"
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

& $stopScript | Out-Null

$processes = @(
    @{
        FilePath = 'dotnet'
        Arguments = 'run --no-build --launch-profile Skoruba.Duende.IdentityServer.AspNetIdentity --project "E:\Skoruba\src\Skoruba.Duende.IdentityServer.STS.Identity\Skoruba.Duende.IdentityServer.STS.Identity.csproj"'
        WorkingDirectory = $root
        StdOut = Join-Path $logDir 'sts.out.log'
        StdErr = Join-Path $logDir 'sts.err.log'
    },
    @{
        FilePath = 'dotnet'
        Arguments = 'run --no-build --launch-profile Skoruba.Duende.IdentityServer.Admin.Api --project "E:\Skoruba\src\Skoruba.Duende.IdentityServer.Admin.Api\Skoruba.Duende.IdentityServer.Admin.Api.csproj"'
        WorkingDirectory = $root
        StdOut = Join-Path $logDir 'admin-api.out.log'
        StdErr = Join-Path $logDir 'admin-api.err.log'
    },
    @{
        FilePath = 'dotnet'
        Arguments = 'run --no-build --launch-profile https --project "E:\Skoruba\src\Skoruba.Duende.IdentityServer.Admin\Skoruba.Duende.IdentityServer.Admin.csproj"'
        WorkingDirectory = $root
        StdOut = Join-Path $logDir 'admin.out.log'
        StdErr = Join-Path $logDir 'admin.err.log'
    }
)

if (-not $NoVite) {
    $processes += @{
        FilePath = 'npm.cmd'
        Arguments = 'run dev'
        WorkingDirectory = 'E:\Skoruba\src\Skoruba.Duende.IdentityServer.Admin.UI.Client'
        StdOut = Join-Path $logDir 'vite.out.log'
        StdErr = Join-Path $logDir 'vite.err.log'
    }
}

foreach ($proc in $processes) {
    Start-Process -FilePath $proc.FilePath `
        -ArgumentList $proc.Arguments `
        -WorkingDirectory $proc.WorkingDirectory `
        -RedirectStandardOutput $proc.StdOut `
        -RedirectStandardError $proc.StdErr `
        -WindowStyle Hidden
}

Start-Sleep -Seconds 8

Get-NetTCPConnection -State Listen |
    Where-Object { $_.LocalPort -in 5001, 5004, 5100, 7127, 7128 } |
    Select-Object LocalAddress, LocalPort, OwningProcess |
    Sort-Object LocalPort |
    Format-Table -AutoSize
