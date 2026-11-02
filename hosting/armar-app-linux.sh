#!/bin/bash
# ARMA EL JUEGO DE ESCRITORIO PARA LINUX: desktop/release/CoverAO-x86_64.AppImage y su aviso firmado,
# desktop/release/actualizacion-linux.json (ver desktop/armar.js). Corre EN LINUX; desde Windows lo llama subir-version.ps1
# adentro de WSL.
#
#   bash hosting/armar-app-linux.sh <raíz del repo vista desde Linux> <clave privada>
#
# Se arma en el disco de Linux y no sobre /mnt/c: el AppImage lleva enlaces simbólicos y permisos de ejecución que ahí no
# existen, y npm sobre /mnt/c tarda diez veces más. Lo armado vuelve a desktop/release del repo. Las dependencias quedan en
# la carpeta de trabajo y se reusan la próxima vez.
set -euo pipefail
REPO="${1:?falta la raíz del repo}"
CLAVE="${2:?falta la clave privada}"
NODE="${COVERAO_NODE:-$(command -v node || echo /opt/node/bin/node)}"
[ -x "$NODE" ] || { echo "No hay node en este Linux (ni en /opt/node): hace falta para armar la app." >&2; exit 2; }
export PATH="$(dirname "$NODE"):$PATH"
TRABAJO="${COVERAO_APP_LINUX:-/opt/coverao-app-linux}"

mkdir -p "$TRABAJO/desktop"
find "$TRABAJO/desktop" -mindepth 1 -maxdepth 1 ! -name node_modules -exec rm -rf {} +
(cd "$REPO/desktop" && tar -cf - --exclude=./node_modules --exclude=./release .) | (cd "$TRABAJO/desktop" && tar -xf -)
cp "$REPO/CHANGELOG.md" "$TRABAJO/CHANGELOG.md"   # las novedades que muestra el menú (armar.js lo busca al lado de desktop/)
cp "$REPO/VERSION" "$TRABAJO/VERSION"   # el mismo número de release que Windows

cd "$TRABAJO/desktop"
npm ci --no-audit --no-fund --loglevel=error
node armar.js --linux --clave "$CLAVE"

mkdir -p "$REPO/desktop/release"
cp release/CoverAO-x86_64.AppImage release/actualizacion-linux.json "$REPO/desktop/release/"
echo "Listo: $REPO/desktop/release/CoverAO-x86_64.AppImage"
