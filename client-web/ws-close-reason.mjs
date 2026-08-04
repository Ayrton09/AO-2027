// El server cierra la conexión CON MOTIVO (Transports.cs WebSocketTransport.Close): el cliente web lo lee en
// `ev.reason` y así distingue un cierre deliberado —lo echaron, lo banearon, pidió /SALIR— de una caída de red.
// Sin eso el navegador sólo ve un 1006 y reconectaba SIEMPRE: dos pestañas con el mismo personaje se expulsaban
// en bucle infinito, y el /SALIR se deshacía solo al reloguear el cliente 1,5 s después.
//
// Este test abre DOS clientes con el mismo personaje: el segundo dispara el gate de sesión y el PRIMERO tiene que
// recibir un cierre limpio (código 1000) con reason 'login duplicado'.
//
// Correr (server arriba en el WS 7667):  node ws-close-reason.mjs [url] [personaje]
import { ByteQueueWriter, xorBuffer, DEFAULT_XOR_KEY } from './dist/src/codec/bytequeue.js';

const URL = process.argv[2] || 'ws://localhost:7667/';
const CHAR = process.argv[3] || 'Cazador';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function loginBytes(name) {
  return new ByteQueueWriter().writeByte(1)               // LoginExistingChar
    .writeAsciiString('cuenta_test').writeAsciiString('hash_password')
    .writeByte(1).writeByte(4).writeByte(5)
    .writeAsciiString(name).writeAsciiString('mac').writeLong(1)
    .toBytes();
}

function abrir(etiqueta) {
  const ws = new WebSocket(URL);
  ws.binaryType = 'arraybuffer';
  const cierre = new Promise((res) => { ws.onclose = (ev) => res({ code: ev.code, reason: ev.reason }); });
  ws.onerror = () => {};
  const abierto = new Promise((res) => { ws.onopen = () => res(); });
  return { ws, etiqueta, cierre, abierto };
}

const fallos = [];
const check = (ok, msg) => { console.log(`  ${ok ? 'OK  ' : 'FALLO'} ${msg}`); if (!ok) fallos.push(msg); };

const a = abrir('A');
await a.abierto;
a.ws.send(xorBuffer(loginBytes(CHAR), DEFAULT_XOR_KEY));
await wait(1200);

const b = abrir('B');
await b.abierto;
b.ws.send(xorBuffer(loginBytes(CHAR), DEFAULT_XOR_KEY));

const cierreA = await Promise.race([a.cierre, wait(4000).then(() => null)]);
check(cierreA !== null, 'al primer cliente lo echa el segundo login');
check(cierreA?.reason === 'login duplicado', `el cierre trae el motivo (reason=${JSON.stringify(cierreA?.reason)})`);
check(cierreA?.code === 1000, `cierre limpio, no 1006 abrupto (code=${cierreA?.code})`);

// El segundo cliente NO se cierra: el server lo rechaza con ShowMessageBox y lo deja reintentar.
const cierreB = await Promise.race([b.cierre, wait(1000).then(() => 'sigue-abierto')]);
check(cierreB === 'sigue-abierto', 'al segundo cliente no lo cierran: puede reintentar');
try { b.ws.close(); } catch { /* ignore */ }

console.log(fallos.length ? `\n=== ${fallos.length} FALLO(S) ===` : '\n=== cierre con motivo: OK ===');
process.exit(fallos.length ? 1 : 0);
