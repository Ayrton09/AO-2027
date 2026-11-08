# Hostear CoverAO

## Armar el paquete

```powershell
pwsh hosting/publish.ps1                 # -> dist/CoverAO-win-x64
pwsh hosting/publish.ps1 -Rid linux-x64  # -> dist/CoverAO-linux-x64
```

Queda una carpeta autocontenida: no hace falta .NET instalado en la máquina destino.

Arma un paquete, **no actualiza una instalación**. Al volver a correrlo sobre la misma carpeta se conservan `data/` y
el `Server.ini`, y se borra todo lo demás sin copia, también `gamedata/` (con los mapas que hayas tocado) y
`actualizacion/`. En `dist/CoverAO-<plataforma>` o con `-Comprimir`, además, el `Server.ini` se repone del ejemplo y el
anterior queda en la carpeta de arriba, como `Server.ini.anterior-...`. Una instalación se actualiza con `actualizar`
(ver "Actualizar a una version nueva").

Con `-Comprimir` deja además el archivo para publicar, con el formato que corresponde a cada plataforma:
`.zip` para Windows y `.tar.gz` para Linux. No hay que armarlo a mano.

> **Ojo con el permiso de ejecución.** Antes acá decía que el `.tar.gz` se usa *porque el zip no guarda el
> permiso de ejecución*, y eso es media verdad: **armado desde Windows, el `.tar.gz` tampoco lo guarda**.
> Medido sobre el paquete publicado, recién descomprimido en Ubuntu, tanto el binario como `servidor.sh`
> salen `-rw-rw-rw-`. Lo que hace que igual arranque es el `chmod +x` que `servidor.sh` se hace a sí mismo,
> y por eso el `bash` de adelante no es un detalle de estilo.

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

En Linux: `bash servidor.sh`. El `bash` adelante importa: el comprimido no conserva el bit de ejecución y el
script se lo pone al binario.

## Puertos

Se configura uno solo, `StartPort`, y de ahí salen los otros tres:

| puerto | servicio |
|---|---|
| `StartPort` | protocolo AO clásico, cerrado en el mundo real |
| `StartPort + 1` | WebSocket suelto: **compatibilidad**, ya no hace falta abrirlo |
| `StartPort + 2` | panel de administración: **sólo loopback** |
| `StartPort + 3` | página de descarga para el navegador y juego de escritorio en `/ws` |

El servidor los imprime al arrancar y quedan en el log del día.

La app usa una sola dirección y se conecta por `/ws`. Para internet publicá esa dirección mediante HTTPS
en el proxy. En el backend, la página y el WebSocket comparten `StartPort + 3`. El navegador abre la página
de descarga. El puerto de administración y el WebSocket de compatibilidad quedan fuera de la ruta pública.

> Antes hacían falta **dos** (el de la página y el del WebSocket) y ésa era la trampa más cara del hosteo:
> abriendo uno solo, el amigo veía la página, veía el login, y el socket se moría contra un puerto cerrado.
> Del lado del servidor no quedaba ni un renglón, porque esa conexión no llegaba nunca.

**El panel de administración no se abre nunca**: desde ahí se puede apagar el servidor, expulsar a todos y
bajar un backup. Para llegar de afuera, un túnel SSH: `ssh -L 7668:localhost:7668 usuario@servidor`. El token va
a la consola; si la salida está redirigida (systemd, Docker, `nohup`), queda en `data/admin-token.txt`.

En **Windows sin permisos de administrador** los puertos HTTP quedan sólo en `localhost`. El servidor lo avisa
al arrancar y da las dos salidas: abrirlo como Administrador, o reservar la URL una sola vez, en una consola de
**cmd** como Administrador:

```
netsh http add urlacl url=http://+:7669/ user=%USERNAME%
```

En **PowerShell** `%USERNAME%` no se expande: usá `$env:USERNAME` o escribí tu usuario. El servidor imprime al
arrancar la línea exacta, con tu usuario ya resuelto. En Linux no hace falta nada.

### Prueba de apertura desde otra red

1. Abrí el dominio HTTPS en un navegador y comprobá que aparezca la descarga del cliente.
2. Agregá la misma dirección en la app de escritorio. Creá una cuenta, elegí un personaje y entrá al mundo.
3. Revisá que el servidor registre las IP de los jugadores cuando haya un proxy. Verificá el apagado limpio
   y una restauración de backup en una instalación separada.

### El cliente clásico de Windows NO puede entrar

En `--fixture`, `StartPort` habla el **protocolo AO clásico byte a byte** para las pruebas de paridad con el
servidor VB6. El cliente clásico conecta ahí, pero **no pasa del login**, y
la causa es la contraseña: el cliente VB6 la manda ofuscada con `SEncriptar`
(`Cliente/Codigo/Protocol.bas:4454`), que le suma a cada carácter **un número al azar entre 10 y 98** y pega
dos caracteres diciendo cuál fue. El servidor VB6 lo deshace con `SDesencriptar` antes de validar
(`Servidor/Codigo/Cuentas.bas:439`); **este servidor no lo deshace en ningún lado**, así que compara contra la
cadena ofuscada.

Medido contra el servidor, con el control adentro:

| lo que se manda | resultado |
|---|---|
| contraseña en claro (lo que manda el cliente web) | entra |
| contraseña con `SEncriptar` (lo que manda el clásico) | *Cuenta o contraseña inválida* |
| cuenta creada con `SEncriptar` y reentrada **con los mismos bytes** | entra, o sea que la cuenta existe |
| esa misma cuenta, reentrada con `SEncriptar` **de nuevo** | *Cuenta o contraseña inválida* |

Las dos últimas filas son las que cierran el caso: como el corrimiento cambia en cada envío, **ni siquiera un
servidor que sólo viera clientes clásicos funcionaría**: la cuenta que el cliente crea no la puede volver a
abrir. Por eso el `StartPort` no hace falta abrirlo en el router.

