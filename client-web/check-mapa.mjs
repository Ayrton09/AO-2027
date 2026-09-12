// LA PANTALLA DEL MAPA NO PUEDE MANDAR NADA, Y SU GRILLA TIENE QUE CAER ADENTRO DE LA LÁMINA.
//
// El ítem mapa es de tipo ESCUDO en el .dat, así que el ruteo del doble clic lo manda a `EquipItem`; la
// pantalla se abre interceptando ANTES de esa decisión. Si alguien mueve la intercepción abajo del envío, el
// server contesta "No podés usar ese objeto" —un mensaje que el VB6 no emite en ese camino— y la divergencia
// no la caza ningún flow, porque del lado del server no cambia nada: es el CLIENTE el que no tiene que hablar.
//
// Y la grilla: los casilleros salen de `table.ind` del cliente VB6 y se dibujan con la fórmula de
// `frmMap.SetMapPoint` sobre un recorte de 800x600. Un casillero que caiga afuera dibuja el punto en el
// borde o directamente no se ve, sin ningún error.
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const RAIZ = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const INDEX = join(RAIZ, 'client-web/index.html');
const ASSETS = join(RAIZ, 'client-web/assets');
const html = readFileSync(INDEX, 'utf8');
const problemas = [];

// --- 1. El ítem mapa se atiende en el cliente, antes de cualquier envío ----------------------------------
const cuerpo = /function usarItemDelSlot\(slot\) \{([\s\S]*?)\n {4}\}/.exec(html);
if (!cuerpo) {
  problemas.push('no encontré `usarItemDelSlot` en index.html (¿lo renombraron?)');
} else {
  const dentro = cuerpo[1];
  const abre = dentro.indexOf('abrirMapaDelMundo()');
  const manda = dentro.indexOf('ws.send(');
  if (abre < 0) problemas.push('el doble clic en el ítem mapa NO abre la pantalla: `usarItemDelSlot` no la llama');
  else if (manda >= 0 && abre > manda) problemas.push('la pantalla del mapa se abre DESPUÉS del envío: el server recibe un paquete que el VB6 no contesta');
  else if (!/ITEMS_MAPA\.has\([^)]*\)\s*\)\s*\{[^}]*return;/.test(dentro))
    problemas.push('la rama del ítem mapa no corta con `return`: se abre la pantalla Y se manda el paquete');
}

// --- 2. Los ítems interceptados existen y son el mapa ----------------------------------------------------
const listaItems = /const ITEMS_MAPA = new Set\(\[([^\]]*)\]\)/.exec(html);
const items = JSON.parse(readFileSync(join(ASSETS, 'items.json'), 'utf8'));
if (!listaItems) problemas.push('no encontré `ITEMS_MAPA` en index.html');
else {
  for (const id of listaItems[1].split(',').map((x) => x.trim()).filter(Boolean)) {
    const it = items[id];
    if (!it) problemas.push(`ITEMS_MAPA nombra el objeto ${id}, que no está en items.json`);
    else if (!/^Mapa\b/i.test(String(it[1]))) problemas.push(`el objeto ${id} de ITEMS_MAPA se llama "${it[1]}", no es un mapa`);
  }
  const mapas = Object.entries(items).filter(([, v]) => /^Mapa\b/i.test(String(v[1]))).map(([k]) => k);
  for (const id of mapas) {
    if (!listaItems[1].includes(id)) problemas.push(`el objeto ${id} ("${items[id][1]}") es un mapa y ITEMS_MAPA no lo tiene: ese no abre nada`);
  }
}

// --- 3. La lámina que se dibuja ---------------------------------------------------------------------------
const hoja = /const HOJA_DEL_MAPA = (\d+)/.exec(html);
if (!hoja) problemas.push('no encontré `HOJA_DEL_MAPA` en index.html');
else if (!existsSync(join(ASSETS, 'graficos', `${hoja[1]}.png`)))
  problemas.push(`la pantalla dibuja la hoja ${hoja[1]} y assets/graficos/${hoja[1]}.png no está`);

