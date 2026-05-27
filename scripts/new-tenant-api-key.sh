#!/usr/bin/env bash
#
# Generate a fresh tenant API key for the public-read endpoint.
#
# OUTPUT
#   - PLAINTEXT (≥ 32 random bytes, base64-url-safe-ish, ~43 chars). Goes
#     into the BFF / SDK consumer config:
#       MobileBff__TenantClientCache__ApiKey=<plaintext>
#       (or, for any other consumer, TenantClientCacheClient:ApiKey)
#   - SHA-256 HEX (64 lowercase hex chars). Goes into the Admin host:
#       TenantClientCachePublicRead__ApiKeys__<tenantKey>=<sha256-hex>
#
# Usage:
#   ./scripts/new-tenant-api-key.sh                    # prints both values
#   ./scripts/new-tenant-api-key.sh --tenant acme      # also prints
#                                                       the env-var lines
#                                                       you can paste
#
# IMPORTANT
#   - The plaintext NEVER lives on the Admin host. It MUST be stored in a
#     secret manager (env var, Vault, AWS Secrets Manager, etc.) on the
#     BFF / consumer side.
#   - Do NOT commit the printed values to git. The script writes nothing
#     to disk.

set -euo pipefail

TENANT=""

while (( $# > 0 )); do
    case "$1" in
        --tenant)
            shift
            TENANT="${1:-}"
            shift || true
            ;;
        -h|--help)
            sed -n '2,28p' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "ERROR: unknown argument '$1'." >&2
            exit 2
            ;;
    esac
done

# 32 random bytes -> base64 -> strip padding/+/=/ to keep the value
# header-safe. Length lands around 43 chars; SHA-256 input length is
# unconstrained so this is just a convention for sane copy/paste.
PLAINTEXT="$(openssl rand -base64 32 | tr -d '=+/\n')"
HASH="$(printf '%s' "${PLAINTEXT}" | shasum -a 256 | awk '{print $1}')"

echo "PLAINTEXT  -> ${PLAINTEXT}"
echo "SHA256 hex -> ${HASH}"

if [[ -n "${TENANT}" ]]; then
    NORMALIZED="$(printf '%s' "${TENANT}" | tr '[:upper:]' '[:lower:]')"
    echo
    echo "Paste these env vars where appropriate:"
    echo
    echo "# 1) Admin host (stores the HASH; rotates the digest only):"
    echo "export TenantClientCachePublicRead__ApiKeys__${NORMALIZED}=${HASH}"
    echo
    echo "# 2) BFF host / SDK consumer (stores the PLAINTEXT; sent in X-Tenant-Api-Key):"
    echo "export MobileBff__TenantClientCache__ApiKey=${PLAINTEXT}"
fi