Que funcione es posible y tiene un solo camino: portar `SDesencriptar` al servidor **y** que el cliente web
mande la contraseña con `SEncriptar`. Van juntos: desencriptar sin lo otro rompe todas las contraseñas ya
creadas.

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
| `LimiteConexionesPorIp` | tope de sockets por IP. 0 = 10, el del original. Es distinto de `MaxPersonajesPorIp`: esto cuenta conexiones (incluidas las que todavía no entraron), aquello personajes jugando |
| `IdleLimit` | minutos sin actividad antes de desconectar. 0 = nunca |
| `MaxPaquetesPorSegundo`, `RafagaDePaquetes` | techo de paquetes de una conexión. Al que lo sostiene se lo desconecta. 0 = sin límite |
| `TrampasParaEchar`, `TrampasParaBanear` | anticheat con memoria: cada trampa detectada (velocidad, ataque fuera de rango, skills inventados, comprar más de una pila…) suma un punto en la CUENTA, que se conserva entre sesiones y se ve en el panel. Desde la primera cifra se lo expulsa en cada trampa nueva; en la segunda se banea cuenta y personaje. Los GMs conectados ven cada una. 0 = ese escalón apagado, y con los dos en 0 (así viene) ninguna trampa echa ni banea: todas quedan en `data/logs/trampas-*.log`. Para sancionar, por ejemplo 3 y 6 |
| `MaxPersonajesPorIp` | cuántos personajes pueden estar jugando a la vez desde una misma IP (el original: tope fijo de 10 conexiones y nada más). Contra la manada de alts. 10 por defecto, como el tope de conexiones del original: alcanza para una LAN. 0 = sin tope |
| `CuentasNuevasPorIp` | cuentas nuevas que una IP puede crear cada diez minutos. 0 = sin cupo |
| `ClaveMinima` | largo mínimo de una contraseña nueva, al crear la cuenta y al cambiar la clave. NO aplica al código de recuperación, que en el original es un PIN de hasta cuatro números. 0 = sin mínimo |
| `EconomiaUmbralOro` | los movimientos de oro de este monto o más (depósito, extracción, compra, venta, tirar, agarrar, casino) y todos los correos con adjunto, transferencias y bolsas van a `data/logs/economia-AAAA-MM-DD.log`, para reconstruir de dónde salió el oro de alguien. 0 = se anota todo |
| `MsEntreMensajes`, `RafagaDeMensajes` | anti-spam de chat, susurro, clan y `/GM`. En ms entre mensajes |
| `MsEntreConsultas`, `RafagaDeConsultas` | lo mismo para `/online`, `/est` y los comandos que sólo te contestan a vos |
| `PuedeCrearPersonajes` | 0 = no se aceptan personajes nuevos |
| `ServerSoloGMs` | 1 = modo mantenimiento |
| `Exp`, `Oro` | cuántas veces la experiencia y el oro que da cada criatura: `1` = normal, `5` = cinco veces (el server original venía con `200`). El evento del GM (`/EXP`, `/ORO`, el panel) multiplica encima |
| `EventoAlRematar` | 1 = el **evento de experiencia** del GM (`/EXP`, el panel) y el medio extra del **donador** multiplican también lo que se cobra **al matar** a la criatura: la parte de su experiencia que no se cobró golpe a golpe. En el original van sólo en lo que se cobra golpe a golpe, y como el hechizo resta la vida antes de cobrar, **el que mata de un hechizo casi no cobra el evento**: con `/EXP` en x50, un Mago que mata de un Misil cobra x1 lo de ese hechizo y un Guerrero que mata con la espada, x50. **0 = como el original. Viene en 0** |
| `OroPorMonton` | cuánto oro entra en cada **montón que se tira al piso** (de 1 a 32767). Tirar más se reparte en varios montones por las baldosas libres de alrededor, y el oro que ya está en el piso se apila hasta ese tope. **Viene en 10000**, como el original |
| `AlcanceDelHechizo` | al tocar **Lanzar**, el cliente web marca **hasta dónde llega el hechizo** mientras apuntás: ocho baldosas a los costados y seis arriba y abajo, lo mismo que acepta el servidor. **Viene en 1**; con 0 no se marca |
| `DestinoDeLosPortales` | cada **portal dice a dónde lleva**: un cartel arriba con el nombre del mapa y el texto al clickearlo ("Teleport a Nix"). En el original se llaman "Teleport" a secas. **Viene en 1**; con 0, como el original |
| `LluviaBajoTecho` | si **llueve también bajo techo y en las cuevas**. **Viene en 0**: la lluvia se corta en las cuevas y se desvanece al entrar bajo techo; con 1 llueve en todos lados, como el original |
| `VidaMinimaDeJefe` | desde cuánta **vida máxima** una criatura hostil que da experiencia es un **jefe**: el cliente muestra su vida en una **barra grande arriba de la pantalla** mientras está a la vista. **Viene en 50000** (los dragones grandes, los gólems, los yetis, el Guardián y otros 16); con 0 no hay barra y el servidor no manda el paquete, que el original no tiene |
| `ComercioEnRinkel`, `ComercioEnLindos`, `ComercioEnCiudadesViejas`, `ComercioEnElResto` | **a quién le vende el mercader** según la zona, con los nombres de las facciones separados por coma (Renegado, Ciudadano, Republicano, Caos, Armada, Milicia); vacía, ahí no le vende a nadie. Rinkel incluye Nueva Esperanza y el Dungeon de Newbies; las ciudades viejas son Ullathorpe, Nix, Banderbill y Arghâl; el resto, el campo y los mapas sueltos. **Vienen como el original**: Rinkel a todos, Lindos a ciudadanos y republicanos con sus ejércitos, y las otras dos a nadie. Los bots usan la misma regla |
| `MinutosDelDia` | duración del ciclo día/noche |
| `MinutosDeClima`, `PorcentajeLluvia` | ciclo de lluvia |
| `OroAlSubirNivel` | oro de regalo por nivel. **0 = como el original**, que no regala nada. El `Server.ini` de ejemplo lo trae en 0 |
| `OroQueSeCaeAlMorir` | al morir donde se cae el inventario (mapa inseguro, fuera de zona segura), el que lleva **esta cantidad de oro o menos** encima la pierde **toda** al piso; con más, no pierde nada. Es la regla del AO clásico. **0 = no se cae nunca**, como el original de 2021. **El ejemplo lo trae en 100000** |
| `VidaInicial` | la vida de nivel 1. `original` (por defecto, el AO de siempre): 15 más de 1 a la Constitución dividida 3, al azar. `fija`: 20 para todos. `constitucion`: 15 más la Constitución dividida 3, sin azar. `clase`: 10 más la vida que gana la clase al subir de nivel. Un valor que no se entiende queda en `original`. **El ejemplo la trae en `original`** |
| `CuraAlSubirNivel` | 1 = subir de nivel deja vida, maná y energía al máximo. 0 = como el original, que sube los máximos y llena sólo la vida. **El ejemplo lo trae en 1** |
| `QuitarEquipoNovato` | 1 = al pasar el nivel 15 desaparecen los objetos `(Newbies)`, y **sólo** ésos: lo que juntaste, compraste o fabricaste no se toca. 0 = como el original, que no los saca. **El ejemplo lo trae en 1** |
| `PersistirSuelo` | 1 = lo que queda tirado en el suelo sobrevive a un reinicio del servidor. 0 = como el original, que tiene el suelo sólo en RAM y lo repuebla del mapa en cada arranque. No toca el decorado del mundo ni las puertas: sólo lo que dejó o se le cayó a la gente. **El ejemplo lo trae en 1**, porque un servidor de beta se reinicia para actualizarlo y cada reinicio le borra a los jugadores lo que se les cayó al morir |
| `MinutosDeFogata` | Cuántos minutos dura prendida una fogata antes de apagarse sola. 0 = como el original, donde **no se apaga nunca**: la rutina que las juntaba para borrarlas quedó declarada y sin escribir (`TrashCollector` aparece dos veces en todo el servidor del original, su declaración y el alta). **El ejemplo lo trae en 5** |
| `Centinela` | 1 = corre el centinela anti-macro: un NPC se le planta al que está trabajando y le pide que escriba `/CENTINELA <clave>`; el que no contesta en dos minutos se come quince de cárcel y lo desconecta. **0 = como el original**, que trae el módulo entero pero con sus dos llamadas comentadas, así que nunca aparece. Un Dios también lo prende y lo apaga en vivo con `/CENTINELAACTIVADO` |
| `FamiliarBusca` | 1 = el familiar sale a pelear solo contra el hostil más cercano a su dueño, y el bicho que recibe el golpe se queda peleando con él. **0 = como el original**, donde la mascota te sigue y sólo pelea contra lo que ya tenga de objetivo. Cambia el balance: el domador deja de tener que abrir cada pelea. **El ejemplo lo trae en 0** |
| `GolpeParalizanteVueltas` | Cuánto queda quieta una **criatura** a la que un golpe a mano limpia paralizó, en **vueltas del reloj del juego** (40 ms cada una): 295 = 11,4 s. **No son segundos**. El original escribe 30, pero en SU reloj de criaturas, que corre cada 380 ms; el default de acá es esa misma duración |
| `GolpeParalizanteVueltasPvP` | Lo mismo contra un **jugador**: 150 = 6 s. Al jugador lo deja quieto **menos** que a una criatura (11,4 s), aunque su número sea más grande: son dos relojes distintos. **Default = el del original** |
| `GolpeParalizanteDado` | Cada cuánto **sale** el golpe que paraliza. La chance es `Lucha sin armas × 0,2 + nivel × 0,66` contra un dado de 1 a este número, así que **bajarlo lo hace más frecuente**. **Default 500 = el del original**. La fórmula no se expone a propósito: es la misma que la de desarmar, y moverla cambiaría las dos cosas de una |
| `GolpeParalizanteDadoGladiadorBardo` | El dado de las dos clases que pelean sin arma. Con 300 les sale ~1,7 veces más seguido que al resto. La clase cambia la **probabilidad**, nunca la duración. **Default = el del original** |
| `PasesDeRinkel` | 1 = los dos pases que vende el pirata del Puerto de Rinkel se pueden usar. **0 = como el install**, donde no se pueden usar en ningún lado: su ficha dice que salen de la ciudad (mapa 20) y el único transportador de Rinkel está en el puerto (mapa 99), así que el pirata te los cobra y después no te los acepta. Es un bug de la data del install: pasa igual en el servidor original. **El ejemplo lo trae en 0** |
| `HipocamposEnMarAbierto` | 1 = con la **red de pesca** en un mapa de mar abierto se saca **Hipocampo** en vez de los cuatro peces de siempre. 0 = como el install, donde el hipocampo no se puede conseguir por ningun camino. **Viene PRENDIDA**; la caña no cambia. |
| `NavegacionParaCruzar` | Los puntos de **Navegación** que pide un **pasaje que cruza el agua a otra tierra**. Las tierras salen de los mapas: el continente de Ullathorpe, Nix, Banderbill, Arghâl y Rinkel; el de Illiandor y Suramei; y las islas de Tiama, Orac, Lindos y Nueva Esperanza. Entre puertos de la misma tierra el pasaje no pide nada. Al que no llega, el pasaje no lo lleva y le avisa cuántos puntos necesita, con el mismo aviso del barco; al GM no lo frena. **0 = como el original**, donde ningún pasaje pide Navegación. **Viene en 80**; el tope es 100 |
| `DropGlobal` | **Una de cada cuántas** criaturas muertas suelta, además de su botín propio, **un** ítem de la tabla de `DropGlobal.dat` (26 accesorios de los mejores del juego). `10000` (el default) = una cada diez mil, que con la data del juego es **alrededor de un ítem por personaje en todo el camino hasta nivel 40**: a nivel 30 son ~1.900 muertes, a 40 ~12.900 y a 50 ~81.000. `0` lo apaga y vuelve al install, donde esa tabla existe en la data y ningún código del servidor original la abre nunca. Los porcentajes del archivo se usan como **pesos** (qué sale cuando sale), no como probabilidades: sumados dan 482 %. **Viene PRENDIDA en 10000** |
| `MetamorfosisDelDruida` | Cuánto dura la transformación de los once hechizos de metamorfosis (Murciélago, Lobo, Jabalí, Tigre, Oso Pardo, Tortuga, Araña, Demonio, Árbol, Gólem y Dragón): `-1` **permanente**, `0` apagada, o los **segundos** que dura (hasta 3600). El druida toma el cuerpo de la criatura y sus bonos al golpe y a la defensa, todos de la data del install (del +6/+20 del murciélago al +50/+20 del dragón), y **las criaturas más débiles que su forma dejan de atacarlo** (el `asusta` del hechizo, 1..10, contra el `Nivel` de la criatura, 1..100). Morir o desconectarse la corta (tampoco sobrevive a un reinicio del servidor), lanzar otra la pisa, y no se puede lanzar montado ni en barca. `0` vuelve al install, donde esos hechizos se venden hasta por 2.200.000 de oro y lanzarlos no hace nada. **Viene PRENDIDA y PERMANENTE**, que es como se comporta en el AO clásico; el original no dejó escrita ninguna duración (su `[INTERVALOS]` tiene 34 entradas y ninguna es de esto) |
| `AtacarAliadoVuelveCriminal` | 1 = **atacar a un ciudadano o a un soldado** (sin guerra de clanes ni facción enemiga de por medio) te vuelve **criminal** y te echa del ejército, con el aviso *"Has atacado a un aliado. Ahora eres CRIMINAL."*. Vale igual para el golpe y para la magia. **0 = atacar no cambia el estado de nadie**, que es lo que dice el fuente del original: ahí renegado sólo se vuelve el que deja una facción. **Viene PRENDIDA** por decisión del autor, que la recuerda del juego |
| `GuardiasAtacan` | 1 = los **guardias de las ciudades atacan** a las facciones enemigas de su bando. El bando es el de la ficha del guardia: **imperial** (122 guardias: Ullathorpe, Nix, Banderbill, Arghûl, Avanzada Imperial…), **republicano** (91: Lindos, Suramei, Illiandor, Avanzada republicana…) y **del Caos** (33: Orac, Ruinas Caóticas, Fortaleza Kerthon…). Pelean con los números de la data (nivel 50, 40.000 de vida, golpes de 250 a 280 y sus hechizos), así que un enemigo que se acerca a una ciudad ajena la pasa muy mal. Al GM, al muerto, al invisible y al oculto no los tocan, como ninguna criatura. No pelean los que la data dejó sin golpe ni hechizo que lastime: los cuatro de Rinkel, los Cazadores y las dos Torres del Distrito Real. **0 = como el original**, donde ningún guardia pelea ni se mueve: su IA quedó apagada con un comentario de *"temporal"*. **Viene PRENDIDA** por decisión del autor |
| `GuardiasPersiguen` | 1 = el guardia que ve a un enemigo sale de su puesto a buscarlo y **no lo suelta mientras siga en su ciudad**, aunque se le vaya de la vista; cuando se va (o muere, o se hace invisible) vuelve a su puesto. 0 = quietos: pegan al que les pasa al lado y los que saben magia le tiran al que ven. **Viene en 1** |
| `GuardiasAtacanNewbies` | 1 = los guardias atacan también a los newbies (hasta nivel 15) del otro bando. Todo personaje nace ciudadano (o republicano si nace en Illiandor), así que con 1 el recién llegado que entra a una ciudad del otro bando muere de un golpe. **Viene en 0** |
| `GuardiasImperialesAtacanA`, `GuardiasRepublicanosAtacanA`, `GuardiasDelCaosAtacanA` | A qué facciones ataca cada bando, separadas por coma: `Renegado`, `Ciudadano`, `Republicano`, `Caos`, `Armada` y `Milicia` (mayúsculas, acentos y plural dan igual). **Vacía = ese bando no ataca a nadie.** Un nombre que no existe se ignora y el arranque lo avisa. Por defecto cada bando ataca a todos menos a los suyos: el imperial a todos menos Ciudadano y Armada, el republicano a todos menos Republicano y Milicia, el del Caos a todos menos Renegado y Caos (el renegado es el que se enlista con el Demonio) |
| `Bots` | Cuántos **personajes que juegan solos** puebla el servidor. `0` (el default) = ninguno. Un bot no es un NPC: entra por el **mismo login** que un jugador y lo que hace pasa por el mismo dispatcher, los mismos intervalos y el mismo anticheat, así que **no puede hacer nada que un jugador no pueda**. Cuesta lo que cuesta un jugador: ocupa lugar en `MaxUsers`, sale en el `/ONLINE` y en la página pública, y su actividad entra en los logs igual que la de cualquiera. **Juegan**: cazan criaturas que les queden chicas (a mano o con hechizos, si su clase sabe), huyen de las que les quedan grandes, levantan lo que dejan, toman pociones en medio de la pelea, comen, beben, meditan, reparten los puntos que da subir de nivel, le venden el botín a un mercader y le compran pociones, suben de nivel, y se desconectan cuando llevan un rato para volver más tarde. Cada uno tiene un temperamento que sale de su nombre (hay cazadores, tranquilos y paseantes, y algunos cazadores son agresivos) así que no se mueven todos igual. **Pelean con gente y se enlistan**: desde nivel 20, en mapas inseguros, le pegan a bots y jugadores del bando enemigo a los que atacar no los vuelve criminales (renegados y Caos; los soldados, además, al ejército enemigo), se defienden del que les pega, y con el nivel y las bajas que pide el enlistador entran a la Armada (los ciudadanos) o a la Milicia (los republicanos) y vuelven a cobrar la recompensa de cada rango. Los agresivos no se enlistan ahí: le pegan también a los civiles del otro bando, con eso se vuelven renegados (si `AtacarAliadoVuelveCriminal` está prendida) y a nivel 40 van a la Legión del Caos. Responden por susurro y en la charla si los nombran. Pueden decir su nivel, clase, actividad y mapa actual, y distinguen una pregunta de grupo de una invitación. Si mueren se quedan cadáver donde cayeron: revivir necesita un sacerdote a la vista, y sólo lo intentan si les queda cerca. Sus cuentas llevan el sufijo `_bot` y quedan anotadas, con su contraseña, en `data/bots.txt` (que no viaja en el paquete, pero sí en cada backup). Esa contraseña **no sirve desde afuera**: la cuenta de un bot queda marcada en su `.cnt` y sólo se abre desde adentro del servidor, así que leer el archivo no le da a nadie el personaje. El máximo son **96**, y si el nombre de alguno ya es de un jugador entra en su lugar uno de los 24 de reserva. Los que entran por primera vez (personaje nuevo) lo hacen de a 6 cada 5 minutos, para no amontonarse todos en el pueblo de inicio; los que ya tienen personaje entran todos al arrancar. Con `--fixture` no arrancan aunque estén pedidos, y el arranque lo dice: un bot caminando cerca le mete a un flow de paridad movimientos que el flow no pidió. **Viene APAGADO** |
| `MinutosGuardarUsuarios` | autosave. 0 = sólo al salir |
| `MinutosBackup` | copia del mundo. 0 = desactivado |
| `BackupsAConservar` | cuántas copias guardar. 0 = todas |
| `LogDias` | días de log a conservar. 0 = sólo consola |
| `CodigoFuente` | URL del código de este servidor |
| `PuertoWebSocketPublico` | sólo si el hosting remapea puertos: el que se le anuncia al cliente |
| `Version` | la versión de cliente que este servidor acepta. Los cinco paquetes de ingreso traen la del cliente y el servidor la compara **como texto**: si no coincide, cartel de *juego desactualizado* y se corta la conexión. **Vacía (el default) = sin control**, que es lo que hacía el servidor hasta ahora. El cliente web que viene con esta versión manda `1.0.0`. Encendela sólo si sabés qué cliente repartiste: la comparación es exacta, así que un valor que no sea el del cliente desplegado deja a **todos** afuera. Sirve para dos cosas: que no se conecte un cliente ajeno al puerto del juego, y forzar a que actualicen cuando publiques una versión nueva. **Se reparte VACÍA por decisión del autor (2026-09-08)**: con el cliente cacheado en el navegador de cada jugador, el cartel de *juego desactualizado* le sale a quien sólo necesita recargar |
| `PaginaPublica` | 1 = publica `estado.html` en el mismo puerto del cliente web: quiénes están conectados y la tabla de posiciones, con su enlace en la pantalla de ingreso. **No es una regla de la casa**: no cambia ninguna mecánica, es una ventana a lo que ya pasa: con pocos jugadores, lo que decide si alguien entra es saber si hay alguien adentro, y el `/ONLINE` del juego sólo se lee estando ya conectado. Sale nombre, nivel, clase y clan; **no** sale el mapa ni la posición de nadie, ni cuentas ni IPs, y los administradores no entran en la tabla. `0` la apaga entera y la dirección deja de existir (404). **Viene PRENDIDA** |
| `BuscarActualizaciones` | 1 (así viene) = al minuto de arrancar y cada 6 horas mira si el autor publicó una versión nueva del server y lo avisa en la consola. No baja ni instala nada: eso lo hace el host cuando quiere, con `actualizar` (ver "Actualizar a una version nueva"). 0 = no consulta |
| `SoloEscritorio` | Sólo cambia el comportamiento de `--fixture`. En el mundo real la app de escritorio siempre es obligatoria, incluso si el INI antiguo dice `0`. El navegador ve la página de descarga, el WebSocket exige la cabecera de la app y el TCP clásico queda cerrado. La cabecera distingue el acceso del navegador convencional y es reproducible por un programa personalizado |
| `DescargaEscritorio` | el enlace que muestra esa página para bajar la app. Vacío = la descarga oficial, `https://github.com/Ayrton09/CoverAO/releases/latest` |
| `ProxyDeConfianza` | las direcciones del reverse-proxy que está delante del cliente web, separadas por coma (`127.0.0.1,::1` si corre en la misma máquina). De una conexión que viene de ahí, la IP del jugador sale de la cabecera `X-Forwarded-For` que agrega el proxy; sin esto, detrás del proxy todos los jugadores tienen la misma IP y los topes por IP los frenan a todos juntos. Vacío = no se le cree a ninguna cabecera |
| `OrigenesPermitidos` | dominios EXTRA desde los que se acepta abrir el WebSocket del juego, separados por coma. Vacío (el default) = sólo el del propio servidor. El navegador declara en `Origin` qué página abrió el socket; sin ese chequeo, **cualquier página que un jugador visite puede abrirle sockets a tu servidor desde su máquina**: gastarle el cupo de conexiones y hacerle bloquear su propia IP con intentos de login fallidos. Un cliente que no es una página (una herramienta) no manda `Origin` y entra igual. Sólo hace falta si servís el cliente desde otro dominio que el servidor |

