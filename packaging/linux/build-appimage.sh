#!/usr/bin/env bash
# Monta o AppImage do ABExplode a partir do executável já publicado (dist-linux/ABExplode).
# Uso: packaging/linux/build-appimage.sh [saida.AppImage]
# Baixa o appimagetool na primeira vez (para a pasta .appimagetool, fora do git).
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
pkg="$root/packaging/linux"
out="${1:-$root/ABExplode-x86_64.AppImage}"
bin="$root/dist-linux/ABExplode"
id="io.github.audreybruno.ABExplode"

[ -x "$bin" ] || { echo "Falta $bin: rode o dotnet publish (veja o comentário em $pkg/$id.yml)." >&2; exit 1; }

appdir="$(mktemp -d)/ABExplode.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/metainfo"
install -m755 "$bin" "$appdir/usr/bin/ABExplode"
install -m644 "$pkg/$id.desktop" "$appdir/usr/share/applications/$id.desktop"
install -m644 "$pkg/$id.png" "$appdir/usr/share/icons/hicolor/256x256/apps/$id.png"
install -m644 "$pkg/$id.metainfo.xml" "$appdir/usr/share/metainfo/$id.appdata.xml"
# O appimagetool quer o .desktop e o ícone também na raiz do AppDir
cp "$pkg/$id.desktop" "$appdir/$id.desktop"
cp "$pkg/$id.png" "$appdir/$id.png"
cp "$pkg/$id.png" "$appdir/.DirIcon"
cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/ABExplode" "$@"
EOF
chmod +x "$appdir/AppRun"

tool="$root/.appimagetool/appimagetool-x86_64.AppImage"
if [ ! -x "$tool" ]; then
    mkdir -p "$(dirname "$tool")"
    curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$tool"
fi
# --appimage-extract-and-run: funciona também onde não há FUSE (como nos runners do GitHub)
ARCH=x86_64 "$tool" --appimage-extract-and-run --no-appstream "$appdir" "$out"
echo "Pronto: $out"
