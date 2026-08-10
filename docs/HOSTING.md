# Hostear CoverAO

## Armar el paquete

```powershell
pwsh hosting/publish.ps1                 # -> dist/CoverAO-win-x64
pwsh hosting/publish.ps1 -Rid linux-x64  # -> dist/CoverAO-linux-x64
```

Queda una carpeta autocontenida: no hace falta .NET instalado en la máquina destino. Al volver a correrlo
sobre la misma carpeta se conservan `data/` y `Server.ini`.

Con `-Comprimir` deja además el archivo para publicar, con el formato que corresponde a cada plataforma:
`.zip` para Windows, y `.tar.gz` para Linux **porque el zip no guarda el permiso de ejecución**. No hay que
armarlo a mano.

```powershell
pwsh hosting/publish.ps1 -Comprimir -Version 1.0.0
pwsh hosting/publish.ps1 -Rid linux-x64 -Comprimir -Version 1.0.0
```

```
CoverAO-win-x64/
  CoverAO.Server.exe    servidor + runtime .NET embebido
  Servidor.bat          arrancar en Windows
  servidor.sh           arrancar en Linux
  Server.ini            configuración
  gamedata/             mundo, objetos, hechizos, NPCs
  webclient/            el cliente web
  wwwroot/admin.html    panel de administración
  data/                 cuentas, personajes, backups y logs (se crea al arrancar)
```

En Linux: `bash servidor.sh`. El `bash` adelante importa — el comprimido no conserva el bit de ejecución y el
script se lo pone al binario.

## Puertos

Se configura uno solo, `StartPort`, y de ahí salen los otros tres:

| puerto | servicio |
|---|---|
| `StartPort` | juego (protocolo AO clásico), cliente de Windows |
| `StartPort + 1` | WebSocket, cliente web |
| `StartPort + 2` | panel de administración — **sólo loopback** |
| `StartPort + 3` | cliente web (la página) |

El servidor los imprime al arrancar y quedan en el log del día.

**En el router se abren el del juego y el del WebSocket**, más el del cliente web si se sirve desde ahí. **El
panel de administración no se abre nunca**: desde ahí se puede apagar el servidor, expulsar a todos y bajar un
backup. Para llegar de afuera, un túnel SSH: `ssh -L 7668:localhost:7668 usuario@servidor`.

En **Windows sin permisos de administrador** los puertos HTTP quedan sólo en `localhost`. El servidor lo avisa
al arrancar y da las dos salidas: abrirlo como Administrador, o reservar la URL una vez con
`netsh http add urlacl url=http://+:7669/ user=%USERNAME%`. En Linux no hace falta nada.

### Qué está verificado desde OTRA máquina

Medido el 2026-08-09 con el servidor Linux corriendo en una máquina y el navegador en otra:

| lo que hace un jugador | resultado |
|---|---|
| abrir `http://<ip-del-server>:StartPort+3/` | la página carga entera (330 KB, con el canvas) |
| el cliente abre su WebSocket | conecta (el puerto se lo publica el server en `/coverao-config.json`) |
| poner cuenta y contraseña | el server contesta con la lista de personajes |
| elegir personaje | entra al mundo, con los personajes de alrededor dibujados |
| cliente clásico contra `StartPort` | login completo |

Lo único que NO se puede probar sin salir a internet es el **NAT del router**: que el reenvío de puertos
esté bien hecho. Todo lo demás del camino "un amigo abre la URL y juega" está ejercido.

> **Ojo con las sondas.** Un `connect` TCP a los puertos HTTP en **Windows** da OK aunque el servidor no los
> tenga registrados: quien acepta la conexión es HTTP.SYS (el kernel), que recién después mira el `Host:` y
> contesta **400**. Para esos puertos hay que probar con un `GET`, no con un `connect`.

> **El `?auto` / `?char=` del cliente no funciona desde otra máquina, y es a propósito**: son credenciales de
> prueba cableadas, así que el atajo está limitado a `localhost`. Desde afuera se entra por el formulario,
> como cualquier jugador.

## Server.ini

Va al lado del ejecutable. Es opcional, pero sin él **nadie es GM** y los comandos de administración quedan
inservibles. Los cambios se aplican al reiniciar. Plantilla comentada:
[`hosting/Server.ini.ejemplo`](../hosting/Server.ini.ejemplo).

