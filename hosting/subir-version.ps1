<#
  subir-version.ps1 - PUBLICA UNA VERSION NUEVA: el juego de escritorio y el server de Windows y de Linux, firmados con la
  clave del autor, en el release v1.0.0 del repo del juego (privado hasta que el autor lo haga publico; mientras tanto, los
  paquetes se le pasan al tester a mano). SOLO cuando lo pide el autor. Se corre desde la raiz del repo (no desde un
  worktree): el instalador de la app necesita desktop\node_modules.

    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1                todo
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinApp        solo el server
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinServidor   solo la app
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinSubir      arma y firma; los deja en dist\

  Los paquetes del server se arman en dist\paquete-<plataforma>, NO en dist\CoverAO-win-x64 (la instalacion con la que
  prueba el autor): armar un paquete repone el Server.ini del ejemplo, y ese es el suyo. Quedan en dist\ como
  CoverAO-servidor-win-x64.zip y CoverAO-servidor-linux-x64.tar.gz, con el aviso servidor.json. La version no cambia
  (1.0.0 hasta que el juego sea publico): lo que distingue una de otra es la fecha de la compilacion.
#>
param([switch]$SinApp, [switch]$SinServidor, [switch]$SinSubir)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$repo = "Ayrton09/CoverAO"
$etiqueta = "v1.0.0"
$dist = Join-Path $Root "dist"
$node = (Get-Command node -ErrorAction Stop).Source
$id = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
$subir = @()
$avisos = @()

if (-not $SinServidor) {
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

if (-not $SinApp) {
  & $node (Join-Path $Root "desktop\armar.js")
  if ($LASTEXITCODE -ne 0) { throw "no se pudo armar el juego de escritorio" }
  $subir += (Join-Path $Root "desktop\release\CoverAO-Setup.exe")
  $avisos += (Join-Path $Root "desktop\release\actualizacion.json")
}

if ($SinSubir) {
  Write-Host "`nArmado, sin subir:" -ForegroundColor Green
  ($subir + $avisos) | ForEach-Object { Write-Host "  $_" }
  return
}

gh release view $etiqueta -R $repo *> $null
if ($LASTEXITCODE -ne 0) {
  $notas = "El juego de escritorio (CoverAO-Setup.exe) y el server para Windows y Linux. La app y el server se actualizan " +
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
Write-Host "`nSubido a https://github.com/$repo/releases/tag/$etiqueta (compilacion $id)" -ForegroundColor Green
