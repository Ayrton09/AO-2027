// Verifica EL MOTOR DE PARTÍCULAS contra las reglas del original (mod_Particulas.bas), corriendo las funciones DE
// VERDAD —se sacan de index.html— con un lienzo de mentira que anota dónde se dibujó cada partícula.
//
// Por qué existe. La mitad de los hechizos del juego (50 de 113) no tienen FX: tienen PARTÍCULA. El cliente las
// dibujaba con un remolino de ocho motas igual para todos, tomaba su tiempo como SEGUNDOS —un hechizo con 50
// duraba cincuenta segundos, y el que manda 0, "la vida del grupo", no se veía— y el de área buscaba el número
// de partícula en la tabla de FX. Las del mapa andaban, pero con el paso 3,3 veces más rápido, el sprite
// sorteado de nuevo en cada paso y apoyadas media altura más abajo. Nada de eso lo veía ningún test: los flows
// hablan el protocolo, y por el protocolo todo estaba bien. Reporte del autor, 2026-09-27: "hay hechizos que no
// se ven bien".
//
// Correr:  node check-particulas.mjs   (o `npm test`, que lo incluye)
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const cuerpo = (html.match(/<script type="module">([\s\S]*?)<\/script>/) || [])[1];
if (!cuerpo) {
  console.error('check-particulas: no encontré el <script type="module"> en index.html');
  process.exit(1);
}
const fallas = [];

/** El texto de `function nombre(...) { ... }`, contando llaves. */
function funcion(nombre) {
  const i = cuerpo.indexOf('function ' + nombre + '(');
  if (i < 0) { fallas.push(`no encontré la función \`${nombre}\` en index.html`); return ''; }
  let prof = 0;
  for (let k = cuerpo.indexOf('{', i); k < cuerpo.length; k++) {
    if (cuerpo[k] === '{') prof++;
    else if (cuerpo[k] === '}' && --prof === 0) return cuerpo.slice(i, k + 1);
  }
  fallas.push(`la función \`${nombre}\` no cierra`);
  return '';
}
/** La línea `const nombre = ...;` tal cual está en el fuente. */
function constante(nombre) {
  const m = cuerpo.match(new RegExp(`const\\s+${nombre}\\s*=[^\\n]*;`));
  if (!m) { fallas.push(`no encontré la constante \`${nombre}\` en index.html`); return ''; }
  return m[0];
}

// El motor, armado con las piezas del cliente. Lo que no es del motor se reemplaza por algo que se pueda mirar.
const fuente = [
  constante('TILE'), constante('ENGINE_BASE_SPEED'), constante('CORRIDOS_16'), constante('SUBIDA_DEL_PERSONAJE'),
  constante('PASO_DE_PARTICULA_POR_MS'), constante('azarEntero'), constante('MAX_PARTICULAS'), constante('ALFA_DE_PARTICULA'),
  'let dibujadasEnElCuadro = 0;',
  funcion('crearGrupo'), funcion('quitarGrupo'), funcion('moverParticula'), funcion('dibujarParticula'), funcion('correrGrupo'),
  'return { crearGrupo, quitarGrupo, correrGrupo, nuevoCuadro: () => { dibujadasEnElCuadro = 0; }, SUBIDA_DEL_PERSONAJE };',
].join('\n');

const base = { n: 3, x: [0, 0], y: [0, 0], vx: [0, 0], vy: [0, 0], vida: [100, 100], fric: 1, grav: 0, gravF: 0, rebote: 0,
  spin: 0, spinV: [0, 0], xmove: 0, mx: [0, 0], ymove: 0, my: [0, 0], alpha: 1, speed: 0.5, grhs: [1], colores: [], vidaGrupo: 40,
  resize: 0, rx: 0, ry: 0 };
const partDefs = {
  1: { ...base },
  2: { ...base, grhs: [1, 2, 3], vida: [50, 50] },                        // tres sprites posibles
  3: { ...base, n: 1, resize: 1, rx: 50, ry: 40 },                         // estirada
  25: { ...base, x: [8, 8], y: [8, 8] },                                   // uno de los ocho corridos 16
  4: { ...base, grhs: [9] },                                               // un grh que es animación
  5: { ...base, speed: 2.5 },
};
const atlas = { 1: { file: 1, w: 16, h: 16 }, 2: { file: 1, w: 16, h: 16 }, 3: { file: 1, w: 16, h: 16 }, 9: { frames: [1, 2] } };
const lienzo = { dibujos: [], globalAlpha: 1, globalCompositeOperation: 'source-over',
  drawImage(cv, x, y, w, h) { this.dibujos.push({ grh: cv.grh, x, y, w, h }); }, setTransform() {} };
