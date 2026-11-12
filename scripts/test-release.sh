#!/usr/bin/env bash
# Release-tier test run: the full pre-release gate. Restores the complete
# net8/net9/net10 target matrix (-p:ReleaseTests=true) and runs the Fuzz
# category (pinned seeds — deterministic), which the epic tier skips.
# CI runs this in the release-tests job that gates pack-publish; run it
# locally before cutting a release tag.
#
# The SQL Server/Postgres/MySQL integration suites run against
# docker-compose.test.yml, brought up here; BifrostQL.Integration.Test reads
# their connection strings from local-databases.runsettings.
#
#   ./scripts/test-release.sh
set -euo pipefail
cd "$(dirname "$0")/.."

docker compose -f docker-compose.test.yml up -d --wait

dotnet test BifrostQL.sln -p:ReleaseTests=true "$@"
