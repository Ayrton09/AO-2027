# Novedades de CoverAO

Lo que cambia en cada versión, escrito para el jugador. El juego las muestra en Menú → Novedades.

## 1.0.0 — 2026-09-24

Esta tanda es larga. La mayor parte sale de cruzar el servidor contra el original sistema por sistema —los
hechizos, las criaturas, el mercader, el banco, las facciones—; el resto son pedidos del tester y lo que quedó sin
contar desde el 4 de septiembre.

- **Ya te podés enlistar.** Los enlistadores del mundo no enlistaban a nadie. Ahora Jerek te enlista en la Armada
  Real, Barinke Fauster en la Milicia y el Demonio en la Legión del Caos, y si ya sos de su bando el doble clic te
  da la recompensa. Y al renegado le dan la ciudadanía Demedor Falentus, la del Imperio, y Halinte Ganre, la de la
  República.

- **Los guardias de las ciudades atacan.** Cada uno va contra las facciones enemigas de su bando: el imperial,
  contra renegados, republicanos, la Milicia y el Caos; el republicano, contra renegados, ciudadanos, la Armada y
  el Caos; el del Caos, contra todos los demás. Te persiguen hasta que salís de la ciudad y después vuelven a su
  puesto. A los newbies (hasta nivel 15) no los tocan. (En el original los guardias no se mueven; quien hostea
  elige a quién atacan, si persiguen y si tocan a los newbies, o los deja quietos con `GuardiasAtacan=0`.)

- **Los pasajes que cruzan el agua piden Navegación.** Todo pasaje a otra tierra —a Tiama, a Nueva Esperanza y
  los demás cruces— te pide 80 puntos de Navegación, y si no llegás te lo dice. Entre puertos del mismo
  continente no pide nada. (En el original no piden nada; quien hostea cambia el mínimo con
  `NavegacionParaCruzar`, y con 0 vuelve a ser así.)

- **Desconectarse no es escapar.** Si cortás la conexión en un mapa inseguro, tu personaje se queda en el mundo
  los cinco segundos de la cuenta de salida, como con /SALIR, y si te atacan mientras tanto la cuenta vuelve a
  empezar. Antes, cerrar el juego en medio de una pelea te sacaba al instante.

- **Las cuevas piden nivel.** Cada dungeon tiene su mínimo y no te deja entrar antes: la Cueva orca pide 20, el
  Veriil 28, el Dragón 30, el Zero 35 y el Cristal 40, entre otros. (Quien hostea cambia la tabla en la sección
  `[NIVELMINIMO]` del `Server.ini`.)

- **La magia pega con otra cuenta.** El daño de los hechizos ahora depende del nivel, de la clase, de los objetos
  que suman daño mágico o atraviesan defensas, y de la resistencia mágica del que lo recibe, en porcentaje. Es un
  cambio de balance hecho a propósito, tomado de aoweb: no es el cálculo del original.

- **La metamorfosis del druida ya no se vence.** Dura hasta que morís o salís del juego, como en el AO clásico;
  antes se cortaba a los dos minutos. (Quien hostea le puede poner duración con `MetamorfosisDelDruida`.)

- **El mapa del mundo.** Hacé doble clic en el objeto Mapa y se abre con tu posición. Al pasar el mouse por una
  zona te dice su nombre, qué criaturas hay y cuántas, y qué cuevas tiene, cada una con las suyas. El dibujo
  coincide con el mundo: cada casillero tiene los colores de su mapa.

- **El hechizo cae sobre lo que clickeaste**, con las reglas del original para elegir el blanco: si en la baldosa
  hay una criatura y un jugador, gana la criatura. Un hechizo sin blanco ya no te cae encima, los que dañan no se
  lanzan sobre uno mismo, y curarle el veneno a alguien sano o sacarle la parálisis a quien no está paralizado no
  te cobra el maná.

- **Atacar a una criatura con un hechizo de daño o de parálisis, o con una flecha, pide el modo combate**, como en
  el original. La flecha que no le puede pegar ya no se gasta.

- **Más hechizos como en el original.** Lanzar uno ya no te saca del ocultamiento. Honor del Paladín sube además
  la agilidad y la fuerza. La Tormenta ácida envenena a los que alcanza. Los de área sobre criaturas siguen las
  mismas reglas que los de un solo blanco. Y las pociones que lanzan un hechizo se gastan sólo si hacen efecto.

- **La invisibilidad, como en el original.** Mientras sos invisible te ves latiendo, podés volver a lanzarla —se
  cobra, pero no estira el tiempo— y cuando se vence te avisa y todos te vuelven a ver. Al invisible ya no se lo
  atraviesa caminando, y el que entra a un mapa donde no se permite queda visible, con aviso.

