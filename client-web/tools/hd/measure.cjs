'use strict';
const fs = require('fs');
const path = require('path');
const { decodeBmp, applyColorKey } = require('./bmp.cjs');
const { scale2x, scale4x } = require('./scale2x.cjs');
const { encodePng } = require('./png.cjs');

const GFX = path.resolve(__dirname, '../../assets/graficos');
const grh = require('../../assets/grh.json').grh;

// --- 1. Diagnóstico del pasto (grh 1 = file 1, región 64,0,32,32) ---
function crop(img, x, y, w, h) {
  const out = new Uint8ClampedArray(w * h * 4);
  for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) {
    const s = ((y + j) * img.w + (x + i)) * 4, d = (j * w + i) * 4;
    out[d] = img.rgba[s]; out[d + 1] = img.rgba[s + 1]; out[d + 2] = img.rgba[s + 2]; out[d + 3] = img.rgba[s + 3];
  }
  return { w, h, rgba: out };
}
{
  const g = grh[1];
  const sheet = decodeBmp(fs.readFileSync(`${GFX}/${g.file}.bmp`));
  const tile = crop(sheet, g.x, g.y, g.w, g.h);
  let r = 0, gg = 0, b = 0, n = 0, minL = 255, maxL = 0;
  const p = tile.rgba;
  for (let i = 0; i < p.length; i += 4) { r += p[i]; gg += p[i + 1]; b += p[i + 2]; n++; const L = (p[i] + p[i + 1] + p[i + 2]) / 3; if (L < minL) minL = L; if (L > maxL) maxL = L; }
  console.log('=== PASTO (grh 1) ===');
  console.log(`  color promedio: rgb(${Math.round(r / n)},${Math.round(gg / n)},${Math.round(b / n)})  rango luma: ${minL}-${maxL}  (rango chico = textura casi uniforme)`);
  // guardar un PNG para inspección visual (valida decode+encode end-to-end)
  fs.writeFileSync(path.resolve(__dirname, 'grass_x1.png'), encodePng(tile.w, tile.h, tile.rgba));
  fs.writeFileSync(path.resolve(__dirname, 'grass_x4.png'), (() => { const s = scale4x(tile); return encodePng(s.w, s.h, s.rgba); })());
  console.log('  escritos grass_x1.png / grass_x4.png para inspección visual');
}

// --- 2. Medición de tamaño en muestra estratificada ---
const files = fs.readdirSync(GFX).filter(f => f.endsWith('.bmp')).sort((a, b) => parseInt(a) - parseInt(b));
const SAMPLE_EVERY = 20;
const sample = files.filter((_, i) => i % SAMPLE_EVERY === 0);
let origBytes = 0, x2Bytes = 0, x4Bytes = 0, ok = 0, fail = 0;
const t0 = Date.now();
for (const f of sample) {
  try {
    const buf = fs.readFileSync(`${GFX}/${f}`);
    origBytes += buf.length;
    const img = decodeBmp(buf);
    applyColorKey(img.rgba);
    const s2 = scale2x(img); x2Bytes += encodePng(s2.w, s2.h, s2.rgba).length;
    const s4 = scale2x(s2); x4Bytes += encodePng(s4.w, s4.h, s4.rgba).length;
    ok++;
  } catch (e) { fail++; console.log('  FAIL', f, e.message); }
}
const totalBmp = files.reduce((a, f) => a + fs.statSync(`${GFX}/${f}`).size, 0);
const ratio2 = x2Bytes / origBytes, ratio4 = x4Bytes / origBytes;
console.log(`\n=== MEDICIÓN (muestra ${ok} sheets, ${fail} fallos, ${((Date.now() - t0) / 1000).toFixed(1)}s) ===`);
console.log(`  BMP original set completo: ${(totalBmp / 1048576).toFixed(0)} MB`);
console.log(`  muestra: BMP ${(origBytes / 1048576).toFixed(1)}MB -> PNG×2 ${(x2Bytes / 1048576).toFixed(1)}MB (${ratio2.toFixed(2)}×)  PNG×4 ${(x4Bytes / 1048576).toFixed(1)}MB (${ratio4.toFixed(2)}×)`);
console.log(`  ESTIMADO set completo:  PNG×2 ≈ ${(totalBmp * ratio2 / 1048576).toFixed(0)} MB   PNG×4 ≈ ${(totalBmp * ratio4 / 1048576).toFixed(0)} MB`);
