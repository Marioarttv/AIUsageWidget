#!/bin/bash
set -euo pipefail
widget_root="$(cd "$(dirname "$0")/../.." && pwd)"
widget_destination="$HOME/Applications/AI Usage Widget.app"
if pgrep -f "^$widget_destination/Contents/MacOS/AIUsageWidget$" >/dev/null; then
    echo "Quit AI Usage Widget from its menu before reinstalling." >&2
    exit 1
fi
"$widget_root/macOS/scripts/build-app.sh"
mkdir -p "$HOME/Applications"
ditto "$widget_root/macOS/build/AI Usage Widget.app" "$widget_destination"
codesign --verify --strict "$widget_destination"
open "$widget_destination"
echo "Installed: $widget_destination"
