# Hostear CoverAO

## Armar el paquete

```powershell
pwsh hosting/publish.ps1                 # -> dist/CoverAO-win-x64
pwsh hosting/publish.ps1 -Rid linux-x64  # -> dist/CoverAO-linux-x64
```

Queda una carpeta autocontenida: no hace falta .NET instalado en la máquina destino. Al volver a correrlo
sobre la misma carpeta se conservan `data/` y `Server.ini`.

Con `-Comprimir` deja además el archivo para publicar, con el formato que corresponde a cada plataforma:
`.zip` para Windows y `.tar.gz` para Linux. No hay que armarlo a mano.

> **Ojo con el permiso de ejecución.** Antes acá decía que el `.tar.gz` se usa *porque el zip no guarda el
> permiso de ejecución*, y eso es media verdad: **armado desde Windows, el `.tar.gz` tampoco lo guarda**.
> Medido sobre el paquete publicado, recién descomprimido en Ubuntu, tanto el binario como `servidor.sh`
> salen `-rw-rw-rw-`. Lo que hace que igual arranque es el `chmod +x` que `servidor.sh` se hace a sí mismo
> — y por eso el `bash` de adelante no es un detalle de estilo.

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
| `StartPort` | juego (protocolo AO clásico) — no hace falta abrirlo, ver más abajo |
| `StartPort + 1` | WebSocket suelto — **compatibilidad**, ya no hace falta abrirlo |
| `StartPort + 2` | panel de administración — **sólo loopback** |
| `StartPort + 3` | cliente web: la página **y el juego**, en `/ws` |

El servidor los imprime al arrancar y quedan en el log del día.

**En el router se abre UNO: el del cliente web** (`StartPort + 3`). El juego entra por `/ws` en ese mismo
puerto, así que el jugador baja la página y juega por la misma dirección. **El del juego (`StartPort`) no
hace falta abrirlo**: ver más abajo por qué el cliente clásico de Windows no puede entrar.

> Antes hacían falta **dos** —el de la página y el del WebSocket— y ésa era la trampa más cara del hosteo:
> abriendo uno solo, el amigo veía la página, veía el login, y el socket se moría contra un puerto cerrado.
> Del lado del servidor no quedaba ni un renglón, porque esa conexión no llegaba nunca.

**El panel de administración no se abre nunca**: desde ahí se puede apagar el servidor, expulsar a todos y
bajar un backup. Para llegar de afuera, un túnel SSH: `ssh -L 7668:localhost:7668 usuario@servidor`.

En **Windows sin permisos de administrador** los puertos HTTP quedan sólo en `localhost`. El servidor lo avisa
al arrancar y da las dos salidas: abrirlo como Administrador, o reservar la URL una sola vez, en una consola de
**cmd** como Administrador:

```
netsh http add urlacl url=http://+:7669/ user=%USERNAME%
```

En **PowerShell** `%USERNAME%` no se expande: usá `$env:USERNAME` o escribí tu usuario. El servidor imprime al
arrancar la línea exacta, con tu usuario ya resuelto. En Linux no hace falta nada.

### Qué está verificado desde OTRA máquina

Medido el 2026-08-09 con el servidor Linux corriendo en una máquina y el navegador en otra:

| lo que hace un jugador | resultado |
|---|---|
| abrir `http://<ip-del-server>:StartPort+3/` | la página carga entera (330 KB, con el canvas) |
| el cliente abre su WebSocket | conecta (por `/ws` en el mismo puerto; el server lo publica en `/coverao-config.json`) |
| poner cuenta y contraseña | el server contesta con la lista de personajes |
| elegir personaje | entra al mundo, con los personajes de alrededor dibujados |

Lo único que NO se puede probar sin salir a internet es el **NAT del router**: que el reenvío de puertos
esté bien hecho. Todo lo demás del camino "un amigo abre la URL y juega" está ejercido.

### El cliente clásico de Windows NO puede entrar

El puerto de juego (`StartPort`) habla el **protocolo AO clásico byte a byte** —es lo que se compara contra el
servidor VB6 original— y el cliente clásico conecta y sus paquetes se entienden. Pero **no pasa del login**, y
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
| cuenta creada con `SEncriptar` y reentrada **con los mismos bytes** | entra — o sea que la cuenta existe |
| esa misma cuenta, reentrada con `SEncriptar` **de nuevo** | *Cuenta o contraseña inválida* |

