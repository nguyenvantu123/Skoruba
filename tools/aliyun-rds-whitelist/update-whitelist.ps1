<#
.SYNOPSIS
  Update Aliyun whitelist groups (RDS + KVStore Redis) with the current public IP.

.DESCRIPTION
  Fetches the workstation's current public IPv4 from https://api.ipify.org,
  compares against a local cache, and (if changed) calls the Aliyun OpenAPI
  `ModifySecurityIps` for each configured product (RDS, KVStore/Redis) to
  overwrite a named whitelist group with that IP only.

  The script is table-driven: each entry in $Targets describes one Aliyun
  product. Empty/blank instance ids are skipped, so you can run RDS-only or
  RDS+Redis from the same .env.

.NOTES
  - Reads credentials from `.env` next to this script (KEY=VALUE).
  - Required: ALIYUN_ACCESS_KEY_ID, ALIYUN_ACCESS_KEY_SECRET,
    ALIYUN_RDS_INSTANCE_ID, ALIYUN_RDS_REGION_ID, ALIYUN_RDS_GROUP_NAME.
  - Optional (Redis): ALIYUN_REDIS_INSTANCE_ID, ALIYUN_REDIS_REGION_ID,
    ALIYUN_REDIS_GROUP_NAME (defaults to `default`).
  - Optional: PUBLIC_IP_PROBE_URL (default https://api.ipify.org).
  - Idempotent: re-running with the same IP is a no-op (no API calls).
  - Aliyun RPC API style 3.0 signing (HMAC-SHA1 / `AccessKeySecret&`).
    https://www.alibabacloud.com/help/en/sdk/product-overview/rpc-mechanism
#>
[CmdletBinding()]
param(
    [string]$EnvFile,
    [string]$CacheFile,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Resolve script directory robustly. $PSScriptRoot can be empty when the
# script is invoked via certain wrapper combinations (e.g. powershell -File
# from another shell with -NoProfile). Fall back to MyInvocation, then cwd.
$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = (Get-Location).Path
}
if ([string]::IsNullOrWhiteSpace($EnvFile))   { $EnvFile   = Join-Path $ScriptRoot '.env' }
if ([string]::IsNullOrWhiteSpace($CacheFile)) { $CacheFile = Join-Path $ScriptRoot '.last-ip.cache' }

function Read-EnvFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        throw "Env file not found: $Path. Copy .env.example to .env and fill in values."
    }
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

function Get-PublicIp {
    param([string]$Url = 'https://api.ipify.org')
    try {
        $ip = (Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10).Content.Trim()
    }
    catch {
        throw "Failed to fetch public IP from $Url : $($_.Exception.Message)"
    }
    if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
        throw "Public IP probe returned non-IPv4 value: '$ip'"
    }
    return $ip
}

function Encode-RpcParam {
    param([string]$Value)
    Add-Type -AssemblyName System.Web
    $enc = [System.Web.HttpUtility]::UrlEncode($Value, [System.Text.Encoding]::UTF8)
    # HttpUtility lowercases hex (%3a), but Aliyun RPC v3 / RFC 3986 requires
    # uppercase hex (%3A). Also: '+' must become %20, '*' must become %2A,
    # encoded '~' must decode back to '~'.
    $enc = $enc.Replace('+', '%20').Replace('*', '%2A').Replace('%7e', '~').Replace('%7E', '~')
    # Uppercase every percent-escape: %xy -> %XY.
    return [regex]::Replace($enc, '%[0-9a-fA-F]{2}', { param($m) $m.Value.ToUpperInvariant() })
}

