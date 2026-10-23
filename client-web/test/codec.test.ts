/**
 * Byte-exact tests for the TypeScript codec — the same vectors as the Python and C# codecs, so all
 * three agree byte-for-byte with the VB6 peer.  Run:  npm test
 */
import {
  ByteQueueWriter, ByteQueueReader, xorBuffer, DEFAULT_XOR_KEY, NotEnoughData, MAX_CADENA, byteCp1252, caracterCp1252,
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
check('ascii fuera del 1252 se aproxima como el server', new ByteQueueWriter().writeAsciiString('aču').toBytes(), bytes(0x03, 0x00, 0x61, 0x63, 0x75));

// Windows-1252 como el ByteQueueWriter/Reader del server (Codec.Ansi = Encoding.GetEncoding(1252) de .NET).
// Los bytes esperados salen de pasar los mismos textos por el codec del server.
check('cp1252 euro', new ByteQueueWriter().writeAsciiString('€5').toBytes(), bytes(0x02, 0x00, 0x80, 0x35));
check('cp1252 comillas tipográficas',
  new ByteQueueWriter().writeAsciiString('“hola” ‘y’').toBytes(),
  bytes(0x0a, 0x00, 0x93, 0x68, 0x6f, 0x6c, 0x61, 0x94, 0x20, 0x91, 0x79, 0x92));
check('cp1252 puntos, guiones y Œ', new ByteQueueWriter().writeAsciiString('…–—Œ™').toBytes(),
  bytes(0x05, 0x00, 0x85, 0x96, 0x97, 0x8c, 0x99));
check('cp1252 huecos como el control del mismo número',
  new ByteQueueWriter().writeAsciiString('\u0081\u008d\u008f\u0090\u009d').toBytes(),
  bytes(0x05, 0x00, 0x81, 0x8d, 0x8f, 0x90, 0x9d));
check('cp1252 controles C1 que no son huecos: ?', new ByteQueueWriter().writeAsciiString('\u0080\u0093').toBytes(),
  bytes(0x02, 0x00, 0x3f, 0x3f));
check('cp1252 aproximados', new ByteQueueWriter().writeAsciiString('Łő─Ａ∞').toBytes(),
  bytes(0x05, 0x00, 0x4c, 0x6f, 0x2d, 0x41, 0x38));
check('cp1252 no representable: ? por unidad, el largo no cambia',
  new ByteQueueWriter().writeAsciiString('a中\u{1F600}b').toBytes(),
  bytes(0x05, 0x00, 0x61, 0x3f, 0x3f, 0x3f, 0x62));
{
  const r = new ByteQueueReader(bytes(0x06, 0x00, 0x80, 0x93, 0x94, 0x81, 0x9d, 0x9f));
  assert('cp1252 lectura de 0x80-0x9F', r.readAsciiString() === '€“”\u0081\u009dŸ');
  const altos = [
    0x20ac, 0x0081, 0x201a, 0x0192, 0x201e, 0x2026, 0x2020, 0x2021, 0x02c6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008d, 0x017d, 0x008f,
    0x0090, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014, 0x02dc, 0x2122, 0x0161, 0x203a, 0x0153, 0x009d, 0x017e, 0x0178,
  ];
  let todos = true;
  for (let b = 0; b < 256; b++) {
    const quiere = b >= 0x80 && b <= 0x9f ? altos[b - 0x80] : b;
    if (caracterCp1252(b) !== quiere || byteCp1252(caracterCp1252(b)) !== b) todos = false;
  }
  assert('cp1252 los 256 bytes se leen como el server y vuelven al mismo byte', todos);
}
{
  const largo = new ByteQueueWriter().writeAsciiString('x'.repeat(70000)).toBytes();
  check('ascii recortado al tope del server', largo.slice(0, 2), bytes(MAX_CADENA & 0xff, MAX_CADENA >> 8));
  assert('ascii recortado: el largo', largo.length === 2 + MAX_CADENA);
}

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
