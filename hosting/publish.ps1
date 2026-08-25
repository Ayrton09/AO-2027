<#
  publish.ps1 — Arma la distribución PORTABLE de CoverAO: un servidor self-contained (.exe sin .NET
  instalado) que trae su data de juego, el panel de admin y el cliente web, listo para copiar y correr.

  Uso:
    pwsh hosting/publish.ps1                      # win-x64 -> dist/CoverAO-win-x64
    pwsh hosting/publish.ps1 -Rid linux-x64       # Linux
    pwsh hosting/publish.ps1 -OutDir D:\CoverAO   # destino custom
    pwsh hosting/publish.ps1 -Comprimir           # + el archivo para publicar (.zip en Windows,
                                                  #   .tar.gz en Linux; el nombre lleva -Version)

  El .exe encuentra solo su data: 'gamedata' (mundo/objetos/hechizos) y 'webclient' (cliente) van
  al lado del ejecutable; FindDataDir y StaticFileServer los detectan por AppContext.BaseDirectory.
#>
param(
  [string]$Rid = "win-x64",
  [string]$OutDir = "",
  [string]$Configuration = "Release",
  [switch]$Comprimir,             # ademas de la carpeta, deja el archivo para publicar
  [string]$Version = "1.0.0"      # solo para el nombre del comprimido
)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$Proj = Join-Path $Root "server\src\CoverAO.Server\CoverAO.Server.csproj"
if ($OutDir -eq "") { $OutDir = Join-Path $Root "dist\CoverAO-$Rid" }
$Pub = Join-Path $env:TEMP "coverao-publish-$Rid"

Write-Host "== 1/4  dotnet publish self-contained ($Rid) ==" -ForegroundColor Cyan
if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
dotnet publish $Proj -c $Configuration -r $Rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -o $Pub --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish falló" }

