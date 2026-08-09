#!/usr/bin/env bash
# Reaps THIS checkout's stray development processes: the E2E headless server and
# its `dotnet run` launcher, `dotnet watch` on BifrostQL.UI, the Vite/esbuild
# watchers started by dev-ui.sh, and orphaned Playwright browsers.
#
# Every rule is anchored to this repository's own paths, marker files, or to
# processes that provably have no live owner. It will not touch a personal
# browser, an unrelated node/dotnet, or another checkout's processes.
#
#   ./scripts/kill-dev-processes.sh          # dry run (default): list only
#   ./scripts/kill-dev-processes.sh --kill   # actually reap them
#
# Kills escalate SIGTERM -> 10s wait -> SIGKILL, and anything that survives is
# reported with a non-zero exit.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UI_CSPROJ="$REPO_ROOT/src/BifrostQL.UI/BifrostQL.UI.csproj"
UI_BIN="$REPO_ROOT/src/BifrostQL.UI/bin/"
E2E_PID_FILE="$REPO_ROOT/tests/BifrostQL.UI.E2E/.server-pid"

DRY_RUN=1
case "${1:-}" in
    "")           ;;
    --kill|-k)    DRY_RUN=0 ;;
    --dry-run|-n) DRY_RUN=1 ;;
    -h|--help)    sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)            echo "unknown option: $1 (try --help)" >&2; exit 2 ;;
esac

# --- protect the caller ------------------------------------------------------
# This script runs from inside the repo, so its own shell and every ancestor
# (the terminal, an agent process, an IDE) can match a cwd-based rule.
declare -A PROTECTED=()
ancestor=$$
while [[ "$ancestor" -gt 1 ]]; do
    PROTECTED[$ancestor]=1
    ancestor=$(ps -o ppid= -p "$ancestor" 2>/dev/null | tr -d ' ' || true)
    [[ -n "$ancestor" ]] || break
done

declare -A MATCHED=()   # pid -> reason

remember() {
    local pid="$1" reason="$2"
    [[ -n "${PROTECTED[$pid]:-}" ]] && return 0
    [[ -n "${MATCHED[$pid]:-}" ]] && return 0
    kill -0 "$pid" 2>/dev/null || return 0
    MATCHED[$pid]="$reason"
}

proc_cwd()     { readlink -f "/proc/$1/cwd" 2>/dev/null || true; }
proc_cmdline() { cat "/proc/$1/cmdline" 2>/dev/null | tr '\0' ' ' || true; }
proc_ppid()    { ps -o ppid= -p "$1" 2>/dev/null | tr -d ' ' || true; }

# --- rule 1: whatever the E2E marker file points at --------------------------
# globalSetup writes the server's process-GROUP id here. A file left behind by a
# crashed run is exactly the case teardown could not clean up.
if [[ -f "$E2E_PID_FILE" ]]; then
    pgid="$(tr -dc '0-9' < "$E2E_PID_FILE")"
    if [[ -n "$pgid" ]]; then
        while read -r pid gid; do
            [[ "$gid" == "$pgid" ]] && remember "$pid" "E2E server group ($pgid) from .server-pid"
        done < <(ps -eo pid=,pgid=)
    fi
fi

# --- rules 2-4: repository-anchored dev processes ----------------------------
for pid_dir in /proc/[0-9]*; do
    pid="${pid_dir#/proc/}"
    cmd="$(proc_cmdline "$pid")"
    [[ -n "$cmd" ]] || continue
    cwd="$(proc_cwd "$pid")"

    # rule 2: the E2E / dev headless server built into this checkout
    if [[ "$cmd" == *"$UI_BIN"* ]]; then
        remember "$pid" "BifrostQL.UI server from this checkout"
        continue
    fi

    # rule 3: dotnet run / dotnet watch driving THIS checkout's UI project.
    # dev-ui.sh passes the project as a RELATIVE path, so accept either an
    # absolute path into this checkout or a relative one resolved by the cwd.
    if [[ "$cmd" == *"dotnet"* && "$cmd" == *"BifrostQL.UI.csproj"* ]]; then
        if [[ "$cmd" == *"$UI_CSPROJ"* || "$cwd" == "$REPO_ROOT"* ]]; then
            remember "$pid" "dotnet run/watch on this checkout's BifrostQL.UI"
            continue
        fi
    fi

    # rule 4: Vite / esbuild watchers whose working directory is in this checkout
    if [[ "$cmd" == *vite* || "$cmd" == *esbuild* ]]; then
        if [[ "$cwd" == "$REPO_ROOT"* || "$cmd" == *"$REPO_ROOT"* ]]; then
            remember "$pid" "Vite/esbuild watcher in this checkout"
        fi
    fi
done

# --- rule 5: orphaned Playwright browsers ------------------------------------
# A browser belonging to a LIVE run (this checkout's or anyone else's) is a child
# of its runner. Requiring ppid == 1 means the owning runner is already gone, so
# the process can only be a leak — and a developer's own browser never matches
# the Playwright browser cache or its temp profile.
while read -r pid; do
    [[ -n "$pid" ]] || continue
    cmd="$(proc_cmdline "$pid")"
    [[ "$cmd" == *ms-playwright* || "$cmd" == *"--user-data-dir=/tmp/playwright"* ]] || continue
    [[ "$(proc_ppid "$pid")" == "1" ]] || continue
    remember "$pid" "orphaned Playwright browser (owning runner is gone)"
done < <(ps -eo pid= )

# --- report ------------------------------------------------------------------
if [[ ${#MATCHED[@]} -eq 0 ]]; then
    echo "No stray BifrostQL dev processes found for $REPO_ROOT"
    exit 0
fi

echo "Stray dev processes for $REPO_ROOT:"
for pid in "${!MATCHED[@]}"; do
    args="$(ps -o args= -p "$pid" 2>/dev/null | cut -c1-110 || true)"
    printf '  %-8s %-52s %s\n' "$pid" "${MATCHED[$pid]}" "$args"
done

if [[ $DRY_RUN -eq 1 ]]; then
    echo
    echo "Dry run — nothing was killed. Re-run with --kill to reap them."
    exit 0
fi

# --- escalate: SIGTERM -> bounded wait -> SIGKILL ----------------------------
echo
echo "Sending SIGTERM..."
for pid in "${!MATCHED[@]}"; do kill -TERM "$pid" 2>/dev/null || true; done

for _ in $(seq 1 20); do
    remaining=0
    for pid in "${!MATCHED[@]}"; do kill -0 "$pid" 2>/dev/null && remaining=1; done
    [[ $remaining -eq 0 ]] && break
    sleep 0.5
done

survivors=()
for pid in "${!MATCHED[@]}"; do kill -0 "$pid" 2>/dev/null && survivors+=("$pid"); done

if [[ ${#survivors[@]} -gt 0 ]]; then
    echo "Escalating to SIGKILL: ${survivors[*]}"
    for pid in "${survivors[@]}"; do kill -KILL "$pid" 2>/dev/null || true; done
    sleep 2
fi

leaked=()
for pid in "${!MATCHED[@]}"; do kill -0 "$pid" 2>/dev/null && leaked+=("$pid"); done

if [[ ${#leaked[@]} -gt 0 ]]; then
    echo "ERROR: survived SIGKILL (uninterruptible or not ours to signal): ${leaked[*]}" >&2
    exit 1
fi

echo "Reaped ${#MATCHED[@]} process(es)."
