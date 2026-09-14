#!/usr/bin/env bash
# Runs exactly the test(s) named by "$1", an exact name as printed by
# scripts/list-tests.sh. Extra args pass through to the underlying runner.
#
#   ./scripts/test-by-name.sh 'BifrostQL.UI.Tests.VaultStoreTests.Load_MissingFile_ReturnsEmptyVault'
#   ./scripts/test-by-name.sh 'src/codegen.test.ts > proto-parser > parses a two-message schema with comments and primitives'
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="${1:?usage: test-by-name.sh <exact name from list-tests.sh> [runner args...]}"
shift

if [[ "$NAME" == *" > "* ]]; then
  # vitest dialect. First segment is the file, the rest is the suite/test chain.
  file="${NAME%% > *}"
  leaf="${NAME##* > }"
  matched=0
  while IFS= read -r pkg; do
    [ -f "$pkg/$file" ] || continue
    matched=1
    pnpm --dir "$pkg" exec vitest run "$file" -t "$leaf" "$@"
  done < <(./scripts/js-test-packages.sh)
  if [ "$matched" -eq 0 ]; then
    echo "test-by-name: no workspace package contains $file" >&2
    exit 1
  fi
  exit 0
fi

# .NET dialect. Filter on DisplayName, which is what --list-tests prints and
# the only field carrying a theory's arguments. Two escapes stack: backslashes
# for the VSTest filter grammar, then MSBuild %XX for the property value —
# a raw comma or quote in a theory name is otherwise parsed as MSBuild syntax
# and fails the run with MSB4177.
filter=$(printf '%s' "$NAME" \
  | sed -e 's/[\\()&|=!~]/\\&/g' -e 's/%/%25/g' -e 's/,/%2C/g' -e 's/"/%22/g' -e 's/;/%3B/g')

dotnet test BifrostQL.sln "-p:VSTestTestCaseFilter=DisplayName=$filter" "$@"
