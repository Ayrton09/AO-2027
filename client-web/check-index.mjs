// Verifica que el <script type="module"> de index.html PARSEE.
//
// El cliente vive en un único script inline sin paso de build, así que un error de sintaxis —una llave de
// más, una variable declarada dos veces— no rompe una parte: **no ejecuta NADA**. La página carga, se ve el
// login, y todo está muerto. Peor: el panel del navegador no siempre lo reporta (console vacía, sin requests),
// así que se puede perder un rato largo buscando el bug en el lugar equivocado.
//
// Esto lo detecta en un segundo. Los `import` se quitan antes de parsear porque `new Function` no los admite;
// lo que interesa es la sintaxis del cuerpo, que es donde se edita.
//
// Correr:  node check-index.mjs   (o `npm test`, que lo incluye)
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const m = html.match(/<script type="module">([\s\S]*?)<\/script>/);
if (!m) {
  console.error('check-index: no encontré el <script type="module"> en index.html');
  process.exit(1);
}
const cuerpo = m[1].replace(/^\s*import[^;]*;/gm, '');
try {
  new Function(cuerpo);
} catch (e) {
  console.error(`check-index: index.html NO PARSEA → ${e.message}`);
  console.error('  (un error de sintaxis acá deja el cliente entero sin ejecutar, no sólo la parte tocada)');
  process.exit(1);
}
console.log('check-index: el script de index.html parsea OK');
