#!/bin/bash
# Builds "ConnectMe Display.app" on the iMac.
# Needs only Apple's Command Line Tools (run `xcode-select --install` once). Works on macOS 10.15 Catalina.
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="ConnectMe Display"
BUNDLE="build/${APP_NAME}.app"

echo "Compiling..."
swift build -c release

echo "Packaging ${BUNDLE}..."
rm -rf "${BUNDLE}"
mkdir -p "${BUNDLE}/Contents/MacOS" "${BUNDLE}/Contents/Resources"
cp ".build/release/ConnectMeDisplay" "${BUNDLE}/Contents/MacOS/ConnectMeDisplay"

cat > "${BUNDLE}/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>ConnectMe Display</string>
    <key>CFBundleDisplayName</key><string>ConnectMe Display</string>
    <key>CFBundleIdentifier</key><string>io.github.kaissabbegh.connectme.display</string>
    <key>CFBundleExecutable</key><string>ConnectMeDisplay</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSLocalNetworkUsageDescription</key><string>ConnectMe Display receives your laptop's screen over the office network.</string>
    <key>NSBonjourServices</key><array><string>_connectme._tcp</string></array>
</dict>
</plist>
PLIST

# Ad-hoc signature so macOS remembers the firewall permission between launches.
codesign --force --deep --sign - "${BUNDLE}" >/dev/null 2>&1 || echo "(codesign skipped)"

echo
echo "Done: mac/ConnectMeDisplay/${BUNDLE}"
echo "Drag it to /Applications and open it. Allow incoming connections if macOS asks."
