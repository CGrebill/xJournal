#!/usr/bin/env bash
# Wraps a macOS publish folder into xJournal.app (run on macOS: needs sips, iconutil, codesign).
# Usage: installer/make-macos-app.sh <publish-dir> <version> <output-dir>
set -euo pipefail
publish="$1"; version="$2"; out="$3"
here="$(cd "$(dirname "$0")" && pwd)"
app="$out/xJournal.app"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"

# Icon: build an .icns from the 512 px PNG.
iconset="$(mktemp -d)/xjournal.iconset"
mkdir -p "$iconset"
png="$here/../src/XJournal.App/Assets/xjournal.png"
for size in 16 32 128 256; do
  sips -z $size $size "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
cp "$png" "$iconset/icon_512x512.png"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/xjournal.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>xJournal</string>
  <key>CFBundleDisplayName</key><string>xJournal</string>
  <key>CFBundleIdentifier</key><string>io.github.cgrebill.xjournal</string>
  <key>CFBundleExecutable</key><string>xJournal</string>
  <key>CFBundleIconFile</key><string>xjournal.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright 2026 CGrebill</string>
</dict>
</plist>
PLIST

chmod +x "$app/Contents/MacOS/xJournal"
# Ad-hoc signature: required for Apple Silicon to run the binaries. Not notarized.
codesign --force --deep --sign - "$app"
echo "Built $app"
