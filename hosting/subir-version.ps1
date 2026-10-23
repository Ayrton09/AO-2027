<#
  subir-version.ps1 - PUBLICA UNA VERSION NUEVA: el juego de escritorio y el server de Windows y de Linux, firmados con la
  clave del autor, en el release v1.0.0 del repo del juego (privado hasta que el autor lo haga publico; mientras tanto, los
  paquetes se le pasan al tester a mano). SOLO cuando lo pide el autor. Se corre desde la raiz del repo (no desde un
  worktree): el instalador de la app necesita desktop\node_modules.

    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1                todo
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinApp        solo el server
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinServidor   solo la app
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinAppLinux   sin la app de Linux
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinSubir      arma y firma; los deja en dist\
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SoloSubir     sube lo que ya esta armado

  Los paquetes del server se arman en dist\paquete-<plataforma>, NO en dist\CoverAO-win-x64 (la instalacion con la que
  prueba el autor): armar un paquete repone el Server.ini del ejemplo, y ese es el suyo. Quedan en dist\ como
  CoverAO-servidor-win-x64.zip y CoverAO-servidor-linux-x64.tar.gz, con el aviso servidor.json. La version no cambia
  (1.0.0 hasta que el juego sea publico): lo que distingue una de otra es la fecha de la compilacion.

  LA APP DE LINUX (CoverAO-x86_64.AppImage y su aviso actualizacion-linux.json) se arma en Linux, adentro de WSL
  (hosting/armar-app-linux.sh): Windows no arma un AppImage. -DistroLinux elige la distro (con node, ver el script).
#>
param([switch]$SinApp, [switch]$SinServidor, [switch]$SinSubir, [switch]$SoloSubir, [switch]$SinAppLinux,
      [string]$DistroLinux = "CoverAO-Verif")
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$repo = "Ayrton09/CoverAO"
$etiqueta = "v1.0.0"
$dist = Join-Path $Root "dist"
$node = (Get-Command node -ErrorAction Stop).Source
$id = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
$subir = @()
$avisos = @()

if ($SoloSubir) {
  if (-not $SinServidor) {
    $subir += (Join-Path $dist "CoverAO-servidor-win-x64.zip"), (Join-Path $dist "CoverAO-servidor-linux-x64.tar.gz")
    $avisos += (Join-Path $dist "servidor.json")
  }
  if (-not $SinApp) {
    $subir += (Join-Path $Root "desktop\release\CoverAO-Setup.exe")
    $avisos += (Join-Path $Root "desktop\release\actualizacion.json")
  }
  if (-not $SinApp -and -not $SinAppLinux) {
    $subir += (Join-Path $Root "desktop\release\CoverAO-x86_64.AppImage")
    $avisos += (Join-Path $Root "desktop\release\actualizacion-linux.json")
  }
  foreach ($f in ($subir + $avisos)) { if (-not (Test-Path $f)) { throw "no esta armado: $f" } }
}

if (-not $SinServidor -and -not $SoloSubir) {
  foreach ($rid in "win-x64", "linux-x64") {
    $carpeta = Join-Path $dist "paquete-$rid"
    # Desde cero cada vez: un archivo que quedo de una version anterior viajaria en el paquete. Es una carpeta propia, sin
    # enlaces adentro.
    if (Test-Path $carpeta) { cmd /c rmdir /s /q "$carpeta" | Out-Null }
    $out = Join-Path $carpeta "CoverAO-$rid"
    & (Join-Path $PSScriptRoot "publish.ps1") -Rid $rid -OutDir $out -Comprimir -Compilacion $id
    if (-not (Test-Path (Join-Path $out "archivos.json"))) { throw "el paquete de $rid salio SIN FIRMA: falta la clave en ~\.coverao-firma" }
    $ext = if ($rid -like "linux*") { "tar.gz" } else { "zip" }
    $final = Join-Path $dist "CoverAO-servidor-$rid.$ext"
    Copy-Item -Force (Join-Path $carpeta "CoverAO-1.0.0-$rid.$ext") $final
    $subir += $final
  }
  $aviso = Join-Path $dist "servidor.json"
  & $node (Join-Path $PSScriptRoot "firmar-servidor.js") --aviso $id $aviso
  if ($LASTEXITCODE -ne 0) { throw "no se pudo firmar el aviso del server" }
  $avisos += $aviso
}