**Acompañantes**. Una vez que el bot te está siguiendo, podés decirle `espera` y `segui` por susurro o en la charla local.
Ayuda contra la criatura que te vio atacar, si puede llegar y las reglas del juego se lo permiten.
El clérigo mira tu ficha y puede curarte si tiene un hechizo y maná de sobra. El cazador que lleva arco y flechas
busca distancia sin alejarse del grupo. El guerrero puede interceptar una criatura que te persigue. El bot avisa
por susurro cuando le falta maná para curarte o decide retirarse.

**Metas al nivel 50**. Preguntales `tu meta` para ver qué persiguen y su avance. El servidor guarda el objetivo y los hitos
en `data/bots-metas.json`. El archivo forma parte de los datos de la partida. La meta de equipo termina al equipar
la pieza elegida. La de facción termina cuando el enlistador confirma el ascenso.

**Mercado de los oficios**. Los mineros ajustan la prioridad del mineral y los herreros la de sus recetas según
ventas y vencimientos recientes. El servidor conserva en memoria hasta ocho cierres por producto y toma en cuenta
los de las últimas 48 horas. Tras reiniciar, la muestra vuelve a llenarse a medida que cierran las subastas.

**Ser GM**: primero creá el personaje en tu cuenta. Después, en `[INIT]` poné `Dioses=1` y agregá la sección:

