/**
 * CoverAO / Argentum Online wire codec — TypeScript implementation (Fase 3, web client).
 *
 * Third faithful port of the VB6 `clsByteQueue` (after the C# server and Python reference), verified
 * byte-for-byte against the same test vectors. This is what the future web client uses to talk the
 * classic AO protocol to the modern server over a WebSocket bridge.
 *
 * Little-endian; Integer is signed 16-bit, Long signed 32-bit. ASCII strings use a 16-bit char-count
 * prefix + one byte per char (Windows-1252, the same codepage as the server's `Codec.Ansi`).
 * XOR is a symmetric single-byte transform applied only client→server.
 */
const scratch = new DataView(new ArrayBuffer(8));
/**
 * Windows-1252 como lo hace el server (`Encoding.GetEncoding(1252)` de .NET, CoverAO.Protocol/Codec.cs).
 *
 * De 0x80 a 0x9F el 1252 no es Latin-1: el 0x80 es el €, el 0x93 y el 0x94 las comillas tipográficas, etc.
 * Los cinco huecos (0x81 0x8D 0x8F 0x90 0x9D) .NET los lee y los escribe como el control C1 del mismo número,
 * y acá igual.
 */
const CP1252_ALTOS = [
    0x20ac, 0x0081, 0x201a, 0x0192, 0x201e, 0x2026, 0x2020, 0x2021, 0x02c6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008d, 0x017d, 0x008f,
    0x0090, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014, 0x02dc, 0x2122, 0x0161, 0x203a, 0x0153, 0x009d, 0x017e, 0x0178,
];
/**
 * Lo que el server escribe para cada carácter que no sale tal cual: los 27 propios del 1252 (€ -> 0x80) y la
 * aproximación de .NET para el resto (č -> c, la Ａ ancha -> A, ─ -> -). Seis dígitos hex por
 * entrada: el carácter (4) y el byte (2). Sale de recorrer los 65536 caracteres por el ByteQueueWriter del
 * server; lo que no figura acá ni es ASCII ni Latin-1 alto ni hueco, el server lo escribe como `?`.
 */
