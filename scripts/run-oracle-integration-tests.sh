#!/usr/bin/env bash
set -euo pipefail

if [[ -n "${REPLICERA_ORACLE_TEST_CONNECTION_STRING:-}" ]]; then
    dotnet test tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj \
        --configuration Release \
        --filter 'Category=OracleIntegration'
    exit
fi

container_name="replicera-oracle-tests-$RANDOM-$RANDOM"
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
    'sqlplus -s "sys/${ORACLE_PWD}@FREEPDB1 as sysdba"' <<SQL >/dev/null
WHENEVER SQLERROR EXIT SQL.SQLCODE
CREATE USER REPLICERA_TEST IDENTIFIED BY "$app_password";
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, UNLIMITED TABLESPACE TO REPLICERA_TEST;
GRANT EXECUTE ON SYS.DBMS_LOCK TO REPLICERA_TEST;
EXIT
SQL

export REPLICERA_ORACLE_TEST_CONNECTION_STRING="User Id=REPLICERA_TEST;Password=$app_password;Data Source=127.0.0.1:$port/FREEPDB1;Pooling=false"
dotnet test tests/Replicera.IntegrationTests/Replicera.IntegrationTests.csproj \
    --configuration Release \
    --filter 'Category=OracleIntegration'
