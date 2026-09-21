#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
environment_file="${REPLICERA_ENV_FILE:-$repository_root/.env.local}"
state_file="${REPLICERA_EXPIRED_CHECKPOINT_STATE:-$repository_root/.replicera-test-state/account-checkpoint.json}"

if [[ -f "$environment_file" ]]; then
    # shellcheck disable=SC1090
    source "$environment_file"
fi

if [[ ! -f "$state_file" ]]; then
    echo "Checkpoint state file not found: $state_file" >&2
    exit 2
fi

export REPLICERA_EXPIRED_CHECKPOINT_STATE="$state_file"
dotnet test "$repository_root/tests/Replicera.Dataverse.Tests/Replicera.Dataverse.Tests.csproj" \
    --configuration Release \
    --filter 'Category=CheckpointExpiry' \
    --logger 'console;verbosity=detailed'
