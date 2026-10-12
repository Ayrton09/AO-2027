# Gestor de servidores CoverAO para Windows, sin Docker y sin programas de terceros.
#
# EL MODELO: UNA INSTALACION COMPLETA POR SERVIDOR, cada una en su carpeta. Es como se hostea un juego
# —igual que un Counter-Strike— y no es una limitacion sino la razon de ser: cada servidor tiene SUS mapas,
# SU data del juego, SU config y SU rango de puertos. Podes tener uno vanilla y otro con los mapas
# modificados, o uno en la version estable y otro probando la nueva, sin que se toquen.
#
# Este script va en la carpeta que CONTIENE a los servidores:
#
#     C:\CoverAO\
#         coverao.ps1          <- este archivo
#         plantilla\           <- una instalacion limpia, la que baja del release
#         rojo\                <- un servidor:  CoverAO.Server.exe, gamedata\, webclient\, data\, Server.ini
#         azul\                <- otro, independiente
#
#   .\coverao.ps1 nueva rojo              copia la plantilla y le asigna puertos libres
#   .\coverao.ps1 iniciar rojo
#   .\coverao.ps1 estado                  todos, con RAM y puertos
#   .\coverao.ps1 detener rojo            APAGADO LIMPIO (guarda a los conectados)
#   .\coverao.ps1 arranque rojo           que arranque solo al prender la maquina
#
# POR QUE `detener` NO ES UN taskkill. Un servidor de fondo en Windows no tiene ventana de consola, asi que
# no hay a quien mandarle la senal de cierre. Medido el 2026-08-02 con el mundo real cargado:
#
#     taskkill /PID <n>  (sin /F)   salio en  87 ms, sin guardar a nadie
#     Stop-Process -Force           salio en  24 ms, identico
#
# Los dos son un kill: cada reinicio le costaba a cada jugador conectado todo lo hecho desde el ultimo
# autosave (hasta 15 minutos). Por eso el servidor mira un archivo `detener` en su propia carpeta, y ese es
# el camino que usa este script. Medido igual: 841 ms, con "Servidor detenido" en el log.
#
# PUERTOS: cada servidor usa StartPort..StartPort+3 (juego, WebSocket, panel de admin, cliente web). Hay que
# separarlos de a 4 como minimo; `nueva` los asigna de a 10 mirando los que ya estan usados.

[CmdletBinding()]
param(
  [Parameter(Position = 0)][string]$Accion = "ayuda",
  [Parameter(Position = 1)][string]$Nombre,
  [int]$Puerto = 0,
  [string]$Desde = "plantilla",
  # OJO: el default NO puede ser `$PSScriptRoot`. Adentro de un `param()` esa variable esta VACIA cuando el
  # script se lanza con `powershell -File coverao.ps1 ...` —que es como lo llama un acceso directo, otro
  # script o una tarea programada— y solo tiene valor si se lo invoca como `.\coverao.ps1`. Con el default
  # vacio, el primer `Join-Path $Raiz $n` tiraba "No se puede enlazar el argumento con el parametro 'Path'
  # porque es una cadena vacia" —un error que no dice nada de lo que pasa— y encima el proceso salia con
  # codigo 0, o sea que para quien lo automatiza el comando "funciono".
  [string]$Raiz
)

$ErrorActionPreference = "Stop"
if (-not $Raiz) { $Raiz = $PSScriptRoot }   # ya en el cuerpo si tiene valor, venga como venga la invocacion
if (-not $Raiz) { $Raiz = (Get-Location).Path }

function CarpetaDe($n) { Join-Path $Raiz $n }
function ExeDe($n) { Join-Path (CarpetaDe $n) "CoverAO.Server.exe" }

function EsServidor($dir) { Test-Path (Join-Path $dir.FullName "CoverAO.Server.exe") }

function Servidores {
  # La PLANTILLA es una instalacion mas —tiene el ejecutable— pero no es un servidor: no se lista, no se
  # arranca, y sobre todo no se le cuenta el puerto. Sin esta exclusion `nueva` la contaba como ocupando el
  # 7666 y el primer servidor de verdad nacia en el 7676, que es raro y no lo explica nada.
  Get-ChildItem $Raiz -Directory -ErrorAction SilentlyContinue |
    Where-Object { (EsServidor $_) -and $_.Name -ne (Split-Path $Desde -Leaf) }
}

