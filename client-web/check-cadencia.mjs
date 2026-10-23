// Verifica LA CADENCIA DE LA ANIMACIÓN: cuántos milisegundos dura un cuadro.
//
// Por qué existe este guardia. El cliente tenía el mundo animado al DOBLE de velocidad y eso sobrevivió a
// una auditoría visual entera y a una puerta verde: ningún test medía cuánto dura un cuadro, sólo que el
// gráfico existiera y se dibujara. Se arregló el camino de los PERSONAJES y el del MUNDO quedó como estaba,
// porque el comentario del arreglo decía —mal— que las baldosas del mapa se animan por otra rutina.
//
// En el original hay DOS cadencias y una sola de ellas mira la `speed` del gráfico:
//
//   * `Draw_Grh` (TileEngine.bas:4637) avanza `FrameCounter + (timerTicksPerFrame * 0.5)`. El 0,5 está
//     CABLEADO y no lee `Grh.speed` nunca. Por acá pasa TODO el mundo —capa 1 (:2080), capa 2 (:2102), el
//     objeto del mundo (:2129), capa 3 (:2136), techos (:2249)— y también las piezas de cada personaje
//     (:2444-2478). Da 1/(0,018*0,5) = 111,1 ms por cuadro, iguales para todos.
//
//   * `DrawGrhtoSurface` (:1788) avanza `timerTicksPerFrame * Grh.speed`, y dibuja SÓLO los FX y el
//     fantasma. `InitGrh` (:1104) le deja `speed` en 0,8 fijo a todo FX, así que son 69,4 ms por cuadro.
//
// O sea que el `speed` del `graficos.ind` NO decide la velocidad de nada que se dibuje en el mundo. Usarlo
// ponía el agua (2109 grh con speed 1) al doble, las baldosas animadas (42 con speed 2) a 4x, y las cuatro
// túnicas del Caos (speed 277 y 333) cambiaban de cuadro en CADA refresco de pantalla.
//
// Correr:  node check-cadencia.mjs   (o `npm test`, que lo incluye)
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const cuerpo = (html.match(/<script type="module">([\s\S]*?)<\/script>/) || [])[1];
if (!cuerpo) {
  console.error('check-cadencia: no encontré el <script type="module"> en index.html');
  process.exit(1);
}

const fallas = [];

/** Lee `const NOMBRE = <número>;` del fuente. Si no está, es una falla: alguien la renombró o la borró. */
function constante(nombre) {
  const m = cuerpo.match(new RegExp(`const\\s+${nombre}\\s*=\\s*([0-9.]+)\\s*;`));
  if (!m) { fallas.push(`no encontré la constante \`${nombre}\` en index.html`); return null; }
  return parseFloat(m[1]);
}

/** El cuerpo de una función, por conteo de llaves desde su declaración. */
function cuerpoDe(nombre) {
  const i = cuerpo.indexOf(`function ${nombre}(`);
  if (i < 0) { fallas.push(`no encontré la función \`${nombre}\` en index.html`); return null; }
  const desde = cuerpo.indexOf('{', i);
  let n = 0;
  for (let j = desde; j < cuerpo.length; j++) {
    if (cuerpo[j] === '{') n++;
    else if (cuerpo[j] === '}' && --n === 0) return cuerpo.slice(desde, j + 1);
  }
  fallas.push(`no pude cerrar las llaves de \`${nombre}\``);
  return null;
}

const BASE = constante('ENGINE_BASE_SPEED');
const PASO = constante('PASO_DRAW_GRH');

// ── 1. El número que sale de las constantes del fuente ────────────────────────────────────────────────
// No se compara contra un literal escrito a mano en otro lado: se DERIVA de lo que el cliente usa de verdad.
if (BASE != null && PASO != null) {
  const ms = 1 / (BASE * PASO);
  if (Math.abs(ms - 111.1) > 0.5) {
    fallas.push(`un cuadro del mundo dura ${ms.toFixed(1)} ms y el original son 111,1 ` +
                `(1/(0,018*0,5), Draw_Grh en TileEngine.bas:4637)`);
  }
}

