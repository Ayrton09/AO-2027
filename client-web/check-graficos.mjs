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
// QUIÉN ALCANZA UN EFECTO NO SE DECIDE SÓLO EN LA DATA. Los hechizos dicen cuál piden, pero hay cuatro que
// salen del CÓDIGO —el fogonazo de aparición que se ve en cada login, el del golpe, el de la sangre, el del
// veneno— y no los nombra ningún hechizo. Contándolos sólo por la data quedaban del lado de "no los usa
// nadie", así que romperlos NO ponía rojo esto: medido apuntando el efecto 1 a un gráfico inexistente, el
// check seguía diciendo OK. Ahora los índices del código se derivan leyendo los `CreateFX(...)` del propio
// servidor, que es la misma lección del extractor de sonidos: una lista a mano se queda vieja justo el día
// que haría falta.
//
// Y SE MIRA EL SENTIDO CONTRARIO, que no lo miraba nada: un índice que el server MANDA y `fx.json` no
// DEFINE no aparecía por ningún lado, porque el recorrido arranca en `fx.json`. El cliente cae ahí en el
// estallido genérico y el jugador ve un destello cualquiera en lugar del efecto — mudo, como el `PlayWave`
// sin su `.wav`.
//
// Correr:  node check-graficos.mjs
import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { basename, dirname, join } from 'node:path';

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
const fxDeHechizo = new Map();          // índice de efecto -> el hechizo que lo pide
for (const h of Object.values(hech).find(Array.isArray) || []) {
  if (!h || typeof h !== 'object') continue;
  const d = baja(h), f = ent(d.fxgrh ?? d.fx);
  usa.efecto.add(f);
  if (f && !fxDeHechizo.has(f)) fxDeHechizo.set(f, `el hechizo ${ent(d._index)} "${d.nombre}"`);
}

// LOS EFECTOS QUE MANDA EL CÓDIGO, derivados del server y NO escritos a mano. Escribirlos a mano es el
// defecto que dejó el juego mudo: el extractor de sonidos tenía los ids del código cableados, y el del
// fuego se agregó sin que nadie lo notara. Acá se leen los `CreateFX(...)` del propio servidor.
//
// Sin esto, los cuatro efectos que sólo salen del código —entre ellos el FOGONAZO DE APARICIÓN, que se ve
// en cada login— quedaban clasificados como "no los usa nadie": romperlos no ponía rojo este check.
const SERVER = join(AQUI, '..', 'server', 'src', 'CoverAO.Server');
if (!existsSync(SERVER)) {
  console.error(`FALLO: no está ${SERVER}; el barrido de efectos del código no puede leer nada. Revisá la ruta, no el juego`);
  process.exit(1);
}
const fuentesCs = [];
(function juntar(dir) {
  for (const f of readdirSync(dir)) {
    const ruta = join(dir, f);
    if (statSync(ruta).isDirectory()) { if (f !== 'obj' && f !== 'bin') juntar(ruta); }
    else if (f.endsWith('.cs')) fuentesCs.push(ruta);
  }
})(SERVER);

const constantes = new Map();
for (const ruta of fuentesCs) {
  const t = readFileSync(ruta, 'utf8');
  for (const m of t.matchAll(/const\s+short\s+(\w+)\s*=\s*(\d+)/g)) constantes.set(m[1], +m[2]);
  for (const m of t.matchAll(/(\w+)\s*=\s*(\d+)\s*[,;]/g))
    if ((m[1].startsWith('Fx') || m[1].startsWith('Particula')) && !constantes.has(m[1])) constantes.set(m[1], +m[2]);
}