```ini
[Dioses]
Dios1=NombreDeTuPersonaje
```

Se resuelve en cada login, así que no hay que tocar el personaje. Rangos, de más a menos autoridad: `Admines`,
`Dioses`, `SemiDioses`, `Consejeros`, `RolesMasters`.

Los nombres configurados como staff quedan reservados: no se pueden crear desde el cliente, aunque todavía no exista el
personaje, ni borrar mientras tengan rango. Para borrar uno, primero quitale el rango desde el panel o el `Server.ini`.
Si configuraste un nombre antes de crear el personaje, sacalo temporalmente de la lista, crealo en tu cuenta y volvé a
darle el rango. Esto evita que otra cuenta tome un nombre libre y herede permisos de GM.

**Sin editar archivos**: cualquier clave se pisa con una variable de entorno `COVERAO_<SECCION>_<CLAVE>`, útil
en contenedores y en systemd.

```bash
COVERAO_INIT_MAXUSERS=200 COVERAO_INIT_DIOSES=1 COVERAO_DIOSES_DIOS1=TuPersonaje ./CoverAO.Server
```

## Actualizar a una version nueva

El código se mantiene en `Ayrton09/CoverAO-privado`; `Ayrton09/CoverAO` contiene las descargas públicas.
La app ya instalada sigue usando la misma dirección de actualización y la misma firma del autor.

