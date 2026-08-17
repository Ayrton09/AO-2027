/**
 * Byte-exact tests for the TypeScript codec — the same vectors as the Python and C# codecs, so all
 * three agree byte-for-byte with the VB6 peer.  Run:  npm test
 */
import {
  ByteQueueWriter, ByteQueueReader, xorBuffer, DEFAULT_XOR_KEY, NotEnoughData,
} from '../src/codec/bytequeue.js';
import { textoDelFlotante } from '../src/protocol/localeSmg.js';

// Minimal Node global (avoids a @types/node dependency for this standalone test).
declare const process: { exit(code: number): never };

let passed = 0;
let failed = 0;

function bytes(...n: number[]): Uint8Array { return Uint8Array.from(n); }

function eqBytes(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

function check(name: string, got: Uint8Array, want: Uint8Array): void {
  if (eqBytes(got, want)) { passed++; }
  else { failed++; console.log(`  FAIL ${name}\n    got  ${[...got]}\n    want ${[...want]}`); }
}

function assert(name: string, cond: boolean): void {
  if (cond) passed++; else { failed++; console.log(`  FAIL ${name}`); }
}

// exact little-endian layout
check('byte', new ByteQueueWriter().writeByte(200).toBytes(), bytes(0xc8));
check('int 258', new ByteQueueWriter().writeInteger(258).toBytes(), bytes(0x02, 0x01));
check('int -1', new ByteQueueWriter().writeInteger(-1).toBytes(), bytes(0xff, 0xff));
check('long 70000', new ByteQueueWriter().writeLong(70000).toBytes(), bytes(0x70, 0x11, 0x01, 0x00));
check('long -1', new ByteQueueWriter().writeLong(-1).toBytes(), bytes(0xff, 0xff, 0xff, 0xff));
check('bool true', new ByteQueueWriter().writeBoolean(true).toBytes(), bytes(0x01));
check('bool false', new ByteQueueWriter().writeBoolean(false).toBytes(), bytes(0x00));

// ascii var string: int16 char-count prefix + Latin-1 bytes
check('ascii AB', new ByteQueueWriter().writeAsciiString('AB').toBytes(), bytes(0x02, 0x00, 0x41, 0x42));
check('ascii empty', new ByteQueueWriter().writeAsciiString('').toBytes(), bytes(0x00, 0x00));
check('ascii a-acute', new ByteQueueWriter().writeAsciiString('á').toBytes(), bytes(0x01, 0x00, 0xe1));

// unicode var string: int16 char-count prefix + UTF-16LE
check('unicode AB', new ByteQueueWriter().writeUnicodeString('AB').toBytes(), bytes(0x02, 0x00, 0x41, 0x00, 0x42, 0x00));

// composite frame [id=3][ascii "Hola AO"]
check('composite frame',
  new ByteQueueWriter().writeByte(3).writeAsciiString('Hola AO').toBytes(),
  bytes(0x03, 0x07, 0x00, 0x48, 0x6f, 0x6c, 0x61, 0x20, 0x41, 0x4f));

// reader round-trips every type
{
  const w = new ByteQueueWriter()
    .writeByte(255).writeInteger(-30000).writeLong(123456789)
    .writeSingle(3.5).writeDouble(-9.75).writeBoolean(true)
    .writeAsciiString('Rugura').writeUnicodeString('Nandu');
  const r = new ByteQueueReader(w.toBytes());
  assert('rt byte', r.readByte() === 255);
  assert('rt int', r.readInteger() === -30000);
  assert('rt long', r.readLong() === 123456789);
  assert('rt single', Math.abs(r.readSingle() - 3.5) < 1e-6);
  assert('rt double', r.readDouble() === -9.75);
  assert('rt bool', r.readBoolean() === true);
  assert('rt ascii', r.readAsciiString() === 'Rugura');
  assert('rt unicode', r.readUnicodeString() === 'Nandu');
  assert('rt at end', r.atEnd);
}

// XOR symmetry + default key
{
  const payload = new ByteQueueWriter().writeByte(0).writeAsciiString('secret').toBytes();
  const enc = xorBuffer(payload, 200);
  assert('xor changes data', !eqBytes(enc, payload));
  assert('xor symmetric', eqBytes(xorBuffer(enc, 200), payload));
  check('xor default key 13', xorBuffer(bytes(0, 1, 2), DEFAULT_XOR_KEY), bytes(0 ^ 13, 1 ^ 13, 2 ^ 13));
}

// underflow protection
{
  let threw = false;
  try { new ByteQueueReader(bytes(1)).readLong(); } catch (e) { threw = e instanceof NotEnoughData; }
  assert('underflow throws', threw);
}

// El numero flotante de ChatOverHeadLocale (modos 2 y 3). Tres reglas del cliente original que el web no
// tenia: el "!" arriba de 150, el FALLO en 0 -- con texto distinto segun quien fallo -- y el numero SIN signo.
{
  assert('flotante normal', textoDelFlotante(37, false) === '37');
  assert('flotante sin signo (la cura usa el mismo paquete)', textoDelFlotante(37, true) === '37');
  assert('flotante 150 todavia no grita', textoDelFlotante(150, false) === '150');
  assert('flotante > 150 grita', textoDelFlotante(151, false) === '¡151!');
  assert('fallo propio', textoDelFlotante(0, true) === '*Fallas*');
  assert('fallo ajeno', textoDelFlotante(0, false) === '*Falla*');
  assert('fallo con negativo', textoDelFlotante(-3, false) === '*Falla*');
}

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
