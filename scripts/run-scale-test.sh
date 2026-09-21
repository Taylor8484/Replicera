#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_GCHeapHardLimit=0x10000000

dotnet test "$repository_root/tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj" \
    --configuration Release \
    --filter 'Category=Scale' \
    --logger 'console;verbosity=detailed'
