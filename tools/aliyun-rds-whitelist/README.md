# Aliyun dynamic-IP whitelist updater (RDS + Redis)

Solo-dev tooling. Aliyun RDS and KVStore (Redis) both enforce IP whitelists for client access; home and office public IPs in Vietnam rotate often (CGNAT, modem reboot, mobile tether). This script keeps a dedicated whitelist group on each Aliyun product in sync with the workstation's current public IP, so `dotnet run` against the cloud RDS + Redis keeps working without manual whitelist edits.

## What it does

- Reads current public IPv4 from `https://api.ipify.org`.
- Compares to a local cache file (`.last-ip.cache`).
- If changed, calls Aliyun OpenAPI `ModifySecurityIps` for each configured target (RDS instance, KVStore Redis instance) and overwrites a single named whitelist group with the new IP.
- Idempotent. Safe to run on a 10-minute timer.
- Atomic-ish: cache is written only after **all** targets succeed, so a partial failure forces a retry next run.

## Hard constraints

- Only the named groups are touched. The `default` groups (unless you point the script at them) and any production groups are left alone.
- Credentials never appear on stdout. They are only printed to the log file when `-Verbose` is enabled. Treat the `.env`, `.last-ip.cache`, and `update-whitelist.log` files as secrets.
- The cache file and `.env` are gitignored.

## One-time setup

### 1. Create a dedicated RAM user with a minimal policy

Aliyun Console -> RAM -> Users -> Create User. Enable OpenAPI access and capture the `AccessKeyId` + `AccessKeySecret`.

Attach a custom policy that limits the user to the two instances below. Example:

```json
{
  "Version": "1",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "rds:ModifySecurityIps",
        "rds:DescribeDBInstanceIPArrayList"
      ],
      "Resource": "acs:rds:*:*:dbinstance/rm-gs50m8i4y99y7t087ko"
    },
    {
      "Effect": "Allow",
      "Action": [
        "kvstore:ModifySecurityIps",
        "kvstore:DescribeSecurityIps"
      ],
      "Resource": "acs:kvstore:*:*:instance/r-gs5ikwkbfv6lktajt7"
    }
  ]
}
```

Replace the instance ids with yours. **Do not** use the root account AccessKey.

### 2. Create dedicated whitelist groups

- **RDS**: Console -> RDS -> instance -> Whitelist Settings -> Add a Whitelist Group. Name it `dev_dynamic` (or whatever you put in `ALIYUN_RDS_GROUP_NAME`). Leave it empty.
- **Redis**: Console -> Redis -> instance -> Whitelist Settings -> Add a Whitelist Group. Name it `dev_dynamic` to match. The default group on Redis is named `default`; leave that alone.

### 3. Configure the script

```powershell
cd tools\aliyun-rds-whitelist
Copy-Item .env.example .env
notepad .env
```

Fill in `ALIYUN_ACCESS_KEY_ID`, `ALIYUN_ACCESS_KEY_SECRET`. The instance ids and region (`ap-southeast-1` for Singapore) are already prefilled. To skip the Redis target, blank out `ALIYUN_REDIS_INSTANCE_ID`.

Region cheat sheet (matches the suffix of the connection string host):

| Host suffix | Region id |
|---|---|
| `mysql.singapore.rds.aliyuncs.com` / `redis.singapore.rds.aliyuncs.com` | `ap-southeast-1` |
| `mysql.cn-hongkong.rds.aliyuncs.com` | `cn-hongkong` |
| `mysql.us-east-1.rds.aliyuncs.com` | `us-east-1` |

### 4. Run once manually to test

```powershell
powershell -ExecutionPolicy Bypass -File .\update-whitelist.ps1 -Verbose
```

Expected: prints the public IP, calls each configured target's API, writes `.last-ip.cache`. Confirm in the Aliyun Console that both groups now contain your IP. Then verify TCP reachability:

```powershell
Test-NetConnection rm-gs50m8i4y99y7t087ko.mysql.singapore.rds.aliyuncs.com -Port 3306
Test-NetConnection r-gs5ikwkbfv6lktajt7.redis.singapore.rds.aliyuncs.com -Port 6379
```

Both should report `TcpTestSucceeded : True` within ~30 seconds of the API call returning.

### 5. Install the scheduled task

```powershell
powershell -ExecutionPolicy Bypass -File .\install-scheduled-task.ps1
```

Runs as the current user (no admin needed). Triggers: at logon + every 10 minutes. Logs to `update-whitelist.log`.

To remove later:
```powershell
powershell -ExecutionPolicy Bypass -File .\install-scheduled-task.ps1 -Uninstall
```

## Force-update (when cache is stale)

```powershell
powershell -ExecutionPolicy Bypass -File .\update-whitelist.ps1 -Force
```

## Troubleshooting

- **`Aliyun API call failed: ... InvalidAccessKeyId.NotFound`** — RAM user disabled or AccessKey rotated; regenerate.
- **`...IncompleteSignature`** — system clock drifted more than 15 minutes from UTC. Sync via `w32tm /resync`.
- **`...Forbidden.RAM`** — policy not attached or instance id mismatch. Check the policy includes both `rds:ModifySecurityIps` and `kvstore:ModifySecurityIps` if you use Redis.
- **`Test-NetConnection ... -Port 3306|6379` still false after script success** — propagation can take ~30 seconds. Confirm the script wrote to the right group (RDS uses `DBInstanceIPArrayName`, Redis uses `SecurityIpGroupName`).
- **Cache says "IP unchanged" but cannot connect** — your IP changed back to a previously cached value, or another whitelist group blocks. Run with `-Force`.
- **Partial failure** — script shows warnings then throws. Cache is intentionally NOT updated, so the next run will retry. Fix the failing target's policy/instance id first.

## Security notes

- `.env`, `.last-ip.cache`, `update-whitelist.log` are gitignored.
- The RAM policy is scoped to specific instance ids and to whitelist actions only. If the AccessKey leaks, the worst the attacker can do is rewrite whitelists on those exact instances.
- For production deployments do **not** use this script. Use a static VPC bastion or VPN with private-network whitelist instead.
- Rotate your AccessKey periodically (Aliyun Console -> RAM -> User -> AccessKeys). The `.env` is the only file that needs updating.