Write-Host "== 2/4  ensamblando $OutDir ==" -ForegroundColor Cyan
# ACTUALIZAR NO ES REINSTALAR. Aca habia un `Remove-Item -Recurse -Force $OutDir` que borraba la carpeta
# ENTERA, y adentro de esa carpeta es donde el servidor crea `data\` -las cuentas y los personajes- y donde
# vive el `Server.ini` que el host edito con su puerto, sus rates y su lista de GMs. O sea que rearmar la
# distribucion sobre una carpeta en uso le borraba a un host TODOS sus jugadores y su configuracion. Para el
# que hostea eso no es un detalle: es perder el servidor.
#
# Ahora se limpia todo MENOS eso: se borra el .exe viejo, gamedata, webclient y los scripts -que es lo que
# hay que reemplazar- y se dejan en pie los dos que son del host y no nuestros.
$preservar = @('data', 'Server.ini')
if (Test-Path $OutDir) {
  $enPie = Get-ChildItem -Force $OutDir | Where-Object { $preservar -contains $_.Name }
  if ($enPie) { Write-Host ("   actualizando: se CONSERVAN {0}" -f (($enPie | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor Yellow }
  Get-ChildItem -Force $OutDir | Where-Object { $preservar -notcontains $_.Name } | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
# el .exe + wwwroot (panel admin, va como Content) + su .pdb-less runtime
Copy-Item -Recurse -Force (Join-Path $Pub "*") $OutDir
# quitar restos que no hacen falta para correr
Get-ChildItem $OutDir -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "== 3/4  copiando gamedata + webclient ==" -ForegroundColor Cyan
# data de juego (mundo/objetos/hechizos/spawns) -> gamedata (FindDataDir la detecta)
Copy-Item -Recurse -Force (Join-Path $Root "gamedata") (Join-Path $OutDir "gamedata")
# cliente web -> webclient (StaticFileServer lo sirve). Sólo lo necesario para correr (no src/tools/node_modules).
$web = Join-Path $OutDir "webclient"
New-Item -ItemType Directory -Force -Path $web | Out-Null
Copy-Item -Force (Join-Path $Root "client-web\index.html") $web
Copy-Item -Recurse -Force (Join-Path $Root "client-web\dist") (Join-Path $web "dist")
# Los TESTS del cliente no son parte del juego: `tsc` los compila al mismo `dist/` que el codigo que corre,
# asi que se colaban en el paquete. El jugador recibe el juego, no el banco de pruebas.
$tests = Join-Path $web "dist\test"
if (Test-Path $tests) { Remove-Item -Recurse -Force $tests }
Copy-Item -Recurse -Force (Join-Path $Root "client-web\assets") (Join-Path $web "assets")
# Las NOTAS INTERNAS no viajan. En `assets/sounds` hay un LEEME.md que le habla a quien mantiene el
# repo -cita fase0/, los checks de npm y que decidio el autor- y el servidor lo PUBLICA por HTTP junto
# al resto de los assets, asi que cualquier jugador se lo puede bajar. El paquete lleva el juego, no
# la bitacora de como se hizo.
Get-ChildItem $web -Recurse -Filter *.md -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# Server.ini: la configuración del host (puerto, rates, y sobre todo QUIÉN ES GM). Va al lado del .exe, que es
# donde el servidor lo busca. Se copia comentado y con las listas de staff vacías: el server arranca igual, y el
# host tiene el archivo delante para editarlo en vez de tener que averiguar que existe.
# Si YA hay un Server.ini es porque el host lo edito (su puerto, sus rates, sus GMs): no se pisa nunca. El
# de ejemplo se copia al lado con otro nombre, para que pueda mirar las claves nuevas de esta version.
$iniDestino = Join-Path $OutDir "Server.ini"
if (Test-Path $iniDestino) {
  Copy-Item -Force (Join-Path $PSScriptRoot "Server.ini.ejemplo") (Join-Path $OutDir "Server.ini.ejemplo")
  Write-Host "   Server.ini: ya existia, se RESPETA (el de ejemplo queda como Server.ini.ejemplo)" -ForegroundColor Yellow
} else {
  Copy-Item -Force (Join-Path $PSScriptRoot "Server.ini.ejemplo") $iniDestino
}

# La guia del host viaja EN EL PAQUETE: el que abre el zip no clona el repo.
$doc = Join-Path $Root "docs\HOSTING.md"
if (Test-Path $doc) {
  # El HOSTING.md del repo abre con "## Armar el paquete", que es para QUIEN COMPILA. En el paquete esa
  # seccion sobra y confunde: quien lo recibe tiene el binario, no el codigo, asi que no puede correr nada
  # de ahi. Se copia todo lo demas --puertos, Server.ini, backups, varios servidores, HTTPS, licencia--,
  # que es justo lo que necesita el que hostea.
  # SE SACAN DOS SECCIONES, las dos por el mismo motivo: le hablan a quien tiene el CODIGO y no a quien
  # recibio el zip. "Armar el paquete" es para el que compila, y "Docker" manda a correr `docker compose`
  # con un docker-compose.yml que vive solo en el repo: seguirla al pie de la letra termina en un error.
  $fuera = $false
  $salida = foreach ($l in Get-Content $doc -Encoding UTF8) {
    if ($l -match '^##\s') { $fuera = ($l -match '^##\s+Armar el paquete') }
    if ($l -match '^\*\*Docker\*\*') { $fuera = $true }
    elseif ($fuera -and $l -match '^\*\*') { $fuera = $false }
    if (-not $fuera) {
      # LAS RUTAS DEL REPO NO EXISTEN EN LA CAJA: los cuatro archivos que HOSTING.md enlaza como
      # hosting/... estan en la RAIZ del paquete, al lado del propio HOSTING.md. Copiadas tal cual, el
      # "sudo cp hosting/coverao@.service ..." falla y los enlaces no llevan a ningun lado.
      # Sin ancla: el nombre de la carpeta tambien aparece DENTRO del texto del enlace
      # (``hosting/Server.ini.ejemplo``), y ahi tampoco existe.
      $l -replace '\.\./hosting/', '' -replace 'hosting/', ''
    }
  }
  Set-Content -Path (Join-Path $OutDir "HOSTING.md") -Value $salida -Encoding UTF8
  Write-Host "   HOSTING.md: sin la seccion de compilacion (el paquete no lleva codigo fuente)" -ForegroundColor DarkGray
}
Copy-Item -Force (Join-Path $Root "CREDITOS.md") (Join-Path $OutDir "CREDITOS.md") -ErrorAction SilentlyContinue
# Units de systemd: `coverao.service` para un servidor, `coverao@.service` (plantilla) para varios.
foreach ($u in @("coverao.service", "coverao@.service")) {
  $unit = Join-Path $PSScriptRoot $u
  if (Test-Path $unit) {
    # Se normaliza a LF igual que el servidor.sh, y por el mismo motivo: la lee systemd en Linux, y con CRLF
    # se queja del retorno de carro al final de cada valor. El .gitattributes ya lo fija en el repo; esto
    # cubre el caso de que alguien arme la distribucion desde una copia que no vino de git.
    # -Encoding UTF8 NO es opcional: sin el, Windows PowerShell 5.1 lee este archivo con la codepage
    # ANSI y cada raya y cada acento salen doble-codificados en el paquete -- incluido el `Description=`
    # que muestra `systemctl status`.
    $txt = (Get-Content -Raw -Encoding UTF8 $unit) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $OutDir $u), $txt, (New-Object Text.UTF8Encoding $false))
  }
}

# El gestor de Windows: va un nivel arriba de los servidores, pero viaja adentro del zip.
$gestor = Join-Path $PSScriptRoot "coverao.ps1"
if (Test-Path $gestor) { Copy-Item -Force $gestor (Join-Path $OutDir "coverao.ps1") }

# La MUSICA ya viene en el repo (14 pistas), asi que esto deberia dar siempre 14 y el aviso no deberia
# saltar nunca. Se deja igual: hasta el 2026-08-03 la carpeta estaba gitignoreada y cualquiera que clonara
# armaba un paquete MUDO de musica sin enterarse -- el mismo agujero que el septimo eje, donde el juego
# sonaba a 3 efectos de 164 y ningun test lo veia. Un contador que confirma lo que ya deberia estar cuesta
# nada y es lo unico que separa "esta" de "suponemos que esta".
$mp3 = @(Get-ChildItem (Join-Path $web "assets\music") -Filter *.mp3 -ErrorAction SilentlyContinue)
if ($mp3.Count -eq 0) {
  Write-Host "   OJO: 0 archivos de musica. El paquete sale SIN MUSICA." -ForegroundColor Red
  Write-Host "        Tendrian que estar en client-web\assets\music (van en el repo). Si clonaste con" -ForegroundColor Red
  Write-Host "        algun filtro, o las borraste:  python fase0/transcode/decode_music.py" -ForegroundColor Red
} else {
  Write-Host ("   musica: {0} pista(s)" -f $mp3.Count) -ForegroundColor DarkGray
}

# LICENCIA. Va en el paquete porque la AGPL lo pide: quien recibe una copia tiene que recibir los terminos.
# No es opcional ni decorativo — sin esto la distribucion no se puede repartir.
$lic = Join-Path $Root "LICENSE"
if (Test-Path $lic) { Copy-Item -Force $lic (Join-Path $OutDir "LICENSE") }
else { Write-Host "   OJO: no hay LICENSE en la raiz; el paquete NO se puede distribuir asi." -ForegroundColor Red }

# NOVEDADES para el jugador. El servidor las manda al entrar (SendUpdate) y el cliente las muestra en
# Menú → Novedades. Salen de la sección MÁS NUEVA del CHANGELOG.md —sus líneas que empiezan con "- "— y se
# escriben en data/updates.txt, que tiene PRIORIDAD sobre lo que venga en el paquete: así el que hostea las
# puede cambiar sin recompilar. Sin esto, el jugador veía el placeholder que los autores de CoverAO nunca
# completaron ("12/8/21: 1", "16/8/21: 2"…), que es lo que trae el ACTUALIZACIONES.ini del install original.
# EL JUGADOR NO LEE MARKDOWN. Estas lineas van a Menu -> Novedades tal cual, DENTRO del juego: los
# asteriscos de la negrita y las comillas invertidas del codigo se le muestran crudos en pantalla
# ("**Cada raza usa la ropa que le corresponde.**"). El CHANGELOG.md se sigue escribiendo en Markdown
# porque tambien se lee en GitHub; lo que se limpia es la copia que viaja al juego.
function Limpiar-Markdown([string]$t) {
  $t = $t -replace '\*\*([^*]+)\*\*', '$1'
  $t = $t -replace '(?<!\*)\*([^*]+)\*', '$1'
  $t = $t -replace '`([^`]+)`', '$1'
  $t = $t -replace '\[([^\]]+)\]\([^)]+\)', '$1'
  return $t.Trim()
}

$changelog = Join-Path $Root "CHANGELOG.md"
if (Test-Path $changelog) {
  $lineas = @()
  $enSeccion = $false
  foreach ($l in Get-Content $changelog -Encoding UTF8) {
    if ($l -match '^##\s') {
      if ($enSeccion) { break }              # empezó la sección siguiente: ya tenemos la más nueva
      if ($l -match '^##\s+\d') { $enSeccion = $true }   # "## 1.0.0 — fecha" (y no "## Cómo se agrega…")
      continue
    }
    if (-not $enSeccion) { continue }
    if ($l -match '^\s*-\s+(.*)$') { $lineas += (Limpiar-Markdown $Matches[1]); continue }
    # CONTINUACION de la vinieta anterior. Sin esto se PERDIA: una vinieta escrita en dos lineas -que en
    # Markdown es una sola frase- llegaba al jugador cortada a la mitad, terminando en "y" o en "de". El
    # bug no se ve en el paquete ni en el CHANGELOG: se ve en Menu -> Novedades, que es lo ultimo que uno
    # mira. Se pega con un espacio, que es como lo renderiza cualquier lector de Markdown.
    if ($lineas.Count -gt 0 -and $l -match '^\s+\S' -and $l -notmatch '^\s*#') {
      $lineas[$lineas.Count - 1] = ($lineas[$lineas.Count - 1] + " " + (Limpiar-Markdown $l))
    }
  }
  if ($lineas.Count -gt 0) {
    $dataDir = Join-Path $OutDir "data"
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -Path (Join-Path $dataDir "updates.txt") -Value $lineas -Encoding UTF8
    Write-Host ("   novedades: {0} linea(s) del CHANGELOG -> data/updates.txt" -f $lineas.Count) -ForegroundColor DarkGray
  } else {
    Write-Host "   OJO: el CHANGELOG no tiene una seccion de version con lineas '-'; el jugador vera el texto del install original." -ForegroundColor Yellow
  }
}

Write-Host "== 4/4  scripts + LEEME ==" -ForegroundColor Cyan
$bat = @"
@echo off
cd /d "%~dp0"
echo Iniciando CoverAO... (cerra esta ventana para apagar el servidor)
echo Cuando diga "Cliente web: http://localhost:7669/", abri esa URL en el navegador.
CoverAO.Server.exe %*
pause
"@
Set-Content -Path (Join-Path $OutDir "Servidor.bat") -Value $bat -Encoding ASCII
# Lanzador de Linux. Se escribe SIEMPRE (no sólo con -Rid linux-*) porque la distribución se arma casi
# siempre desde Windows y el que hostea puede estar armando la de Linux para subirla a un VPS.
# LF obligatorio: con CRLF, bash falla con "bad interpreter: /bin/bash^M".
$sh = @"
#!/bin/bash
# CoverAO - arranque en Linux. Si da "Permission denied":  chmod +x servidor.sh CoverAO.Server
cd "`$(dirname "`$0")" || exit 1
[ -x ./CoverAO.Server ] || chmod +x ./CoverAO.Server 2>/dev/null
echo 'Iniciando CoverAO... (Ctrl+C para apagar el servidor)'
echo 'Cuando diga "Cliente web: http://localhost:7669/", abri esa URL en el navegador.'
exec ./CoverAO.Server "`$@"
"@ -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $OutDir "servidor.sh"), $sh, (New-Object Text.UTF8Encoding $false))
$leeme = @"
CoverAO — servidor + cliente web (distribucion portable, self-contained)
========================================================================

QUE ES: un servidor de Argentum Online moderno (.NET 10) que NO necesita tener .NET
instalado. Trae su propio mundo, el panel de administracion y el cliente web.

COMO JUGAR (Windows):
  1) Ejecuta  Servidor.bat  (o  CoverAO.Server.exe  directamente).
  2) Espera a que cargue el mundo (~10s) y muestre las URLs.
  3) Abri en el navegador:   http://localhost:7669/     (cliente web para jugar)
  4) Crea una cuenta y un personaje, y a jugar.