const CP1252_ESCRITURA = ('010041010161010241010361010441010561010643010763010843010963010A43010B63010C43010D63010E44010F64' +
    '0110D0011164011245011365011445011565011645011765011845011965011A45011B65011C47011D67011E47011F67' +
    '012047012167012247012367012448012568012648012768012849012969012A49012B69012C49012D69012E49012F69' +
    '01304901316901344A01356A01364B01376B01394C013A6C013B4C013C6C013D4C013E6C01414C01426C01434E01446E' +
    '01454E01466E01474E01486E014C4F014D6F014E4F014F6F01504F01516F01528C01539C015452015572015652015772' +
    '015852015972015A53015B73015C53015D73015E53015F7301608A01619A016254016374016454016574016654016774' +
    '016855016975016A55016B75016C55016D75016E55016F75017055017175017255017375017457017577017659017779' +
    '01789F01795A017A7A017B5A017C7A017D8E017E9E0180620189D0019183019283019749019A6C019F4F01A04F01A16F' +
    '01AB7401AE5401AF5501B07501B67A01C07C01C32101CD4101CE6101CF4901D06901D14F01D26F01D35501D47501D555' +
    '01D67501D75501D87501D95501DA7501DB5501DC7501DE4101DF6101E44701E56701E64701E76701E84B01E96B01EA4F' +
    '01EB6F01EC4F01ED6F01F06A02616702B92702BA2202BC2702C45E02C68802C82702C9AF02CAB402CB6002CD5F02DAB0' +
    '02DC980300600301B403025E03037E0304AF0305AF0308A8030AB0030E220327B803315F03325F037E3B039347039854' +
    '03A35303A64603A94F03B16103B2DF03B46403B56503BCB503C07003C37303C47403C66604BB6805893A066A25200020' +
    '20012020022020032020042020052020062020102D20112D20139620149720173D201891201992201A82201C93201D94' +
    '201E842020862021872022952024B720268520308920322720356020398B203A9B20442F2070B0207434207535207636' +
    '207737207838207F6E20803020813120823220833320843420853520863620873720883820893920A1A220A4A320A750' +
    '20AC80210243210745210A67210B48210C48210D48210E6821104921114921124C21136C21154E211850211950211A51' +
    '211B52211C52211D5221229921245A21285A212A4B212BC5212C42212D43212E65212F6521304521314621334D21346F' +
    '2205D822122D2213B122152F22165C22172A2218B02219B7221A76221E3822237C22296E22363A223C7E22489822613D' +
    '22643D22653D226AAB226BBB22C5B72302A623035E2310AC23202823212923293C232A3E25002D2502A6250C2B25102B' +
    '25142B25182B251C2B2524A6252C2D25342D253C2B25502D2551A625522B25532B25542B25552B25562B25572B25582B' +
    '25592B255A2B255B2B255C2B255D2B255EA6255FA62560A62561A62562A62563A625642D25652D25662D25672D25682D' +
    '25692D256A2B256B2B256C2B2580AF25845F2588A6258CA62590A62591A62592A62593A625A0A6263CA427587C300020' +
    '30083C30093E300AAB300BBB301A5B301B5D30FBB7FF0121FF0222FF0323FF0424FF0525FF0626FF0727FF0828FF0929' +
    'FF0A2AFF0B2BFF0C2CFF0D2DFF0E2EFF0F2FFF1030FF1131FF1232FF1333FF1434FF1535FF1636FF1737FF1838FF1939' +
    'FF1A3AFF1B3BFF1C3CFF1D3DFF1E3EFF2040FF2141FF2242FF2343FF2444FF2545FF2646FF2747FF2848FF2949FF2A4A' +
    'FF2B4BFF2C4CFF2D4DFF2E4EFF2F4FFF3050FF3151FF3252FF3353FF3454FF3555FF3656FF3757FF3858FF3959FF3A5A' +
    'FF3B5BFF3C5CFF3D5DFF3E5EFF3F5FFF4060FF4161FF4262FF4363FF4464FF4565FF4666FF4767FF4868FF4969FF4A6A' +
    'FF4B6BFF4C6CFF4D6DFF4E6EFF4F6FFF5070FF5171FF5272FF5373FF5474FF5575FF5676FF5777FF5878FF5979FF5A7A' +
    'FF5B7BFF5C7CFF5D7DFF5E7E');
const MAPA_ESCRITURA = new Map();
for (let i = 0; i < CP1252_ESCRITURA.length; i += 6) {
    MAPA_ESCRITURA.set(parseInt(CP1252_ESCRITURA.slice(i, i + 4), 16), parseInt(CP1252_ESCRITURA.slice(i + 4, i + 6), 16));
}
/** El byte que el server escribiría para una unidad UTF-16 (un surrogate suelto o de un par sale como `?`). */
export function byteCp1252(c) {
    if (c < 0x80 || (c >= 0xa0 && c <= 0xff))
        return c;
    if (c === 0x81 || c === 0x8d || c === 0x8f || c === 0x90 || c === 0x9d)
        return c;
    return MAPA_ESCRITURA.get(c) ?? 0x3f;
}
/** El carácter que el server lee para un byte. */
export function caracterCp1252(b) {
    return b >= 0x80 && b <= 0x9f ? CP1252_ALTOS[b - 0x80] : b;
}
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
     * Variable ASCII string: int16 char-count prefix (LE) + one byte per char (cp1252, como el server).
     *
     * HASTA `MAX_CADENA` CARACTERES, y cada carácter sale como lo escribiría el server (`byteCp1252`): el € y las
     * comillas tipográficas en su byte del 1252, lo aproximable aproximado y el resto como `?`.
     * Un texto pegado de más de 2048 hacía que el server cortara la conexión ("cadena demasiado larga", ClientSession.MaxCadenaDelCliente), y con 65536 o más
     * el largo daba la vuelta y el resto del texto se leía como paquetes. Y el `& 0xff` convertía cualquier carácter
     * de afuera en otro byte: una `č` (U+010D) salía como un salto de línea.
     */
    writeAsciiString(s) {
        const n = Math.min(s.length, MAX_CADENA);
        this.writeInteger(n);
        for (let i = 0; i < n; i++) {
            this.buf.push(byteCp1252(s.charCodeAt(i)));
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
            s += String.fromCharCode(caracterCp1252(this.data[start + i]));
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
