# CoverAO: servidor moderno + cliente web

Port del servidor de Argentum Online **CoverAO-2021** (Visual Basic 6) a **C#/.NET 10**, hablando el
**protocolo AO clásico byte a byte**, con un **cliente web** (TypeScript/canvas) que se conecta por
WebSocket. El objetivo no es un juego nuevo: es una réplica fiel de esa variante.

- **Autosuficiente**: corre sólo con lo que hay acá. Los datos del juego ya vienen decodificados a JSON en
  [`gamedata/`](gamedata/), y los gráficos, sonidos y música están en
  [`client-web/assets/`](client-web/assets/).
- **Cliente web**: se juega desde el navegador, sin instalar nada. El puerto de juego habla el protocolo AO
  clásico byte a byte (es contra lo que se compara este port), pero el **cliente clásico de Windows no llega
  a entrar**: manda la contraseña ofuscada con `SEncriptar` (un corrimiento distinto en cada envío) y este
  servidor no la deshace. Medido; ver [`docs/HOSTING.md`](docs/HOSTING.md).

## Requisitos

- **.NET 10 SDK**
- Un navegador, para el cliente web.

## Correr el servidor

```sh
dotnet build CoverAO.slnx -c Release
dotnet server/src/CoverAO.Server/bin/Release/net10.0/CoverAO.Server.dll --web-root client-web
```

El `--web-root` no es opcional si querés jugar: sin él el servidor levanta igual, pero el puerto del cliente
web no sirve nada y el navegador se encuentra con la nada. Apunta a la carpeta del cliente.

Arranca como **mundo real**: los 863 mapas con su fauna auténtica (5404 NPCs) y sin cuenta demo. Un jugador
crea cuenta y personaje con el flujo normal. Con `--fixture` levanta en su lugar el escenario de pruebas.

Al arrancar imprime sus cuatro puertos, dónde está instalado y qué configuración leyó:

```
Puertos: 7666 juego (cliente clasico)  |  7667 WebSocket (cliente web)  |  7668 panel de admin (solo local)  |  7669 cliente web
```

Todos salen de `StartPort` en el `Server.ini`. El **panel de administración escucha sólo en loopback** a
propósito: desde afuera se llega por un túnel SSH, nunca abriéndolo en el router.

## Jugar

Con el servidor andando, el cliente web se sirve solo en `StartPort+3`:

```
http://localhost:7669/
```

Se le pasa esa dirección a quien quiera entrar (cambiando `localhost` por la IP o el dominio del servidor) y
juega desde el navegador. El cliente descubre el puerto del WebSocket preguntándoselo al servidor, así que
funciona con cualquier configuración de puertos.

Renderiza el mundo completo (terreno, capas, personajes con equipo, ítems en el suelo, clima, día/noche y
FX), y permite moverse, atacar, lanzar hechizos, comerciar y banquear con NPCs, fabricar, equipar y lootear:
todo decidido por el servidor. Se reconecta solo si el servidor reinicia.

## Hostear

Todo lo que hace falta está en [`docs/HOSTING.md`](docs/HOSTING.md): armar el paquete, los
puertos, el `Server.ini`, ser GM, backups, apagar sin perder progreso, varios servidores en una máquina,
Docker y HTTPS con dominio propio.

Lo esencial:

- **Configuración**: [`hosting/Server.ini.ejemplo`](hosting/Server.ini.ejemplo) es una plantilla comentada.
  Cualquier clave se puede pisar con una variable de entorno `COVERAO_<SECCION>_<CLAVE>`, para contenedores.
- **Armar la distribución**: `pwsh hosting/publish.ps1` deja una carpeta autocontenida (no hace falta .NET en
  la máquina destino). `-Rid linux-x64` para Linux, `-Comprimir` para que además arme el archivo a publicar.
- **Varios servidores**: una instalación completa por servidor, cada una con sus mapas y su configuración.
  En Windows, [`hosting/coverao.ps1`](hosting/coverao.ps1) los administra; en Linux,
  [`hosting/coverao@.service`](hosting/coverao@.service) es una unit plantilla de systemd.
- **Docker**: `docker compose up -d`, desde la carpeta del paquete oficial de Linux (o con su `.tar.gz` en `dist/`): la
  imagen sale del paquete firmado para que la app de escritorio pueda entrar.
- **Apagar sin perder progreso**: al apagarse bien, el servidor guarda a todos los conectados. Sirve cerrar
  la ventana, `Ctrl+C`, el botón del panel, `systemctl stop`, o crear un archivo vacío llamado `detener` al
  lado del ejecutable, que es lo único que funciona con un servidor de fondo en Windows, donde no hay
  ventana a la que mandarle la señal.
- **Backups**: copia del mundo entero cada 30 minutos, conservando las últimas 48, sin frenar a los que
  juegan. Copiando la carpeta `data/` se reconstruye el servidor completo en otra máquina.

## Estructura

| ruta | qué es |
|---|---|
| [`server/`](server/) | el servidor .NET 10 y el codec de protocolo |
| [`client-web/`](client-web/) | el cliente web y los assets del juego |
| [`gamedata/`](gamedata/) | los datos del juego decodificados a JSON |
| [`hosting/`](hosting/) | scripts de hosting y el armador de la distribución |
| [`docs/`](docs/) | la guía de hosting |

## Qué no tiene, a propósito

Es una réplica fiel de esta variante, no un superset. Lo único que queda afuera es lo que **el original no
tiene cableado**: el **comercio entre usuarios** es código muerto en CoverAO-2021 (el módulo existe, pero el
handler no tiene rama para otro usuario, los entry-points no tienen llamadores y el cliente VB6 no trae ni el
formulario ni los paquetes para participar), así que portarlo sería inventarlo.

## Reportar errores y proponer cambios

Todo pasa por GitHub: los errores y las sugerencias van a
[Issues](https://github.com/Ayrton09/CoverAO-privado/issues) y los cambios de código, como
[Pull Requests](https://github.com/Ayrton09/CoverAO-privado/pulls). Al reportar, contá qué hiciste, qué esperabas y
qué pasó; si es del cliente web, con qué navegador. El mismo enlace se lo muestra el juego al jugador en
Menú → Licencia cuando el `Server.ini` trae `CodigoFuente`.

## Licencia

**[AGPL-3.0](LICENSE)**: software libre con copyleft. No fue una elección de gusto: el material del que esto
deriva llegó bajo la Affero GPL v1 *"or any later version"*, declarada en el encabezado de 48 de los 79
archivos del servidor VB6. Ese "or any later version" permite subir a la 3; no permite bajar a una permisiva.

Si **hosteás** un servidor, la AGPL obliga a ofrecerle el código de la versión que corrés a quien se conecte a
jugar, aunque nunca se le dé un archivo. Esa es la cláusula de red, y un servidor público de juego es
exactamente el caso que contempla. Se cumple poniendo la URL en `Server.ini` → `[INIT] CodigoFuente=...`: el
juego se la muestra al jugador en Menú → Licencia.

Los **assets** (gráficos, sonidos, música, mapas) vienen de Argentum Online, no llevan aviso de licencia y no
están cubiertos por esto. El detalle, con los encabezados textuales y las fuentes, en
[`CREDITOS.md`](CREDITOS.md).