Las dos últimas filas son las que cierran el caso: como el corrimiento cambia en cada envío, **ni siquiera un
servidor que sólo viera clientes clásicos funcionaría** — la cuenta que el cliente crea no la puede volver a
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
| `TrampasParaEchar`, `TrampasParaBanear` | anticheat con memoria: cada trampa detectada (velocidad, ataque fuera de rango, skills inventados, comprar más de una pila…) suma un punto en la CUENTA, que se conserva entre sesiones y se ve en el panel. Desde la primera cifra se lo expulsa en cada trampa nueva; en la segunda se banea cuenta y personaje. Los GMs conectados ven cada una. 0 = ese escalón apagado |
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
| `VidaMinimaDeJefe` | desde cuánta **vida máxima** una criatura hostil que da experiencia es un **jefe**: el cliente muestra su vida en una **barra grande arriba de la pantalla** mientras está a la vista. **Viene en 50000** (los dragones grandes, los gólems, los yetis, el Guardián y otros 16); con 0 no hay barra y el servidor no manda el paquete, que el original no tiene |
| `ComercioEnRinkel`, `ComercioEnLindos`, `ComercioEnCiudadesViejas`, `ComercioEnElResto` | **a quién le vende el mercader** según la zona, con los nombres de las facciones separados por coma (Renegado, Ciudadano, Republicano, Caos, Armada, Milicia); vacía, ahí no le vende a nadie. Rinkel incluye Nueva Esperanza y el Dungeon de Newbies; las ciudades viejas son Ullathorpe, Nix, Banderbill y Arghâl; el resto, el campo y los mapas sueltos. **Vienen como el original**: Rinkel a todos, Lindos a ciudadanos y republicanos con sus ejércitos, y las otras dos a nadie. Los bots usan la misma regla |
| `MinutosDelDia` | duración del ciclo día/noche |
| `MinutosDeClima`, `PorcentajeLluvia` | ciclo de lluvia |
| `OroAlSubirNivel` | oro de regalo por nivel. **0 = como el original**, que no regala nada. El `Server.ini` de ejemplo lo trae en 0 |
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
| `PasesDeRinkel` | 1 = los dos pases que vende el pirata del Puerto de Rinkel se pueden usar. **0 = como el install**, donde no se pueden usar en ningún lado: su ficha dice que salen de la ciudad (mapa 20) y el único transportador de Rinkel está en el puerto (mapa 99), así que el pirata te los cobra y después no te los acepta. Es un bug de la data del install — pasa igual en el servidor original. **El ejemplo lo trae en 0** |
| `HipocamposEnMarAbierto` | 1 = con la **red de pesca** en un mapa de mar abierto se saca **Hipocampo** en vez de los cuatro peces de siempre. 0 = como el install, donde el hipocampo no se puede conseguir por ningun camino. **Viene PRENDIDA**; la caña no cambia. |
| `NavegacionParaCruzar` | Los puntos de **Navegación** que pide un **pasaje que cruza el agua a otra tierra**. Las tierras salen de los mapas: el continente de Ullathorpe, Nix, Banderbill, Arghâl y Rinkel; el de Illiandor y Suramei; y las islas de Tiama, Orac, Lindos y Nueva Esperanza. Entre puertos de la misma tierra el pasaje no pide nada. Al que no llega, el pasaje no lo lleva y le avisa cuántos puntos necesita, con el mismo aviso del barco; al GM no lo frena. **0 = como el original**, donde ningún pasaje pide Navegación. **Viene en 80**; el tope es 100 |
| `DropGlobal` | **Una de cada cuántas** criaturas muertas suelta, además de su botín propio, **un** ítem de la tabla de `DropGlobal.dat` (26 accesorios de los mejores del juego). `10000` (el default) = una cada diez mil, que con la data del juego es **alrededor de un ítem por personaje en todo el camino hasta nivel 40** — a nivel 30 son ~1.900 muertes, a 40 ~12.900 y a 50 ~81.000. `0` lo apaga y vuelve al install, donde esa tabla existe en la data y ningún código del servidor original la abre nunca. Los porcentajes del archivo se usan como **pesos** —qué sale cuando sale—, no como probabilidades: sumados dan 482 %. **Viene PRENDIDA en 10000** |
| `MetamorfosisDelDruida` | Cuánto dura la transformación de los once hechizos de metamorfosis (Murciélago, Lobo, Jabalí, Tigre, Oso Pardo, Tortuga, Araña, Demonio, Árbol, Gólem y Dragón): `-1` **permanente**, `0` apagada, o los **segundos** que dura (hasta 3600). El druida toma el cuerpo de la criatura y sus bonos al golpe y a la defensa, todos de la data del install (del +6/+20 del murciélago al +50/+20 del dragón), y **las criaturas más débiles que su forma dejan de atacarlo** (el `asusta` del hechizo, 1..10, contra el `Nivel` de la criatura, 1..100). Morir o desconectarse la corta —tampoco sobrevive a un reinicio del servidor—, lanzar otra la pisa, y no se puede lanzar montado ni en barca. `0` vuelve al install, donde esos hechizos se venden hasta por 2.200.000 de oro y lanzarlos no hace nada. **Viene PRENDIDA y PERMANENTE**, que es como se comporta en el AO clásico; el original no dejó escrita ninguna duración (su `[INTERVALOS]` tiene 34 entradas y ninguna es de esto) |
| `AtacarAliadoVuelveCriminal` | 1 = **atacar a un ciudadano o a un soldado** —sin guerra de clanes ni facción enemiga de por medio— te vuelve **criminal** y te echa del ejército, con el aviso *"Has atacado a un aliado. Ahora eres CRIMINAL."*. Vale igual para el golpe y para la magia. **0 = atacar no cambia el estado de nadie**, que es lo que dice el fuente del original: ahí renegado sólo se vuelve el que deja una facción. **Viene PRENDIDA** por decisión del autor, que la recuerda del juego |
| `GuardiasAtacan` | 1 = los **guardias de las ciudades atacan** a las facciones enemigas de su bando. El bando es el de la ficha del guardia: **imperial** (122 guardias: Ullathorpe, Nix, Banderbill, Arghûl, Avanzada Imperial…), **republicano** (91: Lindos, Suramei, Illiandor, Avanzada republicana…) y **del Caos** (33: Orac, Ruinas Caóticas, Fortaleza Kerthon…). Pelean con los números de la data —nivel 50, 40.000 de vida, golpes de 250 a 280 y sus hechizos—, así que un enemigo que se acerca a una ciudad ajena la pasa muy mal. Al GM, al muerto, al invisible y al oculto no los tocan, como ninguna criatura. No pelean los que la data dejó sin golpe ni hechizo que lastime: los cuatro de Rinkel, los Cazadores y las dos Torres del Distrito Real. **0 = como el original**, donde ningún guardia pelea ni se mueve: su IA quedó apagada con un comentario de *"temporal"*. **Viene PRENDIDA** por decisión del autor |
| `GuardiasPersiguen` | 1 = el guardia que ve a un enemigo sale de su puesto a buscarlo y **no lo suelta mientras siga en su ciudad**, aunque se le vaya de la vista; cuando se va (o muere, o se hace invisible) vuelve a su puesto. 0 = quietos: pegan al que les pasa al lado y los que saben magia le tiran al que ven. **Viene en 1** |
| `GuardiasAtacanNewbies` | 1 = los guardias atacan también a los newbies (hasta nivel 15) del otro bando. Todo personaje nace ciudadano —o republicano si nace en Illiandor—, así que con 1 el recién llegado que entra a una ciudad del otro bando muere de un golpe. **Viene en 0** |
| `GuardiasImperialesAtacanA`, `GuardiasRepublicanosAtacanA`, `GuardiasDelCaosAtacanA` | A qué facciones ataca cada bando, separadas por coma: `Renegado`, `Ciudadano`, `Republicano`, `Caos`, `Armada` y `Milicia` (mayúsculas, acentos y plural dan igual). **Vacía = ese bando no ataca a nadie.** Un nombre que no existe se ignora y el arranque lo avisa. Por defecto cada bando ataca a todos menos a los suyos: el imperial a todos menos Ciudadano y Armada, el republicano a todos menos Republicano y Milicia, el del Caos a todos menos Caos |
| `Bots` | Cuántos **personajes que juegan solos** puebla el servidor. `0` (el default) = ninguno. Un bot no es un NPC: entra por el **mismo login** que un jugador y lo que hace pasa por el mismo dispatcher, los mismos intervalos y el mismo anticheat, así que **no puede hacer nada que un jugador no pueda**. Cuesta lo que cuesta un jugador: ocupa lugar en `MaxUsers`, sale en el `/ONLINE` y en la página pública, y su actividad entra en los logs igual que la de cualquiera. **Juegan**: cazan criaturas que les queden chicas —a mano o con hechizos, si su clase sabe—, huyen de las que les quedan grandes, levantan lo que dejan, toman pociones en medio de la pelea, comen, beben, meditan, reparten los puntos que da subir de nivel, le venden el botín a un mercader y le compran pociones, suben de nivel, y se desconectan cuando llevan un rato para volver más tarde. Cada uno tiene un temperamento que sale de su nombre —hay cazadores, tranquilos y paseantes— así que no se mueven todos igual. Lo único que **no hacen es hablar**. Si mueren se quedan cadáver donde cayeron: revivir necesita un sacerdote a la vista, y sólo lo intentan si les queda cerca. Sus cuentas llevan el sufijo `_bot` y quedan anotadas, con su contraseña, en `data/bots.txt` —que no viaja en el paquete, pero sí en cada backup—. Esa contraseña **no sirve desde afuera**: la cuenta de un bot queda marcada en su `.cnt` y sólo se abre desde adentro del servidor, así que leer el archivo no le da a nadie el personaje. El máximo son **48**, que son los nombres que hay para repartir. Con `--fixture` no arrancan aunque estén pedidos, y el arranque lo dice: un bot caminando cerca le mete a un flow de paridad movimientos que el flow no pidió. **Viene APAGADO** |
| `MinutosGuardarUsuarios` | autosave. 0 = sólo al salir |
| `MinutosBackup` | copia del mundo. 0 = desactivado |
| `BackupsAConservar` | cuántas copias guardar. 0 = todas |
| `LogDias` | días de log a conservar. 0 = sólo consola |
| `CodigoFuente` | URL del código de este servidor |
| `PuertoWebSocketPublico` | sólo si el hosting remapea puertos: el que se le anuncia al cliente |
| `Version` | la versión de cliente que este servidor acepta. Los cinco paquetes de ingreso traen la del cliente y el servidor la compara **como texto**: si no coincide, cartel de *juego desactualizado* y se corta la conexión. **Vacía (el default) = sin control**, que es lo que hacía el servidor hasta ahora. El cliente web que viene con esta versión manda `1.0.0`. Encendela sólo si sabés qué cliente repartiste: la comparación es exacta, así que un valor que no sea el del cliente desplegado deja a **todos** afuera. Sirve para dos cosas — que no se conecte un cliente ajeno al puerto del juego, y forzar a que actualicen cuando publiques una versión nueva. **Se reparte VACÍA por decisión del autor (2026-09-08)**: con el cliente cacheado en el navegador de cada jugador, el cartel de *juego desactualizado* le sale a quien sólo necesita recargar |
| `PaginaPublica` | 1 = publica `estado.html` en el mismo puerto del cliente web: quiénes están conectados y la tabla de posiciones, con su enlace en la pantalla de ingreso. **No es una regla de la casa** — no cambia ninguna mecánica —, es una ventana a lo que ya pasa: con pocos jugadores, lo que decide si alguien entra es saber si hay alguien adentro, y el `/ONLINE` del juego sólo se lee estando ya conectado. Sale nombre, nivel, clase y clan; **no** sale el mapa ni la posición de nadie, ni cuentas ni IPs, y los administradores no entran en la tabla. `0` la apaga entera y la dirección deja de existir (404). **Viene PRENDIDA** |
| `OrigenesPermitidos` | dominios EXTRA desde los que se acepta abrir el WebSocket del juego, separados por coma. Vacío (el default) = sólo el del propio servidor. El navegador declara en `Origin` qué página abrió el socket; sin ese chequeo, **cualquier página que un jugador visite puede abrirle sockets a tu servidor desde su máquina** — gastarle el cupo de conexiones y hacerle bloquear su propia IP con intentos de login fallidos. Un cliente que no es una página (una herramienta) no manda `Origin` y entra igual. Sólo hace falta si servís el cliente desde otro dominio que el servidor |

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

