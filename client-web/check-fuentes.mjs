// ¿Los colores del chat siguen siendo los del original?
//
// El último byte del `ConsoleMsg` es el COLOR de la línea, y el cliente VB6 lo usa como índice DIRECTO en
// `fonttypes.ind`. Esa tabla está copiada a mano dentro de `index.html` (34 entradas), porque el cliente web
// es un único script sin paso de build y no tiene de dónde leerla en caliente.
//
// Una copia a mano se desincroniza sin avisar y el síntoma es mudo: el clan sigue apareciendo en el chat,
// sólo que del color equivocado. Nadie lo mira dos veces. Esto lo ata a la fuente de verdad.
//
// Correr:  node check-fuentes.mjs   (o `npm test`, que lo incluye)
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const AQUI = dirname(fileURLToPath(import.meta.url));
const IND = join(AQUI, '..', 'CoverAO-2021', 'CoverAO-2021', 'Herramientas', 'Comprensor',
                 'Descomprimido', 'init', 'fonttypes.ind');

// El .ind es `N=~r~g~b~negrita~cursiva` (el primer campo, antes del primer ~, viene vacío).
const original = new Map();
for (const linea of readFileSync(IND, 'latin1').split(/\r?\n/)) {
  const m = /^(\d+)=(.*)$/.exec(linea.trim());
  if (!m) continue;
  const campos = m[2].split('~');
  original.set(Number(m[1]), [1, 2, 3, 4, 5].map((k) => Number(campos[k]) || 0));
}
if (original.size === 0) {
  console.error(`check-fuentes: no pude leer ninguna fuente de ${IND}`);
  process.exit(1);
}

const html = readFileSync(join(AQUI, 'index.html'), 'utf8');
const bloque = /const FUENTES = \[null\]\.concat\(\[([\s\S]*?)\]\.map\(/.exec(html);
if (!bloque) {
  console.error('check-fuentes: no encontré la tabla FUENTES en index.html');
  process.exit(1);
}
const nuestras = [...bloque[1].matchAll(/\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]/g)]
  .map((m) => m.slice(1).map(Number));

const fallas = [];
if (nuestras.length !== original.size) {
  fallas.push(`la tabla tiene ${nuestras.length} fuentes y el original ${original.size}`);
}
for (const [n, esperada] of original) {
  const mia = nuestras[n - 1];               // el .ind arranca en 1; el array, en 0
  if (!mia) { fallas.push(`falta la fuente ${n}`); continue; }
  if (mia.join(',') !== esperada.join(',')) {
    fallas.push(`fuente ${n}: tenemos [${mia}] y el original dice [${esperada}]`);
  }
}

if (fallas.length) {
  console.error('check-fuentes: la tabla de colores NO coincide con fonttypes.ind');
  for (const f of fallas) console.error(`  ${f}`);
  process.exit(1);
}
console.log(`check-fuentes: las ${nuestras.length} fuentes coinciden con fonttypes.ind`);
