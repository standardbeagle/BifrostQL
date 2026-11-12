#!/usr/bin/env bash
# Epic-tier test run: the fast gate for slice/epic work and the PR/main CI job.
# Runs every test project on the single current TFM (net10.0) and excludes the
# Fuzz category. The full net8/net9/net10 matrix and the fuzzers run in the
# release tier instead: scripts/test-release.sh, gated before pack-publish.
#
#   ./scripts/test-epic.sh                # whole solution, epic tier
#   ./scripts/test-epic.sh tests/BifrostQL.Server.Test   # one project
#
# The SQL Server/Postgres/MySQL integration suites run against
# docker-compose.test.yml, brought up here; BifrostQL.Integration.Test reads
# their connection strings from local-databases.runsettings.
#
# The UI host tests serve the desktop SPA from wwwroot (gitignored Vite output),
# so scripts/build-ui-spa.sh builds it first; it skips when nothing changed.
set -euo pipefail
cd "$(dirname "$0")/.."

docker compose -f docker-compose.test.yml up -d --wait
./scripts/build-ui-spa.sh

TARGET="${1:-BifrostQL.sln}"

dotnet test "$TARGET" --filter "Category!=Fuzz" "${@:2}"
