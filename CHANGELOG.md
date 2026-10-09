# Novedades de CoverAO

Lo que cambia en cada versión, escrito para el jugador. El juego las muestra en Menú → Novedades.


## 1.0.0 (2026-10-06)
Lo nuevo desde el paquete del 5 de octubre.

### Facciones

- **Al enlistarse en la Armada, la Milicia o el Caos, el enlistador te da tus vestimentas** del bando (la de tu clase, o la de
  enanos y gnomos) y te dice su bienvenida, como en el original. El que estaba en un clan queda afuera del clan.

### Chat, mapa y pantalla

- **El mapa del mundo ya no repite las uniones entre mapas**: cada pedazo muestra sólo lo suyo, y la costa de Nix, por ejemplo,
  ya no se ve doble.

- **Al mirar a alguien, la vida en rojo y el nivel en celeste**, separados con un espacio: "Ves a Lobo nivel 8 64% de vida".

- **Al crear el personaje se ven los atributos con los que nace**: la base de la clase, lo que suma o resta la raza y el
  total (por ejemplo, un Gnomo Mago: Fuerza 6 −5 = 1). Antes se veía sólo lo de la raza.

- **Opciones con pestañas, adentro del juego igual que en el menú**: Audio, Video, Juego y Controles, con interruptores y la
  ayuda debajo de cada opción. Son las mismas de antes, y las teclas se reasignan en la pestaña Controles.

### Combate y magia

- **La vida que se gana al subir de nivel, como en el original**: el Gladiador perdía 1 punto por nivel con
  Constitución impar, y en las demás clases la tirada alta salía un poco menos de lo que debía.

### Comercio, banco y hogar

- **El Centro de Subastas**: doble clic al Subastador. Se publica un objeto de la mochila con precio inicial,
  precio final opcional (para comprar directo) y una duración de 3 a 36 horas; el depósito es de 10 monedas por
  hora más el 1 % del precio. Uno solo para todo el mundo. El objeto ganado, el oro de la venta, lo que no se
  vendió y la oferta superada van a la bóveda, aunque estés desconectado.
- **La cantidad del comercio y de la bóveda vuelve a 1** cada vez que se abre una tienda o la bóveda, como en el
  original. Las flechitas de las cajas de cantidad ya no salen blancas.

### Para GMs y para el que hostea

- **El oro que se cae al morir, en el Server.ini** (`OroQueSeCaeAlMorir`): el que muere donde se cae el
  inventario y lleva esa cantidad o menos encima la pierde toda al piso; con más, no pierde nada. Es la regla
  del AO clásico. En 0 no se cae nunca; el Server.ini de ejemplo la trae en 100000.

- **Los bots guardan su oro en el banco**: con el oro que se cae al morir prendido, pasado el nivel de novato y
  al terminar las compras en un pueblo con banquero, el que lleva encima lo que se le caería deja en la bóveda lo
  que no necesita para la próxima compra, y lo retira cuando una compra lo pide.

- **Los bots que empiezan compran el antídoto** y ya no salen de compras cada minuto y medio desde un rincón del
  que sólo se sale por un mapa del que acaban de huir.

- **Los bots eligen mejor el equipo**: comparan las armas y las armaduras por su golpe o defensa mínimo y máximo
  (antes, sólo por el máximo, y algunos se compraban un arma que pegaba menos), y no viajan al pueblo por una pieza
  apenas mejor que la que tienen.
- **Los bots, entre dos caminos igual de largos, toman el que no cruza criaturas que no pueden cazar**: de Illiandor
  a cazar Cuervos van por el Puerto y no por los Lobos.
- **Los bots cazan con menos pausas**: buscando presa en el coto se quedan quietos poco entre paseo y paseo; en el pueblo
  siguen con el ritmo de siempre.
- **Los bots que empiezan viajan menos**: envenenados y con pociones siguen cazando hasta curarse con el antídoto o con un
  sacerdote que tengan a mano, no salen de viaje por una pieza de equipo de menos de 1000 monedas (salvo un arma),
  y si frenan para cazar algo, primero lo cazan.
- **Los bots acorralados se cuidan más**: no se quedan peleando con lo que tienen pegado si a tres baldosas o menos hay
  otra criatura que los mata de dos golpes, salvo que no puedan moverse de la baldosa. Tampoco van a levantar un botín
  que queda a tres baldosas o menos de un peligro.
- **Los bots que fabrican viajan menos**: no van a dos mapas o más a fabricar si les quedan menos de seis puntos para
  llegar al tope de su nivel, salvo que lo que fabrican les haga falta.

## 1.0.0 (2026-10-05)
Lo que se sumó entre el 24 de septiembre y el 5 de octubre: el juego de escritorio, los bots y lo que pidió el tester.

### Facciones y mundo

- **Los guardias de las ciudades atacan** a las facciones enemigas de su bando, te persiguen hasta que salís de
  la ciudad y después vuelven a su puesto. A los newbies (hasta nivel 15) no los tocan. Y al guardia que te
  ataca le podés pegar, y tus mascotas también.

- **Los guardias de Illiandor son todos republicanos.** Uno de sus Magos era imperial y, con los guardias que atacan,
  iba por los republicanos en su propia capital.

- **Los Sastres de Illiandor y de Suramei venden las Vestimentas de Enano y de Enana**, a 25: los de raza baja ya no tienen que ir hasta
  Rinkel o la Avanzada por la ropa más barata.

- **Los animales de las ciudades ya no te siguen**: el Gallo, la Vaca, el Cerdo, el Pato y otras criaturas tranquilas
  deambulan. En el original caminaban hacia vos como si te fueran a atacar.

- **Las puertas cerradas no se atraviesan.** Algunas de Tiama, Nueva Esperanza, Rinkel y Nix se podían cruzar
  hasta que alguien las abría y las cerraba.

- **Sin obstáculos invisibles**: 34 baldosas de 16 mapas frenaban sin nada dibujado encima, seis de ellas en el
  parque de Nix. Ahora se pasa.

### Combate y magia

- **Matar a otro jugador avisa como en el original**: a los dos, con la experiencia que ganaste. Si le llevás
  más de diez niveles, no hay aviso ni experiencia.

- **Atacar a una criatura con hechizos o flechas pide el modo combate.** La flecha que no pega no se gasta.
  Y tirar sin flechas te saca el arco, como en el original.

- **Salir y volver a entrar no cura**: si te desconectás paralizado o prendido fuego, volvés igual, como en el
  original.

- **La Espada Mata Dragones, como el original**: a un dragón le pega muchísimo más fuerte, a cualquier otra
  criatura le saca 1, y a una persona entre 22 y 35. Y el **Arco Caza Demonios** le pega de verdad al Demonio y
  al Demonio Abisal, y a lo demás 1.

- **El Brazalete del Ogro pega más fuerte** contra las criaturas, también con el arco, y la flecha suma la montura
  como el golpe. El Pendiente del Experto no suma nada, como en el original.

- **Más sonidos en la pelea**: el arco al acertar y al errar, y el golpe a otro jugador, que además deja sangre. Y
  la flecha que acierta le borra al blanco el efecto del hechizo que tenía encima, como en el original. Lo que le
  saca la flecha a una criatura aparece sobre tu personaje, como el número del golpe.