EN LINUX (distribucion armada con  -Rid linux-x64 ):

  bash servidor.sh

  Va con  bash  adelante A PROPOSITO. Un .zip no guarda el permiso de ejecucion,
  asi que  ./servidor.sh  te contesta "Permission denied" apenas descomprimis, y
  no hay forma de adivinar por que.  bash servidor.sh  anda siempre, y el script
  le pone el permiso al ejecutable el solo. Si preferis el modo clasico:
      chmod +x servidor.sh CoverAO.Server  &&  ./servidor.sh

  Tampoco necesita .NET instalado. El nombre del Server.ini no importa en
  mayusculas/minusculas: el servidor lo encuentra igual.

JUGAR CON AMIGOS:
  Al arrancar, el servidor imprime un bloque "== Como entrar ==" con las tres
  direcciones que existen. No hace falta averiguar nada por afuera:

    Vos, en esta maquina : http://localhost:7669/
    Desde tu red (WiFi)  : http://TU-IP:7669/    <- esta es la que les pasas
    Desde internet       : hay que abrir puertos en el router

  EN LA MISMA CASA / MISMA RED alcanza con la segunda. Si tus amigos no entran:
    - Windows te pregunta la primera vez si permitis el acceso en la red: hay que
      decir que SI. Si le diste "Cancelar", el puerto queda bloqueado y no hay
      ningun otro sintoma. Se arregla en "Firewall de Windows Defender" ->
      "Permitir una aplicacion", buscando CoverAO.Server.
    - Si el servidor avisa "los puertos HTTP quedaron accesibles SOLO DESDE ESTA
      MAQUINA", segui las instrucciones que imprime ahi mismo.

  POR INTERNET hay que reenviar en el router hacia esta maquina los puertos
  7669 (la pagina) y 7667 (por donde se juega). Los DOS: con solo el 7669 tus
  amigos ven la pantalla de login y no pueden conectarse. El 7666 va ademas si
  alguien usa el cliente clasico de Windows. Y ojo que tu IP de internet suele
  cambiar sola; para algo estable, un dominio y un reverse-proxy con HTTPS
  (esta explicado en HOSTING.md).