- **Inmovilizar no es paralizar.** La criatura inmovilizada no camina pero sigue pegando: la hostil a los cuatro
  lados, la pacífica sólo al frente. El jugador inmovilizado por otro jugador no queda paralizado y puede usar
  /SALIR. El Inmovilizar del fuego fatuo ahora inmoviliza, igual que el tuyo. Y la parálisis sobre una criatura
  dura lo que en el original: antes se le iba mucho más rápido.

- **Las criaturas, más como en el original.** Giran hacia lo que atacan, las magas tiran el hechizo mientras se
  acercan, y entre ellas pelean buscando a su blanco sólo en lo que ven. La que te perseguía te suelta cuando
  salís del mapa, te morís o te desconectás, y la pacífica que provocaste vuelve a ser pacífica. Las que no tienen
  un lugar fijo reaparecen en cualquier punto del mapa, lejos de los jugadores.

- **Lo que mata tu mascota lo cobrás vos**: la experiencia, el oro y lo que suelta, también con los hechizos del
  familiar. Y tu mascota, tu familiar y lo que invocás aparecen en la baldosa libre más cercana, nunca adentro de
  una pared; las criaturas de agua, en el agua.

- **Resucitar lleva un par de segundos**, con su sonido y su brillo, sea con el sacerdote, con /RESUCITAR, con el
  hechizo o al morir en la arena; mientras tanto el sacerdote no te vuelve a atender. El hechizo Resucitar además
  te deja con la comida y la bebida llenas.

- **La gracia al entrar se corta apenas hacés algo.** Los cinco segundos en que las criaturas no te eligen de
  blanco —al conectarte o al viajar— terminan con cualquier cosa que hagas, caminar incluido, como en el original.
  Mientras duran, tampoco te pega la que tenés al lado.

- **Los teletransportes se ven de los dos lados**: el que está donde salís te ve irte y el que está donde llegás
  te ve aparecer, con su sonido y su destello. Y al cambiar de mapa se te vuelve a avisar si llueve.

- **Lo tirado en el piso se ve al volver a un mapa.** Al cambiar de mapa o teletransportarte, los objetos del
  suelo no aparecían en pantalla.

- **El trabajo arranca también con un clic.** Con la caña, el hacha, el pico o las tijeras en la mano, el clic
  arranca la faena igual que el doble clic. Cambiar de herramienta o escribir /SALIR la corta, bajo techo no se
  pesca y picar donde no hay yacimiento no da mineral.

- **Reglas del original que faltaban.** Con el arco en la mano no se pega cuerpo a cuerpo; usar un objeto pide
  su nivel mínimo, la barca también; en la arena no se roba; y el que anda sin ropa no recupera energía, salvo
  montado.

- **La ropa de mujer es sólo para personajes mujer, y cada montura pide el nivel, la clase y la raza que dice su
  ficha**: el huargo, por ejemplo, es sólo para orcos. (Pedidos del tester: en el original ninguna de las dos
  cosas se controla.)

- **Las pociones de fuerza y agilidad duran lo que en el original**: una media hora las comunes y más de hora y
  media las de Alquimia; antes duraban horas. Los atributos no pasan de 35, y el panel te muestra el efecto y te
  lo marca cuando llegás al tope.

- **El pendiente del sacrificio te salva el inventario**: con él puesto no se te cae nada al morir, y se gasta una
  de sus tres cargas.

- **El mercader, como en el original.** Cada uno vende lo que tiene en el original, cada cosa en su ranura. Lo
  que no puede faltarle baja al comprarlo y se repone solo, y cuando se le vacía el mostrador lo vuelve a llenar.
  La llave de una casa se vende una sola vez, aunque se reinicie el servidor. Si otro compra en la tienda que
  tenés abierta lo ves al momento, y comprar o vender ya no llena la consola de mensajes.

- **El banco, como en el original.** El doble clic al banquero abre el panel del oro: cuánto tenés en la cuenta,
  depositar, retirar, transferir y el botón de la bóveda. La bóveda también se abre con /BOVEDA, y para depositar
  o retirar oro alcanza con haber hecho clic en el banquero.

- **El hogar se elige con el sacerdote.** Hacé clic en el sacerdote y pedilo, desde Acciones en el menú o con
  /HOGAR: te pregunta si querés que la ciudad donde estás sea tu hogar. Las neutrales las elige cualquiera;
  Ullathorpe, Nix y Banderbill, los ciudadanos y la Armada; Illiandor, Lindos y Suramei, los republicanos y la
  Milicia.

