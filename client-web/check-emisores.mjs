// TODO PAQUETE QUE EL SERVER MANDA, EL CLIENTE TIENE QUE SABER LEERLO.
//
// El cruce va por ID, no por nombre: el server escribe el enum con la grafía del VB6 (`atributes`,
// `guildList`, en minúscula) y el cliente usa la suya (`Atributes`, `GuildList`). Comparando nombres, esos
// dos salían como faltantes y no lo son — es la trampa de [[sexto-eje-cliente-compuerta]], cruzar por nombre
// en vez de por id.
//
// Lo que sí caza: un emisor nuevo en el server que el cliente no decodifique. Ese paquete llega al navegador
// y se descarta en silencio — sin error de JS, sin nada en consola, y la función simplemente no existe para
// el jugador. Ni los tests unitarios ni los flows lo ven: los flows hablan el protocolo a mano, sin cliente.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const RAIZ = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const SM = join(RAIZ, 'server/src/CoverAO.Server/Net/ServerMessages.cs');
const CLI = join(RAIZ, 'client-web/src/protocol/serverPackets.ts');

/** Cada `public static byte[] Nombre(...)` con el primer ServerPacketId que escribe. */
function emisores(fuente) {
  const lineas = fuente.split('\n');
  const firmas = [];
  lineas.forEach((l, i) => {
    const m = /^\s*public static byte\[\] (\w+)\s*\(/.exec(l);
    if (m) firmas.push([i, m[1]]);
  });
  const ids = new Map();
  firmas.forEach(([i, nombre], k) => {
    const fin = k + 1 < firmas.length ? firmas[k + 1][0] : lineas.length;
    for (let j = i; j < fin; j++) {
      const g = /ServerPacketId\.(\w+)/.exec(lineas[j]);
      if (g) { ids.set(nombre, g[1]); break; }
    }
  });
  return ids;
}

/** Los ids que el enum del SERVER asigna, para poder cruzar por número. */
function idsDelServer() {
  const enumCs = readFileSync(join(RAIZ, 'server/src/CoverAO.Protocol/ServerPacketId.cs'), 'utf8');
  const m = new Map();
  for (const g of enumCs.matchAll(/^\s*(\w+)\s*=\s*(\d+)/gm)) m.set(g[1], Number(g[2]));
  return m;
}

const sm = readFileSync(SM, 'utf8');
const cli = readFileSync(CLI, 'utf8');
const porEmisor = emisores(sm);
const numeroDe = idsDelServer();

// Los emisores que el server REALMENTE llama: uno que nadie usa no es un gap del cliente.
const usados = new Set();
(function barrer(dir) {
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) { if (f !== 'bin' && f !== 'obj') barrer(p); continue; }
    if (!f.endsWith('.cs') || f === 'ServerMessages.cs') continue;
    const t = readFileSync(p, 'utf8');
    for (const nombre of porEmisor.keys()) if (t.includes(`ServerMessages.${nombre}(`)) usados.add(nombre);
  }
})(join(RAIZ, 'server/src/CoverAO.Server'));

// Del lado del cliente: los NÚMEROS de su tabla `ServerPacketId`.
const numerosCliente = new Set();
for (const g of cli.matchAll(/\b\w+\s*[:=]\s*(\d+)\b/g)) numerosCliente.add(Number(g[1]));

const faltan = [];
for (const nombre of usados) {
  const id = porEmisor.get(nombre);
  const n = numeroDe.get(id);
  if (n === undefined) continue;                 // id que el enum no numera explícitamente
  if (!numerosCliente.has(n)) faltan.push(`${nombre} -> ${id} (${n})`);
}

// Sin esto el chequeo pasaría con la lista vacía si el parseo se rompiera.
if (usados.size < 90) {
  console.error(`check-emisores: solo encontre ${usados.size} emisores usados; el parseo se rompio`);
  process.exit(1);
}
if (faltan.length) {
  console.error('check-emisores: el server manda paquetes que el cliente no decodifica:\n  ' + faltan.join('\n  '));
  process.exit(1);
}
console.log(`check-emisores: ${usados.size} emisores del server, todos decodificados por el cliente`);
