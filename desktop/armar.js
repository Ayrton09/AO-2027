// ARMA EL INSTALADOR DE WINDOWS Y SU AVISO DE ACTUALIZACIÓN (ver actualizacion.js). Sólo cuando lo pide el autor.
//
//   node desktop/armar.js            arma release/CoverAO-Setup.exe y release/actualizacion.json
//   node desktop/armar.js --subir    y los sube al release v1.0.0 del repo del juego, reemplazando los de antes
//   --clave <ruta>                   otra clave privada que la de ~/.coverao-firma/clave-privada.pem
//
// La versión NO cambia (1.0.0 hasta que el juego sea público): lo que distingue un instalador del anterior es la fecha que se
// le pone acá, en compilacion.json, junto con las novedades del CHANGELOG que muestra el menú.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');
const { cargarPrivada, estaCifrada } = require('./firma');
const { firmarActualizacion, verificarActualizacion, novedadesDelChangelog } = require('./actualizacion');

const REPO = 'Ayrton09/CoverAO';
const ETIQUETA = 'v1.0.0';
const INSTALADOR = 'CoverAO-Setup.exe';
const COMPILACION = path.join(__dirname, 'compilacion.json');
const SALIDA = path.join(__dirname, 'release');

const args = process.argv.slice(2);
const opcion = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const salir = (codigo, mensaje) => { console.error(mensaje); process.exit(codigo); };

// La clave primero: sin ella no hay aviso firmado, y armar el instalador para nada lleva minutos.
const rutaClave = opcion('--clave') || path.join(os.homedir(), '.coverao-firma', 'clave-privada.pem');
if (!fs.existsSync(rutaClave)) salir(3, `No hay clave privada en ${rutaClave}: sin ella la app no aceptaría la actualización.`);
const pem = fs.readFileSync(rutaClave, 'utf8');
let clave;
try { clave = cargarPrivada(pem, estaCifrada(pem) ? process.env.COVERAO_FIRMA_CLAVE : undefined); }
catch (e) { salir(3, `No se pudo abrir la clave (${e.message}).`); }
const publica = fs.readFileSync(path.join(__dirname, 'clave-publica.pem'), 'utf8');

const changelog = path.join(__dirname, '..', 'CHANGELOG.md');
const novedades = fs.existsSync(changelog) ? novedadesDelChangelog(fs.readFileSync(changelog, 'utf8')) : [];
const id = new Date().toISOString();
fs.writeFileSync(COMPILACION, JSON.stringify({ id, novedades }));
console.log(`compilación ${id}, ${novedades.length} líneas de novedades`);

try {
  const r = spawnSync(process.execPath, [require.resolve('electron-builder/cli.js'), '--win', 'nsis', '--publish', 'never'],
    { cwd: __dirname, stdio: 'inherit' });
  if (r.status !== 0) salir(4, 'electron-builder falló.');
} finally {
  // Sólo hace falta adentro del paquete: sin empaquetar (`npm start`) la app corre sin fecha, como siempre.
  fs.rmSync(COMPILACION, { force: true });
}

const exe = path.join(SALIDA, INSTALADOR);
if (!fs.existsSync(exe)) salir(4, `No apareció ${exe}.`);
const bytes = fs.readFileSync(exe);
const aviso = firmarActualizacion({ id, archivo: INSTALADOR, sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
  tamano: bytes.length }, clave);
// Como en firmar.js: firmado con una clave que la app no conoce es lo mismo que sin firmar.
if (!verificarActualizacion(aviso, publica, '')) salir(5, `La clave ${rutaClave} no es la de la app: no la aceptaría.`);
const rutaAviso = path.join(SALIDA, 'actualizacion.json');
fs.writeFileSync(rutaAviso, JSON.stringify(aviso));
console.log(`${INSTALADOR}: ${(bytes.length / 1048576).toFixed(1)} MB, sha256 ${aviso.sha256.slice(0, 12)}…`);

if (args.includes('--subir')) {
  // El instalador ANTES que el aviso: una app que lee el aviso nuevo tiene que encontrar ya el instalador nuevo.
  for (const archivo of [exe, rutaAviso]) {
    const r = spawnSync('gh', ['release', 'upload', ETIQUETA, archivo, '--clobber', '-R', REPO], { stdio: 'inherit' });
    if (r.status !== 0) salir(6, `No se pudo subir ${path.basename(archivo)} a ${REPO} ${ETIQUETA}.`);
  }
  console.log(`Subido a https://github.com/${REPO}/releases/tag/${ETIQUETA}`);
}
