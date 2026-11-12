#!/usr/bin/env bash
# Source this file to export the BIFROST_TEST_* connection strings for the
# local docker-compose.test.yml containers into a shell (benchmarks, ad-hoc
# tools). The values are read from
# tests/BifrostQL.Integration.Test/local-databases.runsettings, the only copy;
# `dotnet test` picks that file up on its own and needs no sourcing.
#
#   docker compose -f docker-compose.test.yml up -d --wait
#   source scripts/test-env.sh

_bifrost_runsettings="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/tests/BifrostQL.Integration.Test/local-databases.runsettings"
while IFS='|' read -r _bifrost_name _bifrost_value; do
  export "$_bifrost_name=$_bifrost_value"
done < <(sed -n 's|^ *<\(BIFROST_TEST_[A-Z]*\)>\(.*\)</BIFROST_TEST_[A-Z]*>$|\1\|\2|p' "$_bifrost_runsettings")
unset _bifrost_runsettings _bifrost_name _bifrost_value
