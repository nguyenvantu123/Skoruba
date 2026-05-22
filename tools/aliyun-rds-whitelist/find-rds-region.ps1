<#
.SYNOPSIS
  Probe several Aliyun RDS regional endpoints to find which one owns the
  given DBInstanceId.

.DESCRIPTION
  Calls DescribeDBInstanceAttribute against a list of common Aliyun regions
  using credentials from .env. The region that returns 200 owns the
  instance. Useful when you copy a connection string but the API endpoint
  region is unclear.
#>
[CmdletBinding()]
param(
    [string]$EnvFile,
    [string]$InstanceId
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = (Get-Location).Path
}
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $ScriptRoot '.env'
}

# Read env (minimal duplicate to avoid full dependency on update-whitelist.ps1)
function Read-EnvFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { throw "Env file not found: $Path" }
    $env = @{}
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $idx = $trimmed.IndexOf('=')
        if ($idx -lt 1) { continue }
        $key = $trimmed.Substring(0, $idx).Trim()
        $val = $trimmed.Substring($idx + 1).Trim().Trim('"').Trim("'")
        $env[$key] = $val
    }
    return $env
}

function Encode-RpcParam {
    param([string]$Value)
    Add-Type -AssemblyName System.Web
    $enc = [System.Web.HttpUtility]::UrlEncode($Value, [System.Text.Encoding]::UTF8)
    $enc = $enc.Replace('+', '%20').Replace('*', '%2A').Replace('%7e', '~').Replace('%7E', '~')
    return [regex]::Replace($enc, '%[0-9a-fA-F]{2}', { param($m) $m.Value.ToUpperInvariant() })
}

function Probe-Region {
    param(
        [string]$RegionId,
        [string]$AccessKeyId,
        [string]$AccessKeySecret,
        [string]$DBInstanceId
    )
    $params = @{
        'Format'           = 'JSON'
        'Version'          = '2014-08-15'
        'AccessKeyId'      = $AccessKeyId
        'SignatureMethod'  = 'HMAC-SHA1'
        'Timestamp'        = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        'SignatureVersion' = '1.0'
        'SignatureNonce'   = [Guid]::NewGuid().ToString('N')
        'Action'           = 'DescribeDBInstanceAttribute'
        'RegionId'         = $RegionId
        'DBInstanceId'     = $DBInstanceId
    }

    $sortedKeys = [string[]]@($params.Keys)
    [Array]::Sort($sortedKeys, [System.StringComparer]::Ordinal)
    $canonical = ($sortedKeys | ForEach-Object {
        "$(Encode-RpcParam $_)=$(Encode-RpcParam $params[$_])"
    }) -join '&'

    $stringToSign = "GET&$(Encode-RpcParam '/')&$(Encode-RpcParam $canonical)"
    $hmacKey   = [System.Text.Encoding]::UTF8.GetBytes("$AccessKeySecret&")
    $signBytes = [System.Text.Encoding]::UTF8.GetBytes($stringToSign)
    $hmac      = New-Object System.Security.Cryptography.HMACSHA1(,$hmacKey)
    $signature = [Convert]::ToBase64String($hmac.ComputeHash($signBytes))

    $endpoint = "https://rds.$RegionId.aliyuncs.com/"
    $finalUrl = "$endpoint`?$canonical&Signature=$(Encode-RpcParam $signature)"

    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(8)
    try {
        $resp    = $client.GetAsync($finalUrl).GetAwaiter().GetResult()
        $bodyStr = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{
            StatusCode = [int]$resp.StatusCode
            Body       = $bodyStr
        }
    }
    catch {
        return @{
            StatusCode = -1
            Body       = $_.Exception.Message
        }
    }
    finally { $client.Dispose() }
}

$envMap = Read-EnvFile -Path $EnvFile
if (-not $envMap.ContainsKey('ALIYUN_ACCESS_KEY_ID')) { throw "ALIYUN_ACCESS_KEY_ID missing in env" }
if ([string]::IsNullOrWhiteSpace($InstanceId)) {
    $InstanceId = $envMap['ALIYUN_RDS_INSTANCE_ID']
}
if ([string]::IsNullOrWhiteSpace($InstanceId)) { throw "InstanceId not provided and ALIYUN_RDS_INSTANCE_ID empty in env" }

$regions = @(
    'ap-southeast-1',     # Singapore
    'ap-southeast-2',     # Sydney
    'ap-southeast-3',     # Kuala Lumpur
    'ap-southeast-5',     # Jakarta
    'ap-southeast-6',     # Manila
    'ap-southeast-7',     # Bangkok
    'ap-northeast-1',     # Tokyo
    'ap-northeast-2',     # Seoul
    'ap-south-1',         # Mumbai
    'cn-hongkong',
    'cn-shanghai',
    'cn-beijing',
    'cn-shenzhen',
    'us-east-1',          # Virginia
    'us-west-1',          # Silicon Valley
    'eu-west-1',          # London
    'eu-central-1'        # Frankfurt
)

Write-Host "Probing $($regions.Count) regions for instance $InstanceId..."
foreach ($r in $regions) {
    $res = Probe-Region -RegionId $r -AccessKeyId $envMap['ALIYUN_ACCESS_KEY_ID'] -AccessKeySecret $envMap['ALIYUN_ACCESS_KEY_SECRET'] -DBInstanceId $InstanceId
    $tag = ''
    if ($res.StatusCode -eq 200) { $tag = '[FOUND]' }
    elseif ($res.Body -match 'InvalidDBInstance\.NotFound') { $tag = '[not in this region]' }
    elseif ($res.Body -match 'Forbidden\.RAM') { $tag = '[RAM denied - region policy]' }
    elseif ($res.Body -match 'Endpoint') { $tag = '[wrong endpoint]' }
    elseif ($res.Body -match 'Specified parameter Action is not valid') { $tag = '[unsupported action]' }
    else { $tag = "[$($res.StatusCode)] $($res.Body.Substring(0, [Math]::Min(120, $res.Body.Length)))" }
    Write-Host ("{0,-22} {1}" -f $r, $tag)
    if ($res.StatusCode -eq 200) {
        Write-Host "  Body: $($res.Body)"
    }
}