## Actualizar a una version nueva

**Actualizar no es reinstalar.** El paquete nuevo reemplaza el programa y la data del juego, pero
`data/` (las cuentas y los personajes) y tu `Server.ini` se conservan.

```bash
# 1. Parar el servidor. Con el server escribiendo, un update copia archivos a medio guardar.
# 2. Descomprimir el paquete nuevo SOBRE la carpeta de la instalacion.
# 3. Arrancar.
```

Eso es todo. Tres cosas que conviene saber:

**El jugador no hace nada.** El cliente web lo sirve este mismo servidor, asi que no hay nada que
instalar ni actualizar del lado de quien juega: con recargar la pagina ya tiene la version nueva. El
navegador revalida cada archivo contra su ETag, o sea que baja solo lo que cambio y no hace falta que
nadie limpie el cache. El que estaba jugando con la pestaña abierta se reconecta y listo.

**Los personajes viejos abren en la version nueva.** Los guardados son texto y lo que una version agrega
toma su valor por defecto al leerse. Al arrancar, el servidor deja un sello en `data/version.txt` y avisa
por consola si el mundo venia de otra version. **Si el aviso dice que venis de una version MAS NUEVA,
pará y restaurá un backup**: un servidor viejo ignora en silencio lo que no entiende.

**Las opciones nuevas no aparecen solas en tu `Server.ini`.** Como el archivo es tuyo y no se pisa, una
version que agrega una opcion no te la escribe. El servidor las lista al arrancar, bajo
`NUEVAS EN ESTA VERSION`, con el aviso de que estan corriendo con su valor por defecto; si querés
cambiarlas, copiá esas lineas de `Server.ini.ejemplo`.

