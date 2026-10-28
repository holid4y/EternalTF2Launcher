#!/bin/sh
# Installs Eternal TF2 Launcher for the current user (no root needed).
#   ./install.sh            install or update
#   ./install.sh --remove   uninstall (settings and mods are kept)
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
target="$data/eternal-launcher/app"
bin="$HOME/.local/bin"
apps="$data/applications"
icons="$data/icons/hicolor/256x256/apps"

if [ "${1:-}" = "--remove" ]; then
    rm -rf "$target" "$bin/eternal-launcher" "$apps/eternal-launcher.desktop" "$icons/eternal-launcher.png"
    echo "Removed. Your settings and mods are still in ~/.config/eternal-launcher and $data/eternal-launcher."
    exit 0
fi

mkdir -p "$target" "$bin" "$apps" "$icons"
rm -rf "$target"
cp -r "$here" "$target"
rm -f "$target/install.sh"
chmod +x "$target/EternalLauncher"
[ -f "$target/engine/eternal-engine" ] && chmod +x "$target/engine/eternal-engine"
ln -sf "$target/EternalLauncher" "$bin/eternal-launcher"
cp "$here/eternal-launcher.png" "$icons/eternal-launcher.png"
sed "s|^Exec=.*|Exec=\"$bin/eternal-launcher\"|" "$here/eternal-launcher.desktop" > "$apps/eternal-launcher.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" >/dev/null 2>&1 || true

echo "Installed. Start it from your app menu or run: eternal-launcher"
case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: $bin is not on your PATH." ;; esac
