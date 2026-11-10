// ¿Están TODOS los archivos que el juego puede pedir?
//
// Existe por una falla que pasó de largo por todos los controles: el juego se distribuyó MUDO —tres sonidos
// contra los ciento y pico que la data pide— y nada lo detectó. Ni los tests, ni los flows, ni el oráculo:
// todos miran BYTES DEL PROTOCOLO, y por el protocolo un `PlayWave(241)` sale perfecto aunque el 241.wav no
// exista. El agujero se ve recién cuando alguien juega y no escucha nada. Y encima yo pruebo el cliente con
// `?mute`, así que ni jugando lo veía.
//
// La regla es derivar la lista de la DATA, no escribirla a mano: una lista a mano se queda vieja el día que
// alguien agrega un ítem, que es justo cuando haría falta.
//
// Correr:  node check-assets.mjs   (o `npm test`, que lo incluye)
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const AQUI = dirname(fileURLToPath(import.meta.url));
const A = join(AQUI, 'assets');
const RAIZ = join(AQUI, '..');
const leer = (p) => JSON.parse(readFileSync(p, 'utf8'));
const numerosDe = (dir, ext) => {
  if (!existsSync(join(A, dir))) return new Set();
  return new Set(readdirSync(join(A, dir))
    .filter((f) => f.endsWith(ext))
    .map((f) => Number(f.slice(0, -ext.length)))
    .filter((n) => Number.isFinite(n)));
};

const fallas = [];
const avisos = [];
const linea = (t) => console.log(t);

// ¿Alguien dibuja este grh? Se mira la data que lo puede referenciar; los mapas se recorren aparte.
let _usados = null;
function seUsaElGrh(id) {
  if (!_usados) {
    _usados = new Set();
    const recoger = (o) => {
      if (Array.isArray(o)) o.forEach(recoger);
      else if (o && typeof o === 'object') Object.values(o).forEach(recoger);
      else if (typeof o === 'number') _usados.add(o);
    };
    for (const f of ['bodies.json', 'heads.json', 'weapons.json', 'shields.json', 'helmets.json',
                     'items.json', 'fx.json', 'spellicons.json', 'particles.json']) {
      if (existsSync(join(A, f))) recoger(leer(join(A, f)));
    }
    for (const f of readdirSync(A).filter((x) => /^map\d+\.json$/.test(x))) {
      const m = leer(join(A, f));
      // `objs` va en la lista: es la capa de los OBJETOS DEL MUNDO (puertas, carteles, yacimientos), cuyo grh
      // sale del obj.dat y no de las capas del .csm. Si no se cuenta acá, un grafico que falte para una puerta
      // pasa el chequeo y el agujero se ve recien jugando — que es el defecto que este guardia existe para cazar.
      for (const capa of ['objects', 'decorations', 'roofs', 'objs']) for (const e of (m[capa] || [])) _usados.add(e[2]);
    }
  }
  return _usados.has(id);
}

// ── 1. HOJAS DE GRÁFICOS ────────────────────────────────────────────────────────────────────────────
// Cada grh dice de qué hoja sale. Una hoja que falte deja un agujero donde iba el sprite.
{
  const grh = leer(join(A, 'grh.json')).grh;
  const hojas = new Map();                 // hoja -> cuántos grh la usan
  for (const g of Object.values(grh)) {
    if (g && typeof g === 'object' && 'file' in g) hojas.set(g.file, (hojas.get(g.file) || 0) + 1);
  }
  const presentes = numerosDe('graficos', '.png');
  const faltan = [...hojas.keys()].filter((h) => !presentes.has(h)).sort((a, b) => a - b);
  linea(`graficos : ${hojas.size} hojas referenciadas, ${presentes.size} archivos`);
  for (const h of faltan) {
    // Una hoja que sólo usan grh HUÉRFANOS (que nadie dibuja) no rompe nada: se avisa, no se falla.
    const usada = [...Object.entries(grh)].some(([id, g]) => g?.file === h && seUsaElGrh(Number(id)));
    (usada ? fallas : avisos).push(`hoja de gráficos ${h}.png no está (la usan ${hojas.get(h)} grh${usada ? '' : ', todos huérfanos'})`);
  }
}

