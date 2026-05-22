<#
.SYNOPSIS
  List ALL RDS instances visible to the AccessKey in `.env` across common regions.

.DESCRIPTION
  Calls DescribeDBInstances (no DBInstanceId filter) so the response shows
  whatever the AccessKey owner can see. Useful to confirm whether the
  AccessKey actually belongs to the same Aliyun account as the target RDS.
#>
[CmdletBinding()]
param(
    [string]$EnvFile
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) { $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) { $ScriptRoot = (Get-Location).Path }
if ([string]::IsNullOrWhiteSpace($EnvFile))    { $EnvFile = Join-Path $ScriptRoot '.env' }

function Read-EnvFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { throw "Env file not found: $Path" }
    $env = @{}
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $idx = $trimmed.IndexOf('=')
        if ($idx -lt 1) { continue }
        $env[$trimmed.Substring(0, $idx).Trim()] = $trimmed.Substring($idx + 1).Trim().Trim('"').Trim("'")
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

function Invoke-DescribeDBInstances {
    param([string]$RegionId, [string]$Ak, [string]$Sk)
    $params = @{
        'Format'           = 'JSON'
        'Version'          = '2014-08-15'
        'AccessKeyId'      = $Ak
        'SignatureMethod'  = 'HMAC-SHA1'
        'Timestamp'        = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        'SignatureVersion' = '1.0'
        'SignatureNonce'   = [Guid]::NewGuid().ToString('N')
        'Action'           = 'DescribeDBInstances'
        'RegionId'         = $RegionId
        'PageSize'         = '100'
    }
    $sortedKeys = [string[]]@($params.Keys)
    [Array]::Sort($sortedKeys, [System.StringComparer]::Ordinal)
    $canonical = ($sortedKeys | ForEach-Object {
        "$(Encode-RpcParam $_)=$(Encode-RpcParam $params[$_])"
    }) -join '&'
    $stringToSign = "GET&$(Encode-RpcParam '/')&$(Encode-RpcParam $canonical)"
    $hmacKey   = [System.Text.Encoding]::UTF8.GetBytes("$Sk&")
    $signBytes = [System.Text.Encoding]::UTF8.GetBytes($stringToSign)
    $hmac      = New-Object System.Security.Cryptography.HMACSHA1(,$hmacKey)
    $signature = [Convert]::ToBase64String($hmac.ComputeHash($signBytes))
    $url = "https://rds.$RegionId.aliyuncs.com/?$canonical&Signature=$(Encode-RpcParam $signature)"

    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(8)
    try {
        $resp    = $client.GetAsync($url).GetAwaiter().GetResult()
        $bodyStr = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{ Status = [int]$resp.StatusCode; Body = $bodyStr }
    }
    catch { return @{ Status = -1; Body = $_.Exception.Message } }
    finally { $client.Dispose() }
}

$envMap = Read-EnvFile -Path $EnvFile
$ak = $envMap['ALIYUN_ACCESS_KEY_ID']
$sk = $envMap['ALIYUN_ACCESS_KEY_SECRET']

$regions = @('ap-southeast-1','ap-southeast-3','ap-northeast-1','cn-hongkong','cn-shanghai','cn-beijing','us-east-1','eu-central-1')
foreach ($r in $regions) {
    $res = Invoke-DescribeDBInstances -RegionId $r -Ak $ak -Sk $sk
    if ($res.Status -eq 200) {
        $j = $res.Body | ConvertFrom-Json
        $count = if ($j.Items) { $j.Items.DBInstance.Count } else { 0 }
        Write-Host ("{0,-22} {1} instance(s)" -f $r, $count)
        if ($count -gt 0) {
            $j.Items.DBInstance | ForEach-Object {
                Write-Host "    - $($_.DBInstanceId)  type=$($_.DBInstanceType)  status=$($_.DBInstanceStatus)  desc=$($_.DBInstanceDescription)"
            }
        }
    }
    else {
        Write-Host ("{0,-22} ERR {1}" -f $r, $res.Body.Substring(0, [Math]::Min(120, $res.Body.Length)))
    }
}
