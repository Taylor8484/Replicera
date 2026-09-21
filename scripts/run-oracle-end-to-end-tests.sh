#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
environment_file="${REPLICERA_ENV_FILE:-$repository_root/.env.local}"

if [[ -f "$environment_file" ]]; then
    # shellcheck disable=SC1090
    source "$environment_file"
fi

for variable in REPLICERA_DATAVERSE_URL REPLICERA_DATAVERSE_TENANT_ID REPLICERA_DATAVERSE_CLIENT_ID REPLICERA_DATAVERSE_CLIENT_SECRET; do
    if [[ -z "${!variable:-}" ]]; then
        echo "$variable is required for end-to-end tests." >&2
        exit 2
    fi
done

command -v docker >/dev/null || {
    echo "Docker is required for end-to-end tests." >&2
    exit 1
}

container_name="replicera-oracle-e2e-$RANDOM-$RANDOM"
admin_password="RepliceraAdmin${RANDOM}${RANDOM}aA1"
app_password="RepliceraApp${RANDOM}${RANDOM}aA1"
cleanup() {
    docker rm -f "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker run --detach --name "$container_name" \
    --env ORACLE_PWD="$admin_password" \
    --publish 127.0.0.1::1521 \
    container-registry.oracle.com/database/free:latest >/dev/null

port="$(docker inspect --format '{{(index (index .NetworkSettings.Ports "1521/tcp") 0).HostPort}}' "$container_name")"
for _ in $(seq 1 180); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}starting{{end}}' "$container_name")"
    if [[ "$status" == "healthy" ]]; then
        break
    fi
    if [[ "$status" == "unhealthy" ]]; then
        docker logs --tail 100 "$container_name"
        exit 1
    fi
    sleep 2
done
test "$(docker inspect --format '{{.State.Health.Status}}' "$container_name")" = "healthy"

docker exec -i "$container_name" bash -lc \
    'sqlplus -s "system/${ORACLE_PWD}@FREEPDB1"' <<SQL >/dev/null
WHENEVER SQLERROR EXIT SQL.SQLCODE
CREATE USER REPLICERA_E2E IDENTIFIED BY "$app_password";
GRANT CREATE SESSION, CREATE TABLE, UNLIMITED TABLESPACE TO REPLICERA_E2E;
EXIT
SQL

export REPLICERA_ORACLE_TEST_CONNECTION_STRING="User Id=REPLICERA_E2E;Password=$app_password;Data Source=127.0.0.1:$port/FREEPDB1;Pooling=false"
dotnet test "$repository_root/tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj" \
    --configuration Release \
    --filter 'Category=DataverseOracleEndToEnd' \
    --logger 'console;verbosity=detailed'