- **Pelear contra otro jugador, como el original**: el escudo también frena las flechas, y el ladrón, el bardo y el
  gladiador desarman también con el arco. Montado o en barco se pega más fuerte, y el barco o la montura de la víctima
  la cubren sólo donde tiene casco o armadura. Con hambre o sed no se apuñala, y el número del golpe te aparece sobre
  tu personaje.

- **Usar un arma arrojadiza pide objetivo**, como en el original: la Lanza, la Jabalina, las dagas arrojadizas y el
  Shuriken. Si no la tenés equipada o no estás en modo combate, te avisa.

- **Los hechizos se ven como en el original.** La mitad son de partículas, y cada uno muestra ahora las suyas en
  vez de unos puntitos iguales para todos, con la duración que corresponde; los de área también, y la meditación.
  Los demás efectos brillan en lugar de taparse con un borde negro, y los largos ya no se cortan. Los brillos de
  las armas y armaduras toman su color.

### Objetos y oficios

- **Fundir con el inventario lleno** tira los lingotes al piso, como en el original.

- **Tirar oro**: un clic en tu oro abre el cartel de cantidad. Cae en montones de 10.000 por las baldosas de
  alrededor, como en el original, y el tamaño del montón se cambia con `OroPorMonton` en el Server.ini.

- **La ropa de mujer es sólo para mujeres y la de hombre sólo para hombres**: las túnicas, para los dos. La
  mujer nace con la ropa de novato de mujer. Y cada montura pide el nivel, la clase y la raza de su ficha: el
  huargo, sólo orcos.

- **Menos texto en la consola**: asignar habilidades y reordenar la bóveda ya no escriben nada, como en el
  original. Y trabajando, el aviso de que no conseguiste nada sale una sola vez por racha.

- **Botánica con sus textos**: «¡Has obtenido raíces!» al juntar y «¡No has obtenido raices!» al fallar.

### Comercio, banco y hogar

- **Fundar un clan se anuncia a todo el servidor**, con su sonido, y se lleva las gemas si las tenés.

- **Los clanes, como en el original**: aceptar, echar o irse se avisa a todo el clan, con su sonido; aceptar mira
  que tu facción cuadre con la del clan; cualquier miembro puede rechazar una solicitud, y si el rechazado no
  estaba conectado, lee el motivo al entrar.

- **La diplomacia de los clanes, como en el original**: la guerra se avisa a los dos clanes con su sonido, las
  propuestas de paz o alianza le llegan al otro líder en su lista, y firmarlas o rechazarlas se avisa a los dos. Los
  newbies no pueden pedir entrar a un clan, y para cerrar uno hay que quedarse solo.

- **El correo, como en el original**: al que recibe una carta se le avisa que la lea en un buzón, podés mandarte
  una a vos mismo, y la respuesta de un GM llega firmada por el staff.

- **Las noticias del clan avisan de la elección** cuando hay una abierta, como en el original.

- **Las elecciones del clan, sus votos y las propuestas de paz** ya no se borran al reiniciar el servidor.

- **Aceptar o echar del clan a alguien que no está conectado** le cambia el clan a él también, como en el original. Antes
  entraba después sin su clan, o con el de un clan que ya lo había echado.

### Chat, mapa y pantalla

- **La pantalla de ingreso es nueva**: fondo y logo de CoverAO, las novedades al costado para enterarte de cada
  actualización antes de entrar, cuántos están conectados y la música del inicio, con su volumen a mano.

- **El nombre del personaje admite números**, siempre que empiece con una letra: "Ayrton09" vale.

- **En el ingreso suena sólo su música**: la del mapa arranca al entrar al mundo.

- **La música y los efectos vienen al 20 %.**

- **"Recordarme" en el ingreso**: el juego para escritorio guarda la cuenta y la contraseña de cada server, cifradas, y la
  próxima vez el ingreso ya viene completo; en el navegador recuerda la cuenta.

- **Opciones también desde el ingreso** (el ⚙ de abajo), y con la música del inicio. En el juego para escritorio, el ingreso
  tiene "← Volver" y "Salir", y Opciones suma la pantalla completa, "Volver al menú" y "Salir del juego".

- **Apagar la música la apaga**: al entrar al mundo podían quedar dos temas sonando y el botón cortaba uno solo.

- **La tabla de posiciones se abre encima del ingreso**, sin cortar la música del inicio: se cierra con la ×, con Esc
  o con "entrar al juego".

- **El juego para escritorio abre con un menú**: Jugar, Opciones y Salir, en pantalla completa y con la música del inicio
  sonando hasta que entrás. Jugar muestra tus servers con el último elegido: los agregás por su dirección o IP, con el nombre
  que quieras, y ves cuáles están en línea, cuántos juegan, el ping y su tabla de posiciones. Opciones tiene las mismas del
  juego (audio, video, juego y controles) y valen para todos los servers. Desde el juego, Ctrl+Shift+S vuelve al menú.

- **Los errores y las sugerencias se reportan en GitHub**, como dice el cartel de bienvenida:
  github.com/Ayrton09/CoverAO/issues. El /GM queda para las dudas sobre el servidor donde jugás.

- **Crear la cuenta y el personaje avisa como el original**: cada error dice qué pasó, y si el nombre no vale o ya
  existe seguís en la misma pantalla para corregirlo. Una cuenta que no existe contesta como una contraseña mal
  puesta, para que nadie pueda averiguar qué cuentas hay.

- **El juego anda más liviano**, y **no llueve bajo techo ni en las cuevas**: la lluvia se corta en las cuevas y se desvanece
  al entrar bajo techo. El que hostea puede volver a lo del original, que llueve en todos lados (`LluviaBajoTecho=1`).

- **El tope de FPS llega a lo que elegís**: en 240 ya no se queda en 180.

- **Al apuntar un hechizo se ve hasta dónde llega**: un borde marca ocho baldosas a los costados y seis arriba y abajo.
  El que hostea lo apaga con `AlcanceDelHechizo=0` en el Server.ini.

- **Repartir habilidades es más rápido**: mantené apretado el + o el -. Y las barras de desplazamiento de los paneles
  tienen los colores del juego.

- **La cabeza apoya en la ropa.** Con muchas ropas quedaba un hueco en el cuello, sobre todo de frente: ahora la
  cabeza baja lo justo para tocarla.

- **Los portales dicen a dónde llevan**: cada uno tiene un cartel con su destino, y al clickearlo dice "Teleport a
  Nix". El que hostea lo apaga con `DestinoDeLosPortales=0`.

- **Los jefes muestran su vida**: con un jefe cerca aparece arriba de la pantalla una barra con su nombre y su vida.
  Son jefes los dragones grandes, los gólems, los yetis y las otras criaturas de 50.000 de vida o más; el que hostea
  elige desde cuánta vida con `VidaMinimaDeJefe`.

- **Los amigos, como en el original**: el mensaje a tus amigos también te llega a vos, y quitar a alguien de la
  lista te saca de la suya, aunque no esté conectado.

