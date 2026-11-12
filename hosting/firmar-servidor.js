// FIRMA EL PAQUETE DEL SERVER (lo verifica server/src/CoverAO.Server/Data/Actualizacion.cs al correr `actualizar`).
//
//   node hosting/firmar-servidor.js <carpeta del paquete> <win-x64|linux-x64> --id <fecha ISO>
//       escribe en la carpeta compilacion.json (la fecha) y archivos.json (el SHA-256 de cada archivo oficial, firmado)
//   node hosting/firmar-servidor.js --aviso <fecha ISO> <archivo de salida>
//       escribe el aviso de versión nueva (servidor.json) que miran los servers prendidos
//   --clave <ruta>   otra clave privada que la de ~/.coverao-firma/clave-privada.pem
//
// Archivos oficiales: los de la raíz del paquete (menos el Server.ini del host y lo que el server escribe ahí), las carpetas
// gamedata/, webclient/ y wwwroot/, y de data/ sólo updates.txt. Lo demás (personajes, base, logs, backups) no se lista: el
// actualizador no toca lo que no está en la lista.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { cargarPrivada, estaCifrada } = require('../desktop/firma');

const args = process.argv.slice(2);
const opcion = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const salir = (codigo, mensaje) => { console.error(mensaje); process.exit(codigo); };
const FECHA = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/;

const rutaClave = opcion('--clave') || path.join(os.homedir(), '.coverao-firma', 'clave-privada.pem');
if (!fs.existsSync(rutaClave)) salir(3, `No hay clave privada en ${rutaClave}: el paquete queda SIN FIRMA y no se podrá instalar con actualizar.`);
const pem = fs.readFileSync(rutaClave, 'utf8');
let clave;
try { clave = cargarPrivada(pem, estaCifrada(pem) ? process.env.COVERAO_FIRMA_CLAVE : undefined); }
catch (e) { salir(3, `No se pudo abrir la clave (${e.message}).`); }
const firmar = (texto) => crypto.sign(null, Buffer.from(texto, 'utf8'), clave).toString('base64');

if (args[0] === '--aviso') {
  const [, id, salida] = args;
  if (!FECHA.test(id || '') || !salida) salir(2, 'Uso: node hosting/firmar-servidor.js --aviso <fecha ISO> <archivo de salida>');
  fs.writeFileSync(salida, JSON.stringify({ version: 1, id, firma: firmar(`servidor-aviso 1\nid ${id}\n`) }));
  console.log(`Aviso de versión nueva: ${salida} (${id})`);
  process.exit(0);
}

const [carpeta, rid] = args;
const id = opcion('--id');
if (!carpeta || !fs.existsSync(carpeta) || !['win-x64', 'linux-x64'].includes(rid) || !FECHA.test(id || '')) {
  salir(2, 'Uso: node hosting/firmar-servidor.js <carpeta del paquete> <win-x64|linux-x64> --id <fecha ISO>');
}

// Lo de la raíz que NO es del paquete: la config del host y lo que el server escribe al lado suyo.
const NO_SON_DEL_PAQUETE = new Set(['server.ini', '.env', 'archivos.json', 'compilacion.json', 'admin-token.txt', 'detener', 'detener.txt']);
const CARPETAS = ['gamedata', 'webclient', 'wwwroot'];

function recorrer(dir, rel, salida) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const r = rel ? `${rel}/${e.name}` : e.name;
    if (e.isDirectory()) recorrer(path.join(dir, e.name), r, salida);
    else if (e.isFile()) salida.push(r);
  }
}

const oficiales = [];
for (const e of fs.readdirSync(carpeta, { withFileTypes: true })) {
  if (e.isFile() && !NO_SON_DEL_PAQUETE.has(e.name.toLowerCase())) oficiales.push(e.name);
  else if (e.isDirectory() && CARPETAS.includes(e.name)) recorrer(path.join(carpeta, e.name), e.name, oficiales);
}
if (fs.existsSync(path.join(carpeta, 'data', 'updates.txt'))) oficiales.push('data/updates.txt');

const archivos = {};
for (const rel of oficiales.sort()) {
  // El manifiesto v1 firma una línea por archivo. El verificador también rechaza estos caracteres.
  if (/\p{Cc}/u.test(rel)) salir(2, 'El paquete contiene una ruta con caracteres de control. Corregí el nombre antes de firmar.');
  archivos[rel] = crypto.createHash('sha256').update(fs.readFileSync(path.join(carpeta, ...rel.split('/')))).digest('hex');
}
const texto = `servidor 1\nid ${id}\nrid ${rid}\n` + Object.keys(archivos).sort().map((r) => `${r} ${archivos[r]}\n`).join('');
fs.writeFileSync(path.join(carpeta, 'archivos.json'), JSON.stringify({ version: 1, id, rid, archivos, firma: firmar(texto) }));
fs.writeFileSync(path.join(carpeta, 'compilacion.json'), JSON.stringify({ id }));
console.log(`Paquete del server firmado: ${Object.keys(archivos).length} archivos, ${rid}, del ${id}`);
