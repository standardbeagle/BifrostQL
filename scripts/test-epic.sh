#!/usr/bin/env bash
# Epic-tier test run: the fast gate for slice/epic work and the PR/main CI job.
# Runs every test project on the single current TFM (net10.0) and excludes the
# Fuzz category. The full net8/net9/net10 matrix and the fuzzers run in the
# release tier instead: scripts/test-release.sh, gated before pack-publish.
#
#   ./scripts/test-epic.sh                # whole solution, epic tier
#   ./scripts/test-epic.sh tests/BifrostQL.Server.Test   # one project
set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="${1:-BifrostQL.sln}"

dotnet test "$TARGET" --filter "Category!=Fuzz" "${@:2}"