if (-not $SinApp -and -not $SoloSubir) {
  & $node (Join-Path $Root "desktop\armar.js")
  if ($LASTEXITCODE -ne 0) { throw "no se pudo armar el juego de escritorio" }
  $subir += (Join-Path $Root "desktop\release\CoverAO-Setup.exe")
  $avisos += (Join-Path $Root "desktop\release\actualizacion.json")
}

if (-not $SinApp -and -not $SinAppLinux -and -not $SoloSubir) {
  # LA APP DE LINUX, EN WSL: el repo y la clave se le pasan como los ve Linux (/mnt/c/...). La clave no se copia a ningun lado.
  $comoLinux = { param($ruta) "/mnt/" + $ruta.Substring(0, 1).ToLower() + ($ruta.Substring(2) -replace '\\', '/') }
  $clave = Join-Path $HOME ".coverao-firma\clave-privada.pem"
  $env:WSL_UTF8 = "1"
  & wsl -d $DistroLinux -- bash (& $comoLinux (Join-Path $PSScriptRoot "armar-app-linux.sh")) (& $comoLinux $Root) (& $comoLinux $clave)
  if ($LASTEXITCODE -ne 0) { throw "no se pudo armar la app de Linux en WSL ($DistroLinux). Para seguir sin ella: -SinAppLinux" }
  $subir += (Join-Path $Root "desktop\release\CoverAO-x86_64.AppImage")
  $avisos += (Join-Path $Root "desktop\release\actualizacion-linux.json")
}

if ($SinSubir) {
  Write-Host "`nArmado, sin subir:" -ForegroundColor Green
  ($subir + $avisos) | ForEach-Object { Write-Host "  $_" }
  return
}

# gh escribe por la salida de errores ("release not found"), y con ErrorActionPreference=Stop Windows PowerShell 5.1 corta el
# script ahi mismo: para gh se mira solo el codigo de salida.
$ErrorActionPreference = "Continue"
gh release view $etiqueta -R $repo 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  $notas = "El juego de escritorio (CoverAO-Setup.exe para Windows, CoverAO-x86_64.AppImage para Linux) y el server para " +
           "Windows y Linux. La app y el server se actualizan " +
           "desde aca: la app sola, el server con Actualizar.bat o 'bash servidor.sh actualizar'. Windows puede avisar que " +
           "no reconoce al editor del instalador: Mas informacion y despues Ejecutar de todas formas."
  gh release create $etiqueta -R $repo --title "CoverAO 1.0.0" --notes $notas
  if ($LASTEXITCODE -ne 0) { throw "no se pudo crear el release $etiqueta en $repo" }
}
# Los paquetes ANTES que los avisos: el que lee un aviso nuevo tiene que encontrar ya el paquete nuevo.
foreach ($f in ($subir + $avisos)) {
  gh release upload $etiqueta $f --clobber -R $repo
  if ($LASTEXITCODE -ne 0) { throw "no se pudo subir $f" }
}
# EL CODIGO FUENTE DEL RELEASE (los "Source code" que arma GitHub) sale de la etiqueta, no de lo que se sube: se la mueve al
# commit publicado, para que coincida con lo que corre (la AGPL pide ofrecer ese codigo). Sin ese commit en origin, no se toca.
$commit = (git -C $Root rev-parse HEAD).Trim()
git -C $Root fetch -q origin
git -C $Root merge-base --is-ancestor $commit origin/main
if ($LASTEXITCODE -ne 0) {
  Write-Warning "El commit $commit no esta en origin/main: la etiqueta $etiqueta (el codigo fuente del release) no se movio."
} else {
  git -C $Root tag -f $etiqueta $commit | Out-Null
  git -C $Root push -f -q origin "refs/tags/$etiqueta"
  if ($LASTEXITCODE -ne 0) { Write-Warning "No se pudo mover la etiqueta $etiqueta a $commit en origin." }
}
Write-Host ("`nSubido a https://github.com/$repo/releases/tag/$etiqueta" + $(if ($SoloSubir) { "" } else { " (compilacion $id)" })) -ForegroundColor Green