- **Las botellas se llenan en el agua que clickeás** y se toman: te sacan la sed y vuelven a quedar vacías.

- **Gritar y el chat global.** Empezá el mensaje con `-` para gritarle a todo el mapa, o con `;` para hablarle a
  todo el servidor. Hablar por el clan también sale sobre tu cabeza, para los de tu clan que te ven.

- **Al fantasma se lo puede pisar**: si caminás hacia un muerto, pasa a la baldosa de la que venís.

- **Los carteles del ingreso, como en el original.** Si el servidor está lleno, en mantenimiento o abierto sólo
  para GMs, te sale el cartel del original. Las contraseñas nuevas piden seis caracteres o más, y el código para
  recuperar la cuenta es un PIN de cuatro números, como allá.

- **La fogata se ve como fogata**: naranja y con forma de llama, en vez de un punto blanco, y los efectos de
  partículas —el fuego, los hechizos— tienen los colores del original. Además se apaga sola a los cinco minutos.
  (En el original no se apaga nunca; quien hostea cambia los minutos con `MinutosDeFogata`, y con 0 vuelve a ser
  así.)

- **Más sonidos en la pelea**: el arco suena al acertar y al errar, y pegarle a otro jugador suena y deja sangre.
  Y aguantar el hechizo de una criatura entrena la Resistencia mágica.

- **El juego anda más liviano.** Los gráficos se preparan fuera del dibujo, así que se traba menos al entrar a un
  mapa nuevo, y se sueltan los que hace rato que no se usan. En Opciones, Juego, podés ocultar la lluvia bajo
  techo y en las cuevas.

- **Para los GMs:** /TELEP, /IRA, /SUM, /ACC e /INVISIBLE funcionan como en el original. El GM invisible no se ve
  ni se escucha, y lo que dice lo leen sólo los GMs. Pausar el servidor frena sólo a las criaturas y no deja
  entrar a nadie. En el cliente web, Shift+clic en el mapa te lleva a esa baldosa.

- **Para el que hostea:** las tasas `Exp` y `Oro` multiplican directo —1 es lo normal, y el servidor avisa al
  arrancar si una parece de la escala vieja—, y el panel las muestra aparte de los eventos. El anticheat tiene
  memoria: cada trampa suma en la cuenta, a las tres se echa y a las seis se banea, y los GMs conectados las ven
  pasar. Quedan registrados los movimientos grandes de oro, y hay topes de personajes y de cuentas nuevas por IP.

- **Bots que juegan solos.** Vienen apagados; quien hostea los prende con `Bots=` y la cantidad. Cazan,
  comercian, fabrican con su oficio, suben de nivel y se desconectan cada tanto, se alejan de lo que los puede
  matar y de los guardias del otro bando, y no pueden hacer nada que un jugador no pueda. Salen en el /ONLINE
  como cualquiera.

## 1.0.0 — 2026-09-16

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
  perseguirte —se movían una baldosa por segundo contra las casi cinco tuyas— y en que pegaban más
  espaciado de lo que les corresponde.

- **Los bichos grandes tardan en volver.** Reaparecían todos a los cinco segundos, del más chico al más
  grande. Ahora los comunes tardan medio minuto, los que dan mucha experiencia cinco minutos, y los cuatro
  jefes del mundo media hora. El que hostea puede cambiar los tres números.

- **Pegar con la habilidad todavía verde rinde más.** El poder de ataque subía a los saltos: pasar de
  treinta a treinta y uno en una habilidad te daba de golpe lo que antes no te daba nada. Ahora sube
  parejo, igual que la evasión, que siempre lo hizo así.

## 1.0.0 — 2026-09-04

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
  la ranura y no del objeto, y a los mercaderes que nunca se quedan sin nada —el alquimista, el herrero— se
  les agotaba la mercadería.

- **Fabricar de más se rechaza en vez de recortarse.** Pedir cien flechas con material para diez te daba diez
  sin decir nada; ahora te avisa, como en el original. Y la herrería no tiene tope de cantidad.

- **Los comandos de administración aceptan nombres de dos palabras** (escribiéndolos con un `+` en el medio,
  como en el original) y la palabra `YO` para referirte a vos mismo.

- **La alineación del clan se mostraba mal**: las cuatro salían cambiadas.

## 1.0.0 — 2026-08-28

Esta tanda es casi toda **dibujo**: se cruzó el cliente entero contra el motor del juego original y se
corrigió todo lo que no coincidía. Son unos setenta y cinco arreglos; lo que más se nota:

- **El mundo se mueve a la velocidad de siempre.** Todo lo animado —la caminata, el oleaje, las antorchas, la
  lava, las cataratas— iba al doble de rápido, y algunas baldosas hasta seis veces. Por eso el muñeco parecía
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

