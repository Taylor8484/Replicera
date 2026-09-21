#!/usr/bin/env bash
set -euo pipefail

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
for _ in $(seq 1 60); do
    if docker exec "$container_name" pg_isready --username postgres >/dev/null 2>&1; then
        break
    fi
    sleep 1
done
docker exec "$container_name" pg_isready --username postgres >/dev/null

export REPLICERA_POSTGRES_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Password=$password;Pooling=false"
dotnet test tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj \
    --configuration Release \
    --filter 'Category=PostgreSqlIntegration'