| Clave (`[INIT]`) | Qué hace |
|---|---|
| `StartPort` | puerto base |
| `MaxUsers` | tope de jugadores. 0 = sin tope |
| `AllowMultiLogins` | 0 = una sola conexión por IP |
| `LimiteConexionesPorIp` | tope de sockets por IP. 0 = 64 |
| `IdleLimit` | minutos sin actividad antes de desconectar. 0 = nunca |
| `MaxPaquetesPorSegundo`, `RafagaDePaquetes` | techo de paquetes de una conexión. Al que lo sostiene se lo desconecta. 0 = sin límite |
| `MsEntreMensajes`, `RafagaDeMensajes` | anti-spam de chat, susurro, clan y `/GM`. En ms entre mensajes |
| `MsEntreConsultas`, `RafagaDeConsultas` | lo mismo para `/online`, `/est` y los comandos que sólo te contestan a vos |
| `PuedeCrearPersonajes` | 0 = no se aceptan personajes nuevos |
| `ServerSoloGMs` | 1 = modo mantenimiento |
| `Exp`, `Oro` | porcentajes (200 = doble) |
| `MinutosDelDia` | duración del ciclo día/noche |
| `MinutosDeClima`, `PorcentajeLluvia` | ciclo de lluvia |
| `MinutosGuardarUsuarios` | autosave. 0 = sólo al salir |
| `MinutosBackup` | copia del mundo. 0 = desactivado |
| `BackupsAConservar` | cuántas copias guardar. 0 = todas |
| `LogDias` | días de log a conservar. 0 = sólo consola |
| `CodigoFuente` | URL del código de este servidor |
| `PuertoWebSocketPublico` | sólo si el hosting remapea puertos: el que se le anuncia al cliente |

**Ser GM**: en `[INIT]` poner `Dioses=1` y agregar la sección:

```ini
[Dioses]
Dios1=NombreDeTuPersonaje
```

Se resuelve en cada login, así que no hay que tocar el personaje. Rangos, de más a menos autoridad: `Admines`,
`Dioses`, `SemiDioses`, `Consejeros`, `RolesMasters`.

**Sin editar archivos**: cualquier clave se pisa con una variable de entorno `COVERAO_<SECCION>_<CLAVE>`, útil
en contenedores y en systemd.

```bash
COVERAO_INIT_MAXUSERS=200 COVERAO_INIT_DIOSES=1 COVERAO_DIOSES_DIOS1=TuPersonaje ./CoverAO.Server
```

## Backups y apagado

Cada 30 minutos se copia el mundo entero a `data/backups/` sin frenar a nadie, conservando las últimas 48.
Antes de cada copia se guarda a los conectados. También hay un botón en el panel de administración.

**Copiando la carpeta `data/` se reconstruye el servidor completo en otra máquina.** Es lo único que no se
puede volver a generar.

### Volver atrás con un backup

Un backup que nunca se restauró no es un backup. Esto es todo lo que hay que hacer, y conviene probarlo **una
vez** antes de necesitarlo de verdad:

```sh
# 1. Parar el servidor. Sin esto se restaura sobre archivos que el servidor está escribiendo.
#    (cerrar la ventana / Ctrl+C / systemctl stop coverao / docker stop)

# 2. Guardar lo que hay AHORA, por si el backup elegido no era el que uno creía.
mv data/Charfile data/Charfile.antes && mv data/Cuentas data/Cuentas.antes && mv data/Misc data/Misc.antes

# 3. Poner en su lugar las tres carpetas del backup elegido. Se llaman coverao-AAAAMMDD-HHMMSS,
#    así que `ls data/backups/` ya las lista de la más vieja a la más nueva.
cp -r data/backups/coverao-20260808-030000/Charfile data/
cp -r data/backups/coverao-20260808-030000/Cuentas  data/
cp -r data/backups/coverao-20260808-030000/Misc     data/

# 4. Arrancar. Los personajes y las cuentas quedan como estaban en ese momento.
```

Un backup es una carpeta con **exactamente** las tres que importan:

