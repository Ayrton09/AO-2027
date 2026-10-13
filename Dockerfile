# CoverAO — imagen del servidor.
#
#   docker build -t coverao .
#   docker run -d --name coverao -p 7666:7666 -p 7667:7667 -p 7669:7669 -p 127.0.0.1:7668:7668 -v coverao-data:/app/data coverao
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
# El cliente con el mismo criterio que publish.ps1: TODAS las paginas (el login abre estado.html, y con
# SoloEscritorio=1 la pagina es descargar.html), sin los tests de dist/ ni las notas .md de assets/, que el
# .dockerignore deja afuera.
COPY client-web/*.html ./webclient/
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
#
# DEL PROCESO ES SOLO /app/data (auditoria del 13/10): el resto queda de root, asi una falla del server no
# puede reescribir el cliente que bajan los jugadores. Y data/ en 700: las cuentas y el token del panel no
# los lee otra cuenta.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin coverao \
 && mkdir -p /app/data \
 && chown coverao:coverao /app/data \
 && chmod 700 /app/data

# Las cuentas y los personajes viven aca. Declarado como volumen para que sobrevivan a `docker rm`: sin esto,
# actualizar la imagen borra el servidor entero. Al crearse, el volumen hereda el contenido y los permisos que
# /app/data tiene en la imagen — de ahi que el chown tenga que venir antes.
VOLUME /app/data

USER coverao

# 7666 juego (protocolo AO clasico) · 7667 WebSocket (cliente web) · 7669 cliente web.
# El 7668 (panel de administracion) NO se expone: adentro del contenedor escucha en todas sus interfaces
# (AdminServer.cs, con esta variable) y se publica SOLO en el 127.0.0.1 del host (docker-compose.yml);
# desde afuera, un tunel SSH. El token queda en /app/data/admin-token.txt.
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 7666 7667 7669

# Sin `sh -c`: asi el ejecutable es PID 1 y recibe el SIGTERM de `docker stop` derecho, que es lo que dispara
# el guardado de todos los conectados antes de salir. Con un shell en el medio, la senal se pierde y el
# apagado deja de ser limpio.
ENTRYPOINT ["/app/CoverAO.Server"]