// ── 2. SONIDOS ──────────────────────────────────────────────────────────────────────────────────────
// Los ids salen de DOS lados: los literales del servidor y los campos de la data (hechizos, instrumentos,
// y el SndEspecial de los ítems con aura). Sin mirar los tres, el barrido miente por omisión.
{
  const ids = new Set();
  const cs = join(RAIZ, 'server', 'src', 'CoverAO.Server');
  const recorrerCs = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) recorrerCs(p);
      else if (e.name.endsWith('.cs')) {
        // SIN LOS COMENTARIOS. El regex no distingue código de prosa, y un comentario que EXPLICA el sonido
        // —"emitir un PlayWave(0) sería un paquete de más"— le inventaba al barrido un id 0 que el juego no
        // pide nunca, o sea un FALTA falso. Pasó apenas se escribió ese comentario. Se sacan las líneas `//`
        // y `///` enteras y los bloques `/* */`; alcanza, porque acá el código real nunca lleva un `//` antes.
        const fuente = readFileSync(p, 'utf8')
          .replace(/\/\*[\s\S]*?\*\//g, ' ')
          .replace(/^\s*\/\/.*$/gm, ' ');
        for (const m of fuente.matchAll(/PlayWave\(\s*(\d+)/g)) ids.add(Number(m[1]));
        // Y LOS QUE VAN POR CONSTANTE, que son la mayoría de los del servidor: `PlayWave(SndNivel, …)`,
        // `SndEscudo`, `SndBeber`, `SwingWave`… El regex de literales no ve NINGUNO, así que el barrido
        // contaba de menos y no lo notaba nadie. Se resuelven leyendo las `const` del mismo archivo.
        const consts = new Map();
        for (const m of fuente.matchAll(/\bconst\s+(?:short|int|byte)\s+(\w+)\s*=\s*(\d+)/g))
          consts.set(m[1], Number(m[2]));
        // Una misma línea puede declarar varias (`const short A = 1, B = 2;`).
        for (const m of fuente.matchAll(/,\s*(\w+)\s*=\s*(\d+)\s*[;,]/g))
          if (!consts.has(m[1])) consts.set(m[1], Number(m[2]));
        for (const m of fuente.matchAll(/PlayWave\(\s*\(?\s*(?:short\)\s*)?(\w+)/g)) {
          const v = consts.get(m[1]);
          if (v > 0) ids.add(v);
        }
      }
    }
  };
  if (existsSync(cs)) recorrerCs(cs);

  const gd = join(RAIZ, 'gamedata');
  const deLaData = (archivo, claves) => {
    const p = join(gd, archivo);
    if (!existsSync(p)) return;
    const rec = (o) => {
      if (Array.isArray(o)) o.forEach(rec);
      else if (o && typeof o === 'object') {
        for (const [k, v] of Object.entries(o)) {
          if (claves.includes(k.toLowerCase()) && Number(v) > 0) ids.add(Number(v));
          else rec(v);
        }
      }
    };
    rec(leer(p));
  };
  deLaData('Hechizos.json', ['wav']);
  deLaData('obj.json', ['snd1', 'snd2', 'snd3', 'sndespecial']);   // instrumentos y el sonido del aura al equipar
  // LAS CRIATURAS, que faltaban y son la mayoría de los sonidos del juego. Cada NPC trae hasta tres —Snd1 al
  // atacar, Snd2 al recibir el golpe, Snd3 al morir (MODULO_NPCs.bas:1095-1097)— y este barrido no miraba
  // `NPCs.dat` en absoluto. Los traen 66 de los 291 NPCs y apuntan a 107 archivos distintos: más ids que
  // hechizos y objetos juntos. El agujero se descubrió porque un jugador comparó carpetas de sonidos, no
  // porque lo dijera un control.
  deLaData('npcs.json', ['snd1', 'snd2', 'snd3']);

  // Y EL CLIENTE, que toca sonidos por su cuenta sin que el server se los mande. Hoy son los PASOS
  // (`PASOS_POR_TERRENO`, siete ids según el terreno que se pisa): el server no manda nada, el cliente decide
  // solo mirando la hoja de gráficos del tile. Un barrido que sólo mire el servidor no ve ni uno.
  {
    const idx = readFileSync(join(AQUI, 'index.html'), 'utf8');
    const tabla = /const PASOS_POR_TERRENO = \{([^}]*)\}/.exec(idx);
    if (!tabla) fallas.push('no encontré PASOS_POR_TERRENO en index.html (¿lo renombraron?)');
    else for (const m of tabla[1].matchAll(/\d+/g)) {
      const n = Number(m[0]);
      if (n > 10) ids.add(n);      // las claves de la tabla son el TIPO de terreno (1..5), no un sonido
    }
  }

  const presentes = numerosDe('sounds', '.wav');
  const faltan = [...ids].filter((i) => !presentes.has(i)).sort((a, b) => a - b);
  linea(`sonidos  : ${ids.size} ids que el juego puede pedir, ${presentes.size} archivos`);
  for (const i of faltan) fallas.push(`sonido ${i}.wav no está y el juego lo puede pedir`);
}

