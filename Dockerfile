# CoverAO — imagen del servidor.
#
#   docker build -t coverao .
#   docker run -d --name coverao -p 7666-7669:7666-7669 -v coverao-data:/app/data coverao
#
# Y a jugar en  http://localhost:7669/ .
#
# La musica esta EN EL REPO desde el 2026-08-03 (antes se excluia por tamano y una imagen construida desde
# un clon limpio salia muda). No hace falta ningun paso previo: se copia con el resto de los assets.
#
# CONFIGURACION: no hace falta editar el Server.ini adentro de la imagen. Cualquier clave se pisa con una
# variable de entorno `COVERAO_<SECCION>_<CLAVE>`; ver docker-compose.yml y `hosting/`.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# El .csproj primero: mientras no cambien las dependencias, el restore queda cacheado entre builds.
COPY server/src/CoverAO.Server/CoverAO.Server.csproj server/src/CoverAO.Server/
COPY server/src/CoverAO.Protocol/CoverAO.Protocol.csproj server/src/CoverAO.Protocol/
RUN dotnet restore server/src/CoverAO.Server/CoverAO.Server.csproj -r linux-x64

COPY server/ server/
# SIN `PublishSingleFile`, a diferencia de publish.ps1, y no es un olvido. El ejecutable de un solo archivo
# EXTRAE sus librerias nativas a un cache en `$HOME` la primera vez que arranca; en un contenedor sin home
# escribible eso muere con "Failure processing application bundle / DOTNET_BUNDLE_EXTRACT_BASE_DIR is not
# set", y el arreglo tipico —darle un home o apuntar la variable a /tmp— es pagar una extraccion en cada
# arranque para nada. El archivo unico existe para la distribucion portable, donde el valor es tener UN .exe
# que copiar; en una imagen la unidad ya es el sistema de archivos entero.
RUN dotnet publish server/src/CoverAO.Server/CoverAO.Server.csproj \
      -c Release -r linux-x64 --self-contained true -p:DebugType=none \
      --no-restore -o /publicado

# ---------- runtime ----------
# `runtime-deps` y no `runtime`: el publish es self-contained, o sea que trae su propio .NET adentro y solo
# necesita las librerias nativas del sistema. La imagen queda bastante mas chica.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app

COPY --from=build /publicado/ ./
# La data del juego y el cliente web NO se compilan: van tal cual desde el repo, con los mismos nombres de
# carpeta que busca el ejecutable (FindDataDir y StaticFileServer los resuelven por AppContext.BaseDirectory).
COPY gamedata/ ./gamedata/
COPY client-web/index.html ./webclient/
COPY client-web/dist/ ./webclient/dist/
COPY client-web/assets/ ./webclient/assets/
COPY LICENSE docs/HOSTING.md CREDITOS.md ./

# Sin privilegios: el proceso no necesita root para nada, y un servidor de juego expuesto a internet es
# exactamente donde no se quiere estar corriendo como root.
#
# EL ORDEN DE ESTAS DOS INSTRUCCIONES IMPORTA, y al reves no anda. Docker DESCARTA lo que un `RUN` toque
# dentro de un directorio ya declarado con `VOLUME`, asi que con el VOLUME primero el `chown` de /app/data se
# perdia; al arrancar, el volumen nuevo nacia de root y el proceso sin privilegios no podia crear
# /app/data/Charfile. El contenedor moria con `UnauthorizedAccessException` DESPUES de cargar el mundo
# entero, o sea con un log que parecia normal hasta la ultima linea.
#
# Lo encontre construyendo la imagen y corriendola. Leyendo el Dockerfile no se ve: las dos instrucciones
# estan bien escritas, lo que estaba mal era el orden.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin coverao \
 && mkdir -p /app/data \
 && chown -R coverao:coverao /app

# Las cuentas y los personajes viven aca. Declarado como volumen para que sobrevivan a `docker rm`: sin esto,
# actualizar la imagen borra el servidor entero. Al crearse, el volumen hereda el contenido y los permisos que
# /app/data tiene en la imagen — de ahi que el chown tenga que venir antes.
VOLUME /app/data

USER coverao

# 7666 juego (protocolo AO clasico) · 7667 WebSocket (cliente web) · 7669 cliente web.
# El 7668 (panel de administracion) NO se expone a proposito: escucha en loopback y para llegar desde afuera
# lo correcto es un tunel SSH, no publicarlo.
EXPOSE 7666 7667 7669

# Sin `sh -c`: asi el ejecutable es PID 1 y recibe el SIGTERM de `docker stop` derecho, que es lo que dispara
# el guardado de todos los conectados antes de salir. Con un shell en el medio, la senal se pierde y el
# apagado deja de ser limpio.
ENTRYPOINT ["/app/CoverAO.Server"]
