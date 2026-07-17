#!/usr/bin/env bash
# Release-tier test run: the full pre-release gate. Restores the complete
# net8/net9/net10 target matrix (-p:ReleaseTests=true) and runs the Fuzz
# category (pinned seeds — deterministic), which the epic tier skips.
# CI runs this in the release-tests job that gates pack-publish; run it
# locally before cutting a release tag.
#
# For the 4-engine integration suite, start the containers and source the
# connection strings first:
#   docker compose -f docker-compose.test.yml up -d
#   source scripts/test-env.sh
#
#   ./scripts/test-release.sh
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet test BifrostQL.sln -p:ReleaseTests=true "$@"