function Invoke-AliyunRpcGet {
    [CmdletBinding()]
    param(
        [string]$Endpoint,
        [string]$Version,
        [string]$AccessKeyId,
        [string]$AccessKeySecret,
        [hashtable]$ActionParams
    )

    # Common signing params + caller-provided action params.
    $params = @{
        'Format'           = 'JSON'
        'Version'          = $Version
        'AccessKeyId'      = $AccessKeyId
        'SignatureMethod'  = 'HMAC-SHA1'
        'Timestamp'        = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        'SignatureVersion' = '1.0'
        'SignatureNonce'   = [Guid]::NewGuid().ToString('N')
    }
    foreach ($k in $ActionParams.Keys) { $params[$k] = $ActionParams[$k] }

    # Aliyun signing requires ordinal (byte-wise) sort by parameter name.
    # PowerShell's default Sort-Object uses culture-aware comparison which
    # can mis-order keys (e.g. when the active culture is non-English),
    # so we sort manually with [StringComparer]::Ordinal.
    $sortedKeys = [string[]]@($params.Keys)
    [Array]::Sort($sortedKeys, [System.StringComparer]::Ordinal)
    $canonical = ($sortedKeys | ForEach-Object {
        "$(Encode-RpcParam $_)=$(Encode-RpcParam $params[$_])"
    }) -join '&'

    $stringToSign = "GET&$(Encode-RpcParam '/')&$(Encode-RpcParam $canonical)"

    Write-Verbose "CLIENT canonical: $canonical"
    Write-Verbose "CLIENT stringToSign: $stringToSign"
    try {
        $debugFile = Join-Path $env:TEMP 'sign-debug.txt'
        Add-Content -Path $debugFile -Value "ACTION=$($params['Action']) STRINGTOSIGN=$stringToSign" -Encoding ASCII
    } catch { }

    $hmacKey   = [System.Text.Encoding]::UTF8.GetBytes("$AccessKeySecret&")
    $signBytes = [System.Text.Encoding]::UTF8.GetBytes($stringToSign)
    $hmac      = New-Object System.Security.Cryptography.HMACSHA1(,$hmacKey)
    $signature = [Convert]::ToBase64String($hmac.ComputeHash($signBytes))

    $finalUrl = "$Endpoint`?$canonical&Signature=$(Encode-RpcParam $signature)"

    Write-Verbose "GET $Endpoint Action=$($ActionParams['Action']) Version=$Version"

    # Use HttpClient (or [System.Net.WebRequest]) so we can read the
    # response body even on 4xx/5xx, where Invoke-WebRequest swallows it
    # in some Windows PowerShell versions.
    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $client = $null
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(15)
        $resp    = $client.GetAsync($finalUrl).GetAwaiter().GetResult()
        $bodyStr = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode) {
            $statusInt = [int]$resp.StatusCode
            throw "Aliyun API call failed ($($ActionParams['Action'])): HTTP $statusInt $($resp.ReasonPhrase). Body: $bodyStr"
        }
        return $bodyStr
    }
    finally {
        if ($client) { $client.Dispose() }
    }
}

function Build-Targets {
    param([hashtable]$Env, [string]$Ip)

    $targets = @()

    # ---- Aliyun RDS ----
    if (-not [string]::IsNullOrWhiteSpace($Env['ALIYUN_RDS_INSTANCE_ID'])) {
        $targets += @{
            Name     = 'rds'
            Endpoint = "https://rds.$($Env['ALIYUN_RDS_REGION_ID']).aliyuncs.com/"
            Version  = '2014-08-15'
            Params   = @{
                'Action'                     = 'ModifySecurityIps'
                'RegionId'                   = $Env['ALIYUN_RDS_REGION_ID']
                'DBInstanceId'               = $Env['ALIYUN_RDS_INSTANCE_ID']
                'DBInstanceIPArrayName'      = $Env['ALIYUN_RDS_GROUP_NAME']
                'DBInstanceIPArrayAttribute' = 'hidden'
                'WhitelistNetworkType'       = 'Classic'
                'ModifyMode'                 = 'Cover'
                'SecurityIps'                = $Ip
            }
        }
    }

    # ---- Aliyun KVStore (Redis) ----
    if (-not [string]::IsNullOrWhiteSpace($Env['ALIYUN_REDIS_INSTANCE_ID'])) {
        $redisGroup = $Env['ALIYUN_REDIS_GROUP_NAME']
        if ([string]::IsNullOrWhiteSpace($redisGroup)) { $redisGroup = 'default' }

        $targets += @{
            Name     = 'redis'
            Endpoint = "https://r-kvstore.$($Env['ALIYUN_REDIS_REGION_ID']).aliyuncs.com/"
            Version  = '2015-01-01'
            Params   = @{
                'Action'              = 'ModifySecurityIps'
                'RegionId'            = $Env['ALIYUN_REDIS_REGION_ID']
                'InstanceId'          = $Env['ALIYUN_REDIS_INSTANCE_ID']
                'SecurityIpGroupName' = $redisGroup
                'ModifyMode'          = 'Cover'
                'SecurityIps'         = $Ip
            }
        }
    }

    return $targets
}