CONFIGURACION — Server.ini (esta al lado del ejecutable):
  Ahi se ajusta el puerto, el tope de jugadores, los rates de experiencia y oro,
  si se pueden crear personajes, y sobre todo QUIEN ES GM. Los cambios se aplican
  al reiniciar el servidor, y al arrancar el server imprime que leyo del archivo.

  PARA HACERTE GM (si no, no podes usar ningun comando de administracion):
    1) Crea tu personaje en el juego.
    2) Abri Server.ini y en la seccion [INIT] pone     Dioses=1
    3) Mas abajo, en la seccion [Dioses], pone         Dios1=TuPersonaje
    4) Reinicia el servidor. Al entrar vas a tener el nick con color de GM.
  Sacar el nombre de la lista le quita el rango la proxima vez que entre: el
  rango NO queda guardado en el personaje, lo decide el Server.ini en cada login.

PUERTOS (base 7666, configurable con el primer argumento, ej: CoverAO.Server.exe 8000):
  7666  juego (protocolo AO clasico, TCP)
  7667  WebSocket (cliente web)
  7668  panel de administracion (imprime la URL con token al arrancar)
  7669  cliente web (archivos estaticos)

OPCIONES utiles:
  CoverAO.Server.exe 7666            puerto base (los demas son base+1/+2/+3)
  CoverAO.Server.exe --no-web        no servir el cliente web (usar tu propio http)
  CoverAO.Server.exe --no-admin      no levantar el panel de admin
  CoverAO.Server.exe --web-port 80   servir el cliente web en el puerto 80