let motor = null;
if (!fallas.length) {
  try {
    motor = new Function('partDefs', 'atlas', 'colorDelQuad', 'grhTenido', 'partCx', fuente)(
      partDefs, atlas, () => [255, 255, 255], (grh) => ({ cv: { grh }, w: atlas[grh].w, h: atlas[grh].h }), lienzo);
  } catch (e) { fallas.push('el motor sacado de index.html no se pudo armar: ' + e.message); }
}

/** Corre `cuadros` cuadros de `ms` y devuelve cuántos fueron paso (se nota en que la vida de la partícula baja). */
function correr(g, cuadros, ms, ox = 0, oy = 0) {
  let pasos = 0, vivo = true;
  for (let i = 0; i < cuadros && vivo; i++) {
    const antes = g.ps.map((p) => p.vida).join();
    motor.nuevoCuadro();
    vivo = motor.correrGrupo(g, ox, oy, ms);
    if (g.ps.map((p) => p.vida).join() !== antes) pasos++;
  }
  return { pasos, vivo };
}
function esperar(cond, mensaje) { if (!cond) fallas.push(mensaje); }

if (motor) {
  // 1. LA VIDA ES EN PASOS, no en segundos: con 5, nacen en el primer paso y a los cinco pasos no nace ninguna más.
  let g = motor.crearGrupo(1, 5, 0);
  correr(g, 5, 16.7);
  esperar(g.vida === 0 && g.vivas === 3, `con vida 5 el grupo tendría que quedar en 0 a los 5 pasos con 3 nacidas (vida ${g.vida}, nacidas ${g.vivas})`);
  correr(g, 20, 16.7);
  esperar(g.vivas === 3, `después de apagarse no tendría que nacer ninguna (nacidas ${g.vivas})`);

  // 2. VIDA 0 = la del ini (`life_counter`), -1 = para siempre.
  g = motor.crearGrupo(1, 0, 0);
  esperar(g.vida === 40, `con vida 0 el grupo tendría que tomar la del ini (40) y tomó ${g.vida}`);
  g = motor.crearGrupo(1, -1, 0);
  correr(g, 300, 16.7);
  esperar(g.vida === -1 && g.vivas > 3, `con vida -1 el grupo tendría que seguir naciendo (vida ${g.vida}, nacidas ${g.vivas})`);

  // 3. EL PASO: 0,036 por milisegundo y el contador vuelve a cero. Con Speed 2,5: a 60 Hz uno cada 5 cuadros, a
  //    144 Hz uno cada 11. Con el paso viejo (2 por cuadro de 60 Hz) daban 30 y 48 por segundo.
  g = motor.crearGrupo(5, -1, 0);
  let r = correr(g, 60, 1000 / 60);
  esperar(r.pasos === 12, `Speed 2,5 a 60 Hz: tendrían que ser 12 pasos por segundo y fueron ${r.pasos}`);
  g = motor.crearGrupo(5, -1, 0);
  r = correr(g, 144, 1000 / 144);
  esperar(r.pasos === 13, `Speed 2,5 a 144 Hz: tendrían que ser 13 pasos por segundo y fueron ${r.pasos}`);

  // 4. EL SPRITE SE ELIGE AL NACER y es el mismo toda la vida.
  g = motor.crearGrupo(2, -1, 0);
  correr(g, 1, 16.7);
  const alNacer = g.ps.map((p) => p.grh).join();
  correr(g, 20, 16.7);
  esperar(g.ps.map((p) => p.grh).join() === alNacer, 'el sprite de una partícula cambió en medio de su vida');

  // 5. EL ANCLA: centrada en x y apoyada por el borde de ABAJO (`x - ancho/2 + 16`, `y - alto + 32`).
  lienzo.dibujos.length = 0;
  g = motor.crearGrupo(1, -1, 0);
  correr(g, 1, 16.7, 100, 200);
  const d = lienzo.dibujos[0];
  esperar(d && d.x === 108 && d.y === 216 && d.w === 16 && d.h === 16,
    `un sprite de 16x16 en (0,0) de la baldosa (100,200) va en (108,216) y salió en ${d ? `(${d.x},${d.y}) ${d.w}x${d.h}` : 'ningún lado'}`);

  // 6. ESTIRADA (`resize`): sin centrar, rx de ALTO y ry de ANCHO, como se los pasa Grh_Render_Advance.
  lienzo.dibujos.length = 0;
  g = motor.crearGrupo(3, -1, 0);
  correr(g, 1, 16.7, 100, 200);
  const e = lienzo.dibujos[0];
  esperar(e && e.x === 100 && e.y === 200 && e.w === 40 && e.h === 50,
    `la estirada va en (100,200) con 40 de ancho y 50 de alto y salió en ${e ? `(${e.x},${e.y}) ${e.w}x${e.h}` : 'ningún lado'}`);

  // 7. EL PERSONAJE sube sus rangos 15, y los ocho grupos corridos, 16.
  g = motor.crearGrupo(1, -1, motor.SUBIDA_DEL_PERSONAJE);
  esperar(motor.SUBIDA_DEL_PERSONAJE === 15 && g.y1 === -15 && g.y2 === -15, `el personaje tendría que subir 15 (subió ${-g.y1})`);
  g = motor.crearGrupo(25, -1, 0);
  esperar(g.x1 === -8 && g.y1 === -8, `el grupo 25 tendría que correrse 16 (quedó en ${g.x1},${g.y1})`);

  // 8. UN GRH QUE ES ANIMACIÓN NO SE VE (Grh_Render dibuja con el FileNum de la animación, que queda en 0).
  lienzo.dibujos.length = 0;
  g = motor.crearGrupo(4, -1, 0);
  correr(g, 3, 16.7);
  esperar(lienzo.dibujos.length === 0, `un grh animado no tendría que dibujarse y se dibujó ${lienzo.dibujos.length} veces`);

  // 9. QUITAR apaga sin cortar: no nace ninguna más, las que están terminan y recién ahí se suelta el grupo.
  g = motor.crearGrupo(2, -1, 0);
  correr(g, 10, 16.7);
  motor.quitarGrupo(g);
  const nacidas = g.vivas;
  r = correr(g, 40, 16.7);
  esperar(r.vivo && g.vivas === nacidas, 'al quitar un grupo las partículas vivas tendrían que terminar su vida');
  r = correr(g, 100, 16.7);
  esperar(!r.vivo, 'un grupo quitado tendría que soltarse cuando ya no se ve ninguna');
}