// ── 2. Las DOS rutinas que animan usan el mismo 0,5, y ninguna mira la `speed` del gráfico ────────────
// `animGrh` dibuja las capas del mapa, los objetos, los techos y los ítems del suelo; `frameGrh`, las cinco
// piezas de un personaje. En el original las dos salen de `Draw_Grh`, así que comparten cadencia.
for (const nombre of ['animGrh', 'frameGrh']) {
  const f = cuerpoDe(nombre);
  if (!f) continue;
  // Sólo las líneas que calculan un ÍNDICE DE CUADRO; el resto de la función no importa acá.
  const lineas = f.split('\n').filter((l) => /Math\.floor\([^\n]*ENGINE_BASE_SPEED/.test(l));
  if (!lineas.length) {
    fallas.push(`\`${nombre}\` ya no calcula el cuadro con ENGINE_BASE_SPEED: revisá si cambió de forma`);
    continue;
  }
  for (const l of lineas) {
    const t = l.trim();
    if (!/PASO_DRAW_GRH/.test(t)) {
      fallas.push(`\`${nombre}\` anima sin el 0,5 de Draw_Grh → ${t}`);
    }
    if (/\.speed/.test(t)) {
      fallas.push(`\`${nombre}\` multiplica por la \`speed\` del gráfico, y Draw_Grh no la lee nunca → ${t}`);
    }
  }
}

// ── 3. La cadencia de los FX es OTRA y tiene que seguir siéndolo ──────────────────────────────────────
// Es el único camino que sí usa `speed` (el 0,8 que InitGrh le pone a todo FX). Si alguien "unifica" las dos
// creyendo que es la misma, los hechizos pasan a durar un 60% de más.
const mFx = cuerpo.match(/const\s+FX_MS_POR_CUADRO\s*=\s*([^;]+);/);
if (!mFx) {
  fallas.push('no encontré `FX_MS_POR_CUADRO`: la cadencia de los FX es distinta de la del mundo');
} else {
  let msFx = null;
  try { msFx = Function(`"use strict"; return (${mFx[1]});`)(); } catch { /* lo reporta el if de abajo */ }
  if (typeof msFx !== 'number' || Math.abs(msFx - 69.4) > 0.5) {
    fallas.push(`un cuadro de FX dura ${msFx} ms y el original son 69,4 (1/(0,018*0,8), ` +
                `DrawGrhtoSurface en TileEngine.bas:1788 con el speed 0,8 de InitGrh:1104)`);
  }
  if (BASE != null && PASO != null && typeof msFx === 'number' &&
      Math.abs(msFx - 1 / (BASE * PASO)) < 0.5) {
    fallas.push('los FX quedaron con la MISMA cadencia que el mundo, y en el original son dos rutinas ' +
                'distintas (69,4 ms contra 111,1)');
  }
}

// ── 4. Las tres cadencias del MACRO son las del cliente original ──────────────────────────────────────
// `INT_ATTACK = 1200`, `INT_MACRO_HECHIS = 1400` e `INT_MACRO_TRABAJO = 1000` (Declares.bas:318-320). Las dos
// últimas están DECLARADAS Y SIN USAR en el original —el macro estaba pensado y no llegó—, así que son la
// clase de número que se "ajusta a ojo" con toda la buena intención. Que la repetición ande lo mide
// `fase5/herramientas/medir-macro.js` contra el cliente corriendo; acá se fijan los valores, que es lo que
// ese medidor no puede distinguir de una decisión nuestra.
for (const [nombre, esperado, cita] of [
  ['MACRO_MS_GOLPE', 1200, 'INT_ATTACK'],
  ['MACRO_MS_HECHIZO', 1400, 'INT_MACRO_HECHIS'],
  ['MACRO_MS_USAR', 1000, 'INT_MACRO_TRABAJO'],
]) {
  const m = cuerpo.match(new RegExp(`const\\s+${nombre}\\s*=\\s*(\\d+)`));
  if (!m) fallas.push(`no encontré \`${nombre}\`: es la cadencia del macro (VB6 ${cita})`);
  else if (+m[1] !== esperado)
    fallas.push(`\`${nombre}\` vale ${m[1]} y el original son ${esperado} (${cita}, Declares.bas:318-320)`);
}
// Y que el macro salga PRENDIDO, que es la decisión tomada (2026-08-31). El `!== '0'` es lo que hace que la
// clave AUSENTE —el jugador que nunca la tocó— dé prendido; con `=== '1'` saldría apagado para todo el mundo
// y nadie se enteraría, porque la opción existe igual y el botón dice lo que corresponda.
// `leerPref` devuelve null también con el almacenamiento denegado, y `null !== '0'` sigue dando prendido.
if (!/leerPref\('ao_macro'\)\s*!==\s*'0'/.test(cuerpo)) {
  fallas.push('el macro ya no arranca prendido: se espera `leerPref(\'ao_macro\') !== \'0\'`, ' +
              'que da true cuando el jugador nunca lo tocó');
}

if (fallas.length) {
  console.error('check-cadencia: la animación NO corre a la velocidad del original');
  for (const f of fallas) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`check-cadencia: mundo y personajes a ${(1 / (BASE * PASO)).toFixed(1)} ms por cuadro, ` +
            'FX a 69,4 — como el original');