INTERNET / HTTPS: para exponerlo fuera de tu red, poné un reverse-proxy con TLS
delante (ver HOSTING.md). El cliente usa wss:// automaticamente sobre HTTPS.

Los personajes y cuentas se guardan en la carpeta  data\  (se crea al arrancar).
  data\Charfile\  los personajes         data\Cuentas\  las cuentas
  data\backups\   COPIAS DE SEGURIDAD del mundo entero. El servidor hace UNA CADA
                  MEDIA HORA solo, sin frenar a nadie, y conserva las ultimas 48
                  (un dia). Antes de cada copia guarda a todos los conectados, asi
                  el backup no miente sobre lo que habia. Tambien hay un boton
                  "Copia de seguridad" en el panel de administracion.
                  Se ajusta con MinutosBackup / BackupsAConservar en Server.ini.
  data\logs\      un archivo de log por dia (LogDias en Server.ini)

PARA VOLVER ATRAS CON UNA COPIA (probalo UNA vez antes de necesitarlo):
  1. Para el servidor (cerra la ventana). Restaurar con el servidor prendido pisa
     archivos que el servidor esta escribiendo.
  2. Renombra las tres carpetas de ahora, por las dudas:
       data\Charfile -> data\Charfile.antes   (y lo mismo con Cuentas y Misc)
  3. Copia esas mismas tres desde la copia elegida. Cada copia es una carpeta
     llamada coverao-AAAAMMDD-HHMMSS, asi que se ordenan solas por fecha:
       data\backups\coverao-20260808-030000\Charfile  ->  data\Charfile
       ...Cuentas -> data\Cuentas       ...Misc -> data\Misc
  4. Arranca. Los personajes y las cuentas quedan como estaban en ese momento.
  Son SOLO esas tres: los mapas y el cliente web salen de la instalacion. Y no
  alcanza con copiar un .chr suelto: la cuenta que lo lista vive en Cuentas\, asi
  que sin ella el personaje no aparece al entrar.

