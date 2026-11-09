#!/bin/sh
# Installs the `smartwizz` command for the current user. Re-run after pulling to update; existing config is kept.
set -eu
cd "$(dirname "$0")/.."

app="$HOME/.local/share/smartwizz"
dotnet publish src/SmartWizz -c Release -o "$app" --nologo -v q

mkdir -p "$HOME/.local/bin"
cat > "$HOME/.local/bin/smartwizz" <<EOF
#!/bin/sh
exec dotnet "$app/SmartWizz.dll" "\$@"
EOF
chmod +x "$HOME/.local/bin/smartwizz"

conf="${XDG_CONFIG_HOME:-$HOME/.config}/smartwizz"
mkdir -p "$conf"
[ -e "$conf/smartwizz.json" ] || cp smartwizz.json "$conf/"
[ -e "$conf/rules" ] || cp -r rules "$conf/"
if [ -e .env ] && [ ! -e "$conf/.env" ]; then
    cp .env "$conf/"
    chmod 600 "$conf/.env"
fi
echo "Installed. Config: $conf"
