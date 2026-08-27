// ¿Está DEFINIDO todo lo que el juego puede dibujar, y existe su lámina?
//
// `check-assets.mjs` mira si los ARCHIVOS están. Esto mira lo otro: que cada índice de gráfico que la data
// nombra —el cuerpo de una criatura, la cabeza, el casco, el arma, el escudo, un efecto, un cuadro de
// animación— tenga entrada en `grh.json`, y que la lámina que esa entrada pide exista en `assets/graficos`.
//
// Por qué hace falta: por el protocolo un `CharacterCreate(body=250)` sale perfecto aunque el cuerpo 250 no
// esté definido — el jugador ve un hueco. Es el mismo agujero que dejó el juego mudo, del lado de la imagen:
// los tests miran bytes, y por los bytes esto pasa siempre.
//
// LO ALCANZABLE ES LO QUE IMPORTA. La data del install trae 45 referencias rotas que NO usa nadie: ocho
// cuerpos, un arma, un escudo y varios efectos apuntan a gráficos que EL ORIGINAL TAMPOCO define (medido
// parseando su `graficos.ind`: 26167 entradas y el archivo consumido entero, o sea que no es pérdida del
// transcode). Ninguna la alcanza un NPC, una raza, un objeto ni un hechizo. Un check que grita por esas 45
// es un check que nadie mira, así que se listan aparte y sólo se pone rojo lo que un jugador puede ver.
//
// Correr:  node check-graficos.mjs
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const AQUI = dirname(fileURLToPath(import.meta.url));
const A = join(AQUI, 'assets');
const GD = join(AQUI, '..', 'gamedata');
const leer = (p) => JSON.parse(readFileSync(join(A, p), 'utf8'));
const leerGd = (p) => JSON.parse(readFileSync(join(GD, p), 'utf8'));
const ent = (v) => { const m = /^\s*(-?\d+)/.exec(String(v ?? '')); return m ? +m[1] : 0; };
const baja = (o) => Object.fromEntries(Object.entries(o).map(([k, v]) => [k.toLowerCase(), v]));

const grh = leer('grh.json').grh;
const laminas = new Set(readdirSync(join(A, 'graficos')));

// --- QUIÉN USA QUÉ: se deriva de la data, nunca a mano ------------------------------------------------
const usa = { cuerpo: new Set(), cabeza: new Set(), casco: new Set(), arma: new Set(), escudo: new Set(), efecto: new Set() };
for (const n of leerGd('NPCs.json').NPC) {
  if (!n || typeof n !== 'object') continue;
  const d = baja(n);
  usa.cuerpo.add(ent(d.body)); usa.cabeza.add(ent(d.head));
  usa.casco.add(ent(d.cascoanim)); usa.arma.add(ent(d.weaponanim)); usa.escudo.add(ent(d.shieldanim));
}
for (const o of leerGd('obj.json').OBJ) {
  if (!o || typeof o !== 'object') continue;
  const d = baja(o), tipo = ent(d.objtype), anim = ent(d.animacion ?? d.anim);
  if (tipo === 2) usa.arma.add(anim);
  else if (tipo === 4) usa.escudo.add(anim);
  else if (tipo === 16) usa.casco.add(anim);
  else if (tipo === 3) usa.cuerpo.add(ent(d.numropaje));
}
const hech = leerGd('Hechizos.json');
for (const h of Object.values(hech).find(Array.isArray) || []) {
  if (!h || typeof h !== 'object') continue;
  usa.efecto.add(ent(baja(h).fxgrh ?? baja(h).fx));
}
// Lo que puede tener un JUGADOR sale de la pantalla de creación (cuerpos y cabezas por raza y género).
for (const m of JSON.stringify(leer('creacion.json')).matchAll(/\d+/g)) {
  usa.cuerpo.add(+m[0]); usa.cabeza.add(+m[0]);
}

// --- EL RECORRIDO -------------------------------------------------------------------------------------
// ROTO EN EL ORIGINAL TAMBIEN, y por eso no pone rojo: el efecto 44 (el del hechizo Tornado) apunta al
// grafico 16230, que NO esta en el `graficos.ind` del original -- ni el ni ninguno de sus vecinos
// (medido: 16225..16235, todos ausentes). O sea que el Tornado tampoco tiene animacion en el juego
// original. Nuestro cliente no se rompe: `drawFx` cae en el estallido generico cuando el grafico no
// existe. Se deja como esta, por la misma politica con la que se dejan las pistas 3-1 de los mapas Arena
// y el 896.png huerfano: fidelidad a la data del install.
const ROTAS_EN_EL_ORIGINAL = new Map([['efecto 44', 'el grh 16230 no existe en el graficos.ind original']]);