### Para GMs y para el que hostea

- **GMs**: /TELEP, /IRA, /SUM, /ACC e /INVISIBLE como en el original; pausar frena sólo a las criaturas; en el
  cliente web, Shift+clic en el mapa te lleva a esa baldosa. Las denuncias les llegan como en el original.

- **Más comandos de GM como en el original**: /HORA se anuncia a todo el servidor, /RMATA mata a la criatura
  clickeada y la hace reaparecer, /MIEMBROSCLAN lista un miembro por línea y /BANCLAN le anota la pena a cada
  miembro.

- **Lo nuevo se ajusta en el Server.ini**: `GuardiasAtacan` y sus claves, `NavegacionParaCruzar`,
  `[NIVELMINIMO]`, `MinutosDeFogata`, `MetamorfosisDelDruida`, `AtacarAliadoVuelveCriminal` y `EventoAlRematar`.
  Con 0 (o la tabla de cuevas vacía) cada una vuelve a ser como el original.

- **A quién le vende el mercader se elige en el Server.ini**, por zona y por facción: `ComercioEnRinkel`,
  `ComercioEnLindos`, `ComercioEnCiudadesViejas` y `ComercioEnElResto`. Vienen como el original, y los bots compran
  donde les venden.

- **Las tasas `Exp` y `Oro` multiplican directo** (1 es lo normal), y el panel las muestra aparte de los eventos.
  Como en el original, el evento de experiencia no multiplica lo que se cobra al matar a la criatura (todo lo del
  hechizo que la mata); con `EventoAlRematar=1` en el Server.ini, sí.

- **El mundo no se frena al leer o guardar personajes**: la base trabaja aparte, de a uno y en orden. Un baneo desde
  el panel, un /MOD o una transferencia a alguien que justo está entrando ya no se pierden.

- **Los personajes y las cuentas se guardan en una base**: un archivo, `data/coverao.db`, y nada que instalar. Los
  clanes, el correo, el suelo y las llaves vendidas van también ahí, y cada cuenta anota su último ingreso y desde qué
  IP, como el original. Se elige con `[BASEDEDATOS] Tipo=` en el Server.ini.

- **Una instalación que tenía los personajes en archivos los importa sola** en el primer arranque, verificando cada
  uno, y deja lo viejo al lado. Los `.chr` y `.cnt` ya no son un lugar donde guardar: `Tipo=archivos` no arranca y dice
  qué hacer. `CoverAO.Server importar <carpeta>` hace lo mismo con cualquier carpeta de datos,
  también la de un servidor del original con su correo; `exportar` saca todo a `.chr` y `.cnt`.

- **Para un servidor con páginas web, Postgres**: `[BASEDEDATOS] Tipo=postgres` y dónde está la base, con las mismas
  tablas que SQLite. Con la base vacía, el primer arranque importa la `coverao.db`, o se la pasa antes con
  `CoverAO.Server pasar-a-postgres`. El backup lo hace el servidor mismo, sin `pg_dump`. Con Docker, la base va en su
  propio contenedor: `docker-compose.postgres.yml`, encima del de siempre.

- **El `docker-compose.yml` arranca con la experiencia y el oro normales**: traía x100 de cuando las tasas eran un
  porcentaje.

- **Si la base se cae, el juego sigue**: lo que se guarda espera y se reintenta solo, el panel muestra el estado de la
  base, y el que entra mientras tanto recibe un cartel en vez de quedarse esperando. Si se apaga con la base caída, lo
  pendiente queda en un archivo de rescate que el próximo arranque aplica. `CoverAO.Server restaurar` vuelve a un backup.
  Las IPs baneadas también van en la base, y el importador trae las del original.

- **La base es de un servidor a la vez**: un segundo servidor sobre la misma base no arranca, y los comandos de la
  base no la tocan con el servidor andando. Pasar a Postgres o restaurar un backup la lleva entera, con las fechas y
  los personajes borrados, y no le aplica encima un rescate viejo. Una cuenta de más de 20 letras (la 1.0.0 las
  dejaba crear y ya no podían entrar) no frena la actualización: queda anotada en el informe.

- **Una web puede leer la base de Postgres** con un usuario que sólo ve lo público: personajes, tabla de posiciones,
  clanes, conectados y el catálogo del juego con sus nombres, sin nada de las cuentas y sin el staff.
  `CoverAO.Server crear-usuario-web` lo crea.

