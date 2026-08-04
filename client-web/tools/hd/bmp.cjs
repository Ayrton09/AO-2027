// Decodificador BMP mínimo sin dependencias — cubre los formatos que trae AO:
// 8-bit (paleta), 24-bit (BGR), 32-bit (BGRA/BGRX). Bottom-up y top-down.
// Devuelve {w, h, rgba} con rgba = Uint8ClampedArray RGBA top-down (fila 0 = arriba).
'use strict';

function decodeBmp(buf) {
  if (buf[0] !== 0x42 || buf[1] !== 0x4d) throw new Error('no es BMP (falta "BM")');
  const dataOffset = buf.readUInt32LE(10);
  const dibSize = buf.readUInt32LE(14);
  const w = buf.readInt32LE(18);
  const hRaw = buf.readInt32LE(22);
  const topDown = hRaw < 0;
  const h = Math.abs(hRaw);
  const bpp = buf.readUInt16LE(28);
  const compression = buf.readUInt32LE(30);
  if (compression !== 0 && compression !== 3) throw new Error('compresión BMP no soportada: ' + compression);

  const rgba = new Uint8ClampedArray(w * h * 4);

  // Máscaras para 16-bit. BI_BITFIELDS (3): máscaras en 54,58,62. BI_RGB (0): XRGB1555 por defecto.
  let mR = 0x7c00, mG = 0x03e0, mB = 0x001f; // 5-5-5
  if (bpp === 16 && compression === 3) {
    mR = buf.readUInt32LE(54); mG = buf.readUInt32LE(58); mB = buf.readUInt32LE(62);
  }
  const shiftScale = (mask) => { let s = 0, m = mask; if (!m) return [0, 0]; while (!(m & 1)) { m >>= 1; s++; } let bits = 0, mm = m; while (mm & 1) { mm >>= 1; bits++; } return [s, bits]; };
  const [sR, bR] = shiftScale(mR), [sG, bG] = shiftScale(mG), [sB, bB] = shiftScale(mB);
  const expand = (v, bits) => bits >= 8 ? (v >> (bits - 8)) : ((v << (8 - bits)) | (v >> (2 * bits - 8 > 0 ? 2 * bits - 8 : 0)));

  // Paleta (8-bit): justo después del DIB header. nColors en 46-49 (0 => 2^bpp).
  let palette = null;
  if (bpp <= 8) {
    let nColors = buf.readUInt32LE(46);
    if (nColors === 0) nColors = 1 << bpp;
    const palOff = 14 + dibSize;
    palette = new Array(nColors);
    for (let i = 0; i < nColors; i++) {
      const o = palOff + i * 4;
      palette[i] = [buf[o + 2], buf[o + 1], buf[o]]; // BGRA -> RGB
    }
  }

  const rowSize = Math.floor((bpp * w + 31) / 32) * 4; // filas alineadas a 4 bytes
  for (let y = 0; y < h; y++) {
    const srcRow = topDown ? y : (h - 1 - y);   // BMP bottom-up por defecto
    const rowStart = dataOffset + srcRow * rowSize;
    for (let x = 0; x < w; x++) {
      let r, g, b, a = 255;
      if (bpp === 8) {
        const idx = buf[rowStart + x];
        const c = palette[idx] || [0, 0, 0];
        r = c[0]; g = c[1]; b = c[2];
      } else if (bpp === 24) {
        const o = rowStart + x * 3;
        b = buf[o]; g = buf[o + 1]; r = buf[o + 2];
      } else if (bpp === 32) {
        const o = rowStart + x * 4;
        b = buf[o]; g = buf[o + 1]; r = buf[o + 2]; // ignoramos el 4º byte (BGRX): AO usa colorkey, no alpha
      } else if (bpp === 16) {
        const v = buf.readUInt16LE(rowStart + x * 2);
        r = expand((v & mR) >> sR, bR); g = expand((v & mG) >> sG, bG); b = expand((v & mB) >> sB, bB);
      } else if (bpp === 4) {
        const nib = buf[rowStart + (x >> 1)];
        const idx = (x & 1) ? (nib & 0x0f) : (nib >> 4);
        const c = palette[idx] || [0, 0, 0];
        r = c[0]; g = c[1]; b = c[2];
      } else if (bpp === 1) {
        const byte = buf[rowStart + (x >> 3)];
        const idx = (byte >> (7 - (x & 7))) & 1;
        const c = palette[idx] || [0, 0, 0];
        r = c[0]; g = c[1]; b = c[2];
      } else {
        throw new Error('bpp no soportado: ' + bpp);
      }
      const d = (y * w + x) * 4;
      rgba[d] = r; rgba[d + 1] = g; rgba[d + 2] = b; rgba[d + 3] = a;
    }
  }
  return { w, h, rgba, bpp };
}

// Colorkey de AO: negro puro (0,0,0) -> transparente. Igual que hace el cliente al cargar.
function applyColorKey(rgba) {
  for (let i = 0; i < rgba.length; i += 4) {
    if (rgba[i] === 0 && rgba[i + 1] === 0 && rgba[i + 2] === 0) rgba[i + 3] = 0;
  }
  return rgba;
}

module.exports = { decodeBmp, applyColorKey };
