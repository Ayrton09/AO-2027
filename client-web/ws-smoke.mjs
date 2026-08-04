// End-to-end WebSocket client test (Fase 3): a JS client using the TS codec + SC decoder logs into
// the modern server over WebSocket, decodes the login sequence, then walks and sees the position
// update — the full networking layer a Pixi renderer would sit on top of.
//
// Run (server up, `npm run build` done):  node ws-smoke.mjs
import { ByteQueueWriter, xorBuffer, DEFAULT_XOR_KEY } from './dist/src/codec/bytequeue.js';
import { decodeServerStream } from './dist/src/protocol/serverPackets.js';

const URL = process.argv[2] || 'ws://localhost:7667/';
const CLIENT = { LoginExistingChar: 1, Walk: 0, LeftClick: 19, CombatModeToggle: 8, CastSpell: 18 };

const ws = new WebSocket(URL);
ws.binaryType = 'arraybuffer';
let inbox = new Uint8Array(0);
ws.onmessage = (ev) => {
  const c = new Uint8Array(ev.data);
  const m = new Uint8Array(inbox.length + c.length);
  m.set(inbox); m.set(c, inbox.length);
  inbox = m;
};
ws.onerror = (e) => { console.log('WS error:', e.message || e); process.exit(1); };

const wait = (ms) => new Promise((res) => setTimeout(res, ms));
function takeInbox() { const b = inbox; inbox = new Uint8Array(0); return b; }

function login() {
  return new ByteQueueWriter()
    .writeByte(CLIENT.LoginExistingChar)
    .writeAsciiString('cuenta_test').writeAsciiString('hash_password')
    .writeByte(1).writeByte(4).writeByte(5)
    .writeAsciiString('Rugura').writeAsciiString('mac').writeLong(1)
    .toBytes();
}

ws.onopen = async () => {
  ws.send(xorBuffer(login(), DEFAULT_XOR_KEY));   // login XOR'd with initial key 13
  await wait(800);
  const boot = decodeServerStream(takeInbox());
  const logged = boot.find((e) => e.name === 'Logged');
  const cc = boot.find((e) => e.name === 'CharacterCreate' && e.fields.name === 'Rugura');
  const redundance = logged?.fields.redundance;
  console.log(`  login: ${boot.length} paquetes decodificados; Logged.redundance=${redundance}`);
  if (cc) console.log(`  CharacterCreate: ${cc.fields.name} en (${cc.fields.x},${cc.fields.y})`);

  // walk east, XOR'd with the negotiated session key
  const walk = new ByteQueueWriter().writeByte(CLIENT.Walk).writeByte(2).toBytes();
  ws.send(xorBuffer(walk, redundance));
  await wait(400);
  const after = decodeServerStream(takeInbox());
  const pos = after.find((e) => e.name === 'PosUpdate');
  if (pos) console.log(`  tras caminar al este: PosUpdate (${pos.fields.x},${pos.fields.y})`);

  // Click izquierdo sobre una criatura del fixture (52,53): la respuesta es CharMsgStatusNPC, un paquete que
  // el decoder tiene que saber leer entero — el protocolo no lleva length-prefix, asi que uno mal leido se come
  // los bytes del siguiente y desincroniza TODO el stream. Por eso se camina DESPUES del click: si el
  // CharMsgStatusNPC quedara mal decodificado, el PosUpdate de esa caminata no aparece.
  const click = new ByteQueueWriter().writeByte(CLIENT.LeftClick).writeByte(52).writeByte(53).toBytes();
  ws.send(xorBuffer(click, redundance));
  await wait(400);
  const clicked = decodeServerStream(takeInbox());
  const st = clicked.find((e) => e.name === 'CharMsgStatusNPC');
  if (st) console.log(`  click en la criatura: CharMsgStatusNPC numero=${st.fields.numero} vida=${st.fields.vida} exacta=${st.fields.vidaExacta}`);

  const walk2 = new ByteQueueWriter().writeByte(CLIENT.Walk).writeByte(2).toBytes();
  ws.send(xorBuffer(walk2, redundance));
  await wait(400);
  const pos2 = decodeServerStream(takeInbox()).find((e) => e.name === 'PosUpdate');
  if (pos2) console.log(`  y el stream sigue sano: PosUpdate (${pos2.fields.x},${pos2.fields.y})`);

  // HECHIZO DE AREA (slot 7 = Bomba magica): emite EfectoTerrenoFX(93) + PlayWave, paquetes que van por
  // COORDENADAS y no colgados de un charIndex. Su hermano EfectoTerrenoParticula(92) lleva un Long de 4 bytes;
  // leerlo corto desincroniza todo lo que venga detras, y sin length-prefix eso no se nota hasta que el mundo
  // se rompe. De ahi la caminata final: si el FX del terreno quedara mal decodificado, el PosUpdate no llega.
  // Rugura es GM y saltea PuedeLanzar (la Bomba pide 38 de Magia y el fixture tiene 15), pero el gate de modo
  // combate NO exime a los GMs: hay que prenderlo antes o el hechizo rebota con LocaleMsg(102).
  ws.send(xorBuffer(new ByteQueueWriter().writeByte(CLIENT.CombatModeToggle).toBytes(), redundance));
  await wait(250);
  takeInbox();
  ws.send(xorBuffer(new ByteQueueWriter().writeByte(CLIENT.CastSpell).writeByte(7).toBytes(), redundance));
  await wait(600);
  const casted = decodeServerStream(takeInbox());
  const terreno = casted.find((e) => e.name === 'EfectoTerrenoFX');
  if (terreno) console.log(`  hechizo de area: EfectoTerrenoFX fx=${terreno.fields.fx} en (${terreno.fields.x},${terreno.fields.y})`);

  const walk3 = new ByteQueueWriter().writeByte(CLIENT.Walk).writeByte(2).toBytes();
  ws.send(xorBuffer(walk3, redundance));
  await wait(400);
  const pos3 = decodeServerStream(takeInbox()).find((e) => e.name === 'PosUpdate');
  if (pos3) console.log(`  y el stream sigue sano tras el FX del terreno: PosUpdate (${pos3.fields.x},${pos3.fields.y})`);

  try { ws.close(); } catch {}
  const ok = !!logged && !!cc && redundance != null && !!pos &&
             pos.fields.x === (cc.fields.x) + 1 && pos.fields.y === cc.fields.y &&
             !!st && !!pos2 && pos2.fields.x === cc.fields.x + 2 &&
             !!terreno && !!pos3 && pos3.fields.x === cc.fields.x + 3;
  console.log(`\n  [${ok ? 'OK' : 'FALLO'}] cliente web: login + caminar + click + FX de area, todo sin desync`);
  console.log(`Cliente web (WS) ${ok ? 'OK' : 'FALLO'}`);
  process.exit(ok ? 0 : 1);
};
