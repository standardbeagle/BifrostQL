#!/bin/bash
# Build the BifrostQL UI Windows installer (Velopack Setup.exe) from Linux/WSL2.
#
# Prereqs: dotnet 10 SDK, pnpm (see root packageManager), and the Velopack CLI
# (`dotnet tool install -g vpk`, version matching the Velopack PackageReference
# in src/BifrostQL.UI/BifrostQL.UI.csproj).
#
# Output: dist/installer/BifrostUI-win-Setup.exe (plus portable zip + nupkg).
# The Setup.exe installs per-user to %LocalAppData%\BifrostUI with Desktop and
# Start Menu shortcuts; run it with --silent for unattended install.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/BifrostQL.UI/BifrostQL.UI.csproj"
PUBLISH_DIR="$REPO_ROOT/dist/win-x64"
INSTALLER_DIR="$REPO_ROOT/dist/installer"
VPK="${VPK:-$HOME/.dotnet/tools/vpk}"

VERSION=$(grep -o '<Version>[^<]*' "$CSPROJ" | cut -d'>' -f2)
[ -n "$VERSION" ] || { echo "error: no <Version> in $CSPROJ" >&2; exit 1; }

echo "=== Building frontend ==="
pnpm --dir "$REPO_ROOT/src/BifrostQL.UI/frontend" build

echo "=== Publishing bifrostui for win-x64 (v$VERSION) ==="
rm -rf "$PUBLISH_DIR"
dotnet publish "$CSPROJ" -c Release -r win-x64 --self-contained true -o "$PUBLISH_DIR"

echo "=== Packing Windows installer ==="
# vpk refuses to pack over an equal-or-newer release in the output dir; local
# rebuilds of the same version are the normal case here, so start clean.
rm -rf "$INSTALLER_DIR"
"$VPK" [win] pack \
  -u BifrostUI \
  -v "$VERSION" \
  -p "$PUBLISH_DIR" \
  -e bifrostui.exe \
  --packTitle "BifrostQL UI" \
  --packAuthors "Standard Beagle" \
  --icon "$REPO_ROOT/src/BifrostQL.UI/bifrostui.ico" \
  -r win-x64 \
  -o "$INSTALLER_DIR"

echo ""
echo "Installer: $INSTALLER_DIR/BifrostUI-win-Setup.exe"
