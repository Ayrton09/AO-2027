# Ejecuta el publicador completo con git/gh simulados y archivos temporales: no publica ni crea commits/tags.
$ErrorActionPreference = "Stop"
$publicador = Join-Path $PSScriptRoot "subir-version.ps1"
$tokens = $null; $errores = $null
$arbol = [Management.Automation.Language.Parser]::ParseFile($publicador, [ref]$tokens, [ref]$errores)
if ($errores.Count) { throw ($errores | Out-String) }
$temporal = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("coverao-release-test-" + [Guid]::NewGuid().ToString("N"))))
$shaFuente = 'a' * 40
$llamadas = New-Object 'System.Collections.Generic.List[object]'
$origenCorrecto = $true
$tagEnConflicto = $false
$tagRemotoEnConflicto = $false
$enMain = $true
$yaPublicada = $false

function Exigir($condicion, [string]$mensaje) { if (-not $condicion) { throw $mensaje } }
function Probar-PaquetePortable {
  $rutaPublish = Join-Path $PSScriptRoot 'publish.ps1'
  $erroresPublish = $null
  $publish = [Management.Automation.Language.Parser]::ParseFile($rutaPublish, [ref]$null, [ref]$erroresPublish)
  if ($erroresPublish.Count) { throw ($erroresPublish | Out-String) }
  # Ejecuta el empaquetado real con datos sintéticos, sin compilar ni usar la clave de firma.
  $preservarYLimpiar = $publish.EndBlock.Statements | Where-Object {
    $_.Extent.Text.StartsWith('$preservar = ') -or $_.Extent.Text.StartsWith('if (Test-Path $OutDir)')
  }
  $empaquetado = $publish.EndBlock.Statements | Where-Object {
    $_.Extent.Text.StartsWith('$noViajan = ') -or $_.Extent.Text.StartsWith('function Viaja(') -or
    $_.Extent.Text.StartsWith('if ($Comprimir)')
  }
  $docker = @($publish.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('".dockerignore"')
  })
  Exigir (@($preservarYLimpiar).Count -eq 2 -and @($empaquetado).Count -eq 3) 'No se encontró el empaquetado portable.'
  Exigir ($docker.Count -eq 1) 'No se encontró la preparación del Docker portable.'
  $limpiar = [scriptblock]::Create(($preservarYLimpiar.Extent.Text -join "`n"))
  $comprimirPaquete = [scriptblock]::Create(($empaquetado.Extent.Text -join "`n"))
  $copiarDocker = [scriptblock]::Create($docker[0].Extent.Text)

  $Root = Split-Path $PSScriptRoot -Parent
  $Comprimir = $true
  $Version = '0.0.0'
  $casos = @(
    @{ Rid = 'win-x64'; Nombre = 'CoverAO-win-x64'; Env = '.env' }
    @{ Rid = 'win-x64'; Nombre = 'CoverAO-win-x64-mixto'; Env = '.EnV' }
    @{ Rid = 'linux-x64'; Nombre = 'CoverAO-linux-x64'; Env = '.env' }
    @{ Rid = 'linux-x64'; Nombre = 'CoverAO-linux-x64-mixto'; Env = '.EnV' }
  )
  foreach ($caso in $casos) {
    $Rid = $caso.Rid
    $nombre = $caso.Nombre
    $OutDir = Join-Path $temporal $nombre
    New-Item -ItemType Directory -Path $OutDir | Out-Null
    $entorno = Join-Path $OutDir $caso.Env
    Set-Content -LiteralPath $entorno -Value 'COVERAO_PG_CLAVE=prueba-sintetica'
    & $limpiar
    Exigir ((Get-Content -LiteralPath $entorno) -ceq 'COVERAO_PG_CLAVE=prueba-sintetica') 'Se perdió el .env local al actualizar.'

    Set-Content -LiteralPath (Join-Path $OutDir 'oficial.txt') -Value 'contenido portable'
    & $copiarDocker
    & $comprimirPaquete
    if ($Rid -eq 'linux-x64') {
      $archivo = Join-Path $temporal "CoverAO-$Version-$Rid.tar.gz"
      $archivos = @(tar -tf $archivo)
      Exigir ($LASTEXITCODE -eq 0) 'No se pudo leer el tar.gz.'
      Exigir ($archivos -ccontains "$nombre/.dockerignore") 'Falta el .dockerignore en el tar.gz.'
      $ignore = Get-Content -LiteralPath (Join-Path $OutDir '.dockerignore')
      Exigir ($ignore -ccontains '.[eE][nN][vV]') 'Falta la exclusión de .env del contexto Docker portable.'
    } else {
      $archivo = Join-Path $temporal "CoverAO-$Version-$Rid.zip"
      $zip = [IO.Compression.ZipFile]::OpenRead($archivo)
      try { $archivos = @($zip.Entries | ForEach-Object { $_.FullName }) }
      finally { $zip.Dispose() }
    }
    Exigir ($archivos -ccontains "$nombre/oficial.txt") "Falta el archivo oficial en $nombre."
    Exigir ($archivos -notcontains "$nombre/.env") "El archivo $nombre contiene el .env del host."
  }
  Write-Host 'OK: el .env local se conserva y queda fuera del contexto Docker y de ambos comprimidos.'
}
function git {
  $a = @($args | ForEach-Object { [string]$_ })
  $llamadas.Add([pscustomobject]@{ programa = 'git'; argumentos = $a })
  $global:LASTEXITCODE = 0
  switch ($a[2]) {
    'rev-parse' {
      if ($a -contains 'HEAD') { return $shaFuente }
      if ($tagEnConflicto) { return ('c' * 40) }
      $global:LASTEXITCODE = 1; return
    }
    'remote' { if ($origenCorrecto) { return 'https://github.com/Ayrton09/AO-2027.git' }; return 'https://github.com/Ayrton09/CoverAO.git' }
    'merge-base' { if (-not $enMain) { $global:LASTEXITCODE = 1 } }
    'ls-remote' {
      if ($tagRemotoEnConflicto) { return (('b' * 40) + " refs/tags/v1.0.1") }
    }
  }
}
function gh {
  $a = @($args | ForEach-Object { [string]$_ })
  $llamadas.Add([pscustomobject]@{ programa = 'gh'; argumentos = $a })
  $global:LASTEXITCODE = 0
  if ($a[0] -eq 'api') {
    if ($a[1] -like '*/releases') {
      if ($yaPublicada) { return '[[{"tag_name":"v1.0.1","draft":false}]]' }
      return '[[]]'
    }
  }
}

