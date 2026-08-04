/**
 * CoverAO / Argentum Online wire codec — TypeScript implementation (Fase 3, web client).
 *
 * Third faithful port of the VB6 `clsByteQueue` (after the C# server and Python reference), verified
 * byte-for-byte against the same test vectors. This is what the future web client uses to talk the
 * classic AO protocol to the modern server over a WebSocket bridge.
 *
 * Little-endian; Integer is signed 16-bit, Long signed 32-bit. ASCII strings use a 16-bit char-count
 * prefix + one byte per char (Windows-1252; for the ASCII+accented range this equals Latin-1).
 * XOR is a symmetric single-byte transform applied only client→server.
 */

const scratch = new DataView(new ArrayBuffer(8));

export class ByteQueueWriter {
  private buf: number[] = [];

  writeByte(v: number): this { this.buf.push(v & 0xff); return this; }
  writeBoolean(v: boolean): this { this.buf.push(v ? 1 : 0); return this; }

  writeInteger(v: number): this {
    this.buf.push(v & 0xff, (v >> 8) & 0xff);
    return this;
  }

  writeLong(v: number): this {
    this.buf.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff);
    return this;
  }

  writeSingle(v: number): this {
    scratch.setFloat32(0, v, true);
    for (let i = 0; i < 4; i++) this.buf.push(scratch.getUint8(i));
    return this;
  }

  writeDouble(v: number): this {
    scratch.setFloat64(0, v, true);
    for (let i = 0; i < 8; i++) this.buf.push(scratch.getUint8(i));
    return this;
  }

  /** Variable ASCII string: int16 char-count prefix (LE) + one byte per char (Latin-1/cp1252). */
  writeAsciiString(s: string): this {
    this.writeInteger(s.length);
    for (let i = 0; i < s.length; i++) this.buf.push(s.charCodeAt(i) & 0xff);
    return this;
  }

  /** Variable Unicode string: int16 char-count prefix (LE) + UTF-16LE (2 bytes/char). */
  writeUnicodeString(s: string): this {
    this.writeInteger(s.length);
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      this.buf.push(c & 0xff, (c >> 8) & 0xff);
    }
    return this;
  }

  toBytes(): Uint8Array { return Uint8Array.from(this.buf); }
}

export class NotEnoughData extends Error {}

export class ByteQueueReader {
  private view: DataView;
  private pos = 0;

  constructor(private data: Uint8Array) {
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  }

  private need(n: number): number {
    // n<0 rechazado: un length-prefix negativo (leído con signo) movería el cursor HACIA ATRÁS -> loop
    // infinito + OOM en el decoder. Un server malicioso / MITM sobre ws:// tiraba el navegador con 3 bytes.
    if (n < 0 || this.pos + n > this.data.length) throw new NotEnoughData(`need ${n} at ${this.pos}`);
    const p = this.pos;
    this.pos += n;
    return p;
  }

  readByte(): number { return this.data[this.need(1)]; }
  readBoolean(): boolean { return this.data[this.need(1)] === 1; }
  readInteger(): number { return this.view.getInt16(this.need(2), true); }
  readLong(): number { return this.view.getInt32(this.need(4), true); }
  readSingle(): number { return this.view.getFloat32(this.need(4), true); }
  readDouble(): number { return this.view.getFloat64(this.need(8), true); }

  readAsciiString(): string {
    const len = this.view.getUint16(this.need(2), true);   // longitud SIN signo (el writer emite s.length ≥ 0)
    let s = '';
    const start = this.need(len);
    for (let i = 0; i < len; i++) s += String.fromCharCode(this.data[start + i]);
    return s;
  }

  readUnicodeString(): string {
    const len = this.view.getUint16(this.need(2), true);   // longitud SIN signo
    let s = '';
    const start = this.need(len * 2);
    for (let i = 0; i < len; i++) s += String.fromCharCode(this.view.getUint16(start + i * 2, true));
    return s;
  }

  get remaining(): number { return this.data.length - this.pos; }
  get atEnd(): boolean { return this.pos >= this.data.length; }
  get consumed(): number { return this.pos; }
  peekByte(): number { return this.data[this.pos]; }
}

export const DEFAULT_XOR_KEY = 13;

/** Symmetric single-byte XOR over the whole buffer (encrypt == decrypt). */
export function xorBuffer(data: Uint8Array, key: number): Uint8Array {
  const out = new Uint8Array(data.length);
  const k = key & 0xff;
  for (let i = 0; i < data.length; i++) out[i] = data[i] ^ k;
  return out;
}
