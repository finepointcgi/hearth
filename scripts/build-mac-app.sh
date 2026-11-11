#!/bin/bash
# Builds dist/Hearth.app, the desktop app for macOS (Apple Silicon by default; pass osx-x64 for Intel).
#
# Signing matters for "use my computer": macOS remembers Screen Recording and Accessibility permission against the
# app's signature. An ad-hoc signature is tied to that exact build, so every rebuild asks for permission again.
# Signed with a real certificate (any "Apple Development" one from Xcode works, free account included), the
# permission belongs to the app and survives rebuilds. Set HEARTH_SIGN_IDENTITY to pick a certificate yourself.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/dist/Hearth.app"
PUB="$(mktemp -d)"
trap 'rm -rf "$PUB"' EXIT

echo "Building Hearth for $RID…"
dotnet publish "$ROOT/src/Hearth.Desktop" -c Release -r "$RID" --self-contained -o "$PUB" -v q

# Quit a running copy first, or it keeps the old files open.
pkill -f "Hearth.app/Contents/Resources/app/Hearth" 2>/dev/null || true

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# The published app goes in Resources (codesign won't sign a web root inside MacOS); a small launcher starts it,
# so macOS gives the permissions to Hearth.app rather than to a bare program.
cp -R "$PUB" "$APP/Contents/Resources/app"
cat > "$APP/Contents/MacOS/Hearth" <<'SH'
#!/bin/bash
# Starts Hearth from inside the bundle so macOS gives the permissions (Screen Recording, Accessibility) to Hearth.app.
DIR="$(cd "$(dirname "$0")/../Resources/app" && pwd)"
cd "$DIR"
exec "$DIR/Hearth" "$@"
SH
chmod +x "$APP/Contents/MacOS/Hearth"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Hearth</string>
  <key>CFBundleDisplayName</key><string>Hearth</string>
  <key>CFBundleIdentifier</key><string>com.hearth.desktop</string>
  <key>CFBundleExecutable</key><string>Hearth</string>
  <key>CFBundleIconFile</key><string>Hearth</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Hearth uses the microphone for voice chat and dictation.</string>
  <key>NSCameraUsageDescription</key><string>Hearth uses the camera only when you share a picture from it.</string>
  <key>NSAppleEventsUsageDescription</key><string>Hearth controls other apps when you ask a bot to use your computer.</string>
</dict>
</plist>
PLIST

# Icon, from the web app's 512px icon.
ICONSET="$PUB/Hearth.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/src/Hearth.Web/wwwroot/icon-512.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2)); [ $double -le 512 ] && sips -z $double $double "$ROOT/src/Hearth.Web/wwwroot/icon-512.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Hearth.icns"

IDENTITY="${HEARTH_SIGN_IDENTITY:-$(security find-identity -v -p codesigning | sed -n 's/.*"\(Apple Development:[^"]*\)".*/\1/p' | head -1)}"
if [ -n "$IDENTITY" ]; then
  echo "Signing with \"$IDENTITY\" (permissions will survive rebuilds)."
  codesign --force --deep -s "$IDENTITY" "$APP"
else
  echo "No Apple Development certificate found: signing ad hoc. macOS will ask for Screen Recording and"
  echo "Accessibility again after every rebuild. Sign in to Xcode (Settings → Accounts) to get a free certificate."
  codesign --force --deep -s - "$APP"
fi
codesign --verify --deep "$APP"
echo "Built $APP"
