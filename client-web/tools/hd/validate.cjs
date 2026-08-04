'use strict';
const fs = require('fs');
const path = require('path');
const { decodeBmp, applyColorKey } = require('./bmp.cjs');
const { encodePng } = require('./png.cjs');

const GFX = path.resolve(__dirname, '../../assets/graficos');
const OUTDIR = __dirname;
const SAMPLES = [
  { f: 100, bpp: 8 }, { f: 506, bpp: 8 }, { f: 1, bpp: 24 },
  { f: 10044, bpp: 32 }, { f: 15340, bpp: 16 }, { f: 10071, bpp: '256' },
];

const embedded = {};
for (const s of SAMPLES) {
  const bmpBuf = fs.readFileSync(`${GFX}/${s.f}.bmp`);
  const img = decodeBmp(bmpBuf);
  applyColorKey(img.rgba);
  const png = encodePng(img.w, img.h, img.rgba);
  fs.writeFileSync(`${OUTDIR}/val_${s.f}.png`, png);
  embedded[s.f] = {
    w: img.w, h: img.h, bpp: img.bpp,
    bmp: `data:image/bmp;base64,${bmpBuf.toString('base64')}`,
    png: `data:image/png;base64,${png.toString('base64')}`,
  };
  console.log(`decodificado ${s.f}.bmp (${img.bpp}bpp ${img.w}x${img.h}) -> val_${s.f}.png`);
}

const html = `<title>Validación decodificador BMP→PNG</title>
<meta charset="utf-8">
<style>body{font:14px system-ui;background:#111;color:#eee;padding:16px}
table{border-collapse:collapse}td{padding:8px;text-align:center;border:1px solid #333}
canvas{image-rendering:pixelated;background:repeating-conic-gradient(#222 0 25%,#1a1a1a 0 50%) 0 0/16px 16px}
.ok{color:#5c5}.bad{color:#f66}</style>
<h2>Validación: mi PNG (Node) vs BMP original (navegador), pixel a pixel</h2>
<table><thead><tr><td>archivo</td><td>BMP orig</td><td>mi PNG</td><td>diff</td></tr></thead><tbody id="tb"></tbody></table>
<script>
const E = ${JSON.stringify(embedded)};
const tb = document.getElementById('tb');
function decodeToCanvas(src, w, h, colorkey){return new Promise(res=>{const img=new Image();img.onload=()=>{const c=document.createElement('canvas');c.width=w;c.height=h;const cx=c.getContext('2d');cx.drawImage(img,0,0);const id=cx.getImageData(0,0,w,h);if(colorkey){const p=id.data;for(let i=0;i<p.length;i+=4)if(p[i]===0&&p[i+1]===0&&p[i+2]===0)p[i+3]=0;}res({canvas:c,data:id.data});};img.src=src;});}
(async()=>{
  for(const [f,e] of Object.entries(E)){
    const ref=await decodeToCanvas(e.bmp,e.w,e.h,true);   // navegador decodifica BMP + colorkey
    const mine=await decodeToCanvas(e.png,e.w,e.h,false);  // mi PNG (colorkey ya horneado)
    let mism=0,maxd=0;const rp=ref.data,mp=mine.data;
    for(let i=0;i<rp.length;i+=4){
      const ta=rp[i+3]<10,tb2=mp[i+3]<10;
      if(ta&&tb2)continue;                 // ambos transparentes = ok
      if(ta!==tb2){mism++;maxd=255;continue;}
      const d=Math.max(Math.abs(rp[i]-mp[i]),Math.abs(rp[i+1]-mp[i+1]),Math.abs(rp[i+2]-mp[i+2]));
      if(d>2){mism++;if(d>maxd)maxd=d;}
    }
    const tr=document.createElement('tr');
    const verdict=mism===0?'<span class=ok>✓ idéntico</span>':'<span class=bad>✗ '+mism+' px, maxΔ '+maxd+'</span>';
    tr.innerHTML='<td>'+f+'.bmp<br>'+e.bpp+'bpp '+e.w+'×'+e.h+'</td>';
    for(const c of [ref.canvas,mine.canvas]){const td=document.createElement('td');const cv=document.createElement('canvas');cv.width=Math.min(160,e.w*3);cv.height=cv.width*e.h/e.w;const x=cv.getContext('2d');x.imageSmoothingEnabled=false;x.drawImage(c,0,0,cv.width,cv.height);td.appendChild(cv);tr.appendChild(td);}
    const td=document.createElement('td');td.innerHTML=verdict;td.dataset.mism=mism;tr.appendChild(td);
    tb.appendChild(tr);
  }
  const total=[...tb.querySelectorAll('td[data-mism]')].reduce((a,t)=>a+ +t.dataset.mism,0);
  document.title=(total===0?'OK ':'FALLO ')+document.title;
  window.__valTotal=total;
})();
</script>`;
fs.writeFileSync(`${OUTDIR}/validate.html`, html);
console.log('escrito validate.html');
