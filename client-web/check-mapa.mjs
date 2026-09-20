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
//
// La lámina NO es el dibujo que trae el original: es `assets/mapamundi.png`, que arma
// `fase0/transcode/build_mapamundi.py` pintando cada casillero con los colores del minimapa del mapa que la
// tabla pone ahí (el dibujo del original cubre 18 de las 30 columnas del mundo, así que el continente del este
// no figura). Como la lámina se DERIVA de la tabla, las dos tienen que medir lo mismo: si alguien cambia la
// grilla y no vuelve a armar la lámina, el punto del jugador se corre y nadie se entera.
const ladoTxt = /const LADO_DEL_CASILLERO = (\d+)/.exec(html);
if (!ladoTxt) problemas.push('no encontré `LADO_DEL_CASILLERO` en index.html');
const LADO = ladoTxt ? +ladoTxt[1] : 0;
const rutaLamina = join(ASSETS, 'mapamundi.png');
let laminaMide = null;
if (!existsSync(rutaLamina)) {
  problemas.push('falta assets/mapamundi.png (lo arma fase0/transcode/build_mapamundi.py)');
} else {
  const cab = readFileSync(rutaLamina).subarray(0, 24);
  laminaMide = [cab.readUInt32BE(16), cab.readUInt32BE(20)];
}

// --- 4. La grilla: tamaño, mapas reales y todos los puntos adentro de la lámina ---------------------------
const rutaTabla = join(ASSETS, 'maptable.json');
if (!existsSync(rutaTabla)) {
  problemas.push('falta assets/maptable.json (lo genera fase0/transcode/decode_maptable.py)');
} else {
  const t = JSON.parse(readFileSync(rutaTabla, 'utf8'));
  if (t.filas !== t.grilla.length || t.grilla.some((f) => f.length !== t.columnas))
    problemas.push(`maptable.json dice ${t.columnas}x${t.filas} y la grilla no mide eso`);
  // La lámina se deriva de la grilla, así que el único desacuerdo posible es de TAMAÑO: un casillero de más en
  // la tabla y el punto del jugador se corre medio mapa.
  let sinMapa = 0, conMapa = 0;
  for (let fila = 1; fila <= t.filas; fila++) {
    for (let col = 1; col <= t.columnas; col++) {
      const n = t.grilla[fila - 1][col - 1];
      if (n > 0) { conMapa++; if (!existsSync(join(ASSETS, `map${n}.json`))) sinMapa++; }
    }
  }
  if (sinMapa) problemas.push(`${sinMapa} casilleros de la grilla nombran un mapa que no está en assets/`);
  if (laminaMide && LADO) {
    const esperado = [t.columnas * LADO, t.filas * LADO];
    if (laminaMide[0] !== esperado[0] || laminaMide[1] !== esperado[1])
      problemas.push(`mapamundi.png mide ${laminaMide.join('x')} y la grilla pide ${esperado.join('x')}: `
        + 'volvé a correr fase0/transcode/build_mapamundi.py');
    else console.log(`check-mapa: la lámina mide ${laminaMide.join('x')} y cubre los ${conMapa} casilleros con mapa`);
  }
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
  // El índice es data DERIVADA: si alguien toca los spawns y no lo regenera, la pantalla miente sin que nada avise.
  const spawns = JSON.parse(readFileSync(join(RAIZ, 'gamedata/MapSpawns.json'), 'utf8'));
  const hostil = new Map(JSON.parse(readFileSync(join(RAIZ, 'gamedata/NPCs.json'), 'utf8')).NPC
    .map((n) => [Number(n._index), String(n.Hostile) === '1']));
  let viejos = 0;
  for (const [n, puntos] of Object.entries(spawns)) {
    const enLaData = puntos.filter((p) => hostil.get(p[2])).length;
    const enElIndice = ((zonas[n] || {}).b || []).reduce((a, [, c]) => a + c, 0);
    if (enLaData !== enElIndice) viejos++;
  }
  if (viejos) problemas.push(`${viejos} mapas tienen en mapa-zonas.json otras criaturas que en gamedata: regenerar con build_mapa_zonas.py`);
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
