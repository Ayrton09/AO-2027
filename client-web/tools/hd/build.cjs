'use strict';
// Pipeline HD: BMP -> PNG ×4 (Scale2x aplicado 2 veces), por-sprite para no sangrar
// entre sprites vecinos del atlas. Mantiene las coordenadas del atlas (grh.json) SIN cambios:
// una región (x,y,w,h) queda en (x*4, y*4, w*4, h*4) porque cada sheet se reconstruye a escala 4.
// El colorkey (negro->alpha 0) se hornea en el PNG (el cliente ya no necesita el pase getImageData).
//
// Uso:
//   node build.cjs subset            -> solo sheets del mapa 1 + entidades (test rápido)
//   node build.cjs all [outDir]      -> las 1599 sheets
const fs = require('fs');
const path = require('path');
const { decodeBmp, applyColorKey } = require('./bmp.cjs');
const { scale4x } = require('./scale2x.cjs');
const { encodePng } = require('./png.cjs');

const HD = 4;
const ASSETS = path.resolve(__dirname, '../../assets');
const GFX = path.join(ASSETS, 'graficos');
const grh = require(path.join(ASSETS, 'grh.json')).grh;

const mode = process.argv[2] || 'subset';
const outDir = path.resolve(process.argv[3] || GFX); // por defecto: al lado de los BMP (coexisten)
fs.mkdirSync(outDir, { recursive: true });

// --- índice inverso: file -> [{x,y,w,h}] (regiones únicas) ---
const fileRegions = new Map();
for (const k of Object.keys(grh)) {
  const e = grh[k];
  if (!e || e.file == null) continue;
  if (!fileRegions.has(e.file)) fileRegions.set(e.file, []);
  fileRegions.get(e.file).push({ x: e.x, y: e.y, w: e.w, h: e.h });
}
// dedup de regiones idénticas por sheet
for (const [f, regs] of fileRegions) {
  const seen = new Set(), uniq = [];
  for (const r of regs) { const key = `${r.x},${r.y},${r.w},${r.h}`; if (!seen.has(key)) { seen.add(key); uniq.push(r); } }
  fileRegions.set(f, uniq);
}

// --- selección de archivos ---
function subsetFiles() {
  const set = new Set();
  const add = (gi) => { let e = grh[gi]; if (!e) return; if (e.frames) { e.frames.forEach(add); return; } if (e.file != null) set.add(e.file); };
  // mapa 1: ground + objects + decorations + roofs
  const m = require(path.join(ASSETS, 'map1.json'));
  (m.ground || []).flat().forEach(g => { if (typeof g === 'number' && g > 0) add(g); });
  for (const key of ['objects', 'decorations', 'roofs']) (m[key] || []).forEach(o => add(o[2]));
  // entidades: todos los cuerpos, cabezas, armas, escudos, cascos, fx
  const idxFiles = ['bodies.json', 'heads.json', 'weapons.json', 'shields.json', 'helmets.json', 'fx.json'];
  for (const jf of idxFiles) {
    try {
      const j = require(path.join(ASSETS, jf));
      const arr = j.bodies || j.heads || j.anims || j.fx || j;
      JSON.stringify(arr, (k, v) => { if (typeof v === 'number' && grh[v]) add(v); return v; });
    } catch (e) {}
  }
  // items (íconos)
  try { const items = require(path.join(ASSETS, 'items.json')); for (const k of Object.keys(items)) if (Array.isArray(items[k]) && items[k][0]) add(items[k][0]); } catch (e) {}
  return [...set];
}

let targetFiles;
if (mode === 'all') targetFiles = [...fileRegions.keys()];
else targetFiles = subsetFiles().filter(f => fileRegions.has(f));

console.log(`modo=${mode}  sheets a procesar=${targetFiles.length}  outDir=${path.relative(process.cwd(), outDir)}`);

