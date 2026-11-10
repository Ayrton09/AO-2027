# Créditos y procedencia

AO 2027 es un **port** de un juego que ya existía, y una parte grande de lo que se
distribuye (gráficos, sonidos, música, mapas, la tabla de objetos y de hechizos) **no la hizo este proyecto**.
Este archivo dice qué viene de dónde, para que quien reciba una copia lo sepa sin tener que deducirlo.

## De dónde sale el juego

**CoverAO-2021**, la variante de Argentum Online que este repo replica. Su propio `README.md` la describe como *"un Alkon 13.0 dx7, pasado a dx8"*,
publicada por su autor en GitHub bajo la organización **Comunidad-Winter**, y agradece por nombre a quienes
trabajaron en esa versión: Grihilwen, Blandix, Juzenav, Merhak, Avk, shermie80, y a los GMs Valkyria, Eurora
y Avk. El texto de ese README es la única declaración de intención que acompaña al código, y dice que la
publicación fue deliberada.

**Argentum Online**, más atrás, es el juego del que desciende toda esta línea (incluido el motor, el
protocolo por el que hablan cliente y servidor, y buena parte de los nombres que aparecen en el código
(`UserList`, `ConnectUser`, `eSkill`, `LocaleMsg`)), obra de sus autores originales y de dos décadas de
comunidad que lo forkeó, lo tradujo y lo mantuvo vivo.

## Agradecimiento especial

**Francotirador**, que tuvo la idea de este proyecto y lo banca para que siga adelante.

## Qué aporta este repositorio

Lo que sí es trabajo propio de este port:

- El **servidor en C#/.NET 10**, reescrito desde cero hablando el protocolo AO byte a byte.
- El **cliente web** en TypeScript sobre canvas: el original es un cliente VB6/DirectX8.
- La **conversión de los datos y los assets** de los formatos binarios del install original a JSON, PNG,
  WAV y MP3 abiertos.

## Los assets

Los gráficos, los sonidos, la música y todo `gamedata/` son **el contenido del
CoverAO original**, decodificado a formatos abiertos. Cambió el envase, no la obra: los dibujó, los grabó y
los diseñó otra gente. Redistribuirlos es redistribuir su trabajo. La excepción es parte de la música, que se
reemplazó (ver abajo).

## La música

