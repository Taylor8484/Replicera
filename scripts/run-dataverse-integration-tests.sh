#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
environment_file="${REPLICERA_ENV_FILE:-$repository_root/.env.local}"

if [[ -f "$environment_file" ]]; then
    # shellcheck disable=SC1090
    source "$environment_file"
fi

required_variables=(
    REPLICERA_DATAVERSE_URL
    REPLICERA_DATAVERSE_TENANT_ID
    REPLICERA_DATAVERSE_CLIENT_ID
    REPLICERA_DATAVERSE_CLIENT_SECRET
)
for variable in "${required_variables[@]}"; do
    if [[ -z "${!variable:-}" ]]; then
        echo "$variable is required for credentialed Dataverse tests." >&2
        exit 2
    fi
done

dotnet test "$repository_root/tests/Replicera.Dataverse.Tests/Replicera.Dataverse.Tests.csproj" \
    --configuration Release \
    --filter 'Category=Credentialed&Category!=CheckpointExpiry' \
    --logger 'console;verbosity=detailed'
