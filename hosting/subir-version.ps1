<#
  Arma y publica paquetes firmados. VERSION fija el número; la fecha firmada distingue compilaciones.
  La fuente vive en CoverAO-privado y las descargas en CoverAO: nunca se empuja código al repo de distribución.

    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinSubir
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SoloSubir -Borrador
    powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SoloSubir

  -SinApp / -SinServidor / -SinAppLinux permiten preparar un subconjunto. Al subir deben coincidir con lo armado.
  La subida comprueba el recibo local y los hashes; los avisos se suben después de los paquetes.
#>
param([switch]$SinApp, [switch]$SinServidor, [switch]$SinSubir, [switch]$SoloSubir, [switch]$SinAppLinux,
      [switch]$Borrador, [string]$DistroLinux = "CoverAO-Verif", [string]$Version = "",
      [string]$RepoDistribucion = "Ayrton09/CoverAO", [string]$RepoFuente = "Ayrton09/CoverAO-privado",
      [string]$RefDistribucion = "main")
$ErrorActionPreference = "Stop"
$Root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$versionFuente = (Get-Content -LiteralPath (Join-Path $Root "VERSION") -Raw).Trim()
if (-not $Version) { $Version = $versionFuente }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION debe tener formato X.Y.Z." }
if ($Version -ne $versionFuente) { throw "-Version debe coincidir con VERSION; cambiá primero la versión de la fuente." }
if ($RepoDistribucion -eq $RepoFuente) { throw "La fuente y la distribución deben ser repositorios distintos." }
foreach ($r in @($RepoDistribucion, $RepoFuente)) {
  if ($r -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Repositorio inválido: $r" }
}
if ($SoloSubir -and $SinSubir) { throw "Elegí -SoloSubir o -SinSubir." }
if ($SinApp -and $SinServidor) { throw "No se eligió ningún paquete." }
$etiqueta = "v$Version"
$dist = Join-Path $Root "dist"
$recibo = Join-Path $dist "release-preparada.json"
$id = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
$subir = @()
$avisos = @()

function Git-Fuente {
  param([string[]]$Argumentos)
  $salida = & git -C $Root @Argumentos
  if ($LASTEXITCODE -ne 0) { throw "git falló: $($Argumentos -join ' ')" }
  return $salida
}
function Github {
  param([string[]]$Argumentos)
  $salida = & gh @Argumentos
  if ($LASTEXITCODE -ne 0) { throw "gh falló: $($Argumentos -join ' ')" }
  return $salida
}
function Quitar-Staging([string]$Rid) {
  if ($Rid -notin @("win-x64", "linux-x64")) { throw "Plataforma de staging inválida." }
  $base = [IO.Path]::GetFullPath($dist).TrimEnd([IO.Path]::DirectorySeparatorChar)
  $carpeta = [IO.Path]::GetFullPath((Join-Path $base "paquete-$Rid"))
  if (-not $carpeta.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
      (Split-Path $carpeta -Leaf) -ne "paquete-$Rid") { throw "Staging fuera del destino permitido." }
  foreach ($p in @($base, $carpeta)) {
    if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
      throw "No se limpia un staging que contiene enlaces: $p"
    }
  }
  if (Test-Path -LiteralPath $carpeta) {
    $directorios = New-Object 'System.Collections.Generic.Stack[string]'
    $directorios.Push($carpeta)
    while ($directorios.Count -gt 0) {
      foreach ($hijo in Get-ChildItem -LiteralPath ($directorios.Pop()) -Force) {
        if ($hijo.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Enlace dentro del staging: $($hijo.FullName)" }
        if ($hijo.PSIsContainer) { $directorios.Push($hijo.FullName) }
      }
    }
    Remove-Item -LiteralPath $carpeta -Recurse -Force
  }
  return $carpeta
}

$commit = (Git-Fuente -Argumentos @("rev-parse", "HEAD")).Trim()
if (Git-Fuente -Argumentos @("status", "--porcelain")) { throw "Hay cambios sin confirmar: el paquete debe corresponder a un commit exacto." }
if (-not $SinServidor) {
  $subir += (Join-Path $dist "CoverAO-servidor-win-x64.zip"), (Join-Path $dist "CoverAO-servidor-linux-x64.tar.gz")
  $avisos += (Join-Path $dist "servidor.json")
}
if (-not $SinApp) {
  $subir += (Join-Path $Root "desktop\release\CoverAO-Setup.exe")
  $avisos += (Join-Path $Root "desktop\release\actualizacion.json")
  if (-not $SinAppLinux) {
    $subir += (Join-Path $Root "desktop\release\CoverAO-x86_64.AppImage")
    $avisos += (Join-Path $Root "desktop\release\actualizacion-linux.json")
  }
}

if (-not $SoloSubir) {
  $node = (Get-Command node -ErrorAction Stop).Source
  if (-not $SinServidor) {
    foreach ($rid in @("win-x64", "linux-x64")) {
      $carpeta = Quitar-Staging $rid
      $out = Join-Path $carpeta "CoverAO-$rid"
      & (Join-Path $PSScriptRoot "publish.ps1") -Rid $rid -OutDir $out -Comprimir -Compilacion $id -Version $Version
      if (-not (Test-Path -LiteralPath (Join-Path $out "archivos.json"))) { throw "El paquete de $rid salió sin firma." }
      $ext = if ($rid -eq "linux-x64") { "tar.gz" } else { "zip" }
      Copy-Item -LiteralPath (Join-Path $carpeta "CoverAO-$Version-$rid.$ext") -Destination (Join-Path $dist "CoverAO-servidor-$rid.$ext") -Force
    }
    & $node (Join-Path $PSScriptRoot "firmar-servidor.js") --aviso $id (Join-Path $dist "servidor.json")
    if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar el aviso del servidor." }
  }
  if (-not $SinApp) {
    & $node (Join-Path $Root "desktop\armar.js")
    if ($LASTEXITCODE -ne 0) { throw "No se pudo armar la app de Windows." }
    if (-not $SinAppLinux) {
      $comoLinux = { param($ruta) "/mnt/" + $ruta.Substring(0, 1).ToLower() + ($ruta.Substring(2) -replace '\\', '/') }
      $clave = Join-Path $HOME ".coverao-firma\clave-privada.pem"
      $env:WSL_UTF8 = "1"
      $wslEnvAnterior = $env:WSLENV
      try {
        # WSL no hereda variables arbitrarias. Se transfiere la contraseña existente sin imprimirla ni ponerla en argumentos.
        $reenviadas = @($env:WSLENV -split ':' | Where-Object { $_ })
        if ((Test-Path Env:COVERAO_FIRMA_CLAVE) -and
            -not @($reenviadas | Where-Object { ($_ -split '/')[0] -eq 'COVERAO_FIRMA_CLAVE' }).Count) {
          $env:WSLENV = (@($reenviadas) + 'COVERAO_FIRMA_CLAVE') -join ':'
        }
        & wsl -d $DistroLinux -- bash (& $comoLinux (Join-Path $PSScriptRoot "armar-app-linux.sh")) (& $comoLinux $Root) (& $comoLinux $clave)
        if ($LASTEXITCODE -ne 0) { throw "No se pudo armar la app de Linux en $DistroLinux." }
      }
      finally { $env:WSLENV = $wslEnvAnterior }
    }
  }
  New-Item -ItemType Directory -Path $dist -Force | Out-Null
  if ((Git-Fuente -Argumentos @("rev-parse", "HEAD")).Trim() -ne $commit -or (Git-Fuente -Argumentos @("status", "--porcelain"))) {
    throw "La fuente cambió durante el armado: no se certificará este resultado."
  }
  $archivos = @($subir + $avisos | ForEach-Object {
    if (-not (Test-Path -LiteralPath $_)) { throw "No está armado: $_" }
    [ordered]@{ ruta = $_.Substring($Root.Length + 1).Replace([char]92, '/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
  })
  [ordered]@{ version = $Version; commit = $commit; archivos = $archivos } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $recibo -Encoding UTF8
}

# También -SoloSubir valida que los archivos no sean restos de otra compilación o de otro commit.
if (-not (Test-Path -LiteralPath $recibo)) { throw "Falta el recibo de armado. Corré primero con -SinSubir." }
$preparada = Get-Content -LiteralPath $recibo -Raw | ConvertFrom-Json
if ($preparada.version -ne $Version -or $preparada.commit -ne $commit) { throw "El armado no corresponde a VERSION y HEAD actuales." }
foreach ($f in ($subir + $avisos)) {
  if (-not (Test-Path -LiteralPath $f)) { throw "No está armado: $f" }
  $rel = $f.Substring($Root.Length + 1).Replace([char]92, '/')
  $registro = @($preparada.archivos | Where-Object { $_.ruta -eq $rel })
  if ($registro.Count -ne 1 -or $registro[0].sha256 -ne (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash) {
    throw "El archivo no coincide con el armado confirmado: $rel"
  }
}
if ($SinSubir) {
  if ((Git-Fuente -Argumentos @("rev-parse", "HEAD")).Trim() -ne $commit -or (Git-Fuente -Argumentos @("status", "--porcelain"))) {
    throw "La fuente cambió durante el armado: no se publicará este resultado."
  }
  Write-Host "Armado y verificado $etiqueta, sin subir:" -ForegroundColor Green
  $subir + $avisos | ForEach-Object { Write-Host "  $_" }
  return
}

# El remoto del source nunca se usa como destino de releases ni se fuerza una etiqueta existente.
if ((Git-Fuente -Argumentos @("rev-parse", "HEAD")).Trim() -ne $commit -or (Git-Fuente -Argumentos @("status", "--porcelain"))) {
  throw "La fuente cambió durante la preparación: no se publicará este resultado."
}
$origen = (Git-Fuente -Argumentos @("remote", "get-url", "origin")).Trim()
$repoOrigen = if ($origen -match '^git@github\.com:(.+?)(?:\.git)?$') { $Matches[1] }
              elseif ($origen -match '^https://github\.com/(.+?)(?:\.git)?/?$') { $Matches[1] } else { "" }
if ($repoOrigen -ne $RepoFuente) { throw "origin debe apuntar al repositorio fuente $RepoFuente; no se publicará código en la distribución." }
Git-Fuente -Argumentos @("fetch", "--quiet", "origin", "main") | Out-Null
Git-Fuente -Argumentos @("merge-base", "--is-ancestor", $commit, "origin/main") | Out-Null
$tagLocal = & git -C $Root rev-parse --verify --quiet "refs/tags/$etiqueta^{commit}"
if ($LASTEXITCODE -eq 0 -and $tagLocal.Trim() -ne $commit) { throw "La etiqueta fuente $etiqueta ya apunta a otro commit; no se mueve." }
$remotas = @(Git-Fuente -Argumentos @("ls-remote", "origin", "refs/tags/$etiqueta", "refs/tags/$etiqueta^{}"))
if ($remotas.Count -gt 0) {
  $resuelta = @($remotas | Where-Object { $_ -match '\^\{\}$' })
  $tagRemoto = if ($resuelta.Count) { ($resuelta[0] -split '\s+')[0] } else { ($remotas[0] -split '\s+')[0] }
  if ($tagRemoto -ne $commit) { throw "La etiqueta remota fuente $etiqueta ya apunta a otro commit; no se mueve." }
}
if (-not $tagLocal) { Git-Fuente -Argumentos @("tag", $etiqueta, $commit) | Out-Null }
if ($remotas.Count -eq 0) { Git-Fuente -Argumentos @("push", "origin", "refs/tags/$etiqueta") | Out-Null }

$destino = (Github -Argumentos @("api", "repos/$RepoDistribucion/commits/$RefDistribucion", "--jq", ".sha")).Trim()
if ($destino -eq $commit) { throw "El commit de distribución coincide con el source: se requiere el repo de descargas separado." }
# Sin literales entrecomillados en --jq: Windows PowerShell 5.1 los pierde al invocar gh como programa nativo.
$paginas = Github -Argumentos @("api", "repos/$RepoDistribucion/releases", "--paginate", "--slurp") | Out-String | ConvertFrom-Json
$existente = @(foreach ($pagina in $paginas) { foreach ($release in $pagina) { if ($release.tag_name -eq $etiqueta) { $release } } })
if ($existente.Count -gt 0 -and -not $existente[0].draft) { throw "$etiqueta ya está publicada: no se reemplazan sus archivos." }
# Una etiqueta pública existente sólo se reutiliza si apunta al commit esperado del repo de distribución.
$tags = Github -Argumentos @("api", "repos/$RepoDistribucion/git/matching-refs/tags/$etiqueta") | Out-String | ConvertFrom-Json
$tagPublico = @($tags | Where-Object { $_.ref -eq "refs/tags/$etiqueta" })
if ($tagPublico.Count) {
  $objeto = $tagPublico[0].object
  while ($objeto.type -eq "tag") {
    $objeto = (Github -Argumentos @("api", "repos/$RepoDistribucion/git/tags/$($objeto.sha)") | Out-String | ConvertFrom-Json).object
  }
  if ($objeto.type -ne "commit" -or $objeto.sha -ne $destino) { throw "La etiqueta pública $etiqueta no corresponde al destino de distribución; no se mueve." }
}
if ($existente.Count -eq 0) {
  $notas = "CoverAO $Version. App de Windows y Linux, y servidor para ambas plataformas. Los paquetes y avisos de actualización están firmados."
  Github -Argumentos @("release", "create", $etiqueta, "-R", $RepoDistribucion, "--target", $destino, "--title", "CoverAO $Version", "--notes", $notas, "--draft") | Out-Null
}
foreach ($f in ($subir + $avisos)) { Github -Argumentos @("release", "upload", $etiqueta, $f, "--clobber", "-R", $RepoDistribucion) | Out-Null }
if (-not $Borrador) { Github -Argumentos @("release", "edit", $etiqueta, "--draft=false", "--latest", "-R", $RepoDistribucion) | Out-Null }
Write-Host "Preparado $etiqueta en $RepoDistribucion$(if ($Borrador) { ' (borrador)' } else { ' (publicado, latest)' })." -ForegroundColor Green
