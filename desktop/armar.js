// ARMA EL JUEGO DE ESCRITORIO Y SU AVISO DE ACTUALIZACIÓN (ver actualizacion.js). Sólo cuando lo pide el autor.
//
//   node desktop/armar.js            arma release/CoverAO-Setup.exe y release/actualizacion.json (Windows)
//   node desktop/armar.js --linux    arma release/CoverAO-x86_64.AppImage y release/actualizacion-linux.json. Corre EN LINUX:
//                                    el AppImage lleva enlaces simbólicos y permisos que Windows no arma (ver
//                                    hosting/armar-app-linux.sh, que lo hace en WSL)
//   --subir                          sube la app al borrador de la versión de VERSION en el repo principal
//   --clave <ruta>                   otra clave privada que la de ~/.coverao-firma/clave-privada.pem
//
// VERSION fija la versión del release. La fecha de compilacion.json decide si hay una actualización más nueva.
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');
const { cargarPrivada, estaCifrada } = require('./firma');
const { PLATAFORMAS, firmarActualizacion, verificarActualizacion, novedadesDelChangelog } = require('./actualizacion');

const args = process.argv.slice(2);
const LINUX = args.includes('--linux');
const REPO = 'Ayrton09/AO-2027';
const VERSION = fs.readFileSync(path.join(__dirname, '..', 'VERSION'), 'utf8').trim();
if (!/^\d+\.\d+\.\d+$/.test(VERSION)) throw new Error('VERSION debe tener formato X.Y.Z.');
if (require('./package.json').version !== VERSION) throw new Error('desktop/package.json no coincide con VERSION.');
const ETIQUETA = `v${VERSION}`;
const INSTALADOR = LINUX ? 'CoverAO-x86_64.AppImage' : 'CoverAO-Setup.exe';   // los artifactName de package.json
const PLATAFORMA = LINUX ? PLATAFORMAS.linux : PLATAFORMAS.win32;
const COMPILACION = path.join(__dirname, 'compilacion.json');
const SALIDA = path.join(__dirname, 'release');

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
  const objetivo = LINUX ? ['--linux', 'AppImage'] : ['--win', 'nsis'];
  const r = spawnSync(process.execPath, [require.resolve('electron-builder/cli.js'), ...objetivo, '--publish', 'never'],
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
if (!verificarActualizacion(aviso, publica, '', PLATAFORMA.extension)) salir(5, `La clave ${rutaClave} no es la de la app: no la aceptaría.`);
const rutaAviso = path.join(SALIDA, PLATAFORMA.aviso);
fs.writeFileSync(rutaAviso, JSON.stringify(aviso));
console.log(`${INSTALADOR}: ${(bytes.length / 1048576).toFixed(1)} MB, sha256 ${aviso.sha256.slice(0, 12)}…`);

if (args.includes('--subir')) {
  // La publicación completa comprueba el commit del repo principal y publica los avisos al final, en subir-version.ps1.
  const consulta = spawnSync('gh', ['release', 'view', ETIQUETA, '--json', 'isDraft', '-R', REPO], { encoding: 'utf8' });
  if (consulta.status !== 0 || !JSON.parse(consulta.stdout).isDraft)
    salir(6, `Hace falta un borrador ${ETIQUETA} en ${REPO}. Usá hosting/subir-version.ps1 para preparar/publicar la release.`);
  // El instalador ANTES que el aviso: una app que lee el aviso nuevo tiene que encontrar ya el instalador nuevo.
  for (const archivo of [exe, rutaAviso]) {
    const r = spawnSync('gh', ['release', 'upload', ETIQUETA, archivo, '--clobber', '-R', REPO], { stdio: 'inherit' });
    if (r.status !== 0) salir(6, `No se pudo subir ${path.basename(archivo)} a ${REPO} ${ETIQUETA}.`);
  }
  console.log(`Subido a https://github.com/${REPO}/releases/tag/${ETIQUETA}`);
}