Para publicar, con la versión de `VERSION` confirmada y enviada a `origin/main`, ejecutar desde la raíz:

```powershell
powershell -ExecutionPolicy Bypass -File hosting/subir-version.ps1 -SinSubir
powershell -ExecutionPolicy Bypass -File hosting/subir-version.ps1 -SoloSubir
```

El primer paso arma y firma los paquetes de Windows y Linux. El segundo comprueba que correspondan
al mismo commit, etiqueta esa fuente en el privado y publica los paquetes y avisos en el público.
La release se prepara como borrador y pasa a ser la última versión cuando ya están todos sus archivos.
La app del tester busca el aviso al abrir; las actualizaciones del servidor siguen requiriendo ejecutar
`actualizar`. Un commit de desarrollo por sí solo no publica una nueva versión.

**Con un comando, con el server apagado.** Hace el backup, verifica que el paquete sea del autor del juego,
reemplaza solo los archivos oficiales que no tocaste y deja tu `Server.ini` al día con tus valores:

```
Windows:   arrastrá el .zip nuevo encima de Actualizar.bat
           (o:  .\coverao.ps1 actualizar <nombre> -Paquete CoverAO-servidor-win-x64.zip)
Linux:     bash servidor.sh actualizar CoverAO-servidor-linux-x64.tar.gz
```

Sin el paquete (`Actualizar.bat` solo, o `bash servidor.sh actualizar`), lo baja de las releases del juego. Con
`BuscarActualizaciones=1` (así viene) el server avisa en la consola cuando hay una versión nueva; no instala
nada solo. `coverao.ps1 actualizar` además lo apaga si estaba prendido y lo vuelve a arrancar. Si el server corre
con otra cuenta (el `arranque` de Windows corre como `LOCAL SERVICE`), desde una consola sin Administrador no se
puede saber de qué carpeta es: `actualizar` no sigue y pide abrirla como Administrador (o `--forzar`).

### Pasar de 1.0.7 a 1.0.8 en Windows

Apagá el servidor y descomprimí el ZIP oficial 1.0.8 en una carpeta aparte. Desde PowerShell, en la carpeta
del servidor instalado, copiá el ejecutable nuevo con un nombre auxiliar y ejecutá la actualización desde ahí.
Así el proceso que actualiza conserva su propio ejecutable mientras reemplaza el del servidor.

```powershell
Copy-Item 'C:\Descargas\CoverAO-win-x64\CoverAO.Server.exe' '.\CoverAO.Actualizar.exe'
.\CoverAO.Actualizar.exe actualizar --desde 'C:\Descargas\CoverAO-servidor-win-x64.zip'
```

Adaptá las rutas a tu descarga. Cuando termine con código 0, podés borrar `CoverAO.Actualizar.exe` y arrancar
el servidor desde `CoverAO.Server.exe`.

### Qué hace el actualizador