- **Bots que juegan solos**, apagados de fábrica: se prenden con `Bots=` y la cantidad. Se van del mapa de los
  guardias del otro bando, y para cazar o trabajar no cruzan los mapas inseguros donde los matan ni van a donde nace
  algo que los mata de un golpe; y a ningún lado cruzan donde nace algo que los mata de dos. No se quedan peleando
  con lo que no pueden lastimar, y en un mapa inseguro tampoco aguantando los golpes de lo que no les conviene
  pelear: se alejan, y si los sigue, se van de ahí por la salida más cercana (y si esa no los deja dar un paso, por
  otra). Persiguiendo una presa no se meten a la vista de lo que los mata. Si sus hechizos no salen siguen con el
  arma, y de muertos van al sacerdote aunque esté en otro mapa. Dos niveles antes de dejar de ser novatos se compran
  pociones propias (antes que nada), ropa, un arma y provisiones para el camino, aunque el herrero les quede lejos,
  si pueden llegar sin que los maten; y el último nivel de novato no lo pasan adentro del dungeon, ni entre los
  guardias que los van a atacar al dejar de serlo, ni donde ya no van a tener cómo volver a comprar. Ponen su hogar en
  una ciudad donde les venden, y si se quedan sin pociones, comida o bebida lejos de toda tienda, caminan hasta un
  mapa seguro (aunque quede lejos) y vuelven a casa con la runa, en vez de cruzar el desierto; y si no encuentran
  dónde cazar, también. Salen a comprar pociones sólo si les alcanza para una. De un lugar lleno salen sin frenar en
  el camino, y el que viaja a otro mapa para trabajar se queda un rato. Desde el último nivel de novato eligen dónde
  cazar por lo que rinde el mapa entero, hasta cuatro mapas más allá, y el Cazador pelea con el arco. No van a cazar
  donde después no tienen cómo volver a comprar, y en el pueblo compran de una el equipo que los mejora. Si pasan unos
  minutos sin sacarle nada a un lugar se van a otro, y si no tienen por dónde salir, vuelven a casa con la runa. Si
  tienen que irse dos veces huyendo del mismo lugar, no vuelven a cazar ahí hasta subir de nivel; lo que les pega en el
  camino también los hace huir, y sin dónde cazar en un mapa inseguro caminan hasta uno seguro. De camino no se quedan
  a cazar donde no elegirían cazar, y acorralados, los que saben magia se defienden con hechizos. Si salen a comprar
  sólo por un gusto (equipo mejor, un pergamino) y vuelven sin nada, esperan un buen rato antes de volver a intentarlo.
  Al pueblo entran por donde se llega a la tienda, y no por la orilla de afuera de la muralla, y encuentran caminos que
  antes no veían: de los Campos Abiertos a Rinkel por el Río Otoren, sin cruzar el desierto. Si alguien se les para en el
  camino, lo rodean o esperan a que se corra en vez de abandonar el viaje, y si es una criatura que pueden matar, la
  matan y siguen. Por lo que vienen pegando saben de antemano a qué criatura no le van a sacar nada, y no la salen a
  cazar; y lo que les pega y matan rápido (el Lobo, desde cierto nivel) se lo sacan de encima aunque no sea lo que salen a
  buscar. Huyendo corren, y si algo que los mata de dos golpes se les acerca dejan la pelea o el viaje; lo que se acerca
  tampoco los encuentra quietos. Una racha de golpes errados ya no les hace dar una criatura por imposible, y antes de
  pelear se ponen el arma aunque vengan de trabajar con la herramienta en la mano. Si tienen que irse de un mapa
  huyendo, no se meten en un rincón del de al lado que no tiene otra salida que volver.
  Con la vida en rojo, lo que les pega en un mapa seguro también los hace huir, y el que queda varado donde no le
  venden sale a pie a buscar el pueblo más cercano que le vende lo que le falta, sin cruzar los mapas donde los
  guardias van por él.
  Ya no compran comida o bebida para revenderla en el mismo mostrador, y si la runa los deja donde les venden, compran
  ahí. Acorralados por algo que los persigue pelean sólo si la pelea se gana; si no, se van del mapa. El rescate del
  varado sigue si se corta en el camino, y al ir a un pueblo entran por el pueblo y no por un rincón de afuera de la
  muralla.
  Si la puerta por la que iban a salir está tapada, salen por la de al lado, y de un lugar como Lindos, desde el que no se
  caza caminando a ningún otro lado, se van con la runa apenas compran o cuando dejan de ganar experiencia.
  Con la mochila llena tiran lo que no vale nada ni les sirve, como los anillos y las plumas del botín, para que entre el
  arma o la ropa que van a comprar.
  Si a cuatro mapas o menos hay un lugar de caza donde las criaturas suman el doble de experiencia que en el mejor que
  tienen sin cruzar peligro, lo cruzan para ir, y por el camino cazan lo que rinda parecido.
  Desnudos, sin la ropa de novato, van por ropa antes que por pociones, y si no hay sastre al que lleguen sin peligro,
  vuelven con la runa o a pie, como cuando les falta comida; y en las pociones no se gastan lo de la ropa.
  Pasado el novato se mudan a un lugar de caza que sume una vez y media lo del suyo, no el doble.
  Un lugar de caza que dejan porque no les da nada no lo vuelven a elegir por quince minutos, en vez de cinco.
  Camino a un lugar de caza frenan por lo que aparece sólo si tienen ganas de pelear, y entonces lo cazan; trabados por
  una criatura a media vida se toman la poción, y si no llegan al sacerdote para curarse el veneno usan la runa.
  Salen a comprar pociones cuando les quedan menos de cien, no apenas toman una.
  No compran el pergamino de un hechizo que no pueden lanzar, como el de doscientas mil que pide mil de energía.
  Con mucho oro no caminan al pueblo sólo para vender unas monedas de botín, y para fabricar lejos juntan material para una
  tanda entera antes de ir.

## 1.0.0 (2026-09-24)
La mayor parte sale de cruzar el servidor contra el original sistema por sistema; el resto son pedidos del tester
y lo que quedó sin contar desde el 4 de septiembre.

### Facciones y mundo

- **Ya te podés enlistar.** Jerek te enlista en la Armada Real, Barinke Fauster en la Milicia y el Demonio en la
  Legión del Caos; si ya sos de su bando, te dan la recompensa. Demedor Falentus y Halinte Ganre le dan la
  ciudadanía al renegado.

- **Los guardias de las ciudades atacan** a las facciones enemigas de su bando, te persiguen hasta que salís de
  la ciudad y después vuelven a su puesto. A los newbies (hasta nivel 15) no los tocan.

- **Los pasajes que cruzan el agua piden 80 de Navegación.** Entre puertos del mismo continente, nada.

- **Las cuevas piden nivel**: la Cueva orca 20, el Veriil 28, el Dragón 30, el Zero 35 y el Cristal 40, entre
  otras.

- **Desconectarse no es escapar.** En un mapa inseguro tu personaje se queda los cinco segundos de la cuenta de
  salida, y si te atacan la cuenta vuelve a empezar.

### Combate y magia

- **La magia pega con otra cuenta**: depende del nivel, de la clase, de los objetos mágicos y de la resistencia
  del que la recibe. Es un cambio de balance a propósito, tomado de aoweb.

- **El hechizo cae sobre lo que clickeaste**: si hay una criatura y un jugador, gana la criatura. Sin blanco ya
  no te cae encima, y curarle el veneno a alguien sano no te cobra el maná.

- **Atacar a una criatura con hechizos o flechas pide el modo combate.** La flecha que no pega no se gasta.

- **Lanzar un hechizo ya no te saca del ocultamiento.** Honor del Paladín sube además agilidad y fuerza, y la
  Tormenta ácida envenena.

- **La invisibilidad, como en el original**: te ves latiendo, se puede volver a lanzar, avisa al terminarse y
  al invisible ya no se lo atraviesa.

- **Inmovilizar no es paralizar**: la criatura no camina pero sigue pegando, y el jugador inmovilizado puede
  usar /SALIR. El fuego fatuo también inmoviliza.

- **La metamorfosis del druida ya no se vence**: dura hasta que morís o salís del juego.

- **Resucitar lleva un par de segundos**, con su sonido y su brillo, y el hechizo te deja con la comida y la
  bebida llenas.

- **La gracia al entrar se corta apenas hacés algo**, caminar incluido.

- **Más sonidos en la pelea**: el arco al acertar y al errar, y el golpe a otro jugador, que además deja sangre.

### Criaturas y mascotas

- **Las criaturas giran hacia lo que atacan**, las magas tiran el hechizo mientras se acercan, y la parálisis
  les dura lo que en el original.

- **La que te perseguía te suelta** cuando salís del mapa, te morís o te desconectás.

- **Reaparecen en cualquier punto del mapa, lejos de los jugadores**, salvo las que tienen un lugar fijo.

- **Lo que mata tu mascota lo cobrás vos**: la experiencia, el oro y lo que suelta. Y tu mascota, tu familiar y
  lo que invocás nunca aparecen adentro de una pared.

### Objetos y oficios

- **El trabajo arranca con un clic**, igual que con el doble clic. Cambiar de herramienta o /SALIR lo cortan.

- **Las pociones de fuerza y agilidad duran lo que en el original**: media hora las comunes, hora y media las de
  Alquimia. Los atributos no pasan de 35.

- **El pendiente del sacrificio te salva el inventario al morir**, gastando una de sus tres cargas.

- **La ropa de mujer es sólo para personajes mujer**, y cada montura pide el nivel, la clase y la raza de su
  ficha: el huargo, sólo orcos.

- **Reglas del original que faltaban**: con el arco no se pega cuerpo a cuerpo, los objetos piden su nivel
  (la barca también), en la arena no se roba y desnudo no se recupera energía.

- **Las botellas se llenan en el agua** y se toman.

