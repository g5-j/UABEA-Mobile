#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${UABEA_APP_DIR:-/opt/uabea}"
DOTNET_ROOT="${DOTNET_ROOT:-/opt/dotnet}"
DISPLAY_VALUE="${DISPLAY:-:1}"

if ! command -v box64 >/dev/null 2>&1; then
  echo "Box64 was not found in PATH." >&2
  exit 10
fi
if [ ! -f "$APP_DIR/UABEAvalonia.dll" ]; then
  echo "Original UABEA publish output is missing: $APP_DIR/UABEAvalonia.dll" >&2
  exit 11
fi
if [ ! -x "$DOTNET_ROOT/dotnet" ]; then
  echo ".NET 8 x64 runtime was not found at $DOTNET_ROOT/dotnet" >&2
  exit 12
fi
if [ -z "${DISPLAY:-}" ]; then
  export DISPLAY="$DISPLAY_VALUE"
fi
cd "$APP_DIR"
exec box64 "$DOTNET_ROOT/dotnet" "$APP_DIR/UABEAvalonia.dll" "$@"
