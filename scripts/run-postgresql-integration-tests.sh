#!/usr/bin/env bash
set -euo pipefail
# Fail instead of skipping when a test cannot find its database connection.
export REPLICERA_INTEGRATION_REQUIRED=1

if [[ -n "${REPLICERA_POSTGRES_TEST_CONNECTION_STRING:-}" ]]; then
    dotnet test tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj \
        --configuration Release \
        --filter 'Category=PostgreSqlIntegration'
    exit
fi

container_name="replicera-postgresql-tests-$RANDOM-$RANDOM"
password="replicera_test_$RANDOM$RANDOM"
cleanup() {
    docker rm -f "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run --detach --name "$container_name" \
    --env POSTGRES_PASSWORD="$password" \
    --publish 127.0.0.1::5432 \
    postgres:17-alpine >/dev/null

port="$(docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' "$container_name")"
postgres_ready=false
for _ in $(seq 1 60); do
    if docker exec "$container_name" pg_isready --username postgres >/dev/null 2>&1; then
        # The image briefly starts a temporary server during initialization.
        # Require readiness to remain stable across that server's restart.
        sleep 2
        if docker exec "$container_name" pg_isready --username postgres >/dev/null 2>&1; then
            postgres_ready=true
            break
        fi
    fi
    sleep 1
done

if [[ "$postgres_ready" != true ]]; then
    docker logs "$container_name" >&2
    echo "PostgreSQL did not become ready within 60 attempts." >&2
    exit 1
fi

export REPLICERA_POSTGRES_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Password=$password;Pooling=false"
dotnet test tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj \
    --configuration Release \
    --filter 'Category=PostgreSqlIntegration'
