// FIRMA EL CLIENTE DEL PAQUETE (ver firma.js). La clave privada vive FUERA del repo, en la PC del autor; sin ella no hay firma
// y la app no corre ese cliente.
//
//   node desktop/firmar.js --generar             crea el par de claves en ~/.coverao-firma (una sola vez), con contraseña
//   node desktop/firmar.js <carpeta del cliente> firma esa carpeta: escribe <carpeta>/firma.json
//   node desktop/firmar.js --poner-clave         le pone (o le cambia) la contraseña a la clave que ya está
//   node desktop/firmar.js --copia <archivo>     guarda una copia CIFRADA de la clave, para el backup
//   --clave <ruta>                               otra clave privada que la de ~/.coverao-firma/clave-privada.pem
//
// LA CONTRASEÑA se pregunta en la consola, sin que se vea lo que se escribe. Para correrlo sin consola va en la variable
// COVERAO_FIRMA_CLAVE, y la nueva (la de --generar, --poner-clave y la copia) en COVERAO_FIRMA_CLAVE_NUEVA. Una clave sin cifrar
// (la de antes del 12/10) firma igual, sin preguntar nada, hasta que se le ponga con --poner-clave.
//
// Al generar, la clave pública queda también en desktop/clave-publica.pem, que es la que viaja adentro de la app.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const readline = require('readline');
const { ARCHIVO, firmarCarpeta, verificarManifiesto, estaCifrada, cargarPrivada, cifrarPrivada, publicaDe } = require('./firma');

const DIR_CLAVES = path.join(os.homedir(), '.coverao-firma');
const PRIVADA = path.join(DIR_CLAVES, 'clave-privada.pem');
const PUBLICA_DE_LA_APP = path.join(__dirname, 'clave-publica.pem');
const LARGO_MINIMO = 10;

const args = process.argv.slice(2);
const opcion = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const rutaClave = opcion('--clave') || PRIVADA;

function salir(codigo, mensaje) {
  console.error(mensaje);
  process.exit(codigo);
}

/** Pregunta en la consola sin mostrar lo que se escribe. */
function preguntarOculto(texto) {
  if (!process.stdin.isTTY) return Promise.reject(new Error('no hay una consola donde preguntar la contraseña'));
  return new Promise((listo) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: true });
    process.stdout.write(texto);
    rl._writeToOutput = () => {};
    rl.on('SIGINT', () => { rl.close(); process.stdout.write('\n'); process.exit(130); });
    rl.question('', (r) => { rl.close(); process.stdout.write('\n'); listo(r); });
  });
}

/** La contraseña de la clave: la de la variable, o preguntada. */
const contrasenaActual = () => process.env.COVERAO_FIRMA_CLAVE ?? preguntarOculto(`Contraseña de la clave (${rutaClave}): `);

/** Una contraseña nueva: la de la variable, o preguntada dos veces. */
async function contrasenaNueva() {
  let c = process.env.COVERAO_FIRMA_CLAVE_NUEVA;
  if (c === undefined) {
    c = await preguntarOculto(`Contraseña nueva (${LARGO_MINIMO} caracteres o más): `);
    if (c !== await preguntarOculto('Otra vez: ')) salir(5, 'No son iguales: no se cambió nada.');
  }
  if (c.length < LARGO_MINIMO) salir(5, `La contraseña tiene que tener ${LARGO_MINIMO} caracteres o más: no se cambió nada.`);
  return c;
}

/** La clave privada de `rutaClave`, con la contraseña si está cifrada. */
async function leerClave() {
  if (!fs.existsSync(rutaClave)) salir(3, `No hay clave privada en ${rutaClave}: el cliente queda SIN FIRMAR y la app no lo va a correr.`);
  const pem = fs.readFileSync(rutaClave, 'utf8');
  return cargarPrivada(pem, estaCifrada(pem) ? await contrasenaActual() : undefined);
}

