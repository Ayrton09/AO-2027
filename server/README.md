# CoverAO Server (.NET 10)

Habla el protocolo AO clásico byte a byte, de modo que un cliente VB6 sin modificar se conecta.

## Estructura

```
server/
  src/CoverAO.Protocol/           codec de protocolo (compartido cliente/servidor)
    ByteQueueWriter / Reader      little-endian, strings con prefijo de longitud
    Crypto                        XOR simétrico de un byte
    ClientPacketId / ServerPacketId
  src/CoverAO.Server/
    Game/   Player, World, NpcSystem, GameLoop (40 ms), EventBus, combate, magia, oficios
    Net/    TcpGameServer, WebSocketGameServer, ClientSession, PacketDispatcher,
            ServerMessages, StaticFileServer, AdminServer
    Data/   GameData (carga los JSON de gamedata/), Persistence, ServerIni, Backups
  tests/CoverAO.Protocol.Tests/
```

## Decisiones de arquitectura

- **`World` en lugar del `UserList()` global**, que el original referenciaba unas mil veces. El acceso
  es por métodos, no por un array compartido.
- **Transporte separado del dominio.** `ClientSession` (socket, buffer, XOR) es distinto de `Player`
  (estado de juego); el original fundía las colas de red dentro del tipo del usuario.
- **Game loop explícito de 40 ms** con cola de acciones: las mutaciones de estado corren en un solo
  hilo, así que no hay carreras.
- **EventBus**: las reglas publican eventos y los emisores traducen a paquetes, en lugar de escribir
  al socket desde adentro de la lógica.

## Correr

```bash
dotnet run --project src/CoverAO.Server -c Release
```

Con `--fixture` arranca el escenario de pruebas en lugar del mundo real.

## Persistencia

Archivos `.chr` y `.cnt` en el formato INI del original (`data/Charfile/` y `data/Cuentas/`), con el
mismo esquema, de modo que los personajes de un servidor original se leen tal cual. Las contraseñas se
guardan con PBKDF2-HMAC-SHA256 (210 000 iteraciones); los hashes viejos se migran en el primer login.
El personaje se guarda al desconectar, en cada autosave y antes de cada backup.

`COVERAO_DB=sqlite` cambia el respaldo a SQLite detrás de la misma interfaz `IStore`.