Qué hace, en orden, y cualquier problema antes de reemplazar algo deja todo como estaba:

1. **Verifica el paquete:** que esté firmado por el autor (la misma firma que el cliente), que sea para tu
   sistema, que sea más nuevo que lo instalado y que cada archivo sea el firmado. Un paquete cambiado no se instala.
2. **Backup** completo de la base y de `data/`, el mismo de siempre. Si no sale, o le falta un archivo que otro
   programa tiene abierto, no actualiza (salvo con `--forzar`).
3. **Reemplaza solo lo oficial que no tocaste.** Cada versión trae la huella de sus archivos (`archivos.json`):
   si modificaste un mapa o un archivo de la data, queda el tuyo y el nuevo va a `actualizacion/sin-aplicar`.
   El cliente y el ejecutable se reemplazan siempre. Tus personajes, tu base, tus logs y tu `Server.ini` no
   figuran en la lista y no se tocan. Todo lo reemplazado queda en `actualizacion/anterior`. **Una instalación de
   antes de las actualizaciones** (sin `archivos.json`) no sabe qué tocaste: la primera vez se reemplaza todo, mapas
   incluidos, y lo tuyo queda en `actualizacion/anterior`. **Cada actualización borra lo que dejó la anterior** en
   `actualizacion/sin-aplicar` y `actualizacion/anterior`: lo que quieras guardar, copialo antes.
4. **Tu `Server.ini`, al día con tus valores:** se rearma sobre el ejemplo nuevo, con sus explicaciones y las
   claves nuevas, y con tus valores donde los tenías. Una clave que prendería algo que hoy no corre (una sección
   que no tenías) va comentada. Una que el server no reconoce queda al final, bajo un aviso. Antes de escribirlo,
   el server lo lee como si arrancara y lo compara con el tuyo, opción por opción: **si cambiara con qué corre
   tu server, no lo toca** y lo dice. El de antes queda en `actualizacion/anterior`.
5. **Anota la versión instalada**, lo último. Si algo falla después de reemplazar archivos, lo dice, con lo que
   quedó hecho y lo que falta, y volver a correr `actualizar` termina el trabajo.

El resumen queda en `actualizacion/ultima.txt`.

**El jugador no hace nada.** El cliente lo sirve este mismo servidor: el que estaba jugando, al reconectar,
recarga solo el cliente nuevo en la app de escritorio.

**Los personajes viejos abren en la version nueva.** Al arrancar, el servidor deja un sello en
`data/version.txt` y avisa por consola si el mundo venia de otra version. **Si el aviso dice que venis de una
version MAS NUEVA, pará y restaurá un backup**: un servidor viejo ignora en silencio lo que no entiende.

## Dónde se guardan los personajes

En una base de datos, con dos opciones y las mismas tablas en las dos:

- **SQLite: un archivo, `data/coverao.db`**, y nada que instalar. Es lo que usa una instalación nueva, y alcanza para
  un servidor solo.
- **Postgres**, para un servidor con páginas web que lean la misma base (ver más abajo).

**Cómo elegir.** Para jugar solo, en LAN o en un servidor sin página web: SQLite. Los jugadores se conectan al
servidor, no a la base, así que la cantidad de gente no cambia la elección: la base la usa un solo proceso, y un
guardado entero es un milisegundo. Postgres conviene cuando algo más tiene que leer la misma base mientras el juego
corre (una web con la tabla de posiciones, los clanes o los conectados), o cuando ya hay un Postgres administrado
(con sus copias y sus réplicas). Se puede empezar con SQLite y pasar a Postgres cuando haga falta: ver "Con Postgres".

Ahí van los personajes, las cuentas y lo que guarda el mundo: clanes, correo, suelo, llaves vendidas y el récord de
conectados.

Una instalación que ya tenía personajes en archivos (un `.chr` por personaje en `data/Charfile/` y un `.cnt` por
cuenta en `data/Cuentas/`) o la base vieja de `COVERAO_DB=sqlite` **los importa sola en el primer arranque**. Lo viejo
queda al lado, renombrado a `*.importado`, y en `data/` queda un informe `importacion-AAAAMMDD-HHMMSS.txt` con lo
importado y lo que no cerraba (un personaje sin cuenta, un amigo que no existe). Si la verificación no da (cada
personaje se relee de la base y se compara con el archivo), no se toca nada y el servidor no arranca: el informe dice
por qué.

Se elige en el `Server.ini`:

```ini
[BASEDEDATOS]
; sqlite (un archivo) o postgres (un servidor con paginas web). Sin la clave, sqlite.
Tipo=sqlite
; Con sqlite: el archivo de la base, en la carpeta data/.
Archivo=coverao.db
; Con postgres: donde esta la base.
Servidor=localhost
Puerto=5432
Base=coverao
Usuario=coverao
Clave=
```

| Clave | Qué hace |
|---|---|
| `Tipo` | `sqlite` o `postgres`. Vacía: `sqlite`, importando lo que hubiera en archivos |
| `Archivo` | el archivo de SQLite, relativo a `data/`. Por defecto `coverao.db` |
| `Servidor`, `Puerto` | dónde escucha Postgres. Por defecto `localhost` y `5432` |
| `Base`, `Usuario`, `Clave` | la base de Postgres y con qué usuario entra el servidor. Por defecto `coverao` y `coverao` |

Como el resto del `Server.ini`, se pisan desde el entorno: `COVERAO_BASEDEDATOS_TIPO`, `COVERAO_BASEDEDATOS_CLAVE`, etc.
La clave, mejor por ahí que escrita en el archivo.

### Con Postgres

El servidor pone y actualiza las tablas solo; lo que hay que darle es **una base vacía y un usuario que sea su dueño**,
en UTF8. Con `psql`, como administrador de Postgres:

```sql
CREATE USER coverao PASSWORD 'una-clave-larga';
CREATE DATABASE coverao OWNER coverao ENCODING 'UTF8' TEMPLATE template0;
```

Con varios servidores en el mismo Postgres, una base por servidor (`Base=coverao_rojo`, `Base=coverao_azul`).

**Pasar de SQLite a Postgres.** Con el servidor apagado, dos caminos:

- Poner `Tipo=postgres` y arrancar: si la base de Postgres está vacía y en `data/` está la `coverao.db`, la importa
  sola, la verifica y deja el archivo renombrado a `coverao.db.importado`, con el informe en `data/`.
- O pasarla antes, mirar el informe, y recién después cambiar el `Tipo`:

  ```sh
  CoverAO.Server pasar-a-postgres
  ```

  Copia entera la base de SQLite (la de `Archivo=`, u otra si se la nombra), tabla por tabla y con las fechas de alta
  y los personajes borrados, a la de Postgres del `Server.ini`, y deja la de SQLite como estaba.

Si Postgres no responde al arrancar, el servidor no arranca y dice por qué (sin mostrar la clave).