- **La fogata se ve como fogata** y se apaga sola a los cinco minutos.

### Comercio, banco y hogar

- **El mercader, como en el original**: vende lo del original, se repone solo, y la llave de una casa se vende
  una sola vez. Si otro compra en la misma tienda, lo ves al momento.

- **El banco, como en el original**: el doble clic al banquero abre el panel del oro, con la bóveda a un botón
  (o con /BOVEDA).

- **El hogar se pide al sacerdote** de la ciudad donde estás, desde Acciones o con /HOGAR.

### Chat, mapa y pantalla

- **El mapa del mundo**: doble clic en el objeto Mapa. Muestra dónde estás, y por zona, sus criaturas y sus
  cuevas.

- **Gritar y el global**: empezá el mensaje con `-` para todo el mapa o con `;` para todo el servidor. El chat
  del clan sale sobre la cabeza.

- **Los teletransportes se ven de los dos lados**, y lo tirado en el piso se ve al volver a un mapa.

- **Al fantasma se lo puede pisar**: pasa a la baldosa de la que venís.

- **Los carteles del ingreso son los del original.** La contraseña nueva pide seis caracteres, y el código de
  recuperación es un PIN de cuatro números.

- **El juego anda más liviano**, y en Opciones, Juego, podés ocultar la lluvia bajo techo.

### Para GMs y para el que hostea

- **GMs**: /TELEP, /IRA, /SUM, /ACC e /INVISIBLE como en el original; pausar frena sólo a las criaturas; en el
  cliente web, Shift+clic en el mapa te lleva a esa baldosa.

- **Lo nuevo se ajusta en el Server.ini**: `GuardiasAtacan` y sus claves, `NavegacionParaCruzar`,
  `[NIVELMINIMO]`, `MinutosDeFogata` y `MetamorfosisDelDruida`. Con 0 (o la tabla de cuevas vacía) cada una
  vuelve a ser como el original.

- **Las tasas `Exp` y `Oro` multiplican directo** (1 es lo normal), y el panel las muestra aparte de los eventos.

- **El anticheat tiene memoria**: a las tres trampas se echa y a las seis se banea. Los movimientos grandes de oro
  quedan registrados.

- **Bots que juegan solos**, apagados de fábrica: se prenden con `Bots=` y la cantidad.


## 1.0.0 (2026-09-16)
Casi toda esta tanda sale del informe del tester del 14 de septiembre, más dos cosas que aparecieron
midiendo el combate contra el servidor original.

- **Las criaturas te llaman por su nombre.** Cuando un bicho te lastimaba con un hechizo, el mensaje te
  decía su número de ficha: «598 te ha quitado 65 puntos de vida». Ahora dice «Fuego fatuo».

- **La ventana de habilidades muestra lo que tenés.** Se abría con todo en cero apenas te conectabas, y
  después se quedaba con los valores viejos aunque el chat te avisara que habías subido una. Y los puntos
  libres seguían figurando como disponibles después de repartirlos.

- **La puerta cerrada frena.** Cerrabas una puerta y se podía cruzar igual: el servidor la trababa, pero tu
  pantalla te dejaba pasar y después te devolvía de un tirón.

- **Las criaturas reaccionan como en el original.** Pensaban una vez por segundo, cuando el juego original
  las hace pensar dos veces y media más seguido. Se notaba en que tardaban en verte, en que no podían
  perseguirte (se movían una baldosa por segundo contra las casi cinco tuyas) y en que pegaban más
  espaciado de lo que les corresponde.

- **Los bichos grandes tardan en volver.** Reaparecían todos a los cinco segundos, del más chico al más
  grande. Ahora los comunes tardan medio minuto, los que dan mucha experiencia cinco minutos, y los cuatro
  jefes del mundo media hora. El que hostea puede cambiar los tres números.

- **Pegar con la habilidad todavía verde rinde más.** El poder de ataque subía a los saltos: pasar de
  treinta a treinta y uno en una habilidad te daba de golpe lo que antes no te daba nada. Ahora sube
  parejo, igual que la evasión, que siempre lo hizo así.


## 1.0.0 (2026-09-04)
Esta tanda es de **juego**: cosas que se sienten peleando, comerciando o entrando al mundo. Varias las
encontró el tester jugando, y otras salieron de cruzar el servidor contra el original línea por línea.

- **Morir se escucha.** El original hace sonar un grito cuando un personaje cae, y acá no sonaba nada: te
  morías en silencio, y el que te mataba tampoco escuchaba nada.

- **Comer y fabricar también.** Morder una manzana, comer cualquier cosa, el martillo del herrero y el laburo
  del carpintero tenían su sonido en el juego original y acá faltaban los archivos, así que el servidor los
  pedía y no sonaba nada.

- **Las bolsas de oro se abren.** Los sacos de oro daban su valor en monedas en el original y acá contestaban
  "No puedes usar ese objeto".

- **El fuego dejó de ser una condena.** Que te incineraran duraba dieciséis minutos y te sacaba unos dos mil
  setecientos de vida: no se sobrevivía. En el juego original dura dos segundos y te pega dos veces. Y
  mientras te estás quemando no podés pegar, que también es del original.

- **Pelear cuerpo a cuerpo hace ruido.** El golpe que entra y el que falla suenan; hasta ahora, salvo el
  quejido de algunas criaturas, pegar era mudo.

- **Cinco segundos de gracia al entrar.** Las criaturas no te eligen de blanco cuando acabás de conectarte ni
  cuando un GM te teletransporta. Antes, aparecer en medio de un nido era comerte el primer golpe antes de
  que la pantalla terminara de dibujarse. Si vos atacás primero, te devuelven el golpe igual.

- **La montura ya no se pierde al salir y volver.** Aparecías a pie pero el inventario decía que estabas
  montado, y de ahí en más el caballo funcionaba al revés: se veía normal cuando estabas arriba y montado
  cuando te bajabas. Ahora desconectarte te baja del caballo, como en el original.

- **Si morís navegando quedás hecho un barco fantasma** y podés seguir remando. Al llegar a la costa te bajás
  y sos un fantasma común. Antes quedabas hecho un montón de huesos arriba del agua.

- **La criatura que estás peleando es tuya por un rato.** Te persigue a vos aunque haya alguien más cerca, así
  que el que llega después no te la roba parándose en el medio. La marca dura mientras la sigas golpeando.

- **El mercader dice la verdad.** La vitrina mostraba el stock de antes de comprar, algunos precios salían de
  la ranura y no del objeto, y a los mercaderes que nunca se quedan sin nada (el alquimista, el herrero) se
  les agotaba la mercadería.

- **Fabricar de más se rechaza en vez de recortarse.** Pedir cien flechas con material para diez te daba diez
  sin decir nada; ahora te avisa, como en el original. Y la herrería no tiene tope de cantidad.

- **Los comandos de administración aceptan nombres de dos palabras** (escribiéndolos con un `+` en el medio,
  como en el original) y la palabra `YO` para referirte a vos mismo.

- **La alineación del clan se mostraba mal**: las cuatro salían cambiadas.


