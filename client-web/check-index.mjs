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

// EL ALMACENAMIENTO PUEDE ESTAR DENEGADO. Con las cookies bloqueadas el solo acceso a `localStorage` tira
// SecurityError, y uno suelto en el arranque deja el cliente sin ejecutar igual que un error de sintaxis. Todo
// acceso va por `leerPref`/`guardarPref`/`borrarPref` o adentro de un `try` en la misma línea.
const sueltos = [];
const lineaDelScript = html.slice(0, m.index).split('\n').length;
m[1].split('\n').forEach((linea, i) => {
  const t = linea.trim();
  if (t.startsWith('//') || t.startsWith('*') || t.startsWith('/*')) return;
  if (/\b(?:localStorage|sessionStorage)\s*[.[]/.test(linea) && !/\btry\s*\{/.test(linea)) sueltos.push(`${lineaDelScript + i}: ${t}`);
});
if (sueltos.length) {
  console.error('check-index: acceso a localStorage fuera de un try (con el almacenamiento denegado corta el arranque):');
  for (const l of sueltos) console.error(`  index.html:${l}`);
  process.exit(1);
}

// EL coverao-config.json NO PUEDE TRABAR LA CONEXIÓN. Los dos pedidos (el del arranque, que decide el puerto, y
// el de la reconexión) llevan un plazo: sin él, un pedido que no contesta deja al cliente sin conectar nunca.
const pedidos = cuerpo.match(/fetch\(\s*'\.\/coverao-config\.json'[^)]*\)/g) || [];
const sinPlazo = pedidos.filter((p) => !/signal\s*:/.test(p));
if (pedidos.length === 0 || sinPlazo.length) {
  console.error(`check-index: fetch de coverao-config.json sin plazo (signal): ${sinPlazo.join(' | ') || 'no encontré ninguno'}`);
  process.exit(1);
}
console.log(`check-index: el script de index.html parsea OK, sin localStorage suelto, ${pedidos.length} pedidos de configuración con plazo`);