Ademas guarda a los conectados cada 15 minutos, asi un corte de luz no se lleva la
tarde de nadie. Las dos cosas vienen PRENDIDAS aunque borres el Server.ini.

COMO APAGARLO SIN PERDER NADA. Al apagarse bien, el servidor guarda a TODOS los que
estan jugando en ese momento. Hay tres formas y todas sirven:
  - cerra la ventana del servidor (o Ctrl+C si lo abriste desde una consola)
  - el boton "Apagar" del panel de administracion
  - crea un archivo vacio llamado  detener  en esta misma carpeta

La tercera es la que sirve cuando el servidor corre DE FONDO -sin ventana-, que es lo
normal si lo dejas prendido como servicio o con una tarea programada. En ese caso NO
uses el Administrador de tareas ni taskkill: eso lo mata de una y cada jugador
conectado pierde todo lo hecho desde el ultimo guardado (hasta 15 minutos). El archivo
tarda menos de un segundo y guarda todo. El servidor lo borra solo al apagarse.

En Linux, ademas,  systemctl stop  hace lo mismo (manda SIGTERM y el servidor guarda).

SI TE MUDAS DE MAQUINA, o queres tu propio respaldo, copiate la carpeta  data\
entera: ahi esta TODO tu servidor, y es lo unico que no se puede volver a generar.

ACTUALIZAR A UNA VERSION NUEVA: rearma la distribucion sobre ESTA MISMA carpeta.
El armador reemplaza el ejecutable, el mundo y el cliente, y CONSERVA tu  data\  y
tu  Server.ini . Aun asi, antes de actualizar hace una copia de  data\ : es tu
servidor, y una copia de mas nunca sobro.

VARIOS SERVIDORES EN LA MISMA MAQUINA: una copia COMPLETA de esta carpeta por cada
uno. No es desperdicio: es lo que hace que cada servidor tenga SUS mapas, SU data del
juego y SU configuracion -uno vanilla y otro modificado, sin tocarse-. Lo unico que
hay que cuidar es no repetir puertos: cambia StartPort y separalos de a 10.
Cada servidor come ~190 MB de RAM con el mundo entero cargado.
  Windows:  coverao.ps1  (ponelo en la carpeta que CONTIENE a los servidores)
              .\coverao.ps1 nueva rojo      copia una instalacion y le da puertos
              .\coverao.ps1 estado          todos, con RAM y puertos
              .\coverao.ps1 detener rojo    apagado limpio
  Linux:    coverao@.service  ->  systemctl enable --now coverao@rojo coverao@azul

MAS DOCUMENTACION, en esta misma carpeta:
  HOSTING.md       Server.ini, ser GM, puertos, backups, varios servidores, HTTPS
  CREDITOS.md      de donde viene el material heredado y bajo que terminos

LICENCIA: AGPL-3.0 (el texto completo esta en  LICENSE ). Es software libre: podes
usarlo, estudiarlo, modificarlo y repartirlo. A cambio, si lo modificas y lo repartis
-o si lo HOSTEAS para que otros jueguen- tenes que ofrecerles el codigo fuente de la
version que estas corriendo. Eso ultimo es la "clausula de red" de la AGPL, y aplica
aunque nunca les des un archivo: alcanza con que se conecten a jugar.

  Poné tu enlace en  Server.ini  ->  [INIT] CodigoFuente=https://...
  El juego se lo muestra al jugador en  Menu -> Licencia . Con eso cumplis.