## 1.0.0 (2026-08-28)
Esta tanda es casi toda **dibujo**: se cruzó el cliente entero contra el motor del juego original y se
corrigió todo lo que no coincidía. Son unos setenta y cinco arreglos; lo que más se nota:

- **El mundo se mueve a la velocidad de siempre.** Todo lo animado (la caminata, el oleaje, las antorchas, la
  lava, las cataratas) iba al doble de rápido, y algunas baldosas hasta seis veces. Por eso el muñeco parecía
  patinar contra el piso al caminar.

- **Los efectos de los hechizos caen donde tienen que caer.** Salían hasta tres baldosas y media más abajo:
  el destello con el que aparecés al entrar quedaba hundido en los pies en vez de envolverte. Y los hechizos
  de área ahora muestran su animación de verdad, no un cuadrado de color.

- **La noche es gris, no azul**, y el amanecer y el atardecer tienen los colores del original. Las luces de
  adentro de las casas ya no atraviesan el techo: de noche los techos de las ciudades se veían llenos de
  cuadrados brillantes.

- **Estando muerto el mundo se apaga.** Todo se ve a un cuarto de su brillo mientras sos fantasma, y ninguna
  luz te ilumina. Además el fantasma aparece y desaparece, en vez de quedarse como un dibujo fijo, y camina
  más rápido que un vivo.

- **Al que se oculta lo seguís viendo como una silueta que va y viene.** Ocultarse nunca te borró de la
  pantalla de los demás en el juego original; esto cambia bastante el enfrentamiento entre jugadores.

- **Las puertas, los estandartes y las antorchas de pie tapan al que está detrás.** Antes el personaje (y su
  nombre) quedaban flotando por delante del frente de la casa. Lo mismo con los objetos tirados en el piso.

- **El equipo de tope tiene su resplandor.** Quince objetos del juego traen aura y no se dibujaba ninguna.

- **El clan aparece sobre la cabeza**, debajo del nombre, que es con lo que se reconoce a un aliado antes de
  que empiece la pelea.

- **Lo que decís se parte en renglones y dura más**, y el color cambia según cómo hables: susurro, grito,
  global. Los números de daño ahora acompañan al personaje mientras se mueve en vez de quedarse clavados.

- **El agua es una pared.** Contra la orilla el personaje se metía medio paso en el agua y rebotaba varias
  veces por segundo, con toda la pantalla temblando. Y **paralizado ahora girás pero no caminás**: antes el
  muñeco recorría el mapa mientras para el resto seguías quieto donde te paralizaron.

- **El cartel del lugar es el dibujo pintado del original**, y ya no aparece en los 139 mapas que no tienen
  ninguno.

- **Se sacaron cosas que el juego original no hace**: el salto y el arco blanco al pegar, el halo de los
  objetos tirados, el velo azul de la lluvia y el símbolo sobre la cabeza al trabajar.

- **Lo que se te cae de una criatura ya no aparece dentro de una pared** ni sobre un teleport: busca una
  baldosa donde se pueda juntar, y si ahí ya hay algo igual, se apila.

- **Meditar entrena la habilidad Meditar**, cada vez que recuperás maná. Cuanto más alta, más seguido
  recuperás.

- **Los hipocampos se pescan en mar abierto.** Salí al océano con la **red de pesca** y ahí sacás Hipocampo
  en vez de los cuatro peces de siempre. La caña no cambia: sigue dando Pargo en cualquier agua. (En el juego
  original el hipocampo existía en los datos pero no había forma de conseguirlo; quien hostea puede volver a
  ese comportamiento con `HipocamposEnMarAbierto=0`.)

- **El que se desconecta desaparece de golpe.** Se quedaba dos segundos y medio desvaneciéndose en la
  pantalla de los demás.

- **Ya no te quedás caminando solo.** Si movías un objeto del inventario mientras caminabas y soltabas la
  flecha, el personaje seguía de largo hasta que volvieras a apretar y soltar esa misma tecla. Pasaba lo mismo
  con los carteles de confirmación, y con el macro: el golpe quedaba repitiéndose.

- **El teleport que planta un GM se ve.** Gira y alumbra, como en el juego original; antes quedaba invisible.

- **La fogata prendida arde.** Antes era idéntica a las ramitas apagadas (el mismo montón de leña) y sólo la
  prendida sirve para descansar, así que no había forma de saber si te había salido. Ahora se ve el fuego.

- **Podés ver quién está jugando sin entrar.** Hay una página con los conectados y una tabla de posiciones, y
  se llega desde la pantalla de ingreso. Antes, para saber si había alguien adentro había que entrar. Los
  administradores no figuran en la tabla, y no se publica dónde está parado nadie.

- **`/eventos` contesta.** El comando y el botón "¿Hay eventos?" existían y no decían nada, ni siquiera cuando
  un administrador tenía puesta experiencia, oro o botín multiplicados. Ahora dicen qué está corriendo.

- **Lo que se va del inventario se desequipa.** Mandar por correo un anillo o un pendiente que tenías puesto
  se llevaba el objeto y te dejaba su bonus, para siempre y acumulable. Ahora se te saca al irse, como cuando
  lo tirás o lo vendés.

- **Los hechizos de área respetan dónde y a quién.** Eran el único ataque del juego que no preguntaba si se
  podía: con una Bomba mágica se le pegaba a un compañero de clan, al cónyuge, a alguien de tu propia facción
  y en las ciudades, donde el PvP está prohibido. Ahora pasan por el mismo control que el resto.

- **El mercader cobra por lo que vende.** Cuando se le agotaba un producto, la ranura se quedaba con el
  precio del anterior, así que el siguiente objeto que entrara ahí se compraba y se vendía a un precio que no
  era el suyo. Ahora el precio sale siempre del objeto, como en el juego original.

- **Montar pide saber montar.** Cualquiera se subía a una montura con la habilidad de Equitación en cero, y
  arriba del animal se pega más fuerte. Ahora cada montura exige los puntos que pide en el juego original (de
  25 a 65 según cuál sea), igual que los barcos piden Navegación.

- **Meditar no aguanta que te peguen.** La meditación seguía dándote maná mientras te estaban golpeando. Ahora
  un jugador te la corta de un golpe y una criatura también, si el golpe es lo bastante fuerte para tu
  Inteligencia y tu Meditar. Y al que medita le cuesta más esquivar, como allá.

- **El barco no se tira ni se rompe estando arriba.** Soltarlo en medio del mar te dejaba caminando sobre el
  agua para siempre, sin barco con el que volver a la costa. Lo mismo con la montura: tirarla te baja del
  animal antes de que se vaya al piso.

- **Con la tienda o la bóveda abierta no se toca el piso.** Tirar, levantar y destruir objetos con una ventana
  de comercio abierta quedaban fuera de la vista del comerciante, y el doble clic abría una segunda ventana
  encima de la primera.

- **El líder no puede abandonar su propio clan.** Si lo hacía, el clan quedaba sin nadie que pudiera aceptar,
  echar, declarar la guerra o firmar la paz, y no había forma de arreglarlo desde adentro del juego.

