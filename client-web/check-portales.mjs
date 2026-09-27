// LOS PORTALES Y A DÓNDE LLEVAN (pedido del tester, 2026-09-27: "que todos los portales digan a dónde te llevan").
//
// `assets/portales.json` = { "<mapa>": [[x, y], ...] }: cada objeto de tipo Teleport (19) que el mundo pone en un mapa
// (gamedata/MapObjects.json). El cliente no los recibe nunca —el server marca FIJO todo objeto del mapa menos las puertas,
// como el original (`ObjEsFijo`, FileIO.bas:1329), y los fijos no viajan— y casi ninguno tiene gráfico: lo que se ve es su
// partícula. Sin esta lista el cliente no sabría dónde hay un portal para decir a dónde lleva.
//
// Este archivo ARMA la lista (`node check-portales.mjs --escribir`) y la VERIFICA (sin argumentos, en `npm test`): tiene
// que ser la que sale de la data —una lista a mano se queda vieja justo el día que alguien mueve un portal— y cada portal
// tiene que tener su salida, con un destino que tenga nombre en `mapa-zonas.json`.
import { readFileSync, writeFileSync } from 'node:fs';

const leer = (ruta) => JSON.parse(readFileSync(new URL(ruta, import.meta.url), 'utf8'));
const TELEPORT = 19;

const objetos = leer('../gamedata/obj.json').OBJ;
const esTeleport = new Set(objetos.filter((o) => o && Number(o.ObjType) === TELEPORT).map((o) => Number(o._index)));
const colocados = leer('../gamedata/MapObjects.json');
const armada = {};
for (const mapa of Object.keys(colocados).sort((a, b) => a - b)) {
  const lista = colocados[mapa].filter(([, , obj]) => esTeleport.has(obj)).map(([x, y]) => [x, y])
    .sort((a, b) => a[1] - b[1] || a[0] - b[0]);
  if (lista.length) armada[mapa] = lista;
}
const texto = JSON.stringify(armada);
const destino = new URL('./assets/portales.json', import.meta.url);

if (process.argv.includes('--escribir')) {
  writeFileSync(destino, texto);
  console.log(`check-portales: escrito assets/portales.json (${Object.values(armada).flat().length} portales en ${Object.keys(armada).length} mapas)`);
  process.exit(0);
}

const fallas = [];
let actual = '';
try { actual = readFileSync(destino, 'utf8'); } catch { fallas.push('falta assets/portales.json (armarlo con `node check-portales.mjs --escribir`)'); }
if (actual && actual !== texto) fallas.push('assets/portales.json no es el que sale de la data: rearmarlo con `node check-portales.mjs --escribir`');

// Cada portal lleva a algún lado, y ese lado tiene nombre.
const salidas = leer('../gamedata/MapExits.json');
const zonas = leer('./assets/mapa-zonas.json');
let sinSalida = 0, conNombre = 0;
for (const [mapa, lista] of Object.entries(armada)) {
  const deMapa = new Map((salidas[mapa] || []).map(([x, y, dm]) => [x + ',' + y, dm]));
  for (const [x, y] of lista) {
    const dm = deMapa.get(x + ',' + y);
    // Uno solo del mundo no tiene salida (mapa 48, 14,87) y es así en la data del original: se lo nombra aparte para que
    // un portal NUEVO sin salida sí ponga rojo esto.
    if (!dm) { if (!(mapa === '48' && x === 14 && y === 87)) fallas.push(`el portal ${mapa} (${x},${y}) no tiene salida`); sinSalida++; continue; }
    if (!zonas[dm] || !String(zonas[dm].n || '').trim()) fallas.push(`el portal ${mapa} (${x},${y}) lleva al mapa ${dm}, que no tiene nombre`);
    else conNombre++;
  }
}
// Y el cliente la usa: la baja, y la consulta para el cartel y para el texto del clic.
const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
if (!/fetch\('\.\/assets\/portales\.json'\)/.test(html)) fallas.push('index.html no baja assets/portales.json');
if (!/function destinoDelPortal\(/.test(html)) fallas.push('index.html no tiene `destinoDelPortal`');

if (fallas.length) {
  console.error('check-portales: FALLA');
  for (const f of fallas) console.error('  - ' + f);
  process.exit(1);
}
console.log(`check-portales: ${conNombre} portales con destino en ${Object.keys(armada).length} mapas (${sinSalida} sin salida, como en el original)`);