- **Las puertas, los estandartes y las antorchas de pie tapan al que está detrás.** Antes el personaje —y su
  nombre— quedaban flotando por delante del frente de la casa. Lo mismo con los objetos tirados en el piso.

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

- **La fogata prendida arde.** Antes era idéntica a las ramitas apagadas —el mismo montón de leña— y sólo la
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
  arriba del animal se pega más fuerte. Ahora cada montura exige los puntos que pide en el juego original —de
  25 a 65 según cuál sea—, igual que los barcos piden Navegación.

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
  echar, declarar la guerra o firmar la paz — y no había forma de arreglarlo desde adentro del juego.

- **El servidor deja constancia de los intentos de trampa.** No había ninguno registrado: donde el juego
  original anota y corta, acá se recortaba el pedido en silencio y el que hostea no se enteraba nunca. Ahora
  quedan en el registro del servidor y se ven en el panel.

- **Repartir más puntos de habilidad de los que tenés corta la conexión**, y no aplica ninguno — es lo que
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
  preferís apretar una vez por golpe, se apaga en Opciones → Juego. Los oficios —minar, talar, pescar,
  fundir— siguen repitiéndose solos como siempre, prendas o no el macro.

- **Las metamorfosis del druida funcionan.** Los once hechizos (Murciélago, Lobo, Jabalí, Tigre, Oso Pardo,
  Tortuga, Araña, Demonio, Árbol, Gólem y Dragón) transforman al druida en la criatura, con los bonos al
  golpe y a la defensa que dice cada hechizo. **Y las criaturas más débiles que tu forma no te atacan**: con
  el murciélago te dejan de molestar las ratas, con el dragón te ignora todo salvo los doce bichos de tope.
  Por eso el Árbol vale aunque casi no pegue — es para pasar desapercibido. Dura **dos minutos**; morir o
  desconectarse la corta, y no se puede lanzar montado ni arriba de una barca. (En el juego original esos
  pergaminos se vendían hasta por 2.200.000 de oro y lanzarlos no hacía nada. Quien hostea cambia la duración
  con `MetamorfosisDelDruida`.)

- **Drop global: cualquier criatura puede soltar un accesorio de los buenos.** Es raro de verdad —del orden
  de uno por personaje en todo el camino hasta nivel 40—, así que sigue siendo un hallazgo y no una fuente;
  quien hostea lo cambia con `DropGlobal`. La lista de los veintiséis objetos y qué tan raro es cada uno son
  las del juego original, que traía la tabla hecha y nunca la usó.

## 1.0.0 — 2026-08-01

- **Cada raza usa la ropa que le corresponde.** El elfo, el humano, el drow y el orco son razas altas y visten
  igual; el enano y el gnomo son bajos y tienen su propia ropa. Antes el elfo y el gnomo estaban cambiados: al
  elfo sólo le entraba ropa de bajos.

- **Pasá el mouse por un objeto y te dice todo.** Qué es, cuánta defensa o daño da, qué nivel pide —y si
  todavía no lo alcanzás— y a qué clases y razas les está prohibido. También en la tienda, así te enterás
  ANTES de comprarlo.

- **Las ventanas del comercio y del banco son más anchas.** Entran cuatro objetos por fila en vez de dos, así
  se ve el stock del mercader y tu inventario de una sola mirada, sin tanto scroll.

- **Tres habilidades que no subían nunca ahora suben usándolas.** **Tácticas** la entrena el que RECIBE los
  golpes de una criatura —acierte o falle el bicho—, que es como se sube la evasión; **Supervivencia** ahora
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
  —Ent, Lobo, Tigre u Oso—. Al elegirlo te entra al libro el hechizo **Llamado al familiar**, que no vende
  nadie: lanzalo para sacarlo y lanzalo otra vez para guardarlo. Pelea con vos, gana la misma experiencia que
  vos mientras está afuera, sube de nivel y va aprendiendo habilidades — envenenar, paralizar, desarmar, cegar,
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
  no lo es —"estás muy lejos" del banquero, "primero hacé clic" estando al lado del mercader, no poder tirar el
  equipo de novato— te aparece una línea de contexto abajo, una sola vez por sesión. Y al equipar una
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
- Ya no se pierde lo que hiciste si otro programa toca la carpeta del servidor mientras jugás — el antivirus,
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
- Los hechizos que van sobre uno mismo —invisibilidad, las metamorfosis, Aura sagrada, Golpe certero,
  Inspiración, Honor del Paladín— se lanzan solos, sin pedir un objetivo.
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