- **El servidor deja constancia de los intentos de trampa.** No había ninguno registrado: donde el juego
  original anota y corta, acá se recortaba el pedido en silencio y el que hostea no se enteraba nunca. Ahora
  quedan en el registro del servidor y se ven en el panel.

- **Repartir más puntos de habilidad de los que tenés corta la conexión**, y no aplica ninguno: es lo que
  hace el original. El panel de Habilidades del cliente ahora no te deja pasarte, y te muestra cuántos te
  quedan mientras repartís.

- **Correr más rápido de lo que el juego permite te saca.** El control que había miraba paso por paso; le
  faltaba el del juego original, que mira el promedio sostenido y avisa a los administradores conectados. La
  primera vez no echa a nadie: hace falta repetirlo.

- **Comprar una cantidad imposible de un producto te banea.** Es el único baneo automático que tiene el juego
  original, y se anuncia a todo el servidor.

- **El servidor puede exigir la versión del cliente.** Los cinco paquetes de ingreso siempre trajeron la
  versión y no la miraba nadie; ahora el que hostea puede pedir una y al que llega con otra le sale el cartel
  de *juego desactualizado*. Viene apagado: se enciende escribiendo la versión en el `Server.ini`.

- **Estando muerto, tirar y destruir no te contestan.** Los dos avisaban "estás muerto" donde el juego
  original no dice nada. Es un detalle chico, pero de los que se notan: el cartel salía por cosas que allá
  pasan en silencio.

- **La montura que estás usando no se vende.** Se la podías vender al mercader estando arriba: te bajaba del
  animal y se la llevaba igual. El juego original no te deja y te lo dice.

- **El arco tiene su cadencia.** Se podía disparar tan rápido como el cliente mandara los clics: el camino del
  disparo no tenía ninguna espera. Ahora respeta el intervalo del juego original, y como allá, recién disparado
  tampoco se puede pegar ni tirar un hechizo.

- **Para el que hostea: un solo puerto.** El juego entra por la misma dirección que sirve la página, así que
  en el router se abre uno en vez de dos. Antes, con uno solo, tus amigos veían la pantalla de ingreso y no
  podían conectarse nunca. Y con dominio propio, el HTTPS pasa a ser una sola regla del proxy.

- **Macro: mantené la tecla apretada y la acción se repite.** Sostener **Ctrl** sigue golpeando y sostener
  una **F1–F10** sigue usando lo que tengas atado, a la velocidad del cliente original. Viene puesto; si
  preferís apretar una vez por golpe, se apaga en Opciones → Juego. Los oficios (minar, talar, pescar,
  fundir) siguen repitiéndose solos como siempre, prendas o no el macro.

- **Las metamorfosis del druida funcionan.** Los once hechizos (Murciélago, Lobo, Jabalí, Tigre, Oso Pardo,
  Tortuga, Araña, Demonio, Árbol, Gólem y Dragón) transforman al druida en la criatura, con los bonos al
  golpe y a la defensa que dice cada hechizo. **Y las criaturas más débiles que tu forma no te atacan**: con
  el murciélago te dejan de molestar las ratas, con el dragón te ignora todo salvo los doce bichos de tope.
  Por eso el Árbol vale aunque casi no pegue: es para pasar desapercibido. Dura **dos minutos**; morir o
  desconectarse la corta, y no se puede lanzar montado ni arriba de una barca. (En el juego original esos
  pergaminos se vendían hasta por 2.200.000 de oro y lanzarlos no hacía nada. Quien hostea cambia la duración
  con `MetamorfosisDelDruida`.)

- **Drop global: cualquier criatura puede soltar un accesorio de los buenos.** Es raro de verdad (del orden
  de uno por personaje en todo el camino hasta nivel 40), así que sigue siendo un hallazgo y no una fuente;
  quien hostea lo cambia con `DropGlobal`. La lista de los veintiséis objetos y qué tan raro es cada uno son
  las del juego original, que traía la tabla hecha y nunca la usó.


## 1.0.0 (2026-08-01)
- **Cada raza usa la ropa que le corresponde.** El elfo, el humano, el drow y el orco son razas altas y visten
  igual; el enano y el gnomo son bajos y tienen su propia ropa. Antes el elfo y el gnomo estaban cambiados: al
  elfo sólo le entraba ropa de bajos.

- **Pasá el mouse por un objeto y te dice todo.** Qué es, cuánta defensa o daño da, qué nivel pide (y si
  todavía no lo alcanzás) y a qué clases y razas les está prohibido. También en la tienda, así te enterás
  ANTES de comprarlo.

- **Las ventanas del comercio y del banco son más anchas.** Entran cuatro objetos por fila en vez de dos, así
  se ve el stock del mercader y tu inventario de una sola mirada, sin tanto scroll.

- **Tres habilidades que no subían nunca ahora suben usándolas.** **Tácticas** la entrena el que RECIBE los
  golpes de una criatura (acierte o falle el bicho), que es como se sube la evasión; **Supervivencia** ahora
  también sube al hacer fogatas, no sólo al encenderlas; y **Domar** sube cada vez que domás una criatura.
  (Recordá que con hambre o sed en cero no sube ninguna habilidad, y viajar en barco te deja las dos en cero.)

- **Las criaturas pegan a su ritmo.** Cada una vuelve a atacar recién cuando le toca, como en el original;
  antes te pegaban mucho más seguido, así que el mundo era bastante más duro de lo que debía. También suena
  el golpe que entra, se ve la sangre, y el que te erra te lo muestra.

- **Los pases del pirata de Rinkel.** Te los vende y después no te los acepta: su ficha dice que salen de la
  ciudad y él está en el puerto. Es un error de los datos del juego original, así que por fidelidad quedan
  como están; quien hostea puede hacerlos funcionar con `PasesDeRinkel=1`.

- **Los lingotes se hacen solos.** Usá el mineral, hacé clic en la fragua y el personaje sigue fundiendo sin
  que le toques nada más, igual que al minar o al pescar. Y cuánta Minería hace falta depende de tu oficio: el
  **minero** funde con el mínimo del mineral (hierro 30, plata 60, oro 80), el **herrero** necesita la mitad
  más (45 y 90, y el oro no lo puede hacer) y cualquier otra clase el triple. (Quien hostea puede poner un
  guardia anti-macro que te pida una clave mientras trabajás, con `Centinela=1`.)

- **El Carro de Minería cumple lo que promete**: si lo llevás encima, al morir salvás el 70% de cada mineral.

- **Las monedas de oro del piso van directo a tu bolsa.** Antes te ocupaban una ranura del inventario como un
  objeto cualquiera.

- **El "?" del libro de conjuros dice cuánto pega y cuánto cuesta**: el daño o la cura calculados para tu
  nivel, el maná, la energía y la Magia que pide.

- **Tocá a tu familiar y te muestra su ficha**: nivel, vida, golpe y las habilidades que fue aprendiendo.
  (Quien hostea puede además hacer que salga a pelear solo, con `FamiliarBusca=1`.)

- **Volumen de los efectos**, en Opciones, aparte del de la música. Antes sólo se podían apagar del todo.

- **Más avisos cuando el juego te frena**: por qué no podés usar esa ropa, cuánta Minería te falta, o que el
  cartel de "no tenés materiales" a veces significa que te quedaste sin energía.

