// Cruza la tabla GM_COMANDOS del cliente contra los ids que el server DESPACHA de verdad.
//
// La tabla del cliente está escrita a mano y `buscarComandoGm` devuelve null para todo lo que no esté en
// ella: no hay camino genérico. O sea que un id que falte no es "difícil de encontrar", es INALCANZABLE, y
// nada lo delata — el server compila, el cliente parsea, los flows pasan, y el comando simplemente no existe
// para nadie. Así quedaron afuera diez, incluidos los dos que el cliente ya sabía DIBUJAR: el panel de
// conectados y la caja con la respuesta del GM eran decodificadores vivos que nadie disparaba.
//
// Se cruza por ID y nunca por nombre: el cliente y el server bautizan distinto.
//
// Correr:  node check-comandos-gm.mjs   (o `npm test`, que lo incluye)
import { readFileSync } from 'node:fs';

const RUTA_SERVER = new URL('../server/src/CoverAO.Server/Net/PacketDispatcher.cs', import.meta.url);

// Los que el server atiende A PROPÓSITO sin hacer nada, con su motivo en el propio switch. Están para que el
// stream no se desincronice, no para que el jugador los pueda tipear.
const MUDOS_A_PROPOSITO = new Map([
  [34, '/LIMPIAR: el HandleCleanWorld del original no hace nada después de chequear privilegios'],
  [58, '/LASTIP: en el original está todo comentado, no devuelve una sola letra'],
  [66, '/APASS: no se porta — copia una contraseña en claro y acá se guarda un PBKDF2'],
]);

function fallar(msg) {
  console.error('check-comandos-gm: ' + msg);
  process.exit(1);
}

// El cuerpo de HandleGMCommands, acotado por llaves. Acotarlo importa: el archivo tiene otros switch con
// `case N:` (el /MOD numera sus opciones del 1 al 20) y contarlos daría ids que no existen.
function cuerpoDelSwitch(fuente) {
  const inicio = fuente.indexOf('private void HandleGMCommands');
  if (inicio < 0) return null;
  let i = fuente.indexOf('{', inicio);
  if (i < 0) return null;
  let profundidad = 0;
  for (let j = i; j < fuente.length; j++) {
    if (fuente[j] === '{') profundidad++;
    else if (fuente[j] === '}' && --profundidad === 0) return fuente.slice(i, j);
  }
  return null;
}

let fuente;
try {
  fuente = readFileSync(RUTA_SERVER, 'utf8');
} catch (e) {
  // Callar acá sería el mismo defecto que este check existe para cazar.
  fallar(`no pude leer el despachador del server (${e.code}). Sin él este cruce no prueba nada.`);
}

const cuerpo = cuerpoDelSwitch(fuente);
if (!cuerpo) fallar('no encontré el cuerpo de HandleGMCommands en el despachador');

const delServer = new Set([...cuerpo.matchAll(/^\s*case\s+(\d+)\s*:/gm)].map((m) => Number(m[1])));
if (delServer.size < 50) fallar(`sólo leí ${delServer.size} ids del server; el parseo se rompió`);

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const tabla = html.match(/const GM_COMANDOS = \{([\s\S]*?)\n\s*\};/);
if (!tabla) fallar('no encontré GM_COMANDOS en index.html');
const delCliente = new Set([...tabla[1].matchAll(/:\s*\[(\d+)\s*,/g)].map((m) => Number(m[1])));
if (delCliente.size < 50) fallar(`sólo leí ${delCliente.size} ids del cliente; el parseo se rompió`);

const faltan = [...delServer].filter((id) => !delCliente.has(id) && !MUDOS_A_PROPOSITO.has(id)).sort((a, b) => a - b);
const sobran = [...delCliente].filter((id) => !delServer.has(id)).sort((a, b) => a - b);

if (faltan.length) {
  console.error('check-comandos-gm: el server despacha ids que el cliente NO PUEDE PEDIR:');
  for (const id of faltan) {
    const linea = cuerpo.match(new RegExp(`^\\s*case\\s+${id}\\s*:.*$`, 'm'));
    console.error(`  ${id}  ${(linea ? linea[0] : '').trim()}`);
  }
  console.error('  agregalos a GM_COMANDOS en index.html, o al mapa MUDOS_A_PROPOSITO con su motivo');
  process.exit(1);
}
if (sobran.length) {
  console.error(`check-comandos-gm: el cliente manda ids que el server no atiende: ${sobran.join(', ')}`);
  console.error('  el server los lee como argumentos del siguiente paquete y le corta el stream al GM');
  process.exit(1);
}

console.log(`check-comandos-gm: ${delCliente.size} comandos GM alcanzables, ` +
            `${MUDOS_A_PROPOSITO.size} mudos a propósito, 0 inalcanzables`);
