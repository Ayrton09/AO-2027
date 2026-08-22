# Novedades de CoverAO

Lo que cambia en cada versión, escrito para el jugador. El juego las muestra en Menú → Novedades.

## 1.0.0 — 2026-08-01

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
