#!/usr/bin/env bash
# Prints EVERY test name in the repo, one per line on stdout, exit 0.
#
# Names are in the native dialect of the framework that owns them:
#   .NET    Namespace.Class.Method            (theory args included)
#   vitest  path/file.test.ts > suite > test  (path relative to its package)
# The two forms are unambiguous, so both stacks concatenate into one list.
#
# This is the project-supplied test inventory: the automation loop diffs a
# plan's declared test names against it without knowing what a test framework
# is. Machine-readable, so build chatter goes to stderr and only names reach
# stdout.
set -euo pipefail
cd "$(dirname "$0")/.."

# Build first and list with --no-build: `dotnet test --list-tests` otherwise
# interleaves MSBuild output with the indented test names on stdout.
dotnet build BifrostQL.sln --nologo -v:q 1>&2

# VSTest indents each name by four spaces and leaves its banners flush left.
dotnet test BifrostQL.sln --no-build --list-tests | sed -n 's/^    //p'

while IFS= read -r pkg; do
  pnpm --dir "$pkg" exec vitest list --passWithNoTests
done < <(./scripts/js-test-packages.sh)
