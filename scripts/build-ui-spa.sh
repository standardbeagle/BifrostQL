#!/usr/bin/env bash
# Builds the desktop UI SPA into src/BifrostQL.UI/wwwroot: install the pnpm
# workspace, build the workspace packages the SPA imports (topological order, so
# their dist/types exist for the SPA's tsc), then the SPA itself.
#
# wwwroot is gitignored Vite output, yet BifrostQL.UI.Tests serves it, and the
# BifrostQL.UI build copies it into bin. So it must be built before the dotnet
# build that runs the tests. CI (dotnet.yml, e2e.yml) and the local test
# scripts (test-epic.sh, test-release.sh, test-by-name.sh) all call this one
# script.
#
# A rebuild costs ~85s even when nothing changed, so the script records a hash
# of every input file (tracked and untracked, gitignored excluded) and skips the
# build when wwwroot exists and the hash matches. BIFROST_SPA_FORCE=1 rebuilds
# regardless.
#
#   ./scripts/build-ui-spa.sh
set -euo pipefail
cd "$(dirname "$0")/.."

WWWROOT=src/BifrostQL.UI/wwwroot
STAMP=src/BifrostQL.UI/obj/spa-build-inputs.sha256
DEPS=(--filter "...@standardbeagle/edit-db" --filter "...@bifrostql/binary-client")

pnpm install --frozen-lockfile --prefer-offline

# Every workspace package the SPA build reads, as repo-relative paths.
mapfile -t dep_dirs < <(pnpm "${DEPS[@]}" ls --depth -1 --json \
  | node -e 'const r=process.cwd();for(const p of JSON.parse(require("fs").readFileSync(0,"utf8")))console.log(require("path").relative(r,p.path))')

inputs_hash() {
  git ls-files -z --cached --others --exclude-standard -- \
      pnpm-lock.yaml src/BifrostQL.UI/frontend "${dep_dirs[@]}" \
    | sort -z \
    | xargs -0 sha256sum \
    | sha256sum | cut -d' ' -f1
}

want=$(inputs_hash)
if [[ "${BIFROST_SPA_FORCE:-0}" != 1 && -f "$WWWROOT/index.html" && -f "$STAMP" && "$(cat "$STAMP")" == "$want" ]]; then
  echo "build-ui-spa: $WWWROOT is up to date (inputs $want)"
  exit 0
fi

pnpm "${DEPS[@]}" build
pnpm --dir src/BifrostQL.UI/frontend build

# Hash again: a build that rewrites a tracked or unignored input would make
# every later run rebuild. Fail loudly so that file gets ignored instead.
after=$(inputs_hash)
if [[ "$after" != "$want" ]]; then
  echo "build-ui-spa: the build changed its own inputs; gitignore what it wrote:" >&2
  git status --short -- src/BifrostQL.UI/frontend "${dep_dirs[@]}" >&2
  exit 1
fi

mkdir -p "$(dirname "$STAMP")"
printf '%s\n' "$want" > "$STAMP.tmp"
mv "$STAMP.tmp" "$STAMP"
echo "build-ui-spa: built $WWWROOT (inputs $want)"