Antes de actualizar, mirá que haya un backup reciente en `data/backups/` — es el unico "deshacer" que hay.

## Dónde se guardan los personajes

En una base de datos, con dos opciones y las mismas tablas en las dos:

- **SQLite: un archivo, `data/coverao.db`**, y nada que instalar. Es lo que usa una instalación nueva, y alcanza para
  un servidor solo.
- **Postgres**, para un servidor con páginas web que lean la misma base (ver más abajo).

Ahí van los personajes, las cuentas y lo que guarda el mundo: clanes, correo, suelo, llaves vendidas y el récord de
conectados.

Una instalación que ya tenía personajes en archivos —un `.chr` por personaje en `data/Charfile/` y un `.cnt` por
cuenta en `data/Cuentas/`— o la base vieja de `COVERAO_DB=sqlite` **los importa sola en el primer arranque**. Lo viejo
queda al lado, renombrado a `*.importado`, y en `data/` queda un informe `importacion-AAAAMMDD-HHMMSS.txt` con lo
importado y lo que no cerraba (un personaje sin cuenta, un amigo que no existe). Si la verificación no da —cada
personaje se relee de la base y se compara con el archivo—, no se toca nada y el servidor no arranca: el informe dice
por qué.

Se elige en el `Server.ini`:

```ini
[BASEDEDATOS]
; sqlite (un archivo), postgres (un servidor con paginas web) o archivos (un .chr por personaje, como el original).
; Sin la clave, sqlite.
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
| `Tipo` | `sqlite`, `postgres` o `archivos`. Vacía: `sqlite`, importando lo que hubiera en archivos |
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

  Copia la base de SQLite —la de `Archivo=`, u otra si se la nombra— a la de Postgres del `Server.ini`, y deja la de
  SQLite como estaba.

Si Postgres no responde al arrancar, el servidor no arranca y dice por qué (sin mostrar la clave).

**Una página web sobre la base.** La web se conecta a la misma base con un usuario propio que **sólo puede leer** el
esquema `publico`: vistas con los personajes, la tabla de posiciones, los clanes y sus miembros, los conectados, la
versión y el récord del servidor, y el catálogo del juego (objetos, hechizos, clases, razas y mapas, con sus nombres).
No ve nada de las cuentas —ni contraseñas, ni IPs, ni baneos—, ni el oro, la bóveda, la posición o el inventario, y el
staff del `Server.ini` y los personajes borrados quedan afuera. Se crea una vez, con el servidor apagado y los datos de
un administrador de Postgres en el entorno:

```sh
COVERAO_PG_ADMIN_USUARIO=postgres COVERAO_PG_ADMIN_CLAVE=... CoverAO.Server crear-usuario-web
```

Imprime la clave del usuario `coverao_web`, que no se vuelve a mostrar (correrlo otra vez le pone una nueva). Hace falta
Postgres 15 o más nuevo.

**Si la base se cae con el servidor andando** —Postgres que se reinicia, un disco lleno, el archivo tomado por un
antivirus—, el juego sigue: lo que se guarda espera en orden, en memoria, y se reintenta solo cada unos segundos. La
consola lo avisa una vez cuando se cae y otra cuando vuelve, y el panel muestra el estado de la base y cuánto espera. El
que quiere entrar mientras tanto recibe, a los 10 segundos, un cartel de que no se puede cargar el personaje. Si se
apaga el servidor con la base todavía caída, lo que no se pudo guardar queda en `data/rescate-AAAAMMDD-HHMMSS.json`, y el
próximo arranque lo aplica antes de abrir.

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

## Backups y apagado

Cada 30 minutos se copia el mundo entero a `data/backups/` sin frenar a nadie, conservando las últimas 48.
Antes de cada copia se guarda a los conectados. También hay un botón en el panel de administración.

Con la base, cada backup es **un archivo**, una copia consistente aunque el servidor esté escribiendo, más una
carpeta con el mismo nombre para los archivos sueltos de `data/`, como la lista de IPs baneadas. Con SQLite es
`coverao-AAAAMMDD-HHMMSS.db`, una base que se abre tal cual. Con Postgres es `coverao-AAAAMMDD-HHMMSS.zip`: cada tabla
en CSV y un manifiesto con la versión de las tablas, hecho por el servidor mismo, sin necesitar `pg_dump`. Para quien
sabe de Postgres, `pg_dump` sirve igual.

**Copiando la carpeta `data/` se reconstruye el servidor completo en otra máquina.** Es lo único que no se
puede volver a generar.

### No dejes `data/` adentro de una carpeta que sincroniza sola

En Windows, un `.chr` **abierto por otro programa** no se puede reemplazar: el guardado de ese jugador falla, y
el que hostea lo ve como alguien a quien "se le borró lo de la última media hora", cada tanto y sin patrón. Los
sospechosos de siempre son OneDrive o Dropbox sincronizando `Documentos`, el antivirus escaneando al vuelo y el
agente de backup del hosting.

Está medido: con un solo proceso pidiéndole el **tamaño** a los diez `.chr` de prueba —ni siquiera leyéndolos—
se perdieron 181 de 10.620 escrituras. El servidor ahora reintenta y eso baja a 0, pero el reintento tiene 31
ms de presupuesto a propósito (esperar más congelaría el mundo para todos). Si el programa que molesta se queda
el archivo más tiempo, el guardado se pierde igual — y entonces sí lo vas a ver en la consola, con el nombre
del archivo. La forma barata de no tener el problema es tener el servidor **fuera** de la carpeta sincronizada.

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
backup de una versión anterior del servidor se restaura igual: las tablas se ponen al día al restaurar.

**Con los archivos** (`data/Charfile/`, `data/Cuentas/`):

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

**Sin TLS la contraseña de cada jugador viaja legible** para cualquiera que mire la red: el cifrado XOR del
protocolo original no protege nada, porque la clave se anuncia en el propio protocolo. En una LAN de confianza
da igual; abierto a Internet, esta sección es la diferencia.

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
    }
}
```