const fxDelCodigo = new Map();          // índice de efecto -> dónde se manda
for (const ruta of fuentesCs) {
  readFileSync(ruta, 'utf8').split('\n').forEach((linea, i) => {
    const l = linea.trim();
    if (l.startsWith('//') || !l.includes('CreateFX(')) return;
    for (const m of l.matchAll(/CreateFX\(\s*[^,]+,\s*(?:\(short\)\s*)?([A-Za-z_]\w*|\d+)/g)) {
      const tok = m[1];
      const id = /^\d+$/.test(tok) ? +tok : constantes.get(tok);
      if (id === undefined) continue;   // sale de la DATA (spell.FxGrh, municion.Snd2): se cubre aparte
      if (!id) continue;                // 0 = APAGAR el efecto (al dejar de meditar), no es un índice
      usa.efecto.add(id);
      if (!fxDelCodigo.has(id)) fxDelCodigo.set(id, `${basename(ruta)}:${i + 1}`);
    }
  });
}

// La MUNICIÓN equipada manda su `Snd2` como índice de efecto, no como sonido (VB6 SistemaCombate.bas:918,
// `CreateFX(npc, ObjData(Municion).Snd2, 0)`). En este install ninguna de las once flechas lo trae, así que
// hoy no aporta ninguno; se deriva igual para que entre solo el día que una flecha lo traiga.
const fxDeMunicion = new Map();
for (const o of leerGd('obj.json').OBJ) {
  if (!o || typeof o !== 'object') continue;
  const d = baja(o);
  if (ent(d.objtype) !== 32) continue;  // otFlechas (Declares.bas:610)
  const f = ent(d.snd2);
  if (!f) continue;
  usa.efecto.add(f);
  if (!fxDeMunicion.has(f)) fxDeMunicion.set(f, `la munición "${d.name ?? d.nombre}"`);
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

const efectos = leer('fx.json').fx;
for (const [i, e] of Object.entries(efectos))
  verGrh(Array.isArray(e) ? e[0] : e, `efecto ${i}`, usa.efecto.has(+i));

// EL SENTIDO CONTRARIO, que hasta acá no lo miraba nada. El recorrido de arriba va de `fx.json` HACIA
// AFUERA —"este efecto que está definido, ¿lo usa alguien?"—, así que un índice que el server MANDA y
// `fx.json` NO DEFINE le pasa por al lado: no está en el recorrido para empezar. El síntoma es mudo, igual
// que el `PlayWave` sin `.wav`: el cliente cae en el estallido genérico y el jugador ve un destello
// cualquiera en vez del efecto de verdad.
//
// COLGADO EN EL INSTALL, y por eso no pone rojo: el `fxs.ind` del original define 51 efectos y el hechizo
// 74 ("Animar a la naturaleza") pide el 57. La referencia ya viene rota de la data, no la rompió el
// transcode. El original además NUNCA la manda —allá la invocación es código muerto, su llamada está
// comentada (modHechizos.bas:710)—, mientras que acá la invocación se revivió a propósito, así que el 57
// sí sale. Se deja como está por la misma política que el efecto 44 del Tornado: fidelidad a la data.
const SIN_DEFINIR_CONOCIDOS = new Map([
  [57, 'lo pide el hechizo 74 "Animar a la naturaleza"; el fxs.ind del install llega hasta el 51'],
]);

const emitibles = new Map();            // índice de efecto -> quién lo manda
for (const [id, donde] of fxDelCodigo) emitibles.set(id, `lo manda ${donde}`);
for (const [id, quien] of fxDeHechizo) if (!emitibles.has(id)) emitibles.set(id, `lo pide ${quien}`);
for (const [id, quien] of fxDeMunicion) if (!emitibles.has(id)) emitibles.set(id, `lo pide ${quien}`);

// LAS PARTÍCULAS son el otro efecto que el server manda por índice, y fallan igual de calladas: el cliente
// no encuentra la definición y no dibuja nada. Los índices salen de tres lados —el `Particle` de un hechizo,
// los literales de los emisores, y `ParticleToLevel`, que devuelve el aura de meditación por nivel y facción.
// Los tres se derivan; ninguno se escribe a mano.
const particulas = leer('particles.json');
const partEmitibles = new Map();
for (const o of Object.values(hech).find(Array.isArray) || []) {
  if (!o || typeof o !== 'object') continue;
  const d = baja(o), q = ent(d.particle ?? d.particula);
  if (q && !partEmitibles.has(q)) partEmitibles.set(q, `lo pide el hechizo ${ent(d._index)} "${d.nombre}"`);
}
for (const ruta of fuentesCs) {
  const texto = readFileSync(ruta, 'utf8');
  texto.split('\n').forEach((linea, i) => {
    const l = linea.trim();
    if (l.startsWith('//') || !/Efecto(Char|Terreno)Particula\(/.test(l)) return;
    for (const m of l.matchAll(/Efecto(?:Char|Terreno)Particula\(\s*[^,]+,\s*(?:\(short\)\s*)?([A-Za-z_]\w*|\d+)/g)) {
      const tok = m[1];
      const id = /^\d+$/.test(tok) ? +tok : constantes.get(tok);
      if (!id) continue;              // sale de la data o de una función: se cubre aparte
      if (!partEmitibles.has(id)) partEmitibles.set(id, `lo manda ${basename(ruta)}:${i + 1}`);
    }
  });
  // `ParticleToLevel` elige el aura de meditación por nivel y facción: sus índices están en los `return`
  // del cuerpo, no en el llamador, así que se leen de ahí.
  const cuerpo = /int ParticleToLevel\(Player p\)\s*\{([\s\S]*?)\n    \}/.exec(texto);
  if (cuerpo) for (const m of cuerpo[1].matchAll(/return\s+(?:p\.IsCitizen\s*\?\s*(\d+)\s*:\s*(\d+)|lvl == 50 \? (\d+) : (\d+)|(\d+))/g))
    for (const g of m.slice(1)) {
      const id = +g;
      if (g !== undefined && id && !partEmitibles.has(id)) partEmitibles.set(id, 'lo elige ParticleToLevel');
    }
}

const partSinDefinir = [];
for (const [id, quien] of partEmitibles)
  if (!particulas[id]) partSinDefinir.push(`la partícula ${id} no está definida en particles.json y ${quien}`);

const sinDefinir = [];
for (const [id, quien] of emitibles) {
  if (efectos[id] || SIN_DEFINIR_CONOCIDOS.has(id)) continue;
  sinDefinir.push(`el efecto ${id} no está definido en fx.json y ${quien}`);
}

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
console.log(`  efectos que el server puede mandar: ${emitibles.size} `
  + `(${fxDelCodigo.size} desde el código, ${fxDeHechizo.size} desde hechizos, ${fxDeMunicion.size} desde munición)`);
console.log(`  partículas que el server puede mandar: ${partEmitibles.size} de ${Object.keys(particulas).length} definidas`);
if (muertas.length) console.log(`  (${muertas.length} rotas en data que NO usa nadie — están rotas en el original también)`);

// Población de control: un verde sobre cero referencias es el silencio de un guardia que no mira.
if (mirados < 2000) {
  console.error(`FALLO: sólo ${mirados} referencias miradas; el check no está leyendo la data`);
  process.exit(1);
}
// Y las premisas de los dos barridos nuevos: si el que lee el server deja de resolver índices —le cambian
// el nombre a la carpeta, o al emisor— este check se queda verde para siempre sin mirar nada. Que falle
// acusándose a sí mismo y no al juego.
if (fuentesCs.length < 20) {
  console.error(`FALLO: sólo ${fuentesCs.length} archivos .cs leídos del server; revisá la ruta, no el juego`);
  process.exit(1);
}
if (fxDelCodigo.size < 3) {
  console.error(`FALLO: el barrido de CreateFX resolvió sólo ${fxDelCodigo.size} efectos; revisá el patrón, no el juego`);
  process.exit(1);
}
if (partEmitibles.size < 20) {
  console.error(`FALLO: el barrido de partículas resolvió sólo ${partEmitibles.size}; revisá el patrón, no el juego`);
  process.exit(1);
}
if (partSinDefinir.length) {
  console.error(`FALLO: ${partSinDefinir.length} partícula(s) que el server manda y particles.json no define`);
  for (const f of partSinDefinir) console.error('  ' + f);
  process.exit(1);
}
if (sinDefinir.length) {
  console.error(`FALLO: ${sinDefinir.length} efecto(s) que el server manda y fx.json no define`);
  for (const f of sinDefinir) console.error('  ' + f);
  process.exit(1);
}
if (rotas.length) {
  console.error(`FALLO: ${rotas.length} gráfico(s) que un jugador PUEDE ver no están definidos`);
  for (const f of rotas.slice(0, 40)) console.error('  ' + f);
  if (rotas.length > 40) console.error(`  ... y ${rotas.length - 40} más`);
  process.exit(1);
}
console.log('OK: todo lo que un jugador puede ver está definido y tiene su lámina');
