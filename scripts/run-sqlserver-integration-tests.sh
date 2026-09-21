#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj"

if [[ -n "${REPLICERA_SQL_TEST_CONNECTION_STRING:-}" ]]; then
    dotnet test "$repository_root/$test_project" --filter 'Category=SqlServerIntegration'
    exit 0
fi

command -v docker >/dev/null || {
    echo "Docker is required when REPLICERA_SQL_TEST_CONNECTION_STRING is not set." >&2
    exit 1
}

container_name="replicera-sql-test-$RANDOM-$RANDOM"
test_password="R${RANDOM}x${RANDOM}Y${RANDOM}!9a"
cleanup() {
    docker rm -f "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run --detach --name "$container_name" \
    --env ACCEPT_EULA=Y \
    --env MSSQL_SA_PASSWORD="$test_password" \
    --publish 127.0.0.1::1433 \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null

for attempt in $(seq 1 60); do
    if docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$test_password" -C -Q 'SELECT 1' >/dev/null 2>&1; then
        break
    fi

    if [[ "$attempt" == 60 ]]; then
        docker logs --tail 100 "$container_name" >&2
        echo "SQL Server did not become ready within 60 seconds." >&2
        exit 1
    fi
    sleep 1
done

published_endpoint="$(docker port "$container_name" 1433/tcp)"
published_port="${published_endpoint##*:}"
export REPLICERA_SQL_TEST_CONNECTION_STRING="Server=127.0.0.1,$published_port;User ID=sa;Password=$test_password;Encrypt=True;TrustServerCertificate=True;Connect Timeout=10"

dotnet test "$repository_root/$test_project" --filter 'Category=SqlServerIntegration'
