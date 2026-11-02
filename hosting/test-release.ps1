# Ejecuta el publicador completo con git/gh simulados y archivos temporales: no publica ni crea commits/tags.
$ErrorActionPreference = "Stop"
$publicador = Join-Path $PSScriptRoot "subir-version.ps1"
$tokens = $null; $errores = $null
$arbol = [Management.Automation.Language.Parser]::ParseFile($publicador, [ref]$tokens, [ref]$errores)
if ($errores.Count) { throw ($errores | Out-String) }
$temporal = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ("coverao-release-test-" + [Guid]::NewGuid().ToString("N"))))
$shaFuente = 'a' * 40
$shaPublico = 'b' * 40
$llamadas = New-Object 'System.Collections.Generic.List[object]'
$origenPublico = $false
$tagEnConflicto = $false
$yaPublicada = $false

function Exigir($condicion, [string]$mensaje) { if (-not $condicion) { throw $mensaje } }
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
    'remote' { if ($origenPublico) { return 'https://github.com/Ayrton09/CoverAO.git' }; return 'https://github.com/Ayrton09/CoverAO-privado.git' }
  }
}
function gh {
  $a = @($args | ForEach-Object { [string]$_ })
  $llamadas.Add([pscustomobject]@{ programa = 'gh'; argumentos = $a })
  $global:LASTEXITCODE = 0
  if ($a[0] -eq 'api') {
    if ($a[1] -like '*/commits/main') { return $shaPublico }
    if ($a[1] -like '*/git/matching-refs/*') { return '[]' }
    if ($a[1] -like '*/releases') {
      if ($yaPublicada) { return '[[{"tag_name":"v1.0.1","draft":false}]]' }
      return '[[]]'
    }
  }
}

try {
  New-Item -ItemType Directory -Path (Join-Path $temporal 'hosting'), (Join-Path $temporal 'dist'), (Join-Path $temporal 'desktop/release') -Force | Out-Null
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
  Exigir ($creacion.argumentos -contains $shaPublico) 'El release público no apunta al commit de distribución.'
  Exigir (-not ($creacion.argumentos -contains $shaFuente)) 'Se intentó publicar el commit fuente.'
  Exigir (-not @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[1] -eq 'edit' }).Count) 'Un borrador se publicó sin pedirlo.'

  $llamadas.Clear()
  & $script -SoloSubir
  Exigir ($llamadas[$llamadas.Count - 1].argumentos -contains '--latest') 'Latest no se publicó al terminar las subidas.'

  foreach ($caso in @('tag', 'origen', 'publicada', 'hash')) {
    $llamadas.Clear(); $tagEnConflicto = $caso -eq 'tag'; $origenPublico = $caso -eq 'origen'; $yaPublicada = $caso -eq 'publicada'
    if ($caso -eq 'hash') { Add-Content -LiteralPath (Join-Path $temporal $rutas[0]) -Value 'alterado' }
    $rechazada = $false
    try { & $script -SoloSubir } catch { $rechazada = $true }
    Exigir $rechazada "No se rechazó el caso inseguro: $caso"
    Exigir (-not @($llamadas | Where-Object { $_.programa -eq 'gh' -and $_.argumentos[1] -eq 'upload' }).Count) "Se subieron archivos en el caso $caso."
  }
  Write-Host 'OK: borrador/latest, siete artefactos ordenados, fuente separada, tags sin force y rechazos por conflicto/origen/hash.'
}
finally {
  $baseTemporal = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
  if (-not $temporal.StartsWith($baseTemporal + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
      (Split-Path $temporal -Leaf) -notlike 'coverao-release-test-*') { throw 'Temporal fuera del destino permitido.' }
  if (Test-Path -LiteralPath $temporal) { Remove-Item -LiteralPath $temporal -Recurse -Force }
}
