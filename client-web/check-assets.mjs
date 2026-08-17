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
        for (const m of readFileSync(p, 'utf8').matchAll(/PlayWave\(\s*(\d+)/g)) ids.add(Number(m[1]));
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
  deLaData('obj.json', ['snd1', 'sndespecial']);   // instrumentos y el sonido del aura al equipar

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
  const presentes = numerosDe('music', '.mp3');
  linea(`musica   : ${pedidas.size} pistas pedidas por los mapas, ${presentes.size} archivos`);
  for (const [pista, mapas] of pedidas) {
    if (!/^\d+$/.test(pista)) { avisos.push(`música "${pista}" no es un número (${mapas.length} mapas) — el original tampoco tiene ese archivo`); continue; }
    if (!presentes.has(Number(pista))) fallas.push(`música ${pista}.mp3 no está y la piden los mapas ${mapas.slice(0, 5).join(', ')}`);
  }
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
