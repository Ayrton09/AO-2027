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
de Narnia y The Legend of Zelda). Se reemplazaron por la música del **Argentum Online** original y, donde el
original no tenía un lugar parecido, por temas de libre uso. Cuando un lugar tiene varios temas, se turnan
(`client-web/assets/music/listas.json`). Todo se normalizó en volumen (-18 LUFS) y se pasó a MP3.

### La música del Argentum Online original

Los temas `aoN.mp3` son el MIDI número N del cliente del Argentum Online
([AUDIO/MIDI](https://github.com/ao-libre/ao-cliente/tree/master/AUDIO/MIDI) de Argentum Online Libre). Cada uno
suena donde sonaba en el mundo original, según el `MusicNum` de los mapas del servidor
([Maps/MapaN.dat](https://github.com/ao-libre/ao-server/tree/268221cc/Maps)). Los mapas 1 a 76 son los mismos del
mundo original y llevan su tema mapa por mapa; el resto, por lugar. Se pasaron de MIDI a MP3 con
[FluidSynth](https://www.fluidsynth.org) y el banco de sonidos [GeneralUser GS](https://github.com/mrbumpy409/GeneralUser-GS)
de S. Christian Collins (uso libre). Ninguna fuente nombra a su compositor.

| Archivo | Tema del original | Dónde suena |
| --- | --- | --- |
| `ao4.mp3` | Ullathorpe | Ullathorpe |
| `ao5.mp3` | Nix | Nix |
| `ao13.mp3` | Banderbill | Banderbill y Banderbill Central |
| `ao12.mp3` | Banderbill Norte y su muelle | Distrito Real y Puerto de Banderbill; se turna en los puertos |
| `ao11.mp3` | Suburbios de Banderbill | Barrios Bajos y Terrenos de Banderbill |
| `ao16.mp3` | Lindos | Lindos y su abadía |
| `ao68.mp3` | Arghâl | Arghâl y su puerto |
| `ao19.mp3` | Isla Nueva Esperanza | Nueva Esperanza y su puerto |
| `ao3.mp3`, `ao98.mp3` | Bosques de Ullathorpe, Nix y Dorck; Bosque Central | Bosques, senderos y campos |
| `ao20.mp3` | Fuerte Pretoriano y bosques de Banderbill | Los fuertes y los Bosques de Banderbill |
| `ao67.mp3`, `ao53.mp3`, `ao117.mp3` | Bosque Élfico, Paraíso y Nubal | Bosques de Suramei e Illiandor y el Sendero del Este; `ao53` y `ao117` también en Suramei e Illiandor |
| `ao10.mp3` | Desierto | Rinkel y su desierto, Isla Beleta, Intermundia |
| `ao58.mp3` | Dungeon Inferno | El Infierno |
| `ao7.mp3` | Dungeon Newbie | Dungeon Newbie |
| `ao9.mp3` | Minas de Hierro y Catacumbas | Minas, catacumbas y el Sendero del Norte; se turna en las mazmorras |
| `ao15.mp3` | Dungeon Marabel | Dungeon Marabel; se turna en las mazmorras |
| `ao97.mp3` | Ruinas de Earost | Se turna en las mazmorras |
| `ao18.mp3` | Dungeon Veriil | Dungeon Veriil y su fuerte |
| `ao14.mp3` | Dungeon Dragon y Prisión | Dungeon Dragon y la Cárcel del Imperio |
| `ao110.mp3` | Interior de la Montaña | Las mazmorras heladas |
| `ao28.mp3` | Mapa de GMs (los rings) | Las arenas |
| `ao105.mp3` | Cementerio | Cementerio de Nix; se turna en el Bosque Muerto |
| `ao59.mp3` | Interior de Cementerio (Mausoleo) | El Bosque Muerto |
| `ao69.mp3`, `ao112.mp3`, `ao62.mp3` | Mar Occidental, Mar Austral y Mar al norte del desierto | Se turnan en el océano |
| `ao17.mp3`, `ao116.mp3` | Mar e Isla Pirata | Se turnan en el océano y en costas, ríos e islas; `ao17` también en Isla Morgolock |
| `ao56.mp3` | Isla Otto | Costas, ríos, lagos e islas |
| `ao81.mp3` | Arkhein | Los puertos |

### Temas de libre uso

Donde el original no tenía un lugar parecido (las tierras heladas y los orcos), suenan temas publicados por sus
autores en [OpenGameArt](https://opengameart.org).

| Archivo | Tema | Autor | Licencia | Dónde suena |
| --- | --- | --- | --- | --- |
| `7.mp3` | [Land of Misdeeds](https://opengameart.org/content/land-of-misdeeds-rpg-orchestral-essentials-evil-music) | Jonathan Shaw | CC-BY 3.0 | Orac, el campamento orco |
| `38.mp3` | [Encounter of Misdeeds](https://opengameart.org/content/encounter-of-misdeeds-rpg-orchestral-essentials-villain-music) | Jonathan Shaw | CC-BY 3.0 | Orac, el campamento orco |
| `18.mp3` | [Morning Frost](https://opengameart.org/content/morning-frost-rpg-orchestral-essentials-village-music) | Jonathan Shaw | CC-BY 3.0 | Tiama, la tundra y los mares helados |
| `36.mp3` | [Snowland](https://opengameart.org/content/snowland-town) | Matthew Pablo (www.matthewpablo.com) | CC-BY 3.0 | Tiama, la tundra y los mares helados |
| `37.mp3` | [Snowfall](https://opengameart.org/content/snowfall) | Kistol | CC0 | Tiama, la tundra y los mares helados |

Los temas de Jonathan Shaw son de la colección
[RPG Orchestral Essentials](https://opengameart.org/content/rpg-orchestral-essentials-legacy): Music by
**Jonathan Shaw** ([www.jshaw.co.uk](https://www.jshaw.co.uk)). Licencias:
[CC-BY 3.0](https://creativecommons.org/licenses/by/3.0/), [CC0](https://creativecommons.org/publicdomain/zero/1.0/).

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
