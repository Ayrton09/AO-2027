# Pipeline de sprites HD (Scale2x ×4)

Convierte las hojas de arte de AO (`.bmp`, pixel-art de baja resolución) a **PNG remasterizado ×4**
con **Scale2x** (EPX aplicado dos veces): redondea el escalonado diagonal sin blur, así a 1080p el
mundo se ve nítido y "remasterizado" en vez de bloques. El **colorkey de AO (negro → transparente)**
queda horneado en el canal alpha del PNG, así el cliente ya no necesita el pase `getImageData` al cargar.

Sin dependencias externas: decodificador BMP propio (8/16/24/32 bpp) + encoder PNG con el `zlib` de Node.

## Archivos

| archivo | qué hace |
|---|---|
| `bmp.cjs` | decodifica BMP → RGBA (8/16/24/32 bpp, top-down y bottom-up) + colorkey |
| `scale2x.cjs` | Scale2x/EPX sobre RGBA; `scale4x` = aplicarlo dos veces |
| `png.cjs` | encoder PNG RGBA (filtro adaptativo + `zlib.deflateSync`) |
| `build.cjs` | pipeline: por-sprite (no sangra entre sprites del atlas), reconstruye cada hoja a ×4 |
| `measure.cjs` | mide el tamaño estimado del set completo en ×2/×4 |
| `validate.cjs` | valida el decodificador contra el del navegador (pixel a pixel) |

## Regenerar todas las hojas

```bash
node build.cjs all        # procesa todas las hojas referenciadas por grh.json → assets/graficos/*.png
```

`build.cjs subset` procesa solo el mapa 1 + entidades (test rápido). El destino por defecto es
`assets/graficos/` (al lado de la fuente); pasá otro dir como 2º argumento para no pisar.

## Integración en el cliente

`grh.json` **no cambia** (queda en unidades lógicas). El cliente define `const HD = 4` y multiplica el
rectángulo **origen** del `drawImage` por `HD` (helper `dGrh()` en `index.html`); el destino sigue en
píxeles de tile, así toda la matemática de posicionamiento (anclajes, offsets de cabeza) queda intacta.
El mundo dibuja con `imageSmoothingEnabled = true` para suavizar el downscale HD→backing cuando `SS < 4`.

## Fuente de los BMP

Los `.bmp` originales **se quitaron del árbol** al pasar a PNG (clone-and-run: el runtime solo necesita
los PNG). Si hace falta regenerar, los BMP se recuperan del historial git (`git show <commit>:client-web/assets/graficos/N.bmp`)
o del árbol `CoverAO-2021/` (gitignoreado). El mapeo de coordenadas es 1:1: una región `(x,y,w,h)` del
atlas vive en el PNG en `(x·4, y·4, w·4, h·4)`.