**Una página web sobre la base.** La web se conecta a la misma base con un usuario propio que **sólo puede leer** el
esquema `publico`: vistas con los personajes, la tabla de posiciones, los clanes y sus miembros, los conectados, la
versión y el récord del servidor, y el catálogo del juego (objetos, hechizos, clases, razas y mapas, con sus nombres).
No ve nada de las cuentas (ni contraseñas, ni IPs, ni baneos), ni el oro, la bóveda, la posición o el inventario, y el
staff del `Server.ini` y los personajes borrados quedan afuera. Se crea una vez, con el servidor apagado y los datos de
un administrador de Postgres en el entorno:

```sh
COVERAO_PG_ADMIN_USUARIO=postgres COVERAO_PG_ADMIN_CLAVE=... CoverAO.Server crear-usuario-web
```

Imprime la clave del usuario `coverao_web`, que no se vuelve a mostrar (correrlo otra vez le pone una nueva). Hace falta
Postgres 15 o más nuevo.

**Si la base se cae con el servidor andando** (Postgres que se reinicia, un disco lleno, el archivo tomado por un
antivirus), el juego sigue: lo que se guarda espera en orden, en memoria, y se reintenta solo cada unos segundos. La
consola lo avisa una vez cuando se cae y otra cuando vuelve, y el panel muestra el estado de la base y cuánto espera. El
que quiere entrar mientras tanto recibe, a los 10 segundos, un cartel de que no se puede entrar en ese momento. Si se
apaga el servidor con la base todavía caída, no se la espera: lo que no se pudo guardar queda enseguida en
`data/rescate-AAAAMMDD-HHMMSS.json` (cerrar la ventana da unos pocos segundos) y el próximo arranque lo aplica antes de
abrir. Si en vez de eso se restaura un backup o se importa, lo rescatado es de la base de antes y no se aplica encima:
queda al lado, como `*.sin-aplicar`.

Los autoguardados repetidos de una misma cosa se reemplazan mientras esperan. Si aun así se alcanzan
10.000 pedidos pendientes, el servidor frena las acciones nuevas y se apaga ordenadamente para guardar
el rescate. Termina las operaciones ya admitidas y conserva también los guardados del cierre; no descarta
una escritura después de haber movido oro u objetos. El proceso devuelve error para que el supervisor
del host detecte el cierre. Revisá la base y los mensajes de la consola antes de volver a iniciarlo.

Si una transferencia falla durante el cierre, su devolución también se guarda o queda en el rescate,
aunque el emisor ya se haya desconectado. Las devoluciones que esperan otro cambio se despachan después
de los guardados finales; reproducir el rescate no vuelve a sumar el mismo crédito.

### Importar y exportar

Con el servidor **apagado**, contra la base que diga el `Server.ini`:

```sh
# Pasar a la base una carpeta de datos (la de otra instalación, o la del servidor ORIGINAL: su Charfile y sus Cuentas)
# o un archivo de SQLite (otra coverao.db, o un backup).
CoverAO.Server importar /ruta/a/la/carpeta
# Pisar una base que ya tiene cosas. Con SQLite, la de antes queda al lado, como coverao.db.antes-de-importar-...;
# con Postgres, lo que había queda en un backup, data/backups/antes-de-importar-....zip.
CoverAO.Server importar /ruta/a/la/carpeta --reemplazar
# Sacar todo a .chr y .cnt, para mirar un personaje con un editor o para volver atrás.
CoverAO.Server exportar /ruta/a/una/carpeta/nueva
```

La importación es todo o nada: si algo no se puede verificar, la base queda como estaba.

La base es de **un proceso a la vez**: con el servidor andando, estos comandos no la tocan y lo dicen, y un segundo
servidor sobre la misma base tampoco arranca.

## Backups y apagado

Cada 30 minutos se copia el mundo entero a `data/backups/` sin frenar a nadie, conservando las últimas 48.
Antes de cada copia se guarda a los conectados. También hay un botón en el panel de administración.

Con la base, cada backup es **un archivo**, una copia consistente aunque el servidor esté escribiendo, más una
carpeta con el mismo nombre para los archivos sueltos de `data/`, como el mensaje del día. Con SQLite es
`coverao-AAAAMMDD-HHMMSS.db`, una base que se abre tal cual. Con Postgres es `coverao-AAAAMMDD-HHMMSS.zip`: cada tabla
en CSV y un manifiesto con la versión de las tablas, hecho por el servidor mismo, sin necesitar `pg_dump`. Para quien
sabe de Postgres, `pg_dump` sirve igual.

**Copiando la carpeta `data/` se reconstruye el servidor completo en otra máquina.** Es lo único que no se
puede volver a generar.

### No dejes `data/` adentro de una carpeta que sincroniza sola

Un programa que sincroniza o escanea la carpeta (OneDrive o Dropbox sincronizando `Documentos`, el antivirus
escaneando al vuelo, el agente de backup del hosting) **se agarra la base** (`data/coverao.db` y su diario, el
`-wal`) mientras el servidor escribe. El servidor lo aguanta: lo que se guarda espera y se reintenta, y la consola
avisa que la base no responde. Pero la copia que hace ese programa de una base abierta puede salir a medio escribir,
y ésa no sirve para volver atrás: para eso están los backups de `data/backups/`, que son copias consistentes. La forma
barata de no tener el problema es tener el servidor **fuera** de la carpeta sincronizada.

### Volver atrás con un backup

Un backup que nunca se restauró no es un backup. Conviene probarlo **una vez** antes de necesitarlo de verdad.

**Con la base** (SQLite o Postgres), con el servidor **apagado**:

```sh
# `ls data/backups/` los lista del más viejo al más nuevo. Lo que hay AHORA no se pierde: con SQLite la base de antes queda
# al lado (coverao.db.antes-de-importar-...); con Postgres, en un backup (data/backups/antes-de-restaurar-....zip).
CoverAO.Server restaurar data/backups/coverao-20260808-030000.db --reemplazar    # SQLite
CoverAO.Server restaurar data/backups/coverao-20260808-030000.zip --reemplazar   # Postgres
```

Un backup de SQLite (`.db`) se puede restaurar también en Postgres; uno de Postgres (`.zip`), sólo en Postgres. Un
backup de una versión anterior del servidor se restaura igual: las tablas se ponen al día al restaurar. Vuelve entero,
también con las fechas de alta y los personajes borrados.

No hace falta tocar nada más: los mapas, la data del juego y el cliente web se regeneran desde la
instalación.

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

**Windows**: [`hosting/coverao.ps1`](../hosting/coverao.ps1) va en la carpeta que contiene a los servidores:

```powershell
.\coverao.ps1 nueva rojo        # copia una instalación y le asigna puertos libres
.\coverao.ps1 iniciar rojo
.\coverao.ps1 estado            # todos, con RAM y puertos
.\coverao.ps1 detener rojo      # apagado limpio
.\coverao.ps1 arranque rojo     # OPCIONAL: que arranque al prender la máquina
```

