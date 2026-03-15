#!/usr/bin/env bash
# Orchestrates BifrostQL.UI dev mode:
#   1. Builds edit-db library (watch mode, background)
#   2. Starts .NET backend (watch mode, no hot-reload to prevent zombie listeners)
#   3. Starts Vite dev server once backend is healthy
#
# Usage: ./dev-ui.sh [--port 5000] [-- <dotnet-args>]

set -euo pipefail

PORT="${BIFROST_DEV_PORT:-5000}"
DOTNET_ARGS=()
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Parse args
while [[ $# -gt 0 ]]; do
    case "$1" in
        --port) PORT="$2"; shift 2 ;;
        --) shift; DOTNET_ARGS=("$@"); break ;;
        *) DOTNET_ARGS+=("$1"); shift ;;
    esac
done

# Track child PIDs for cleanup
PIDS=()
cleanup() {
    echo ""
    echo "[dev-ui] Shutting down..."
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null || true
    done
    # Kill entire process group to catch grandchildren
    kill 0 2>/dev/null || true
    wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

kill_port() {
    local pids
    pids=$(lsof -ti :"$1" 2>/dev/null || true)
    if [[ -n "$pids" ]]; then
        echo "[dev-ui] Killing stale processes on port $1: $pids"
        echo "$pids" | xargs kill 2>/dev/null || true
        sleep 1
    fi
}

wait_for_port() {
    local port=$1 timeout=${2:-60} elapsed=0
    echo "[dev-ui] Waiting for port $port..."
    while ! curl -sf "http://localhost:$port/api/health" >/dev/null 2>&1; do
        sleep 1
        elapsed=$((elapsed + 1))
        if [[ $elapsed -ge $timeout ]]; then
            echo "[dev-ui] ERROR: Backend did not start within ${timeout}s"
            exit 1
        fi
        if [[ $((elapsed % 10)) -eq 0 ]]; then
            echo "[dev-ui] Still waiting for backend... (${elapsed}s)"
        fi
    done
    echo "[dev-ui] Backend is healthy on port $port"
}

# Ensure port is free
kill_port "$PORT"

# Step 1: Build edit-db in watch mode (background)
echo "[dev-ui] Starting edit-db build (watch mode)..."
cd "$SCRIPT_DIR/examples/edit-db"
pnpm vite build --watch --mode production &
PIDS+=($!)
cd "$SCRIPT_DIR"

# Step 2: Start .NET backend with dotnet watch (no hot-reload to prevent zombie listeners)
echo "[dev-ui] Starting .NET backend on port $PORT..."
dotnet watch run \
    --no-hot-reload \
    --project src/BifrostQL.UI/BifrostQL.UI.csproj \
    -- --headless --port "$PORT" "${DOTNET_ARGS[@]}" &
PIDS+=($!)

# Step 3: Wait for backend, then start Vite dev server
wait_for_port "$PORT" 120

echo "[dev-ui] Starting Vite dev server..."
cd "$SCRIPT_DIR/src/BifrostQL.UI/frontend"
pnpm dev &
PIDS+=($!)
cd "$SCRIPT_DIR"

echo "[dev-ui] All services running:"
echo "  Backend:  http://localhost:$PORT"
echo "  Frontend: http://localhost:5173 (Vite HMR)"
echo "  Press Ctrl+C to stop all"

# Wait for any child to exit
wait