// 10. LOS PAQUETES llegan al motor: la partícula del personaje y la de la baldosa arman grupos, y la de la baldosa
//     ya no se busca en la tabla de FX.
const caso = (nombre) => {
  const i = cuerpo.indexOf(`case '${nombre}':`);
  const j = cuerpo.indexOf('case \'', i + 10);
  return i < 0 ? '' : cuerpo.slice(i, j);
};
const deChar = caso('EfectoCharParticula'), deBaldosa = caso('EfectoTerrenoParticula');
esperar(/crearGrupo\(/.test(deChar) && !/\*\s*1000/.test(deChar), '`EfectoCharParticula` tiene que armar un grupo con la vida en pasos');
esperar(/crearGrupo\(/.test(deBaldosa) && !/groundFx/.test(deBaldosa), '`EfectoTerrenoParticula` tiene que armar un grupo en la baldosa, no un FX');
// 11. EL FX SUMA LUZ (DrawGrhtoSurface con AlphaB = 1): se dibuja con `lighter`.
esperar(/'lighter'/.test(funcion('drawFx')), 'el FX del personaje tiene que dibujarse aditivo (`lighter`)');

if (fallas.length) {
  console.error('check-particulas: FALLA');
  for (const f of fallas) console.error('  - ' + f);
  process.exit(1);
}
console.log('check-particulas: el motor sigue al original — vida en pasos, paso de 0,036 por ms, sprite fijo, ancla abajo, estirada, 15 y 16, animación invisible, quitar sin cortar');