function PuertoDe($n) {
  $ini = Join-Path (CarpetaDe $n) "Server.ini"
  if (Test-Path $ini) {
    $m = Select-String -Path $ini -Pattern '^\s*StartPort\s*=\s*(\d+)' | Select-Object -First 1
    if ($m) { return [int]$m.Matches[0].Groups[1].Value }
  }
  return 7666
}

# El proceso se reconoce por SU RUTA, no por el nombre del ejecutable: con varios servidores hay N procesos
# llamados CoverAO.Server.exe y hay que saber cual es cual.
function ProcesoDe($n) {
  $exe = ExeDe $n
  Get-CimInstance Win32_Process -Filter "Name='CoverAO.Server.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $exe } | Select-Object -First 1
}

function Existe($n) {
  if (-not $n) { throw "Falta el nombre del servidor." }
  # El nombre es UNA carpeta de la raiz: con barras, ':' o '..' apuntaria a cualquier otro lado.
  if ($n -match '[\\/:]' -or $n -in '.', '..') { throw "'$n' no es un nombre de servidor: va sin barras, ':' ni '..'." }
  if (-not (Test-Path (ExeDe $n))) { throw "No hay un servidor '$n' en $Raiz. Crealo con:  .\coverao.ps1 nueva $n" }
}

# Se avisa ANTES de intentar: sin esto el usuario recibe "Register-ScheduledTask : Acceso denegado" con su volcado de
# CimException, que no dice que hay que reabrir la consola como Administrador.
function ExigirAdministrador($que, $accion) {
  $yo = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  if (-not $yo.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "$que pide una consola COMO ADMINISTRADOR. Abri PowerShell con boton derecho -> 'Ejecutar como administrador' y corre de nuevo:  .\coverao.ps1 $accion $Nombre"
  }
}

# La cuenta con la que corre el arranque automatico, LOCAL SERVICE, por su SID: el nombre cambia con el idioma de Windows
# ("NT AUTHORITY\SERVICIO LOCAL" en castellano).
$SidDelServicio = "S-1-5-19"
function CuentaDelServicio {
  (New-Object Security.Principal.SecurityIdentifier $SidDelServicio).Translate([Security.Principal.NTAccount]).Value
}

# Los dos puertos HTTP que se abren en toda interfaz: el WebSocket (StartPort+1) y el cliente web (StartPort+3).
function UrlsDe($n) {
  $p = PuertoDe $n
  "http://+:$($p + 1)/", "http://+:$($p + 3)/"
}

function Ayuda {
  Write-Host ""
  Write-Host "  CoverAO - gestor de servidores (Windows, sin Docker)" -ForegroundColor Cyan
  Write-Host "  Una instalacion completa por servidor: cada uno con sus mapas, su config y sus puertos."
  Write-Host ""
  Write-Host "    nueva <nombre> [-Puerto n] [-Desde plantilla]   copia una instalacion y le da puertos"
  Write-Host "    iniciar <nombre>                                lo arranca"
  Write-Host "    detener <nombre>                                lo apaga LIMPIO (guarda a los conectados)"
  Write-Host "    estado [nombre]                                 que hay corriendo, con RAM y puertos"
  Write-Host "    arranque <nombre> / sin-arranque <nombre>       que arranque solo al prender la maquina"
  Write-Host ""
  Write-Host "  Carpeta de servidores: $Raiz"
  Write-Host ""
}