Si no completas esa clave, el jugador ve el aviso de licencia sin enlace. Los graficos,
sonidos y mapas heredados de Argentum Online no son parte de esta licencia: ver CREDITOS.
"@
Set-Content -Path (Join-Path $OutDir "LEEME.txt") -Value $leeme -Encoding UTF8

$size = "{0:N0}" -f ((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "`nLISTO -> $OutDir  ($size MB)" -ForegroundColor Green
# El comando de prueba tiene que ser el de la plataforma que se ACABA de armar. Decia siempre
# `.\CoverAO.Server.exe`, incluso armando la de Linux, donde ese archivo no existe.
if ($Rid -like "linux*") { Write-Host "Probalo (en la maquina Linux):  cd '$OutDir'; bash servidor.sh" -ForegroundColor Green }
else                     { Write-Host "Probalo:  cd `"$OutDir`"; .\CoverAO.Server.exe" -ForegroundColor Green }

# --- El comprimido para publicar (-Comprimir) --------------------------------------------------------
# ESTE PASO VIVE ACA A PROPOSITO. El 2026-08-04 se publico un release cuyo "CoverAO-1.0.0-win-x64.zip" no
# era un zip: se habia armado a mano con `tar -a -c -f archivo.zip`, y la -a de GNU tar elige el compresor
# por la extension, pero NO conoce .zip -- asi que escribio un tar plano, sin comprimir, con nombre de zip.
# Salio 412 MB (mas que la carpeta) y el Explorador de Windows no lo abre. Paso desapercibido porque la
# verificacion lo extrajo con `tar`, que lee tar contento aunque se llame .zip. Mientras el empaquetado sea
# un comando escrito a mano al lado del script, se vuelve a errar; aca queda con el formato correcto fijo.
#
# El formato NO es indistinto por plataforma:
#   win-x64   -> .zip     porque Windows lo abre sin instalar nada.
#   linux-x64 -> .tar.gz  porque el zip no guarda el permiso de ejecucion. Igual servidor.sh se lo pone.
if ($Comprimir) {
  Write-Host "`n== comprimiendo ==" -ForegroundColor Cyan
  $padre  = Split-Path $OutDir -Parent
  $nombre = Split-Path $OutDir -Leaf
  $sw = Get-Date
  if ($Rid -like "linux*") {
    $arch = Join-Path $padre "CoverAO-$Version-$Rid.tar.gz"
    if (Test-Path $arch) { Remove-Item $arch -Force }
    # EL tar DE WINDOWS, POR RUTA COMPLETA. `tar` a secas agarra el primero del PATH, y si la maquina tiene
    # las herramientas de Git for Windows adelante gana el GNU tar, que lee `C:\...` como si fuera un host
    # REMOTO: "tar (child): Cannot connect to C: resolve failed" y el .tar.gz no se arma. Paso de verdad al
    # publicar la 1.0.1 desde una consola con el PATH de Git puesto -- o sea que armar el paquete dependia de
    # DESDE DONDE se lo llamara. El bsdtar que trae Windows no tiene esa sintaxis y toma la ruta tal cual.
    $tarExe = Join-Path $env:SystemRoot ("System32" + [char]92 + "tar.exe")
    if (-not (Test-Path $tarExe)) { $tarExe = "tar" }
    & $tarExe -czf $arch -C $padre $nombre
    if ($LASTEXITCODE -ne 0) { throw "tar fallo con $LASTEXITCODE (con $tarExe)" }
  } else {
    $arch = Join-Path $padre "CoverAO-$Version-$Rid.zip"
    if (Test-Path $arch) { Remove-Item $arch -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # A mano y no con ZipFile::CreateFromDirectory: bajo Windows PowerShell 5.1 ese metodo escribe los
    # nombres de entrada con '\', y el formato zip exige '/'. Con backslash, un `unzip` de Linux no crea
    # carpetas: crea archivos cuyo nombre incluye la barra.
    $fs = [IO.File]::Open($arch, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($fs, 'Create')
    foreach ($f in Get-ChildItem $OutDir -Recurse -File) {
      $rel = $f.FullName.Substring($padre.Length + 1).Replace('\', '/')
      [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $zip.Dispose(); $fs.Dispose()
  }
  Write-Host ("COMPRIMIDO -> {0}  ({1:N0} MB, {2:N1} min)" -f `
    $arch, ((Get-Item $arch).Length / 1MB), ((Get-Date) - $sw).TotalMinutes) -ForegroundColor Green
}
