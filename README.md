<p align="center">
  <img src="client-web/assets/login/logo.webp" width="520" alt="AO 2027">
</p>

<h1 align="center">AO 2027</h1>

<p align="center">
  <a href="https://github.com/Ayrton09/AO-2027/releases/latest"><img src="https://img.shields.io/github/v/release/Ayrton09/AO-2027?display_name=tag&amp;color=007ec6" alt="Última release"></a>
  <a href="https://github.com/Ayrton09/AO-2027/releases"><img src="https://img.shields.io/github/downloads/Ayrton09/AO-2027/total?color=4c1" alt="Descargas totales"></a>
</p>

<p align="center">
  Argentum Online de escritorio. Explorá el mundo, armá tu clan y elegí tu facción.
</p>

<p align="center">
  <a href="https://github.com/Ayrton09/AO-2027/releases/latest"><strong>Descargar el juego</strong></a>
  · <a href="docs/HOSTING.md">Guía de hosting</a>
  · <a href="https://github.com/Ayrton09/AO-2027/issues">Reportar un problema</a>
</p>

## Jugar

| Sistema | Descarga | Cómo abrirlo |
| --- | --- | --- |
| **Windows** | [Instalador](https://github.com/Ayrton09/AO-2027/releases/latest/download/CoverAO-Setup.exe) | Ejecutá el instalador y abrí la app. |
| **Linux** | [AppImage](https://github.com/Ayrton09/AO-2027/releases/latest/download/CoverAO-x86_64.AppImage) | Dale permiso de ejecución y abrí el archivo. |

Abrí **Servidores** en el menú, agregá la dirección que te pasó quien hostea y entrá. Si el servidor corre en tu propia PC con la configuración de fábrica, usá `http://localhost:7669/`. Al abrir la dirección desde el navegador vas a ver la página de descarga.

La app busca **actualizaciones firmadas** en este repositorio cada vez que la abrís.

## Qué podés hacer

- **Combate y personajes.** Elegí tu raza y clase, subí de nivel y repartí puntos de habilidad. Peleá contra criaturas y otros jugadores con armas, hechizos y familiares.
- **Facciones y clanes.** Armá un clan con tus amigos o subí de rango en la Armada Imperial, la Milicia Republicana o la Legión del Caos.
- **Exploración y viajes.** Recorré ciudades y mazmorras a pie, en barco o con montura. Hay 92 rutas de pasajes entre 11 ciudades, con destinos según la facción de cada lugar.
- **Oficios y economía.** Miná, talá o pescá. Fabricá objetos con herrería, sastrería o alquimia. Podés comerciar con otros jugadores, guardar oro y objetos en el banco o vender en el Centro de Subastas.
- **Bots opcionales.** Quien hostea puede poblar el mundo con personajes que entrenan, se enlistan, hacen oficios, viajan y comercian.
- **Cliente de escritorio.** Jugá en Windows o Linux con gráficos HD, iluminación, clima, música por mapa y opciones de FPS.

## Hostear un servidor

Descargá el paquete autocontenido para [Windows](https://github.com/Ayrton09/AO-2027/releases/latest/download/CoverAO-servidor-win-x64.zip) o [Linux](https://github.com/Ayrton09/AO-2027/releases/latest/download/CoverAO-servidor-linux-x64.tar.gz), descomprimilo y arrancá con `Servidor.bat` o `bash servidor.sh`, respectivamente. El servidor incluido trae su propio runtime .NET.

El servidor tiene un **panel de administración** para gestionar el staff y ver los jugadores conectados. Los GM también pueden abrir el **modo espectador** desde la selección de personajes y seguir a un jugador o bot sin entrar con su propio personaje.

La [guía de hosting](docs/HOSTING.md) cubre puertos, HTTPS, Docker, SQLite, PostgreSQL, copias de seguridad y actualizaciones. Al actualizar, podés conservar tu `Server.ini` y el mundo en `data/`. Para ofrecer a tus jugadores el código exacto de tu servidor, configurá `[INIT] CodigoFuente` con el enlace a la versión que estás ejecutando.

Cliente y servidor deben tener la misma versión. El juego la comprueba automáticamente al entrar.

## Trabajar con el código

AO 2027 continúa el servidor **CoverAO-2021**, una variante de Argentum Online escrita en Visual Basic 6. El servidor actual usa C# y .NET 10. El cliente de escritorio usa Electron, TypeScript, Canvas y WebSocket.

Con el SDK de .NET 10 y Node.js instalados, compilá primero el cliente para generar la versión de ingreso desde `VERSION`.

```sh
npm --prefix client-web ci
npm --prefix client-web run build
dotnet build CoverAO.slnx -c Release
dotnet server/src/CoverAO.Server/bin/Release/net10.0/CoverAO.Server.dll --web-root client-web
```

Para trabajar con la app, usá `npm ci` y `npm start` en [`desktop/`](desktop/). Los paquetes oficiales incluyen el cliente firmado. Consultá la [guía de hosting](docs/HOSTING.md) para firmar el cliente de tu propia compilación.

| Carpeta | Contenido |
| --- | --- |
| [`server/`](server/) | Mundo, persistencia, bots y protocolo de juego |
| [`desktop/`](desktop/) | Lanzador, lista de servidores y actualizador firmado |
| [`client-web/`](client-web/) | Interfaz del juego y recursos que entrega el servidor |
| [`gamedata/`](gamedata/) | Mapas, criaturas, objetos y reglas |
| [`hosting/`](hosting/) | Empaquetado, configuración y publicación de releases |

Los reportes y las propuestas van en [Issues](https://github.com/Ayrton09/AO-2027/issues). Para cambios de código usá [Pull Requests](https://github.com/Ayrton09/AO-2027/pulls). Si reportás un error, incluí la versión, qué hiciste y qué pasó.

## Licencia y créditos

El código se publica bajo [AGPL-3.0](LICENSE). Los gráficos, sonidos, mapas y demás recursos heredados se documentan por separado en [Créditos](CREDITOS.md), junto con los autores y los proyectos de origen.