| | |
|---|---|
| `Charfile/` | los personajes (`.chr`) |
| `Cuentas/` | las cuentas (`.cnt`), con sus contraseñas |
| `Misc/` | clanes, correo, records del mundo |

No hace falta tocar nada más: los mapas, la data del juego y el cliente web se regeneran desde la
instalación. Y **no** se restaura sólo un personaje copiando su `.chr` suelto: la cuenta que lo lista vive en
`Cuentas/`, así que si el personaje no existía en ese momento el `.chr` queda huérfano y no aparece al entrar.

Con `COVERAO_DB=sqlite` el almacén es una base y el backup es **un archivo** `coverao-AAAAMMDD-HHMMSS.db`:
ahí se restaura copiándolo sobre `data/coverao.db` en vez de las tres carpetas.

Al apagarse bien, el servidor guarda a todos los que están jugando. Sirve cualquiera de estas:

- cerrar la ventana, o `Ctrl+C`
- el botón *Apagar* del panel
- `systemctl stop` en Linux
- crear un archivo vacío llamado `detener` al lado del ejecutable

La última es la que hay que usar con un **servidor de fondo en Windows**, que no tiene ventana a la que
mandarle la señal. `taskkill` y *Finalizar tarea* lo matan sin guardar: cada jugador conectado pierde lo hecho
desde el último autosave.

## Varios servidores en la misma máquina

Una instalación completa por servidor, cada una en su carpeta. Así cada uno tiene sus mapas, su `gamedata`, su
configuración y hasta su versión. Lo único que hay que cuidar es no repetir puertos: separar `StartPort` de a
10. Cada servidor usa unos 190 MB de RAM con el mundo cargado.

**Windows** — [`hosting/coverao.ps1`](../hosting/coverao.ps1) va en la carpeta que contiene a los servidores:

```powershell
.\coverao.ps1 nueva rojo        # copia una instalación y le asigna puertos libres
.\coverao.ps1 iniciar rojo
.\coverao.ps1 estado            # todos, con RAM y puertos
.\coverao.ps1 detener rojo      # apagado limpio
.\coverao.ps1 arranque rojo     # OPCIONAL: que arranque al prender la máquina
```

> **El servidor lo abrís vos cuando querés.** Ni `arranque` (Windows) ni `systemctl enable` (Linux) están en
> el camino recomendado: el juego se levanta a mano y se apaga a mano. Los dos existen para quien tenga un
> servidor dedicado y los quiera, pero no se registran solos ni hacen falta para jugar con amigos.

**Linux** — [`hosting/coverao@.service`](../hosting/coverao@.service) es una unit plantilla:

```bash
sudo useradd --system --home /srv/coverao --shell /usr/sbin/nologin coverao
# una instalación por servidor en /srv/coverao/<nombre>, cada una con su StartPort
sudo cp hosting/coverao@.service /etc/systemd/system/ && sudo systemctl daemon-reload
sudo systemctl start coverao@rojo coverao@azul     # arranca AHORA, y sólo cuando vos lo pidas
systemctl status 'coverao@*'
journalctl -u coverao@rojo -f
```

Para un solo servidor está [`hosting/coverao.service`](../hosting/coverao.service), sin el `@nombre`.

**Docker** — un contenedor por servidor:

```bash
docker compose up -d
docker compose logs -f
```

El volumen `coverao-data:/app/data` es lo único que no se regenera. El `stop_grace_period: 60s` está puesto
para que el guardado del apagado llegue a terminar.

## Con dominio y HTTPS

Terminar TLS en un reverse-proxy delante del cliente web y del WebSocket. El cliente pasa solo a `wss://`
cuando la página se sirve por HTTPS. Con Caddy:

```
juego.tudominio.com {
    reverse_proxy /ws* localhost:7667
    reverse_proxy localhost:7669
}
```

## Licencia

CoverAO es AGPL-3.0 y su cláusula de red obliga a ofrecerle el código de la versión que corrés a quien se
conecte a jugar. Se cumple poniendo la URL en `Server.ini`:

```ini
CodigoFuente=https://github.com/tu-usuario/tu-fork
```

El juego se la muestra al jugador en Menú → Licencia. Si no se completa, el jugador ve el aviso sin enlace.
