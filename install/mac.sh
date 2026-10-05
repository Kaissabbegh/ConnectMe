#!/bin/bash
# ConnectMe Display: install or update on a macOS iMac (10.15 Catalina or newer).
#
#   curl -fsSL https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/mac.sh | bash
#
# Builds the app from source on the iMac (needs Apple's Command Line Tools), then installs it to /Applications.
set -euo pipefail

REPO="Kaissabbegh/ConnectMe"
APP="ConnectMe Display.app"

if ! xcode-select -p >/dev/null 2>&1; then
    echo "Apple's Command Line Tools are needed. An installer window will open."
    xcode-select --install || true
    echo "When it has finished, run this command again."
    exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
echo "Downloading ConnectMe source..."
curl -fsSL "https://github.com/$REPO/archive/refs/heads/main.tar.gz" | tar -xz -C "$TMP"

cd "$TMP"/ConnectMe-main/mac/ConnectMeDisplay
chmod +x build.sh
./build.sh

pkill -x ConnectMeDisplay 2>/dev/null && sleep 1 || true
rm -rf "/Applications/$APP"
cp -R "build/$APP" "/Applications/$APP"

echo
echo "Installed /Applications/$APP. Opening it now."
echo "Tip: add it to System Preferences > Users & Groups > Login Items to start it automatically."
open "/Applications/$APP"
