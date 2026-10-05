#!/bin/bash
# ConnectMe Display: install or update on a Linux iMac (Kali/Debian/Ubuntu; X11 or Wayland).
#
#   curl -fsSL https://raw.githubusercontent.com/Kaissabbegh/ConnectMe/main/install/linux.sh | bash -s -- "Desk 1 iMac"
#
# Re-run the same command to update. If a ConnectMeDisplay binary sits next to this script,
# that one is installed instead of downloading the latest release.
set -euo pipefail

REPO="Kaissabbegh/ConnectMe"
NAME="${1:-$(hostname)}"
DEST="$HOME/.local/share/connectme"
BIN="$DEST/ConnectMeDisplay"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo "")"

if ! command -v mpv >/dev/null 2>&1; then
    echo "Installing mpv (needs your password)..."
    sudo apt-get update -qq && sudo apt-get install -y mpv
fi

# Stop a running copy so its file can be replaced (the receiver also closes its mpv window).
pkill -f "$BIN" 2>/dev/null && sleep 1 || true

mkdir -p "$DEST" "$HOME/.config/autostart" "$HOME/.local/share/applications"
if [ -n "$SCRIPT_DIR" ] && [ -f "$SCRIPT_DIR/ConnectMeDisplay" ]; then
    echo "Installing ConnectMeDisplay from $SCRIPT_DIR"
    cp "$SCRIPT_DIR/ConnectMeDisplay" "$BIN.new"
else
    echo "Downloading the latest ConnectMe Display..."
    curl -fL --progress-bar -o "$BIN.new" "https://github.com/$REPO/releases/latest/download/ConnectMeDisplay-linux-x64"
fi
mv -f "$BIN.new" "$BIN"
chmod +x "$BIN"

cat > "$HOME/.local/share/applications/connectme-display.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ConnectMe Display
Comment=Use this iMac as a wireless screen for a laptop
Exec="$BIN" --name "$NAME"
Icon=video-display
Terminal=false
Categories=Network;
EOF
cp "$HOME/.local/share/applications/connectme-display.desktop" "$HOME/.config/autostart/connectme-display.desktop"

# Open the ports if a firewall is active (Kali has none by default).
if command -v ufw >/dev/null 2>&1 && sudo -n ufw status 2>/dev/null | grep -q "Status: active"; then
    sudo ufw allow 47800/tcp
    sudo ufw allow 5353/udp
fi

echo
echo "Installed ConnectMe Display as \"$NAME\". It starts automatically at login."
echo "Start it now:   \"$BIN\" --name \"$NAME\""
echo "Stop it:        press q on its screen, or run: pkill -f ConnectMeDisplay"
echo "Logs:           ~/.local/share/ConnectMe/display.log  and  mpv.log"