- **Los Magos, Druidas y Cazadores tienen familiar.** Se elige al crear el personaje y se le pone nombre: el
  Mago puede llevar un Fuego Fatuo, un Elemental, un Ely, un Duende o un Hada; el Druida y el Cazador, bestias
  (Ent, Lobo, Tigre u Oso). Al elegirlo te entra al libro el hechizo **Llamado al familiar**, que no vende
  nadie: lanzalo para sacarlo y lanzalo otra vez para guardarlo. Pelea con vos, gana la misma experiencia que
  vos mientras está afuera, sube de nivel y va aprendiendo habilidades: envenenar, paralizar, desarmar, cegar,
  o tirar hechizos si es de los mágicos. El Ely aprende a **curarte a vos** cuando te ve herido. Si lo matan
  queda muerto y hay que llevarlo a la **Veterinaria**, que también lo cura. Su ficha está en el panel de
  estadísticas.

- **Tus mascotas viajan con vos.** Antes se quedaban tiradas en el mapa anterior cuando cruzabas una salida o
  te teletransportabas. Y el hechizo **Resucitar familiar o mascota** ahora resucita de verdad: antes decía que
  sí y no pasaba nada.

- **Las criaturas hacen ruido.** Rugen cuando te atacan, se quejan cuando les pegás y gritan al morir. El
  combate contra bichos era completamente mudo. También suena el escudo cuando alcanzás a bloquear un golpe, y
  el hechizo *Resucitar* volvió a su sonido clásico.

- **Lo que se te cae al morir sigue ahí aunque el servidor se reinicie.** Antes, cada reinicio limpiaba el
  suelo y te quedabas sin la mochila. (Quien hostea puede apagarlo con `PersistirSuelo=0`.)

- **El juego te explica los rechazos que confunden.** Cuando el servidor te frena por algo que parece un bug y
  no lo es ("estás muy lejos" del banquero, "primero hacé clic" estando al lado del mercader, no poder tirar el
  equipo de novato) te aparece una línea de contexto abajo, una sola vez por sesión. Y al equipar una
  herramienta te avisa que todavía falta usarla para ponerte a trabajar.

- **Crear personaje pasó a estar dentro de tu cuenta.** La pantalla de inicio ofrece sólo conectarse y crear
  cuenta; al crear la cuenta entrás directo a tu lista de personajes, y el botón de crear ya no te vuelve a
  pedir la cuenta ni la contraseña. Y **borrar un personaje funciona** también con una cuenta recién creada:
  antes se quedaba sin hacer nada.

- **La fragua y el yunque del herrero funcionan.** Para hacer lingotes: usá el mineral que tenés en el
  inventario y hacé clic en la fragua. Para forjar: equipate el martillo y hacé doble clic en el yunque. Fundir
  pide skill de Minería, así que si te rebota es que todavía te falta oficio.
- Darle rango de GM a alguien desde el panel de administración ahora tiene efecto. Antes el panel decía que sí
  y la persona seguía sin poder usar los comandos.
- Ya no se pierde lo que hiciste si otro programa toca la carpeta del servidor mientras jugás: el antivirus,
  una copia de seguridad o una carpeta sincronizada alcanzaban para que un guardado se perdiera en silencio.

- Al crear el personaje se elige bando: Nix te hace del Imperio e Illiandor de la República. Antes la
  pantalla ofrecía doce ciudades que no existen y todos nacían imperiales.
- Los personajes nuevos aparecen en el Dungeon Newbie, con su sacerdote, su tabernero y sus salidas a las
  ciudades, como en el original.
- Se puede cambiar la contraseña de la cuenta, y recuperarla con el código que se elige al crearla. Antes,
  perder la contraseña era perder la cuenta y todos sus personajes.
- El Convertidor cobra las 100.000 monedas de oro que cuesta la ciudadanía. Antes la regalaba.
- El selector de hogar lista las doce ciudades que existen de verdad. Las que ofrecía antes no existen en
  este juego, y elegir una te mandaba a otra.
- Cuando se rompe el récord de jugadores conectados a la vez, el servidor lo anuncia.
- Los administradores ven quién se desconecta, no sólo quién entra.
- `/fundarclan`, `/cerrarclan` y `/divorcio` andan escribiéndolos, además de por su botón.
- Ya se puede reconfigurar cualquier tecla: engranaje ⚙ → Opciones → Configuración de controles.
- Opciones nuevas: volumen de la música, tope de FPS, zoom y escalado del mundo. Todo se guarda.
- El teclado es el del original: C combate, E equipar, R robar, S seguro, A agarrar, T tirar, U usar, D domar.
- Privados con `\Nombre mensaje`, o con doble clic derecho sobre un jugador.
- El juego suena: 178 efectos y 14 pistas de música (la música arranca apagada; se prende con el botón ♪).
- Las estadísticas muestran de verdad tus muertes y tus kills por bando, que antes salían siempre en cero.
- Los guardias, hechiceros y nigromantes llevan puestos su arma, su escudo y su casco: antes salían desarmados.
- Los yacimientos de oro y de plata dan oro y plata. Antes todos daban hierro, picaras donde picaras.
- Los ítems que prometen un bonus en el nombre ahora lo dan: Nudillos (+10), Anillo de Agilidad (+5),
  Botas de Celeridad (+10) y Anillo del Carisma (+10).
- Meditar devuelve maná según tu maná máximo, y la habilidad Meditar por fin sirve: entrenada acierta siete
  veces más seguido.
- Los hechizos que van sobre uno mismo (invisibilidad, las metamorfosis, Aura sagrada, Golpe certero,
  Inspiración, Honor del Paladín) se lanzan solos, sin pedir un objetivo.
- Los sacos de créditos, el de tiempo de donador y el Anillo Dorado ya no se pueden tirar al piso sin querer.
- Los comandos de administración se pueden usar desde el cliente web: /telep, /ci, /carcel, /sum,
  /invisible y 73 más. Se listan con /ayudagm. Antes el cliente contestaba "comando desconocido"
  a todos, así que quien hosteaba no tenía forma de usarlos.
- Si cerrás sesión navegando, volvés en tu barco y no a pie en medio del mar.
- El servidor guarda a todos los conectados al apagarse, aunque se lo cierre desde la ventana.
- El Portal planar abre de verdad: se lo tira sobre una baldosa libre, tarda cinco segundos en aparecer y
  dura diez. Mientras hay uno abierto el juego te dice cuánto falta para poder tirar otro.
- Cuando se conecta alguien de tu lista de amigos te avisa, igual que ya avisaba cuando se iba.
- Dejar tu facción te avisa que la dejaste. Antes te ibas sin que el juego dijera nada.
- Los mapas de newbies no dejan pasar al que ya dejó de serlo, y el que sube de nivel dentro del dungeon de
  newbies sale a la ciudad que le toca por su bando.
- Tirar un arma arrojadiza sin tener ninguna equipada te contesta; antes la tecla no hacía absolutamente nada.
- Un miembro de la Armada Real no puede atacar de una a las criaturas: primero tiene que hacerse renegado.
