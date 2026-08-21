# Los sonidos, y el único que NO es el del install

Estos `.wav` salen del `Sounds.cao` de `CoverAO-2021`, que es el paquete que lee el cliente original. El
extractor es `fase0/transcode/decode_sounds.py` y la lista de cuáles hacen falta se **deriva de la data** —el
`WAV` de cada hechizo, los `Snd1/2/3` de cada criatura y de cada objeto, las constantes del server y los pasos
que elige el cliente—, no se escribe a mano. Lo comprueba `client-web/check-assets.mjs` en cada `npm test`.

## La excepción: el 20 (Resucitar)

**`20.wav` es una divergencia elegida, no un descuido.** No lo cambies "para volver a lo fiel" sin leer esto.

El install de CoverAO-2021 trae para *Resucitar* un clip de **0,73 s** en 44 kHz estéreo. Es corto y seco, y a
quien conoce el juego le suena mal: el sonido del Argentum clásico es un **coro de ~5 s** en 11 kHz mono, el
formato de la librería original de los 90. O sea que **el fork que estamos portando ya se había apartado del AO
clásico en este sonido**, y acá se eligió volver al clásico.

Verificado antes de tocarlo, en cuatro fuentes:

| | |
|---|---|
| `Servidor/Dat/Hechizos.dat` | `WAV=20` para Resucitar |
| nuestro `Hechizos.json` | `WAV: "20"` — coincide |
| `Herramientas/.../Descomprimido/Sounds/20.wav` | idéntico al que teníamos |
| **`Cliente/Recursos/Sounds.cao`** (lo que el cliente REAL carga) | también idéntico |

O sea que el archivo que teníamos era, sin dudas, el de este install. Lo que cambió es la decisión: el juego es
del autor y prefirió el sonido clásico.

**No toca el protocolo ni la paridad.** El server sigue mandando `PlayWave(20)`; lo único distinto es qué
reproduce el cliente al recibirlo. El oráculo VB6 no ve ninguna diferencia.

## Si alguien quiere revisar el resto

De los 181 `.wav`, **157 son byte por byte iguales** a la librería del AO clásico y sólo **24 difieren**:

    1, 2, 4, 10, 17, 25, 32, 49, 92, 124, 129, 205, 208, 299, 300, 301, 305, 329, 330, 331, 346, 350, 351, 352

Los ids altos (299+) son casi seguro contenido propio de CoverAO y no tienen equivalente clásico. Los bajos
valdría escucharlos uno por uno antes de decidir: **que un archivo esté en 44 kHz no prueba que esté mal**, así
que la comparación es de oído, no de formato.