# ---------------- main ----------------
$envMap = Read-EnvFile -Path $EnvFile

$required = @(
    'ALIYUN_ACCESS_KEY_ID',
    'ALIYUN_ACCESS_KEY_SECRET',
    'ALIYUN_RDS_INSTANCE_ID',
    'ALIYUN_RDS_REGION_ID',
    'ALIYUN_RDS_GROUP_NAME'
)
$missing = $required | Where-Object { -not $envMap.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($envMap[$_]) }
if ($missing) { throw "Missing required env keys: $($missing -join ', ')" }

# Optional Redis section: must be all-or-nothing.
$redisAny = @('ALIYUN_REDIS_INSTANCE_ID','ALIYUN_REDIS_REGION_ID') |
    Where-Object { $envMap.ContainsKey($_) -and -not [string]::IsNullOrWhiteSpace($envMap[$_]) }
if ($redisAny.Count -gt 0) {
    foreach ($k in 'ALIYUN_REDIS_INSTANCE_ID','ALIYUN_REDIS_REGION_ID') {
        if (-not $envMap.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($envMap[$k])) {
            throw "Redis target partially configured. Missing: $k. Either fill all ALIYUN_REDIS_* keys or leave them all empty."
        }
    }
}

$probeUrl = $envMap['PUBLIC_IP_PROBE_URL']
if ([string]::IsNullOrWhiteSpace($probeUrl)) { $probeUrl = 'https://api.ipify.org' }

$currentIp = Get-PublicIp -Url $probeUrl
Write-Host "Current public IP: $currentIp"

$cachedIp = ''
if (Test-Path $CacheFile) { $cachedIp = (Get-Content $CacheFile -Raw).Trim() }

if (-not $Force -and $currentIp -eq $cachedIp) {
    Write-Host "IP unchanged ($cachedIp). No API call. Pass -Force to override."
    exit 0
}

$targets = Build-Targets -Env $envMap -Ip $currentIp
if ($targets.Count -eq 0) {
    throw "No targets configured. Fill in at least ALIYUN_RDS_* keys."
}

Write-Host ("Updating {0} target(s): {1}" -f $targets.Count, (($targets | ForEach-Object { $_.Name }) -join ', '))

$failures = @()
foreach ($t in $targets) {
    try {
        Write-Host ("[$($t.Name)] -> $currentIp (group=$($t.Params['DBInstanceIPArrayName']) $($t.Params['SecurityIpGroupName']))".Trim())
        $result = Invoke-AliyunRpcGet `
            -Endpoint $t.Endpoint `
            -Version $t.Version `
            -AccessKeyId $envMap['ALIYUN_ACCESS_KEY_ID'] `
            -AccessKeySecret $envMap['ALIYUN_ACCESS_KEY_SECRET'] `
            -ActionParams $t.Params
        Write-Verbose "[$($t.Name)] response: $result"
    }
    catch {
        Write-Warning "[$($t.Name)] $_"
        $failures += $t.Name
    }
}

if ($failures.Count -gt 0) {
    throw "One or more targets failed: $($failures -join ', '). Cache NOT updated; re-run after fixing."
}

Set-Content -Path $CacheFile -Value $currentIp -Encoding ASCII -NoNewline
Write-Host "All targets updated. Cached IP. Done."
