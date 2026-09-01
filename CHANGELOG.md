# Novedades de CoverAO

Lo que cambia en cada versión, escrito para el jugador. El juego las muestra en Menú → Novedades.

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
