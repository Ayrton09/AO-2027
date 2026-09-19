"""Recorta de cada hoja HD el margen que ningún sprite usa.

Las hojas del original venían rellenadas hasta una potencia de dos —así las quería DirectDraw— y el
remasterizado ×4 multiplica ese relleno por DIECISÉIS. Lo que pesa una hoja en memoria no es el `.png` sino
la imagen decodificada (ancho × alto × 4): `73.png` son 3,9 MB en disco y 256 MB en RAM, con el atlas usando
sólo hasta 6144×6144 de sus 8192×8192.

Recortar no cambia un píxel de lo que se dibuja: `grh.json` queda igual (las coordenadas se cuentan desde
arriba a la izquierda) y lo que se saca es transparente. Eso NO se asume: cada hoja se mira antes y, si
afuera de la caja hay un solo píxel con alfa, se la deja como está y se avisa.

Queda un art-píxel (HD) de margen: el sprite que toca el borde se suaviza contra lo que haya del otro lado,
y eso tiene que seguir siendo vacío.

Uso:
    python recortar.py            # recorta en su lugar
    python recortar.py --ver      # sólo dice cuánto se ahorraría
"""
import io
import json
import os
import sys

from PIL import Image

HD = 4
AQUI = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.normpath(os.path.join(AQUI, "..", "..", "assets"))
GFX = os.path.join(ASSETS, "graficos")


def cajas():
    """file -> (ancho, alto) que el atlas usa de verdad, en píxeles de la hoja."""
    atlas = json.load(io.open(os.path.join(ASSETS, "grh.json"), encoding="utf-8"))["grh"]
    caja = {}
    for g in atlas.values():
        f = g.get("file")
        if f is None:
            continue
        w, h = caja.get(f, (0, 0))
        caja[f] = (max(w, (g["x"] + g["w"]) * HD), max(h, (g["y"] + g["h"]) * HD))
    return caja


def main():
    soloVer = "--ver" in sys.argv
    caja = cajas()
    antes = despues = 0
    recortadas = dejadas = 0
    for n in sorted(caja):
        p = os.path.join(GFX, "%d.png" % n)
        if not os.path.exists(p):
            continue
        im = Image.open(p)
        w, h = im.size
        cw = min(w, caja[n][0] + HD)
        ch = min(h, caja[n][1] + HD)
        antes += w * h * 4
        if cw >= w and ch >= h:
            despues += w * h * 4
            continue
        im = im.convert("RGBA")
        alfa = im.getchannel("A")
        # Lo que se va tiene que estar vacío: la franja de la derecha y la de abajo.
        sobra = 0
        if cw < w:
            sobra = max(sobra, alfa.crop((cw, 0, w, h)).getextrema()[1])
        if ch < h:
            sobra = max(sobra, alfa.crop((0, ch, cw, h)).getextrema()[1])
        if sobra:
            print("  %5d: NO se recorta, afuera de la caja hay alfa %d" % (n, sobra))
            despues += w * h * 4
            dejadas += 1
            continue
        despues += cw * ch * 4
        recortadas += 1
        if not soloVer:
            im.crop((0, 0, cw, ch)).save(p, "PNG", optimize=True)
    print("hojas recortadas: %d, dejadas como estaban: %d" % (recortadas, dejadas))
    print("memoria decodificada: %.2f GB -> %.2f GB" % (antes / 2 ** 30, despues / 2 ** 30))
    if soloVer:
        print("(--ver: no se escribió nada)")


if __name__ == "__main__":
    main()