try {
  New-Item -ItemType Directory -Path (Join-Path $temporal 'hosting'), (Join-Path $temporal 'dist'), (Join-Path $temporal 'desktop/release') -Force | Out-Null
  Probar-PaquetePortable
  # La limpieza real sólo puede tocar el staging esperado; las pruebas usan su propia carpeta temporal.
  $dist = Join-Path $temporal 'dist'
  $limpieza = $arbol.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Quitar-Staging'}, $true)[0]
  Invoke-Expression $limpieza.Extent.Text
  $staging = Join-Path $dist 'paquete-win-x64'
  New-Item -ItemType Directory -Path (Join-Path $staging 'anterior') -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $staging 'anterior/resto.txt') -Value 'anterior'
  Quitar-Staging 'win-x64' | Out-Null
  Exigir (-not (Test-Path -LiteralPath $staging)) 'No se retiró el staging anterior.'
  $rechazada = $false
  try { Quitar-Staging '..' | Out-Null } catch { $rechazada = $true }
  Exigir $rechazada 'La limpieza aceptó una ruta ajena al staging.'
  Copy-Item -LiteralPath $publicador -Destination (Join-Path $temporal 'hosting/subir-version.ps1')
  Set-Content -LiteralPath (Join-Path $temporal 'VERSION') -Value '1.0.1' -Encoding ASCII
  $rutas = @('dist/CoverAO-servidor-win-x64.zip', 'dist/CoverAO-servidor-linux-x64.tar.gz',
    'desktop/release/CoverAO-Setup.exe', 'desktop/release/CoverAO-x86_64.AppImage',
    'dist/servidor.json', 'desktop/release/actualizacion.json', 'desktop/release/actualizacion-linux.json')
  $archivos = @($rutas | ForEach-Object {
    $ruta = Join-Path $temporal $_
    Set-Content -LiteralPath $ruta -Value $_ -Encoding ASCII
    [ordered]@{ ruta = $_; sha256 = (Get-FileHash -LiteralPath $ruta -Algorithm SHA256).Hash }
  })
  [ordered]@{ version = '1.0.1'; commit = $shaFuente; archivos = $archivos } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $temporal 'dist/release-preparada.json') -Encoding UTF8
  $script = Join-Path $temporal 'hosting/subir-version.ps1'

  & $script -SoloSubir -Borrador
  $subidas = @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[0] -eq 'release' -and $_.argumentos[1] -eq 'upload' })
  Exigir ($subidas.Count -eq 7) 'Faltaron artefactos de una plataforma o sus avisos.'
  for ($i = 0; $i -lt 4; $i++) { Exigir (-not $subidas[$i].argumentos[3].EndsWith('.json')) 'Se subió un aviso antes de todos los paquetes.' }
  Exigir (-not @($llamadas | Where-Object { $_.argumentos -contains '--force' -or $_.argumentos -contains '-f' }).Count) 'Se intentó forzar una etiqueta.'
  $creacion = @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[1] -eq 'create' })[0]
  $etiquetado = @($llamadas | Where-Object { $_.programa -eq 'git' -and $_.argumentos[2] -eq 'tag' })[0]
  Exigir ($etiquetado.argumentos -contains $shaFuente) 'La etiqueta no apunta al commit de los archivos armados.'
  Exigir ($creacion.argumentos -contains '--verify-tag') 'La release podría crear una etiqueta en otro commit.'
  Exigir ($creacion.argumentos -contains 'Ayrton09/AO-2027') 'La release no se creó en el repositorio principal.'
  Exigir (-not @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[1] -eq 'edit' }).Count) 'Un borrador se publicó sin pedirlo.'

  $llamadas.Clear()
  & $script -SoloSubir
  Exigir ($llamadas[$llamadas.Count - 1].argumentos -contains '--latest') 'Latest no se publicó al terminar las subidas.'

  foreach ($caso in @('tag', 'tag-remoto', 'origen', 'commit-fuera-de-main', 'publicada', 'hash')) {
    $llamadas.Clear()
    $tagEnConflicto = $caso -eq 'tag'
    $tagRemotoEnConflicto = $caso -eq 'tag-remoto'
    $origenCorrecto = $caso -ne 'origen'
    $enMain = $caso -ne 'commit-fuera-de-main'
    $yaPublicada = $caso -eq 'publicada'
    if ($caso -eq 'hash') { Add-Content -LiteralPath (Join-Path $temporal $rutas[0]) -Value 'alterado' }
    $rechazada = $false
    try { & $script -SoloSubir } catch { $rechazada = $true }
    Exigir $rechazada "No se rechazó el caso inseguro: $caso"
    Exigir (-not @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[1] -eq 'upload' }).Count) "Se subieron archivos en el caso $caso."
    Exigir (-not @($llamadas | Where-Object { $_.programa -eq 'git' -and $_.argumentos[2] -in @('tag', 'push') }).Count) "Se modificó una etiqueta en el caso $caso."
  }
  Write-Host 'OK: borrador/latest, siete artefactos ordenados, etiqueta en el commit armado y rechazo de publicaciones inseguras.'
}
finally {
  $baseTemporal = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
  if (-not $temporal.StartsWith($baseTemporal + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
      (Split-Path $temporal -Leaf) -notlike 'coverao-release-test-*') { throw 'Temporal fuera del destino permitido.' }
  if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
}