// --- 4. La grilla: tamaño, mapas reales y todos los puntos adentro de la lámina ---------------------------
const rutaTabla = join(ASSETS, 'maptable.json');
if (!existsSync(rutaTabla)) {
  problemas.push('falta assets/maptable.json (lo genera fase0/transcode/decode_maptable.py)');
} else {
  const t = JSON.parse(readFileSync(rutaTabla, 'utf8'));
  if (t.filas !== t.grilla.length || t.grilla.some((f) => f.length !== t.columnas))
    problemas.push(`maptable.json dice ${t.columnas}x${t.filas} y la grilla no mide eso`);
  // Se mide el CENTRO del punto, no el círculo entero: el original deja que el casillero de la última columna
  // pise el borde —`shpMap` arranca en 793 y mide 9— y eso se ve igual. Lo que no se ve es un centro afuera.
  const ANCHO = 800, ALTO = 600;
  let afuera = 0, pegados = 0, sinMapa = 0;
  for (let fila = 1; fila <= t.filas; fila++) {
    for (let col = 1; col <= t.columnas; col++) {
      const n = t.grilla[fila - 1][col - 1];
      if (n > 0 && !existsSync(join(ASSETS, `map${n}.json`))) sinMapa++;
      const x = (col - 1) * t.anchoCasillero + t.izquierda + 4;
      const y = (fila - 1) * t.altoCasillero + t.arriba + 4;
      if (x < 0 || x >= ANCHO || y < 0 || y >= ALTO) afuera++;
      else if (x + 7 > ANCHO || y + 7 > ALTO) pegados++;
    }
  }
  if (sinMapa) problemas.push(`${sinMapa} casilleros de la grilla nombran un mapa que no está en assets/`);
  if (afuera) problemas.push(`${afuera} casilleros caen afuera del recorte de ${ANCHO}x${ALTO} de la lámina`);
  if (pegados) console.log(`check-mapa: ${pegados} casilleros quedan pegados al borde y el punto se ve cortado, igual que en el original`);
}

// --- 5. El índice de zonas: todo casillero tiene ficha, y toda criatura y cueva se pueden nombrar ---------
const rutaZonas = join(ASSETS, 'mapa-zonas.json');
if (!existsSync(rutaZonas)) {
  problemas.push('falta assets/mapa-zonas.json (lo arma fase0/transcode/build_mapa_zonas.py)');
} else if (existsSync(rutaTabla)) {
  const zonas = JSON.parse(readFileSync(rutaZonas, 'utf8'));
  const npcs = JSON.parse(readFileSync(join(ASSETS, 'npcs.json'), 'utf8'));
  const t = JSON.parse(readFileSync(rutaTabla, 'utf8'));
  const sinFicha = new Set(), sinNombre = new Set(), cuevaSuelta = new Set();
  for (const fila of t.grilla) for (const n of fila) if (n > 0 && !zonas[n]) sinFicha.add(n);
  for (const [n, z] of Object.entries(zonas)) {
    for (const [ficha] of z.b || []) if (!npcs[ficha]) sinNombre.add(ficha);
    for (const cueva of z.c || []) if (!zonas[cueva]) cuevaSuelta.add(`${n}->${cueva}`);
  }
  if (sinFicha.size) problemas.push(`${sinFicha.size} casilleros de la lámina no tienen ficha en mapa-zonas.json`);
  if (sinNombre.size) problemas.push(`${sinNombre.size} criaturas del índice no están en npcs.json: se mostrarían como "criatura N"`);
  if (cuevaSuelta.size) problemas.push(`${cuevaSuelta.size} cuevas apuntan a un mapa que no está en el índice (${[...cuevaSuelta][0]})`);
}

if (problemas.length) {
  console.error('check-mapa: PROBLEMAS');
  for (const p of problemas) console.error('  - ' + p);
  process.exit(1);
}
console.log('check-mapa: la pantalla del mapa no manda paquetes y sus 690 casilleros caen adentro de la lámina');
