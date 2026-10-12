// FIRMA EL CLIENTE DEL PAQUETE (ver firma.js). La clave privada vive FUERA del repo, en la PC del autor; sin ella no hay firma
// y la app no corre ese cliente.
//
//   node desktop/firmar.js --generar             crea el par de claves en ~/.coverao-firma (una sola vez; hacer una copia)
//   node desktop/firmar.js <carpeta del cliente> firma esa carpeta: escribe <carpeta>/firma.json
//   --clave <ruta>                               otra clave privada que la de ~/.coverao-firma/clave-privada.pem
//
// Al generar, la clave pública queda también en desktop/clave-publica.pem, que es la que viaja adentro de la app.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { ARCHIVO, firmarCarpeta, verificarManifiesto } = require('./firma');

const DIR_CLAVES = path.join(os.homedir(), '.coverao-firma');
const PRIVADA = path.join(DIR_CLAVES, 'clave-privada.pem');
const PUBLICA_DE_LA_APP = path.join(__dirname, 'clave-publica.pem');

const args = process.argv.slice(2);
const opcion = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };

if (args.includes('--generar')) {
  if (fs.existsSync(PRIVADA)) {
    console.error(`Ya hay una clave en ${PRIVADA}: no se pisa. Si de verdad hace falta otra, movela antes.`);
    process.exit(1);
  }
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  fs.mkdirSync(DIR_CLAVES, { recursive: true });
  fs.writeFileSync(PRIVADA, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
  fs.writeFileSync(PUBLICA_DE_LA_APP, publicKey.export({ type: 'spki', format: 'pem' }));
  console.log(`Clave privada: ${PRIVADA}  (HACÉ UNA COPIA: sin ella no se pueden firmar clientes nuevos)`);
  console.log(`Clave pública: ${PUBLICA_DE_LA_APP}  (viaja en la app)`);
  process.exit(0);
}

const carpeta = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--clave');
if (!carpeta || !fs.existsSync(carpeta)) {
  console.error('Uso: node desktop/firmar.js <carpeta del cliente> [--clave <ruta>]   |   node desktop/firmar.js --generar');
  process.exit(2);
}
const rutaClave = opcion('--clave') || PRIVADA;
if (!fs.existsSync(rutaClave)) {
  console.error(`No hay clave privada en ${rutaClave}: el cliente queda SIN FIRMAR y la app no lo va a correr.`);
  process.exit(3);
}
const manifiesto = firmarCarpeta(carpeta, fs.readFileSync(rutaClave, 'utf8'));
// Se verifica contra la clave pública de la app antes de escribir: firmar con una clave que la app no conoce es lo mismo que
// no firmar, y mejor enterarse acá que cuando nadie puede entrar.
if (fs.existsSync(PUBLICA_DE_LA_APP) && !verificarManifiesto(manifiesto, fs.readFileSync(PUBLICA_DE_LA_APP, 'utf8'))) {
  console.error(`La clave ${rutaClave} no es la de la app (${PUBLICA_DE_LA_APP}): la app no aceptaría este cliente.`);
  process.exit(4);
}
fs.writeFileSync(path.join(carpeta, ARCHIVO), JSON.stringify(manifiesto));
console.log(`Cliente firmado: ${Object.keys(manifiesto.archivos).length} archivos, el ${manifiesto.fecha} (${path.join(carpeta, ARCHIVO)})`);