Con el proxy puesto, en el router se abre **el 443 y nada más**: ni el 7669 ni el 7667 tienen que llegar desde
afuera. El **7666** (el del protocolo clásico) es aparte y no pasa por el proxy: ése va en claro salvo que
arranques con `--tls cert.pfx` — y hoy no hace falta abrirlo, porque el cliente clásico no puede entrar.

**Verificado ejecutándolo** (2026-09-01), y no sólo leído: con un proxy TLS delante del servidor, la página se
sirve por `https://`, el cliente abre `wss://<mismo host>/ws` y se juega — login, caminata, clic sobre una
criatura y hechizo de área, sin desincronizar el stream. La receta anterior pedía **dos** nombres (uno para la
página y otro para el socket) y traía una regla `reverse_proxy /ws* localhost:7667` que no podía engancharse
nunca, porque el cliente de entonces no pedía ninguna ruta: abría el socket contra la raíz de otro puerto.

## Licencia

CoverAO es AGPL-3.0 y su cláusula de red obliga a ofrecerle el código de la versión que corrés a quien se
conecte a jugar. Se cumple poniendo la URL en `Server.ini`:

```ini
CodigoFuente=https://github.com/tu-usuario/tu-fork
```

El juego se la muestra al jugador en Menú → Licencia. Si no se completa, el jugador ve el aviso sin enlace.