Si ya tenés una copia del gestor en esa carpeta, reemplazala por la de la versión nueva antes de actualizar.

> **El servidor lo abrís vos cuando querés.** Ni `arranque` (Windows) ni `systemctl enable` (Linux) están en
> el camino recomendado: el juego se levanta a mano y se apaga a mano. Los dos existen para quien tenga un
> servidor dedicado y los quiera, pero no se registran solos ni hacen falta para jugar con amigos.

`arranque` registra una tarea que corre como **`LOCAL SERVICE`**, no como SYSTEM: una cuenta que puede lo justo. Le da
permiso de escritura sobre la carpeta de ese servidor y le reserva sus dos puertos HTTP (`StartPort+1` y `StartPort+3`) para
que se alcancen desde la red. `sin-arranque` saca la tarea, las reservas y el permiso.

**Linux**: [`hosting/coverao@.service`](../hosting/coverao@.service) es una unit plantilla:

```bash
sudo useradd --system --home /srv/coverao --shell /usr/sbin/nologin coverao
# una instalación por servidor en /srv/coverao/<nombre>, cada una con su StartPort
sudo cp hosting/coverao@.service /etc/systemd/system/ && sudo systemctl daemon-reload
sudo systemctl start coverao@rojo coverao@azul     # arranca AHORA, y sólo cuando vos lo pidas
systemctl status 'coverao@*'
journalctl -u coverao@rojo -f
```

Para un solo servidor está [`hosting/coverao.service`](../hosting/coverao.service), sin el `@nombre`.

**Docker**: un contenedor por servidor. La imagen se arma desde el **paquete oficial de Linux**, que incluye el cliente
firmado para la app de escritorio. Desde la carpeta del paquete (`CoverAO-servidor-linux-x64.tar.gz` descomprimido), que ya trae
su `Dockerfile` y su `docker-compose.yml`:

```bash
docker compose up -d
docker compose logs -f
```

Desde el repo es lo mismo con el `.tar.gz` en `dist/` (lo deja ahí `hosting/subir-version.ps1 -SinSubir`). Si el paquete no
trae la firma, la imagen no se arma y lo dice. La imagen no lleva `Server.ini`: se configura con variables de entorno
(ver el compose).

El volumen `coverao-data:/app/data` es lo único que no se regenera. El `stop_grace_period: 60s` está puesto
para que el guardado del apagado llegue a terminar. El panel de administración se publica sólo en el `127.0.0.1`
del servidor (el túnel SSH de "Puertos" llega igual), y el token se lee con
`docker compose exec coverao cat /app/data/admin-token.txt`.

**Con Postgres**, la base va en su propio contenedor. Se pone la clave en un archivo `.env` al lado del compose (una
línea `COVERAO_PG_CLAVE=` con una clave larga) y se levantan los dos:

```bash
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up -d
```

El servidor espera a que Postgres conteste antes de arrancar, y si en `coverao-data` ya había una `coverao.db` de
cuando andaba con SQLite, la importa sola. El volumen de la base, `coverao-pg`, tampoco se regenera. El puerto de
Postgres no se publica: el juego (y una web en el mismo compose) le llegan por la red interna.

## Con dominio y HTTPS

**Sin TLS la contraseña de cada jugador viaja legible** para cualquiera que mire la red: el cifrado XOR del
protocolo original no protege nada, porque la clave se anuncia en el propio protocolo. En una LAN de confianza
da igual; abierto a Internet, esta sección es la diferencia. La app de escritorio lo avisa: en su lista, un server por
`http://` que no está en la misma PC sale marcado **sin cifrar**.

Se termina TLS en un reverse-proxy delante del cliente web. **Es una sola regla y un solo nombre**, porque el
juego entra por `/ws` en el mismo puerto que sirve la página: el cliente abre su socket contra la dirección
desde la que se bajó, así que servida por `https://` el socket sale `wss://` solo, sin configurar nada.

Con Caddy (reenvía el upgrade a WebSocket sin que haya que decírselo):

```
juego.tudominio.com {
    reverse_proxy localhost:7669
}
```

Con nginx hay que pasarle las cabeceras del upgrade a mano, o la página abre y el juego no conecta:

```
server {
    listen 443 ssl;
    server_name juego.tudominio.com;
    ssl_certificate     /ruta/fullchain.pem;
    ssl_certificate_key /ruta/privkey.pem;

    location / {
        proxy_pass http://localhost:7669;
        proxy_http_version 1.1;
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host       $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

**Y en el `Server.ini`, `ProxyDeConfianza=127.0.0.1,::1`** (o la dirección del proxy, si corre en otra máquina). Detrás del
proxy todas las conexiones llegan desde él, y sin esta clave el server ve a todos los jugadores con la misma IP: el tope de
conexiones por IP pasa a ser un tope para toda la web, y el freno de intentos de login de uno bloquea a todos. Con la clave, la
IP de cada jugador sale de la cabecera `X-Forwarded-For` que agrega el proxy (Caddy la pone solo; en nginx va
`proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;` junto a las otras).

Con Pulsar en Windows, usá `CoverAO.Server.exe` desde la carpeta de instalación. El proxy debe preservar `Host`,
reenviar el upgrade de `/ws` y pasar la cabecera `X-CoverAO-Escritorio` que manda la app. Si Windows sólo reservó
el puerto web para `localhost`, reservá el prefijo para la cuenta de Windows que ejecuta CoverAO y limitá el backend
con el firewall. Para detener el proceso de forma ordenada, usá el archivo `detener` y esperá la salida del servidor.

Con el proxy puesto, en el router se abre **el 443**. Los puertos 7667, 7668 y 7669 quedan internos y
el 7666 del protocolo clásico permanece cerrado en el mundo real.

**Verificado ejecutándolo** (2026-09-01), y no sólo leído: con un proxy TLS delante del servidor, la página se
sirve por `https://`, el cliente abre `wss://<mismo host>/ws` y se juega: login, caminata, clic sobre una
criatura y hechizo de área, sin desincronizar el stream. La receta anterior pedía **dos** nombres (uno para la
página y otro para el socket) y traía una regla `reverse_proxy /ws* localhost:7667` que no podía engancharse
nunca, porque el cliente de entonces no pedía ninguna ruta: abría el socket contra la raíz de otro puerto.

## Licencia

CoverAO es AGPL-3.0 y su cláusula de red obliga a ofrecerle el código de la versión que corrés a quien se
conecte a jugar. Se cumple poniendo la URL en `Server.ini`:

```ini
CodigoFuente=https://github.com/tu-usuario/tu-fork
```

El enlace aparece en Menú → Licencia y en la página de descarga. Puede apuntar a un archivo del código de esa
versión. En una prueba cerrada, todos los testers deben poder descargarlo con su propio acceso. Si una instalación
anterior conserva el enlace al repositorio privado de CoverAO, configurá la dirección del código de esa versión.
