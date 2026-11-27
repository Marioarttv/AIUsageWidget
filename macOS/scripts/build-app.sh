#!/bin/bash
set -euo pipefail
widget_root="$(cd "$(dirname "$0")/../.." && pwd)"
widget_build="$widget_root/macOS/build"
widget_app="$widget_build/AI Usage Widget.app"
# AI_WIDGET_UNIVERSAL=1 builds for Apple silicon and Intel in one app, as the release does.
widget_arch=()
if [ "${AI_WIDGET_UNIVERSAL:-}" = 1 ]; then widget_arch=(--arch arm64 --arch x86_64); fi
swift build --package-path "$widget_root/macOS" -c release --jobs 4 ${widget_arch[@]+"${widget_arch[@]}"}
widget_bin="$(swift build --package-path "$widget_root/macOS" -c release ${widget_arch[@]+"${widget_arch[@]}"} --show-bin-path)"
mkdir -p "$widget_app/Contents/MacOS" "$widget_app/Contents/Resources" "$widget_build/AppIcon.iconset"
cp "$widget_bin/AIUsageWidget" "$widget_app/Contents/MacOS/AIUsageWidget"
cp "$widget_root/macOS/Info.plist" "$widget_app/Contents/Info.plist"
for widget_size in 16 32 128 256 512; do
    sips -z "$widget_size" "$widget_size" "$widget_root/assets/widget.png" --out "$widget_build/AppIcon.iconset/icon_${widget_size}x${widget_size}.png" >/dev/null
    widget_double=$((widget_size * 2))
    sips -z "$widget_double" "$widget_double" "$widget_root/assets/widget.png" --out "$widget_build/AppIcon.iconset/icon_${widget_size}x${widget_size}@2x.png" >/dev/null
done
iconutil -c icns "$widget_build/AppIcon.iconset" -o "$widget_app/Contents/Resources/AppIcon.icns"
# A stable signing identity avoids changing the app's identity across local rebuilds.
# No certificates are created or downloaded. Use '-' explicitly for ad-hoc signing.
widget_identity="${AI_WIDGET_SIGNING_IDENTITY:-}"
if [ -z "$widget_identity" ]; then
    widget_identity="$(security find-identity -v -p codesigning | awk '/Apple Development:/{print $2; exit}')"
fi
codesign --force --sign "${widget_identity:--}" --identifier com.mariomanzocco.aiusagewidget "$widget_app"
codesign --verify --strict "$widget_app"
echo "$widget_app"
