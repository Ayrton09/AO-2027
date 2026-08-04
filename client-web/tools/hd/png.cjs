// Encoder PNG mínimo (RGBA, 8-bit/canal) usando el zlib nativo de Node — sin dependencias.
// Filtro por-scanline tipo 1 (Sub) para pixel-art (mejor ratio que 0 en sprites con áreas planas);
// probamos None/Sub/Up/Paeth por fila y elegimos el de menor suma absoluta (heurística estándar).
'use strict';
const zlib = require('zlib');

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c >>> 0;
  }
  return t;
})();
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const body = Buffer.concat([typeBuf, data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

const paeth = (a, b, c) => {
  const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
};

function encodePng(w, h, rgba) {
  const bpp = 4, stride = w * bpp;
  // Filtro adaptativo: por fila elegimos el filtro (0..4) de menor suma de |valor con signo|.
  const filtered = Buffer.alloc((stride + 1) * h);
  const prev = new Uint8Array(stride);
  const cur = new Uint8Array(stride);
  const cand = [new Uint8Array(stride), new Uint8Array(stride), new Uint8Array(stride), new Uint8Array(stride), new Uint8Array(stride)];
  for (let y = 0; y < h; y++) {
    for (let i = 0; i < stride; i++) cur[i] = rgba[y * stride + i];
    // 0 None, 1 Sub, 2 Up, 3 Average, 4 Paeth
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? cur[i - bpp] : 0;
      const b = prev[i];
      const c = i >= bpp ? prev[i - bpp] : 0;
      cand[0][i] = cur[i];
      cand[1][i] = (cur[i] - a) & 255;
      cand[2][i] = (cur[i] - b) & 255;
      cand[3][i] = (cur[i] - ((a + b) >> 1)) & 255;
      cand[4][i] = (cur[i] - paeth(a, b, c)) & 255;
    }
    let best = 0, bestSum = Infinity;
    for (let f = 0; f < 5; f++) {
      let s = 0;
      for (let i = 0; i < stride; i++) { const v = cand[f][i]; s += v < 128 ? v : 256 - v; }
      if (s < bestSum) { bestSum = s; best = f; }
    }
    const rowOff = y * (stride + 1);
    filtered[rowOff] = best;
    filtered.set(cand[best].subarray(0, stride), rowOff + 1);
    prev.set(cur);
  }

  const idat = zlib.deflateSync(filtered, { level: 9 });
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;   // bit depth
  ihdr[9] = 6;   // color type 6 = RGBA
  ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([sig, chunk('IHDR', ihdr), chunk('IDAT', idat), chunk('IEND', Buffer.alloc(0))]);
}

module.exports = { encodePng };