/** La clave cifrada a `ruta`, pasando por un temporal y comprobando antes que con la contraseña vuelve a dar la misma clave. */
function escribirCifrada(ruta, clave, contrasena) {
  const pem = cifrarPrivada(clave, contrasena);
  if (publicaDe(cargarPrivada(pem, contrasena)) !== publicaDe(clave)) salir(6, 'La clave cifrada no da la misma: no se escribió nada.');
  const temporal = ruta + '.tmp';
  fs.writeFileSync(temporal, pem, { mode: 0o600 });
  fs.renameSync(temporal, ruta);
}

async function principal() {
  if (args.includes('--generar')) {
    if (fs.existsSync(PRIVADA)) salir(1, `Ya hay una clave en ${PRIVADA}: no se pisa. Si de verdad hace falta otra, movela antes.`);
    const contrasena = await contrasenaNueva();
    const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
    fs.mkdirSync(DIR_CLAVES, { recursive: true });
    escribirCifrada(PRIVADA, privateKey, contrasena);
    fs.writeFileSync(PUBLICA_DE_LA_APP, publicKey.export({ type: 'spki', format: 'pem' }));
    console.log(`Clave privada: ${PRIVADA}  (HACÉ UNA COPIA con --copia: sin ella no se pueden firmar clientes nuevos)`);
    console.log(`Clave pública: ${PUBLICA_DE_LA_APP}  (viaja en la app)`);
    return;
  }

  if (args.includes('--poner-clave')) {
    const clave = await leerClave();
    escribirCifrada(rutaClave, clave, await contrasenaNueva());
    console.log(`La clave de ${rutaClave} quedó cifrada. Sin la contraseña no se firma: no te la olvides.`);
    return;
  }

  if (args.includes('--copia')) {
    const destino = opcion('--copia');
    if (!destino || destino.startsWith('--')) salir(2, 'Uso: node desktop/firmar.js --copia <archivo nuevo>');
    if (fs.existsSync(destino)) salir(1, `Ya hay un archivo en ${destino}: no se pisa.`);
    if (!fs.existsSync(rutaClave)) salir(3, `No hay clave privada en ${rutaClave}.`);
    const pem = fs.readFileSync(rutaClave, 'utf8');
    // Cifrada, la copia es el mismo archivo y se abre con la misma contraseña; sin cifrar, la copia sale cifrada con una nueva.
    if (estaCifrada(pem)) fs.writeFileSync(destino, pem, { mode: 0o600, flag: 'wx' });
    else escribirCifrada(destino, cargarPrivada(pem), await contrasenaNueva());
    console.log(`Copia cifrada en ${destino}: guardala fuera de esta PC (un pendrive, la nube). Sin la contraseña no sirve.`);
    return;
  }

  const carpeta = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--clave');
  if (!carpeta || !fs.existsSync(carpeta)) {
    salir(2, 'Uso: node desktop/firmar.js <carpeta del cliente> [--clave <ruta>]   |   --generar   |   --poner-clave   |   --copia <archivo>');
  }
  let clave;
  try { clave = await leerClave(); }
  catch (e) { salir(3, `No se pudo abrir la clave (${e.message}): el cliente queda SIN FIRMAR y la app no lo va a correr.`); }
  const manifiesto = firmarCarpeta(carpeta, clave);
  // Se verifica contra la clave pública de la app antes de escribir: firmar con una clave que la app no conoce es lo mismo que
  // no firmar, y mejor enterarse acá que cuando nadie puede entrar.
  if (fs.existsSync(PUBLICA_DE_LA_APP) && !verificarManifiesto(manifiesto, fs.readFileSync(PUBLICA_DE_LA_APP, 'utf8'))) {
    salir(4, `La clave ${rutaClave} no es la de la app (${PUBLICA_DE_LA_APP}): la app no aceptaría este cliente.`);
  }
  fs.writeFileSync(path.join(carpeta, ARCHIVO), JSON.stringify(manifiesto));
  console.log(`Cliente firmado: ${Object.keys(manifiesto.archivos).length} archivos, el ${manifiesto.fecha} (${path.join(carpeta, ARCHIVO)})`);
}

principal().catch((e) => salir(7, String(e?.message ?? e)));