const rotas = [];      // alcanzables: el jugador ve el hueco
const muertas = [];    // nadie las usa (y el original tampoco las define)
let mirados = 0;

const verGrh = (idx, quien, alcanzable, vistos = new Set()) => {
  if (!idx) return;                       // 0 = "nada", es válido en la data del original
  mirados++;
  const conocida = [...ROTAS_EN_EL_ORIGINAL.keys()].some((k) => quien.startsWith(k));
  const anotar = (m) => (alcanzable && !conocida ? rotas : muertas).push(m);
  const g = grh[idx];
  if (!g) { anotar(`${quien}: el gráfico ${idx} no está definido en grh.json`); return; }
  if (g.frames) {
    if (vistos.has(idx)) return;          // corta un ciclo de animación, si lo hubiera
    vistos.add(idx);
    if (!g.frames.length) anotar(`${quien}: la animación ${idx} no tiene cuadros`);
    for (const f of g.frames) verGrh(f, `${quien} → cuadro de la animación ${idx}`, alcanzable, vistos);
    return;
  }
  if (g.file === undefined) { anotar(`${quien}: el gráfico ${idx} no tiene lámina ni cuadros`); return; }
  if (!laminas.has(`${g.file}.png`)) anotar(`${quien}: el gráfico ${idx} pide la lámina ${g.file}.png, que no está`);
  if (!(g.w > 0 && g.h > 0)) anotar(`${quien}: el gráfico ${idx} mide ${g.w}x${g.h}`);
};

// Cuerpos: cuatro direcciones cada uno — es lo que se ve de cada personaje y cada criatura.
for (const [i, b] of Object.entries(leer('bodies.json').bodies))
  (b.walk || []).forEach((idx, d) => verGrh(idx, `cuerpo ${i} dirección ${d + 1}`, usa.cuerpo.has(+i)));

for (const [archivo, clave, nombre, quienUsa] of [
  ['heads.json', 'heads', 'cabeza', usa.cabeza], ['helmets.json', 'anims', 'casco', usa.casco],
  ['weapons.json', 'anims', 'arma', usa.arma], ['shields.json', 'anims', 'escudo', usa.escudo],
]) {
  for (const [i, e] of Object.entries(leer(archivo)[clave])) {
    const lista = Array.isArray(e) ? e : (e.walk || e.anims || []);
    lista.forEach((idx, d) => verGrh(idx, `${nombre} ${i} dirección ${d + 1}`, quienUsa.has(+i)));
  }
}

for (const [i, e] of Object.entries(leer('fx.json').fx))
  verGrh(Array.isArray(e) ? e[0] : e, `efecto ${i}`, usa.efecto.has(+i));

// AURAS del equipo (auras.ini → auras.json). Alcanzables todas: cada una la nombra un objeto del obj.dat
// por su clave `Aura`, y el server manda AuraToChar al equiparlo.
const aurasUsadas = new Set();
for (const o of leerGd('obj.json').OBJ) {
  if (!o || typeof o !== 'object') continue;
  const a = ent(baja(o).aura);
  if (a) aurasUsadas.add(a);
}
for (const [i, a] of Object.entries(leer('auras.json').auras))
  verGrh(a.grh, `aura ${i}`, aurasUsadas.has(+i));

// --- VEREDICTO ----------------------------------------------------------------------------------------
console.log(`check-graficos: ${mirados} referencias miradas, ${laminas.size} láminas en disco`);
if (muertas.length) console.log(`  (${muertas.length} rotas en data que NO usa nadie — están rotas en el original también)`);

// Población de control: un verde sobre cero referencias es el silencio de un guardia que no mira.
if (mirados < 2000) {
  console.error(`FALLO: sólo ${mirados} referencias miradas; el check no está leyendo la data`);
  process.exit(1);
}
if (rotas.length) {
  console.error(`FALLO: ${rotas.length} gráfico(s) que un jugador PUEDE ver no están definidos`);
  for (const f of rotas.slice(0, 40)) console.error('  ' + f);
  if (rotas.length > 40) console.error(`  ... y ${rotas.length - 40} más`);
  process.exit(1);
}
console.log('OK: todo lo que un jugador puede ver está definido y tiene su lámina');
