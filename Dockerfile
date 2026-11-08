# CoverAO - imagen del servidor, desde el PAQUETE OFICIAL de Linux (firmado), no desde el código.
#
#   1. El paquete, en dist/:
#        powershell -ExecutionPolicy Bypass -File hosting\subir-version.ps1 -SinSubir     (el autor, que tiene la clave)
#      o el CoverAO-servidor-linux-x64.tar.gz del release, copiado a dist/.
#   2. docker compose up -d      (o:  docker build -t coverao .)
#
# Agrega http://localhost:7669/ en la app de escritorio.
#
# POR QUE DEL PAQUETE: la app de escritorio sólo entra a un server cuyo cliente web trae la firma del autor
# (webclient/firma.json), y esa firma no se puede generar sin su clave. La imagen se compilaba desde el repo, que no la
# trae, y la app no entraba a ningún server en Docker (auditoría externa del 23/10). Armada desde el paquete, el server
# y el cliente son los mismos que en el .zip, firmados, y la clave nunca entra a la imagen.
#
# El paquete de Linux trae además su propio Dockerfile (hosting/Dockerfile.paquete), para el que hostea sin el repo. Es
# el mismo que éste salvo el primer paso: si cambia uno, cambia el otro.
#
# CONFIGURACION: la imagen no lleva Server.ini. Cualquier clave se pisa con una variable de entorno
# `COVERAO_<SECCION>_<CLAVE>`; ver docker-compose.yml y `hosting/`.

# ---------- el paquete ----------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS paquete
# ADD de un .tar.gz local lo descomprime. El .dockerignore manda al demonio sólo este archivo.
ADD dist/CoverAO-servidor-linux-x64.tar.gz /paquete/
# SIN LA FIRMA NO HAY IMAGEN: un cliente sin firma.json deja afuera a la app de escritorio, y que eso se descubra al
# armar es mejor que al primer jugador que no puede entrar. Después se saca lo que no es para un contenedor: los
# arranques de Windows y systemd, el Server.ini (la configuración va por entorno) y las novedades de data/, que caerían
# en el volumen y quedarían viejas al actualizar la imagen (el server usa entonces las del juego).
RUN set -e; mv /paquete/CoverAO-linux-x64 /app; cd /app; \
    if [ ! -f webclient/firma.json ] || [ ! -f archivos.json ]; then \
      echo "El paquete de dist/ no trae la firma del autor (webclient/firma.json): la app de escritorio no entraria. Usa el paquete oficial."; \
      exit 1; \
    fi; \
    rm -rf Dockerfile .dockerignore docker-compose.yml docker-compose.postgres.yml Server.ini Servidor.bat Actualizar.bat \
           coverao.ps1 servidor.sh coverao.service coverao@.service data; \
    chmod 755 CoverAO.Server

# ---------- runtime ----------
# `runtime-deps` y no `runtime`: el paquete es self-contained, trae su propio .NET y sólo necesita las librerías del sistema.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=paquete /app/ ./

# Sin privilegios, y del proceso es SOLO /app/data (auditoría del 13/10): el resto queda de root, así una falla del server no
# puede reescribir el cliente que bajan los jugadores. data/ en 700: las cuentas y el token del panel no los lee otra cuenta.
#
# EL ORDEN DE ESTAS DOS INSTRUCCIONES IMPORTA, y al revés no anda. Docker DESCARTA lo que un `RUN` toque dentro de un
# directorio ya declarado con `VOLUME`, así que con el VOLUME primero el `chown` de /app/data se perdía: el volumen nuevo
# nacía de root y el proceso sin privilegios moría con `UnauthorizedAccessException` DESPUÉS de cargar el mundo entero.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin coverao \
 && mkdir -p /app/data \
 && chown coverao:coverao /app/data \
 && chmod 700 /app/data

# Las cuentas y los personajes viven acá. Un volumen, para que sobrevivan a `docker rm` y a una imagen nueva: al crearse
# hereda el contenido y los permisos que /app/data tiene en la imagen, de ahí que el chown venga antes.
VOLUME /app/data

USER coverao

ENV DOTNET_RUNNING_IN_CONTAINER=true
# EL EJECUTABLE DEL PAQUETE ES DE UN SOLO ARCHIVO (publish.ps1, PublishSingleFile): al arrancar extrae sus librerías
# nativas, y sin un home escribible muere con "DOTNET_BUNDLE_EXTRACT_BASE_DIR is not set". Van a /tmp: se vuelven a
# extraer cuando se recrea el contenedor, que es un instante.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/coverao-net

# 7669 sirve el cliente de escritorio y el juego en /ws.
# El 7668 (panel de administración) NO se expone: adentro del contenedor escucha en todas sus interfaces y se publica
# SOLO en el 127.0.0.1 del host (docker-compose.yml); desde afuera, un túnel SSH. El token queda en /app/data/admin-token.txt.
EXPOSE 7669

# Sin `sh -c`: así el ejecutable es PID 1 y recibe el SIGTERM de `docker stop` derecho, que es lo que dispara el guardado de
# todos los conectados antes de salir. Con un shell en el medio, la señal se pierde y el apagado deja de ser limpio.
ENTRYPOINT ["/app/CoverAO.Server"]
