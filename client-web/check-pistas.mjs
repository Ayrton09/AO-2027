// TODA PISTA TIENE QUE COLGAR DE ALGO QUE EL SERVER MANDE DE VERDAD.
//
// Las pistas explican los rechazos que son FIELES al original pero se leen como un bug (informe del tester).
// Cuelgan de dos cosas distintas: un id de `LocaleMsg`, o el texto de un `ConsoleMsg`. Las dos se pueden
// quedar huérfanas sin que nada avise — la pista simplemente NO SE MUESTRA NUNCA, y no hay error de JS, ni
// línea en consola, ni test rojo. Es código muerto que parece una feature.
//
// EXISTE POR UN ERROR REAL, el 2026-08-21: la pista de la parálisis colgaba del `LocaleMsg 136`, que es el id
// cuyo texto en el catálogo dice "¡Tu ataque paralizó a la criatura!". Parecía obvio. Pero el server NO manda
// ese id en ese camino: manda un `ConsoleMsg` con el texto escrito a mano. La pista habría viajado a
// producción sin mostrarse jamás. Se encontró leyendo el server, no corriendo nada — por eso hace falta esto.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const RAIZ = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const INDEX = join(RAIZ, 'client-web/index.html');
const SERVER = join(RAIZ, 'server/src/CoverAO.Server');

function fuentesDelServer(dir, acc = []) {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) fuentesDelServer(p, acc);
    else if (n.endsWith('.cs')) acc.push(readFileSync(p, 'utf8'));
  }
  return acc;
}
const server = fuentesDelServer(SERVER).join('\n');
const html = readFileSync(INDEX, 'utf8');

const problemas = [];

// --- 1. Los ids de locale -------------------------------------------------------------------------------
// Se lee el bloque `const PISTAS_DE_RECHAZO = { ... };` y se sacan las claves numéricas del primer nivel.
const bloque = /const PISTAS_DE_RECHAZO = \{([\s\S]*?)\n {4}\};/.exec(html);
if (!bloque) {
  problemas.push('no encontré el bloque PISTAS_DE_RECHAZO en index.html (¿lo renombraron?)');
}
const ids = bloque ? [...bloque[1].matchAll(/^\s{6}(\d+):/gm)].map((m) => +m[1]) : [];

for (const id of ids) {
  // El server puede escribirlo como `LocaleMsg(8)` o `LocaleMsg(8, algo)`.
  if (!new RegExp(`LocaleMsg\\(${id}[,)]`).test(server))
    problemas.push(`la pista del locale ${id} cuelga de un id que el server NUNCA manda`);
}

// --- 2. Los textos de ConsoleMsg ------------------------------------------------------------------------
// Cada `pista(...)` disparada por un `startsWith('…')` / `includes('…')` sobre `f.chat`.
const porTexto = [...html.matchAll(/f\.chat\.(?:startsWith|includes)\('([^']+)'\)\s*\)?\s*pista\(/g)]
  .map((m) => m[1]);
for (const texto of porTexto) {
  if (!server.includes(texto))
    problemas.push(`la pista atada al texto "${texto}" no coincide con ningún literal del server`);
}

// --- 3. La premisa --------------------------------------------------------------------------------------
// Sin esto, un regex que dejara de matchear haría que este check pasara sin mirar nada — el verde que sale de
// no hacer nada. No se fija un número exacto: sólo que la extracción siga encontrando de las dos clases.
if (ids.length === 0) problemas.push('no extraje ni un id de PISTAS_DE_RECHAZO: ¿cambió el formato del bloque?');
if (porTexto.length === 0) problemas.push('no extraje ni una pista atada a texto: ¿cambió la forma de dispararlas?');

if (problemas.length) {
  problemas.forEach((p) => console.error('  ' + p));
  console.error(`check-pistas: ${problemas.length} problema(s)`);
  process.exit(1);
}
console.log(`check-pistas: ${ids.length} pistas por id de locale y ${porTexto.length} por texto, todas las manda el server`);