function crop(img, x, y, w, h) {
  const out = new Uint8ClampedArray(w * h * 4);
  for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) {
    const sx = x + i, sy = y + j;
    if (sx < 0 || sy < 0 || sx >= img.w || sy >= img.h) continue;
    const s = (sy * img.w + sx) * 4, d = (j * w + i) * 4;
    out[d] = img.rgba[s]; out[d + 1] = img.rgba[s + 1]; out[d + 2] = img.rgba[s + 2]; out[d + 3] = img.rgba[s + 3];
  }
  return { w, h, rgba: out };
}
function blit(dst, dstW, dstH, src, ox, oy) {
  // Clamp al buffer destino: algunas regiones grh degeneradas se salen de su hoja (x+w>ancho o
  // y+h>alto). Sin el clamp, el índice lineal envuelve a la fila siguiente y pisa el sprite vecino.
  for (let j = 0; j < src.h; j++) {
    const dy = oy + j; if (dy < 0 || dy >= dstH) continue;
    for (let i = 0; i < src.w; i++) {
      const dx = ox + i; if (dx < 0 || dx >= dstW) continue;
      const s = (j * src.w + i) * 4, d = (dy * dstW + dx) * 4;
      dst[d] = src.rgba[s]; dst[d + 1] = src.rgba[s + 1]; dst[d + 2] = src.rgba[s + 2]; dst[d + 3] = src.rgba[s + 3];
    }
  }
}

let done = 0, outBytes = 0, srcBytes = 0, fail = 0;
const t0 = Date.now();
for (const f of targetFiles) {
  try {
    const bmpBuf = fs.readFileSync(`${GFX}/${f}.bmp`);
    srcBytes += bmpBuf.length;
    const img = decodeBmp(bmpBuf);
    applyColorKey(img.rgba);
    // LA HOJA SALE DEL TAMAÑO QUE USA EL ATLAS, no del BMP entero. Los BMP del original vienen rellenados
    // hasta una potencia de dos —así los quería DirectDraw— y el ×4 multiplica ese relleno por DIECISÉIS:
    // la mitad de los bytes que las hojas ocupan DECODIFICADAS en el navegador era margen que no dibuja
    // nadie. `73.bmp` es 2048×2048 con el atlas llegando a 1536×1536, o sea 256 MB en RAM contra 144.
    // Recortar no mueve `grh.json`: las coordenadas se cuentan desde arriba a la izquierda. Queda un
    // art-píxel de margen para que el sprite que toca el borde se siga suavizando contra el vacío.
    const usado = fileRegions.get(f).reduce((a, r) => ({ w: Math.max(a.w, r.x + r.w), h: Math.max(a.h, r.y + r.h) }), { w: 0, h: 0 });
    const OW = Math.min(img.w * HD, usado.w * HD + HD), OH = Math.min(img.h * HD, usado.h * HD + HD);
    const outRgba = new Uint8ClampedArray(OW * OH * 4);
    for (const r of fileRegions.get(f)) {
      const region = crop(img, r.x, r.y, r.w, r.h);
      const hd = scale4x(region);       // ×4 = Scale2x dos veces
      blit(outRgba, OW, OH, hd, r.x * HD, r.y * HD);
    }
    const png = encodePng(OW, OH, outRgba);
    fs.writeFileSync(`${outDir}/${f}.png`, png);
    outBytes += png.length;
    if (++done % 100 === 0) console.log(`  ${done}/${targetFiles.length}  (${((Date.now() - t0) / 1000).toFixed(0)}s)`);
  } catch (e) { fail++; console.log('  FAIL', f, e.message); }
}
console.log(`\nlisto: ${done} PNG escritos, ${fail} fallos, ${((Date.now() - t0) / 1000).toFixed(1)}s`);
console.log(`  BMP fuente ${(srcBytes / 1048576).toFixed(1)}MB -> PNG×4 ${(outBytes / 1048576).toFixed(1)}MB`);