La música de los mapas que traía el CoverAO original incluía temas de bandas sonoras comerciales (Las Crónicas
de Narnia y The Legend of Zelda). Se reemplazaron por la música del Argentum Online original (más abajo) y por temas de libre uso, publicados por sus autores en
[OpenGameArt](https://opengameart.org). Cada lugar tiene una lista de temas que se turnan
(`client-web/assets/music/listas.json`). Todos se normalizaron en volumen (-18 LUFS) y se pasaron a MP3; no se
cambió nada más.

| Archivo | Tema | Autor | Licencia | Dónde suena |
| --- | --- | --- | --- | --- |
| `2.mp3` | [The World At Your Door](https://opengameart.org/content/the-world-at-your-door-rpg-orchestral-essentials-overworld-music) | Jonathan Shaw | CC-BY 3.0 | Bosques, senderos y campos |
| `27.mp3` | [Green Plains](https://opengameart.org/content/green-plains) | tcarisland | CC-BY 4.0 | Bosques, senderos y campos |
| `28.mp3` | [Forest](https://opengameart.org/content/forest) | syncopika | CC-BY 3.0 | Bosques, senderos y campos |
| `4.mp3` | [In Tranquil Spring](https://opengameart.org/content/in-tranquil-spring-rpg-orchestral-essentials) | Jonathan Shaw | CC-BY 3.0 | Bosques de Suramei e Illiandor, Sendero del Este |
| `29.mp3` | [Sylvan Waltz](https://opengameart.org/content/sylvan-waltz-standard-looped-version) | Otto Halmén | CC-BY 3.0 | Bosques de Suramei e Illiandor, Sendero del Este |
| `30.mp3` | [The Enchanted Forest of Min](https://opengameart.org/content/rpg-the-enchanted-forest-of-min) | HitCtrl | CC-BY 3.0 | Bosques de Suramei e Illiandor, Sendero del Este |
| `9.mp3` | [A Tale of Wind](https://opengameart.org/content/a-tale-of-wind-and-water-rpg-orchestral-essentials-peace-music) | Jonathan Shaw | CC-BY 3.0 | Océano abierto |
| `21.mp3` | [Exploring the Ocean](https://opengameart.org/content/ocean-music-pack) | Leonardo Paz | CC-BY 4.0 | Océano abierto |
| `22.mp3` | [Brave New Ocean](https://opengameart.org/content/ocean-theme) | Wolfgang_ | CC-BY 4.0 | Océano abierto |
| `23.mp3` | [A Dream of First Flight](https://opengameart.org/content/a-dream-of-first-flight-rpg-orchestral-essentials-title-music) | Jonathan Shaw | CC-BY 3.0 | Océano abierto |
| `19.mp3` | [A Tale of Water](https://opengameart.org/content/a-tale-of-wind-and-water-rpg-orchestral-essentials-peace-music) | Jonathan Shaw | CC-BY 3.0 | Costas, ríos, lagos e islas |
| `24.mp3` | [Island Cove](https://opengameart.org/content/island-cove) | Bobjt, sobre "Town hayfields" de tridentkeep | CC-BY 3.0 | Costas, ríos, lagos e islas |
| `25.mp3` | [Seaside Village](https://opengameart.org/content/ocean-music-pack) | Leonardo Paz | CC-BY 4.0 | Costas, ríos, lagos e islas |
| `26.mp3` | [Memories of a Lost Autumn](https://opengameart.org/content/memories-of-a-lost-autumn-rpg-orchestral-essentials-conversation-music) | Jonathan Shaw | CC-BY 3.0 | Costas, ríos, lagos e islas |
| `5.mp3` | [Market on the Sea](https://opengameart.org/content/market-on-the-sea-rpg-orchestral-essentials-village-music) | Jonathan Shaw | CC-BY 3.0 | Arghâl, los Barrios Bajos y los puertos |
| `34.mp3` | [Medieval Town](https://opengameart.org/content/medieval-town) | syncopika | CC-BY 3.0 | Arghâl, los Barrios Bajos y los puertos |
| `6.mp3` | [A Tale of Peace](https://opengameart.org/content/a-tale-of-peace-rpg-orchestral-essentials-peace-music) | Jonathan Shaw | CC-BY 3.0 | Suramei e Illiandor |
| `35.mp3` | [Woodland Fantasy](https://opengameart.org/content/woodland-fantasy) | Matthew Pablo (www.matthewpablo.com) | CC-BY 3.0 | Suramei e Illiandor |
| `8.mp3` | [The Tread of War](https://opengameart.org/content/the-tread-of-war-rpg-orchestral-essentials-military-music) | Jonathan Shaw | CC-BY 3.0 | Los fuertes |
| `7.mp3` | [Land of Misdeeds](https://opengameart.org/content/land-of-misdeeds-rpg-orchestral-essentials-evil-music) | Jonathan Shaw | CC-BY 3.0 | Orac, el campamento orco, las arenas |
| `38.mp3` | [Encounter of Misdeeds](https://opengameart.org/content/encounter-of-misdeeds-rpg-orchestral-essentials-villain-music) | Jonathan Shaw | CC-BY 3.0 | Orac, el campamento orco, las arenas |
| `20.mp3` | [A Bitter Hope](https://opengameart.org/content/a-bitter-hope-rpg-orchestral-essentials-dungeon-music) | Jonathan Shaw | CC-BY 3.0 | El Bosque Muerto |
| `33.mp3` | [The Graveyard](https://opengameart.org/content/rpg-the-graveyard) | HitCtrl | CC-BY 3.0 | El Bosque Muerto |
| `3.mp3` | [Treading The Unknown](https://opengameart.org/content/treading-the-unknown-rpg-orchestral-essentials) | Jonathan Shaw | CC-BY 3.0 | Mazmorras, cuevas y minas |
| `31.mp3` | [Midnight Creeping](https://opengameart.org/content/midnight-creeping-rpg-orchestral-essentials-stealth-music) | Jonathan Shaw | CC-BY 3.0 | Mazmorras, cuevas y minas |
| `32.mp3` | [RPG Ambience - Dungeon](https://opengameart.org/content/rpg-ambience-dungeon) | HitCtrl | CC-BY 3.0 | Mazmorras, cuevas y minas |
| `17.mp3` | [Cold Sanctuary](https://opengameart.org/content/cold-sanctuary-rpg-orchestral-essentials-dungeon-music) | Jonathan Shaw | CC-BY 3.0 | Mazmorras heladas |
| `18.mp3` | [Morning Frost](https://opengameart.org/content/morning-frost-rpg-orchestral-essentials-village-music) | Jonathan Shaw | CC-BY 3.0 | Tiama, la tundra y los mares helados |
| `36.mp3` | [Snowland](https://opengameart.org/content/snowland-town) | Matthew Pablo (www.matthewpablo.com) | CC-BY 3.0 | Tiama, la tundra y los mares helados |
| `37.mp3` | [Snowfall](https://opengameart.org/content/snowfall) | Kistol | CC0 | Tiama, la tundra y los mares helados |
| `16.mp3` | [Heated Lands](https://opengameart.org/content/heated-lands-rpg-orchestral-essentials-dungeon-music) | Jonathan Shaw | CC-BY 3.0 | Rinkel, su desierto y el Infierno |

Los temas de Jonathan Shaw son de la colección
[RPG Orchestral Essentials](https://opengameart.org/content/rpg-orchestral-essentials-legacy): Music by
**Jonathan Shaw** ([www.jshaw.co.uk](https://www.jshaw.co.uk)). Licencias:
[CC-BY 3.0](https://creativecommons.org/licenses/by/3.0/), [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/),
[CC0](https://creativecommons.org/publicdomain/zero/1.0/).

### La música del Argentum Online original

Los temas `aoN.mp3` son la música del **Argentum Online** original: el MIDI número N del cliente
([AUDIO/MIDI](https://github.com/ao-libre/ao-cliente/tree/master/AUDIO/MIDI) de Argentum Online Libre), en el lugar
donde sonaba en el mundo original (`Mundos/Alkon/MapaN.dat` del servidor de Argentum Online Libre). Se pasaron de
MIDI a MP3 con [FluidSynth](https://www.fluidsynth.org) y el banco de sonidos
[GeneralUser GS](https://github.com/mrbumpy409/GeneralUser-GS) de S. Christian Collins (uso libre), y se
normalizaron a -18 LUFS. Ninguna fuente nombra a su compositor.

| Archivo | Dónde suena |
| --- | --- |
| `ao4.mp3` | Ullathorpe |
| `ao5.mp3` | Nix |
| `ao13.mp3` | Banderbill y el Distrito Real |
| `ao20.mp3` | Terrenos de Banderbill y los fuertes; también se turna en bosques y campos |
| `ao12.mp3` | Puerto de Banderbill; también se turna en los puertos |
| `ao16.mp3` | Lindos y su abadía |
| `ao19.mp3` | Nueva Esperanza y su puerto |
| `ao105.mp3` | Cementerio de Nix |
| `ao14.mp3` | Dungeon Dragon y la Cárcel del Imperio |
| `ao7.mp3` | Dungeon Newbie |
| `ao18.mp3` | Dungeon Veriil y su fuerte |
| `ao15.mp3` | Dungeon Marabel; también se turna en las mazmorras |
| `ao9.mp3` | Minas y catacumbas; también se turna en las mazmorras |
| `ao3.mp3`, `ao11.mp3`, `ao108.mp3`, `ao126.mp3` | Se turnan en bosques, senderos y campos |
| `ao81.mp3` | Se turna en las mazmorras |
| `ao17.mp3`, `ao116.mp3` | Se turnan en el océano y en costas, ríos e islas |
| `ao56.mp3` | Se turna en el océano |
| `ao98.mp3` | Se turna en costas, ríos e islas |
| `ao10.mp3` | Se turna en Rinkel, su desierto y el Infierno |
| `ao110.mp3` | Se turna en Tiama, la tundra y los mares helados |
| `ao85.mp3` | Se turna en las mazmorras heladas |
| `ao50.mp3` | Se turna en el Bosque Muerto |

`12.mp3` a `15.mp3` y `62.mp3` vienen del CoverAO original. La música del menú y del ingreso (`login.mp3`) es
"Argentum Online Intro", de **Ricardo Amil** (2001), la del juego original.

## La licencia del material heredado

El repositorio de CoverAO-2021 en GitHub **no declara ninguna licencia**: no hay archivo `LICENSE` ni mención
de términos, sólo el enlace al hilo de la "liberación". Pero eso no es el final de la historia, porque **el
aviso de licencia está en los propios archivos fuente**, que es donde manda. De los 79 archivos del servidor
VB6, **48 abren con este encabezado**, textual:

```
'Argentum Online 0.12.2
'Copyright (C) 2002 Márquez Pablo Ignacio
'
'This program is free software; you can redistribute it and/or modify
'it under the terms of the Affero General Public License;
'either version 1 of the License, or any later version.
```

El cliente VB6 lo repite en 31 de sus 104 archivos. Hay un solo desviado, `Comercio.bas`, que cita la **GNU
GPL versión 2 "or any later version"**: una contribución de otra procedencia.

Tres consecuencias, y la tercera es la que importa para este proyecto:

1. **La línea es copyleft, no propietaria.** El "or any later version" del encabezado permite adoptar
   versiones posteriores; el árbol moderno de la misma línea (`ao-libre/ao-server`) está publicado bajo
   **AGPL-3.0**.
2. **La ausencia de `LICENSE` en el repo de CoverAO no revoca nada**: el aviso por archivo sigue vigente, y
   quitarlo o ignorarlo no cambia los términos bajo los que ese código llegó.
3. **La AGPL tiene cláusula de red**, y este proyecto es exactamente el caso que esa cláusula contempla: un
   **servidor de juego**. Bajo AGPL, a quien interactúa con el servidor por la red hay que ofrecerle el
   código fuente de la versión que está corriendo. Hostear CoverAO públicamente no es "uso interno".

## La decisión: 2026-08-02

**Este port se publica bajo [AGPL-3.0](LICENSE)**, que es la licencia libre coherente con la evidencia de arriba.
No es una elección entre varias licencias libres: el "or any later version" del encabezado Affero permite
subir de versión, pero **no** permite bajar a una permisiva como MIT o BSD. El copyleft viene con el material.

Lo que eso implica para quien hostee esto, y es la parte que suele sorprender: **la AGPL tiene cláusula de
red**. A los jugadores que se conecten a tu servidor les tenés que ofrecer el código de la versión que estás
corriendo, aunque nunca les des un binario. Un servidor público de este juego no es "uso interno".

Se conservan intactos los encabezados de copyright del árbol VB6 (`CoverAO-2021/`): la licencia nueva se suma,
no los reemplaza.

**Sobre [`aoweb`](https://github.com/dcatanzaro/aoweb)**, que se usó como referencia de contraste para el
cliente: ese repositorio **no declara licencia**. Por eso no se copió ni una línea suya: lo que salió de ahí
fueron *pistas* sobre dónde mirar en el VB6 (el `velocidadMontando`, el margen de culling), y lo que se portó
se portó desde el VB6 original, citando el archivo y la línea. La licencia de este port no depende de la suya.

Queda igual una pregunta que el código no contesta: **los assets** (gráficos, sonidos, música, mapas) no
llevan encabezado de licencia: el copyleft del código no dice nada sobre ellos, y parte del arte viene, según
los propios encabezados, del *ORE de Baronsoft*. La AGPL cubre el **código** de este repositorio; el material
heredado de Argentum Online sigue con la situación que tenía.

Fuentes: el árbol VB6 local (`CoverAO-2021/Servidor/Codigo/*.bas`),
[Comunidad-Winter/CoverAO](https://github.com/Comunidad-Winter/CoverAO),
[ao-libre/ao-server (LICENSE)](https://github.com/ao-libre/ao-server/blob/master/LICENSE),
[entrada de Argentum Online en opensourcegames](https://github.com/Trilarion/opensourcegames/blob/master/entries/argentum_online.md).
