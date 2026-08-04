// Scale2x / EPX: duplica la resolución preservando el pixel-art (sin blur).
// Opera sobre RGBA planar {w,h,rgba}. Comparación por pixel entero (incluye alpha),
// así el colorkey (alpha 0) cuenta como "un color" y no sangra con los opacos.
'use strict';

function px(rgba, w, x, y) {
  const i = (y * w + x) * 4;
  return (rgba[i] << 24) | (rgba[i + 1] << 16) | (rgba[i + 2] << 8) | rgba[i + 3];
}

function scale2x({ w, h, rgba }) {
  const ow = w * 2, oh = h * 2;
  const out = new Uint8ClampedArray(ow * oh * 4);
  const put = (x, y, v) => {
    const i = (y * ow + x) * 4;
    out[i] = (v >>> 24) & 255; out[i + 1] = (v >>> 16) & 255; out[i + 2] = (v >>> 8) & 255; out[i + 3] = v & 255;
  };
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const P = px(rgba, w, x, y);
      const A = y > 0 ? px(rgba, w, x, y - 1) : P;
      const D = y < h - 1 ? px(rgba, w, x, y + 1) : P;
      const C = x > 0 ? px(rgba, w, x - 1, y) : P;
      const B = x < w - 1 ? px(rgba, w, x + 1, y) : P;
      let E0 = P, E1 = P, E2 = P, E3 = P;
      if (C === A && C !== D && A !== B) E0 = A;
      if (A === B && A !== C && B !== D) E1 = B;
      if (D === C && D !== B && C !== A) E2 = C;
      if (B === D && B !== A && D !== C) E3 = D;
      put(x * 2, y * 2, E0); put(x * 2 + 1, y * 2, E1);
      put(x * 2, y * 2 + 1, E2); put(x * 2 + 1, y * 2 + 1, E3);
    }
  }
  return { w: ow, h: oh, rgba: out };
}

// ×4 = Scale2x aplicado dos veces (lo que el usuario eligió en el comparador).
function scale4x(img) { return scale2x(scale2x(img)); }

module.exports = { scale2x, scale4x };
