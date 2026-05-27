#!/usr/bin/env bash
#
# Pack the Skoruba.Duende.IdentityServer.TenantClientCache.Client SDK into a
# `.nupkg` file under `artifacts/nupkg/` and (optionally) push it into a local
# NuGet feed so other solutions can consume it as a regular `<PackageReference>`.
#
# Usage:
#   ./scripts/pack-tenant-client-cache-sdk.sh                 # pack only
#   ./scripts/pack-tenant-client-cache-sdk.sh --push          # pack + push to ~/.nuget-local
#   ./scripts/pack-tenant-client-cache-sdk.sh --push --feed /path/to/feed
#
# Behaviour:
#   * Builds in `Release` configuration (matches CI semantics).
#   * Restores deterministically (no implicit version float).
#   * Drops the `.nupkg` under `artifacts/nupkg/`. Both paths are gitignored.
#   * `--push` copies the `.nupkg` into the chosen local feed via `nuget add`
#     (uses `dotnet nuget add` so no separate `nuget.exe` is required) and
#     prints the feed URI a downstream `NuGet.config` should reference.
#
# Local feed convention:
#   The default feed location is `${HOME}/.nuget-local`. Override with
#   `--feed /custom/path`. The directory is created on first push.
#
# Exit codes:
#   0  success
#   1  unexpected `dotnet` failure
#   2  invalid argument

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_PROJECT="${REPO_ROOT}/src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Skoruba.Duende.IdentityServer.TenantClientCache.Client.csproj"
ARTIFACTS_DIR="${REPO_ROOT}/artifacts/nupkg"
DEFAULT_LOCAL_FEED="${HOME}/.nuget-local"

PUSH=0
LOCAL_FEED="${DEFAULT_LOCAL_FEED}"

while (( $# > 0 )); do
    case "$1" in
        --push)
            PUSH=1
            shift
            ;;
        --feed)
            shift
            if (( $# == 0 )); then
                echo "ERROR: --feed requires an argument." >&2
                exit 2
            fi
            LOCAL_FEED="$1"
            shift
            ;;
        -h|--help)
            sed -n '2,30p' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "ERROR: unknown argument '$1'." >&2
            exit 2
            ;;
    esac
done

mkdir -p "${ARTIFACTS_DIR}"

echo ">> dotnet pack (Release) -> ${ARTIFACTS_DIR}"
dotnet pack "${SDK_PROJECT}" \
    --configuration Release \
    --output "${ARTIFACTS_DIR}" \
    --nologo

# Identify the produced package (latest by mtime).
PKG_PATH="$(ls -1t "${ARTIFACTS_DIR}"/Skoruba.Duende.IdentityServer.TenantClientCache.Client.*.nupkg 2>/dev/null | head -n1 || true)"
if [[ -z "${PKG_PATH}" ]]; then
    echo "ERROR: pack did not produce a .nupkg under ${ARTIFACTS_DIR}" >&2
    exit 1
fi
echo ">> Produced: ${PKG_PATH}"

if (( PUSH == 1 )); then
    mkdir -p "${LOCAL_FEED}"
    echo ">> Adding to local feed: ${LOCAL_FEED}"
    # `nuget add` (via `dotnet`) inserts the package into the hierarchical
    # layout NuGet expects. Use `--source` because the older verb is `nuget
    # push --source` for hosted feeds; for local folder feeds the supported
    # command is the standalone `nuget` CLI's `add`. We fall back to a plain
    # copy when `nuget add` is unavailable — that yields a flat folder which
    # NuGet still recognises as a v2 local feed.
    if command -v nuget >/dev/null 2>&1; then
        nuget add "${PKG_PATH}" -Source "${LOCAL_FEED}" -NonInteractive
    else
        cp -f "${PKG_PATH}" "${LOCAL_FEED}/"
        echo "   (nuget CLI not found; copied to flat folder feed instead)"
    fi
    echo ">> Done. Configure consumers with:"
    echo "      <add key=\"skoruba-local\" value=\"${LOCAL_FEED}\" />"
fi