// ── 3. MÚSICA ───────────────────────────────────────────────────────────────────────────────────────
// Cada mapa declara su pista. En el original el campo es un String de 16, no un número: hay valores como
// "3-1" que no corresponden a ningún archivo NI EN EL ORIGINAL. Eso es fiel, no es nuestro: se avisa.
{
  const pedidas = new Map();               // pista -> mapas que la piden
  for (const f of readdirSync(A).filter((x) => /^map\d+\.json$/.test(x))) {
    const m = leer(join(A, f));
    const mu = String(m.music ?? '').trim();
    if (!mu || mu === '0') continue;
    if (!pedidas.has(mu)) pedidas.set(mu, []);
    pedidas.get(mu).push(Number(f.match(/\d+/)[0]));
  }
  // Por NOMBRE, no por número: los temas del AO original van como `aoN` (el MIDI N), al lado de los libres numerados.
  const presentes = new Set(readdirSync(join(A, 'music')).filter((x) => x.endsWith('.mp3')).map((x) => x.slice(0, -4)));
  linea(`musica   : ${pedidas.size} pistas pedidas por los mapas, ${presentes.size} archivos`);
  for (const [pista, mapas] of pedidas) {
    if (presentes.has(pista)) continue;
    if (!/^(ao)?\d+$/.test(pista)) { avisos.push(`música "${pista}" no es un número (${mapas.length} mapas) — el original tampoco tiene ese archivo`); continue; }
    fallas.push(`música ${pista}.mp3 no está y la piden los mapas ${mapas.slice(0, 5).join(', ')}`);
  }
  // Las listas que se turnan bajo una pista (assets/music/listas.json): un tema que falta corta la lista en silencio.
  const listas = existsSync(join(A, 'music', 'listas.json')) ? leer(join(A, 'music', 'listas.json')) : {};
  for (const [pista, temas] of Object.entries(listas)) {
    if (!pedidas.has(pista)) avisos.push(`la lista de música "${pista}" no la pide ningún mapa`);
    for (const t of temas) if (!presentes.has(String(t))) fallas.push(`música ${t}.mp3 no está y es parte de la lista "${pista}"`);
  }
  linea(`listas   : ${Object.keys(listas).length} pistas con temas que se turnan`);
}

// ── 4. MAPAS ────────────────────────────────────────────────────────────────────────────────────────
// Una salida que apunte a un mapa inexistente deja al jugador en una pantalla en blanco, sin vuelta.
{
  const hay = new Set();
  const destinos = new Map();
  for (const f of readdirSync(A).filter((x) => /^map\d+\.json$/.test(x))) {
    hay.add(Number(f.match(/\d+/)[0]));
  }
  for (const f of readdirSync(A).filter((x) => /^map\d+\.json$/.test(x))) {
    const origen = Number(f.match(/\d+/)[0]);
    for (const e of (leer(join(A, f)).exits || [])) {
      const d = Array.isArray(e) ? e[2] : null;
      if (Number.isInteger(d) && d > 0 && !destinos.has(d)) destinos.set(d, origen);
    }
  }
  linea(`mapas    : ${hay.size} archivos, ${destinos.size} destinos de salida distintos`);
  for (const [d, origen] of destinos) if (!hay.has(d)) fallas.push(`el mapa ${origen} tiene una salida al mapa ${d}, que no existe`);
}

// ── 5. LOS JSON QUE EL CLIENTE PIDE POR fetch() ─────────────────────────────────────────────────────
// Los bloques de arriba derivan la lista de la data; este la deriva del CÓDIGO, que es el otro lado del
// mismo agujero: un `fetch('./assets/X.json')` que devuelve 404 no rompe nada visible —el cliente arranca,
// entra al mundo y pasa el check de errores de JS— y simplemente le falta ESE pedazo del juego, en silencio.
// Pasó con las palabras mágicas: el paquete llega y sin su archivo no hay texto que dibujar.
{
  const html = readFileSync(join(AQUI, 'index.html'), 'utf8');
  const pedidos = new Set();
  for (const m of html.matchAll(/fetch\(\s*'\.\/assets\/([^']+)'/g)) pedidos.add(m[1]);
  linea(`fetch()  : ${pedidos.size} archivos de assets que el cliente pide por nombre`);
  for (const f of [...pedidos].sort()) if (!existsSync(join(A, f))) fallas.push(`el cliente hace fetch de assets/${f} y ese archivo no está`);
}

// ── veredicto ───────────────────────────────────────────────────────────────────────────────────────
console.log();
for (const a of avisos) console.log(`  aviso: ${a}`);
if (fallas.length === 0) {
  console.log(`check-assets: OK — no falta ningún archivo que el juego pueda pedir${avisos.length ? ` (${avisos.length} aviso(s))` : ''}`);
  process.exit(0);
}
for (const f of fallas) console.error(`  FALTA: ${f}`);
console.error(`check-assets: ${fallas.length} archivo(s) que el juego pide y no se distribuyen`);
process.exit(1);