switch ($Accion.ToLowerInvariant()) {

  "nueva" {
    if (-not $Nombre) { throw "Falta el nombre. Ej:  .\coverao.ps1 nueva rojo" }
    if ($Nombre -notmatch '^[A-Za-z0-9_-]+$') { throw "El nombre solo puede tener letras, numeros, guion y guion bajo." }
    $destino = CarpetaDe $Nombre
    if (Test-Path $destino) { throw "Ya existe '$Nombre' en $destino" }
    $origen = if ([IO.Path]::IsPathRooted($Desde)) { $Desde } else { Join-Path $Raiz $Desde }
    # SI NO HAY `plantilla`, SE USA LA UNICA INSTALACION QUE HAYA. El default se llama asi, pero lo que el
    # jugador descomprime se llama `CoverAO-win-x64`: siguiendo el LEEME al pie de la letra
    # (`.\coverao.ps1 nueva rojo`) el comando fallaba pidiendo una carpeta que nadie le dijo que creara.
    if (-not (Test-Path (Join-Path $origen "CoverAO.Server.exe"))) {
      $candidatas = @(Get-ChildItem -Directory $Raiz -ErrorAction SilentlyContinue | Where-Object { EsServidor $_ })
      if ($candidatas.Count -eq 1) {
        $origen = $candidatas[0].FullName
        Write-Host "No hay carpeta '$Desde'; uso la unica instalacion que encontre: $($candidatas[0].Name)" -ForegroundColor Yellow
      } elseif ($candidatas.Count -gt 1) {
        throw ("Hay varias instalaciones ({0}) y ninguna carpeta '{1}'. Deci cual copiar con -Desde <carpeta>." -f
               (($candidatas | ForEach-Object { $_.Name }) -join ', '), $Desde)
      } else {
        throw "No encuentro una instalacion en '$origen'. Descomprimi ahi el release, o pasa -Desde <carpeta>."
      }
    }
    if ($Puerto -le 0) {
      # Elegir puertos a mano es de donde salen los choques, y un choque se ve como "no arranca" sin decir
      # por que. Se busca el primero libre de a 10, mirando los servidores que ya hay.
      $usados = @(Servidores | ForEach-Object { PuertoDe $_.Name })
      $Puerto = 7666; while ($usados -contains $Puerto) { $Puerto += 10 }
    }
    Write-Host "Copiando la instalacion a $destino (son varios cientos de MB, tarda un rato)..."
    Copy-Item $origen $destino -Recurse -Force
    # data/ de la plantilla NO se hereda: un servidor nuevo arranca sin cuentas ni personajes de nadie.
    Remove-Item (Join-Path $destino "data") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $destino "detener") -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $destino "data") | Out-Null
    $ini = Join-Path $destino "Server.ini"
    if (Test-Path $ini) {
      (Get-Content $ini -Raw) -replace '(?m)^\s*StartPort\s*=.*$', "StartPort=$Puerto" | Set-Content $ini -Encoding ascii
    } else {
      "[INIT]`r`nStartPort=$Puerto`r`n" | Out-File -Encoding ascii $ini
    }
    Write-Host "Listo: '$Nombre' (puertos $Puerto..$($Puerto+3))" -ForegroundColor Green
    Write-Host "  Sus mapas y su data del juego estan en $destino\gamedata  -- podes cambiarlos sin tocar los otros."
    # ${ini} con llaves y no $ini: PowerShell lee `$ini:` como una unidad (tipo C:) y el archivo entero
    # deja de parsear -- o sea que el script no corre NINGUNA accion, no falla solo esta linea.
    Write-Host "  Para ser GM edita ${ini}: en [INIT] pone Dioses=1 y en [Dioses] Dios1=TuPersonaje"
    Write-Host "  Despues:  .\coverao.ps1 iniciar $Nombre"
  }

  "iniciar" {
    Existe $Nombre
    if (ProcesoDe $Nombre) { Write-Host "'$Nombre' ya esta corriendo." -ForegroundColor Yellow; break }
    $d = CarpetaDe $Nombre
    # Un `detener` que haya quedado de un apagado anterior lo apagaria apenas arranque, y desde afuera se ve
    # como "arranca y se cae solo".
    Remove-Item (Join-Path $d "detener") -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath (ExeDe $Nombre) -WorkingDirectory $d -WindowStyle Hidden | Out-Null
    $p = PuertoDe $Nombre
    Write-Host "Arrancando '$Nombre' (puertos $p..$($p+3))..." -NoNewline
    for ($i = 0; $i -lt 120; $i++) {
      Start-Sleep -Milliseconds 500
      try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $p); $c.Close()
            Write-Host " listo. Cliente web: http://localhost:$($p+3)/" -ForegroundColor Green; break } catch {}
    }
  }

  "detener" {
    Existe $Nombre
    $proc = ProcesoDe $Nombre
    if (-not $proc) { Write-Host "'$Nombre' no esta corriendo." -ForegroundColor Yellow; break }
    New-Item -ItemType File (Join-Path (CarpetaDe $Nombre) "detener") -Force | Out-Null
    Write-Host "Apagando '$Nombre' (guardando a los conectados)..." -NoNewline
    for ($i = 0; $i -lt 240; $i++) {
      Start-Sleep -Milliseconds 500
      if (-not (Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue)) { Write-Host " listo." -ForegroundColor Green; break }
    }
    if (Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue) {
      Write-Host " NO se apago en 2 minutos." -ForegroundColor Red
      Write-Host "  Si hace falta forzarlo:  Stop-Process -Id $($proc.ProcessId) -Force   (OJO: eso NO guarda)"
    }
  }

  { $_ -in "estado", "listar" } {
    $filas = foreach ($dir in Servidores) {
      if ($Nombre -and $dir.Name -ne $Nombre) { continue }
      $proc = ProcesoDe $dir.Name
      $p = PuertoDe $dir.Name
      [pscustomobject]@{
        Servidor = $dir.Name
        Estado   = if ($proc) { "corriendo" } else { "detenido" }
        PID      = if ($proc) { $proc.ProcessId } else { "" }
        RAM_MB   = if ($proc) { [int]((Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue).WorkingSet64 / 1MB) } else { "" }
        Puertos  = "$p..$($p+3)"
        Web      = "http://localhost:$($p+3)/"
      }
    }
    if ($filas) { $filas | Format-Table -AutoSize }
    else { Write-Host "No hay servidores en $Raiz. Crea uno con:  .\coverao.ps1 nueva rojo" }
  }

  "arranque" {
    Existe $Nombre
    # Tarea programada y no servicio de Windows: un servicio necesita que el ejecutable hable con el Service
    # Control Manager, que es codigo que este servidor no tiene (y agregarlo lo ataria a Windows). La tarea
    # arranca al prender la maquina sin que nadie inicie sesion y reintenta si se cae, que es lo que hace
    # falta. Registrarla a nivel maquina pide PowerShell COMO ADMINISTRADOR.
    ExigirAdministrador "Registrar el arranque automatico" "arranque"
    $d = CarpetaDe $Nombre
    # CORRE COMO "LOCAL SERVICE", NO COMO SYSTEM (auditoria del 12/10). SYSTEM es el dueno de la maquina: un agujero en el
    # servidor era un agujero en todo Windows. Esta cuenta no puede mas que lo que se le da aca: escribir en la carpeta del
    # servidor (los personajes, los logs, el Server.ini) y abrir sus dos puertos HTTP, el WebSocket y el cliente web, en toda
    # interfaz, que sin ser Administrador pide una reserva de URL (ver HttpBind.cs). El panel de admin es solo local: no la pide.
    $cuenta = CuentaDelServicio
    icacls $d /grant "*$($SidDelServicio):(OI)(CI)M" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo darle permiso de escritura a '$cuenta' sobre $d." }
    foreach ($url in UrlsDe $Nombre) {
      $ya = netsh http show urlacl url=$url | Out-String
      if ($ya -match [regex]::Escape($cuenta)) { continue }
      if ($ya -match [regex]::Escape($url)) {
        Write-Host "  Ya hay una reserva de $url para otra cuenta: hasta borrarla (netsh http delete urlacl url=$url) ese puerto queda solo para esta maquina." -ForegroundColor Yellow
        continue
      }
      netsh http add urlacl url=$url "sddl=D:(A;;GX;;;LS)" | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "No se pudo reservar $url para '$cuenta'." }
    }
    $accionT = New-ScheduledTaskAction -Execute (ExeDe $Nombre) -WorkingDirectory $d
    $disparo = New-ScheduledTaskTrigger -AtStartup
    $opts = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
              -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
    $principal = New-ScheduledTaskPrincipal -UserId $cuenta -LogonType ServiceAccount -RunLevel Limited
    Register-ScheduledTask -TaskName "CoverAO $Nombre" -Action $accionT -Trigger $disparo `
      -Settings $opts -Principal $principal -Force | Out-Null
    Write-Host "'$Nombre' va a arrancar solo al prender la maquina, como '$cuenta'." -ForegroundColor Green
  }

  "sin-arranque" {
    Existe $Nombre
    ExigirAdministrador "Sacar el arranque automatico" "sin-arranque"
    Unregister-ScheduledTask -TaskName "CoverAO $Nombre" -Confirm:$false
    # Y lo que le dio `arranque` a la cuenta del servicio: las reservas de URL que son suyas y el permiso sobre la carpeta.
    $cuenta = CuentaDelServicio
    foreach ($url in UrlsDe $Nombre) {
      if ((netsh http show urlacl url=$url | Out-String) -match [regex]::Escape($cuenta)) { netsh http delete urlacl url=$url | Out-Null }
    }
    icacls (CarpetaDe $Nombre) /remove:g "*$SidDelServicio" /Q | Out-Null
    Write-Host "'$Nombre' ya no arranca solo." -ForegroundColor Green
  }

  default { Ayuda }
}
