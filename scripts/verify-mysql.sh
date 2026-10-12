#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
container="allen-office-build-mysql-$(date +%s)-$$"
cleanup() { docker rm -f "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT
# Disposable loopback-only test database; these credentials never configure production.
docker run --detach --rm --name "$container"   --publish 127.0.0.1::3306   --env MYSQL_RANDOM_ROOT_PASSWORD=yes   --env MYSQL_DATABASE=office_check   --env MYSQL_USER=office_check   --env MYSQL_PASSWORD=local-test-only   mysql:8.4 >/dev/null
ready=false
for attempt in $(seq 1 90); do
  if docker exec "$container" mysql --user=office_check --password=local-test-only --database=office_check --execute='SELECT 1' >/dev/null 2>&1; then
    ready=true; break
  fi
  sleep 1
done
if [ "$ready" != true ]; then
  echo 'The isolated MySQL server did not become ready.' >&2
  exit 1
fi
port=$(docker port "$container" 3306/tcp | awk -F: '{print $NF}')
export OFFICE_TEST_MYSQL="Server=127.0.0.1;Port=$port;Database=office_check;User ID=office_check;Password=local-test-only;SslMode=Disabled;MaximumPoolSize=10;"
dotnet test tests/AllenBuilding.IntegrationTests --configuration Release --filter FullyQualifiedName~MySql_migrations
