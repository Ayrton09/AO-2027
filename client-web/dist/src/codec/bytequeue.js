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
/** El texto más largo que acepta el server de un cliente (ClientSession.MaxCadenaDelCliente). */
export const MAX_CADENA = 2048;
export class ByteQueueWriter {
    buf = [];
    writeByte(v) { this.buf.push(v & 0xff); return this; }
    writeBoolean(v) { this.buf.push(v ? 1 : 0); return this; }
    writeInteger(v) {
        this.buf.push(v & 0xff, (v >> 8) & 0xff);
        return this;
    }
    writeLong(v) {
        this.buf.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff);
        return this;
    }
    writeSingle(v) {
        scratch.setFloat32(0, v, true);
        for (let i = 0; i < 4; i++)
            this.buf.push(scratch.getUint8(i));
        return this;
    }
    writeDouble(v) {
        scratch.setFloat64(0, v, true);
        for (let i = 0; i < 8; i++)
            this.buf.push(scratch.getUint8(i));
        return this;
    }
    /**
     * Variable ASCII string: int16 char-count prefix (LE) + one byte per char (Latin-1/cp1252).
     *
     * HASTA `MAX_CADENA` CARACTERES, y lo que no es Latin-1 sale como `?`. Un texto pegado de más de 2048 hacía que
     * el server cortara la conexión ("cadena demasiado larga", ClientSession.MaxCadenaDelCliente), y con 65536 o más
     * el largo daba la vuelta y el resto del texto se leía como paquetes. Y el `& 0xff` convertía cualquier carácter
     * de afuera en otro byte: una `č` (U+010D) salía como un salto de línea.
     */
    writeAsciiString(s) {
        const n = Math.min(s.length, MAX_CADENA);
        this.writeInteger(n);
        for (let i = 0; i < n; i++) {
            const c = s.charCodeAt(i);
            this.buf.push(c <= 0xff ? c : 0x3f);
        }
        return this;
    }
    /** Variable Unicode string: int16 char-count prefix (LE) + UTF-16LE (2 bytes/char). */
    writeUnicodeString(s) {
        this.writeInteger(s.length);
        for (let i = 0; i < s.length; i++) {
            const c = s.charCodeAt(i);
            this.buf.push(c & 0xff, (c >> 8) & 0xff);
        }
        return this;
    }
    toBytes() { return Uint8Array.from(this.buf); }
}
export class NotEnoughData extends Error {
}
export class ByteQueueReader {
    data;
    view;
    pos = 0;
    constructor(data) {
        this.data = data;
        this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    }
    need(n) {
        // n<0 rechazado: un length-prefix negativo (leído con signo) movería el cursor HACIA ATRÁS -> loop
        // infinito + OOM en el decoder. Un server malicioso / MITM sobre ws:// tiraba el navegador con 3 bytes.
        if (n < 0 || this.pos + n > this.data.length)
            throw new NotEnoughData(`need ${n} at ${this.pos}`);
        const p = this.pos;
        this.pos += n;
        return p;
    }
    readByte() { return this.data[this.need(1)]; }
    readBoolean() { return this.data[this.need(1)] === 1; }
    readInteger() { return this.view.getInt16(this.need(2), true); }
    readLong() { return this.view.getInt32(this.need(4), true); }
    readSingle() { return this.view.getFloat32(this.need(4), true); }
    readDouble() { return this.view.getFloat64(this.need(8), true); }
    readAsciiString() {
        const len = this.view.getUint16(this.need(2), true); // longitud SIN signo (el writer emite s.length ≥ 0)
        let s = '';
        const start = this.need(len);
        for (let i = 0; i < len; i++)
            s += String.fromCharCode(this.data[start + i]);
        return s;
    }
    readUnicodeString() {
        const len = this.view.getUint16(this.need(2), true); // longitud SIN signo
        let s = '';
        const start = this.need(len * 2);
        for (let i = 0; i < len; i++)
            s += String.fromCharCode(this.view.getUint16(start + i * 2, true));
        return s;
    }
    get remaining() { return this.data.length - this.pos; }
    get atEnd() { return this.pos >= this.data.length; }
    get consumed() { return this.pos; }
    peekByte() { return this.data[this.pos]; }
}
export const DEFAULT_XOR_KEY = 13;
/** Symmetric single-byte XOR over the whole buffer (encrypt == decrypt). */
export function xorBuffer(data, key) {
    const out = new Uint8Array(data.length);
    const k = key & 0xff;
    for (let i = 0; i < data.length; i++)
        out[i] = data[i] ^ k;
    return out;
}
