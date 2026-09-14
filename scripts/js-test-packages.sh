#!/usr/bin/env bash
# Prints the absolute directory of every pnpm workspace package whose `test`
# script runs vitest, one per line. Derived from the workspace rather than
# hard-coded so a new package joins the inventory by existing, not by being
# added to a filter list (package.json's `test:js` filter list is the tier
# gate and is deliberately separate).
#
# Playwright packages (tests/BifrostQL.UI.E2E) are excluded: their test script
# is not vitest and enumerating them needs an installed browser.
set -euo pipefail
cd "$(dirname "$0")/.."

pnpm ls -r --depth -1 --json 2>/dev/null \
  | jq -r '.[] | select(.path) | .path' \
  | while IFS= read -r dir; do
      case "$(jq -r '.scripts.test // ""' "$dir/package.json" 2>/dev/null)" in
        *vitest*) printf '%s\n' "$dir" ;;
      esac
    done
