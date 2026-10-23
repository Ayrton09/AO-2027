// Los tests del aviso de actualización y de las novedades (actualizacion.js), sin Electron: `node desktop/test-actualizacion.js`.
const assert = require('assert');
const crypto = require('crypto');
const { PLATAFORMAS, firmarActualizacion, verificarActualizacion, novedadesDelChangelog, reemplazarAppImage } = require('./actualizacion');

const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
const pub = publicKey.export({ type: 'spki', format: 'pem' });
const otra = crypto.generateKeyPairSync('ed25519').publicKey.export({ type: 'spki', format: 'pem' });

const base = { id: '2026-10-12T20:00:00.000Z', archivo: 'CoverAO-Setup.exe', sha256: 'a'.repeat(64), tamano: 123456 };
const aviso = firmarActualizacion(base, privateKey);

assert.ok(verificarActualizacion(aviso, pub, ''), 'el aviso del autor pasa sin fecha local (sin empaquetar)');
assert.ok(verificarActualizacion(aviso, pub, '2026-10-12T19:00:00.000Z'), 'y si es más nuevo que la app');
assert.strictEqual(verificarActualizacion(aviso, pub, '2026-10-12T20:00:00.000Z'), null, 'el mismo que la app no es nuevo');
assert.strictEqual(verificarActualizacion(aviso, pub, '2026-10-13T00:00:00.000Z'), null, 'uno viejo no vuelve a instalarse');
assert.strictEqual(verificarActualizacion(aviso, otra, ''), null, 'con otra clave no pasa');
for (const [campo, valor] of [['id', '2026-10-30T00:00:00.000Z'], ['archivo', 'Otro.exe'], ['sha256', 'b'.repeat(64)],
  ['tamano', 654321]]) {
  assert.strictEqual(verificarActualizacion({ ...aviso, [campo]: valor }, pub, ''), null, `cambiarle ${campo} rompe la firma`);
}
for (const archivo of ['../CoverAO-Setup.exe', 'a/b.exe', 'C:\\x.exe', 'instalador.bat', '']) {
  assert.strictEqual(verificarActualizacion(firmarActualizacion({ ...base, archivo }, privateKey), pub, ''), null,
    `"${archivo}" no es un nombre de instalador`);
}
for (const tamano of [0, -1, 1.5, '123']) {
  assert.strictEqual(verificarActualizacion(firmarActualizacion({ ...base, tamano }, privateKey), pub, ''), null, `tamaño ${tamano}`);
}
assert.strictEqual(verificarActualizacion(firmarActualizacion({ ...base, id: 'ayer' }, privateKey), pub, ''), null, 'id sin fecha');
assert.strictEqual(verificarActualizacion(null, pub, ''), null);
assert.strictEqual(verificarActualizacion({ ...aviso, version: 2 }, pub, ''), null);

const changelog = `# Novedades

Lo de arriba no es una sección.

## 1.0.0 (2026-10-09)

Lo nuevo desde el paquete del 6 de octubre.

### Combate y magia

- **Rematar una criatura** ya no cobra dos veces.
- Un mago que te tenía marcado ya no le tira hechizos
  a tu cadáver: \`uno\` de daño.

### Para el que hostea

- [ProxyDeConfianza](docs/HOSTING.md) en el *Server.ini*.

## 1.0.0 (2026-10-06)

- Esto es de antes y no va.
`;
assert.deepStrictEqual(novedadesDelChangelog(changelog), [
  '== Combate y magia ==',
  'Rematar una criatura ya no cobra dos veces.',
  'Un mago que te tenía marcado ya no le tira hechizos a tu cadáver: uno de daño.',
  '== Para el que hostea ==',
  'ProxyDeConfianza en el Server.ini.',
], 'la sección más nueva, sin Markdown y con las viñetas de dos renglones enteras');
assert.deepStrictEqual(novedadesDelChangelog(changelog.replace(/\n/g, '\r\n')), novedadesDelChangelog(changelog), 'con CRLF igual');
assert.deepStrictEqual(novedadesDelChangelog(''), []);

// LINUX: su aviso es otro y su archivo un .AppImage. El de una plataforma no le sirve a la otra.
assert.deepStrictEqual(PLATAFORMAS.linux, { aviso: 'actualizacion-linux.json', extension: '.AppImage' });
assert.strictEqual(PLATAFORMAS.win32.aviso, 'actualizacion.json', 'Windows sigue leyendo el aviso de siempre');
const linux = firmarActualizacion({ ...base, archivo: 'CoverAO-x86_64.AppImage' }, privateKey);
assert.ok(verificarActualizacion(linux, pub, '', '.AppImage'), 'el AppImage firmado pasa en Linux');
assert.strictEqual(verificarActualizacion(linux, pub, ''), null, 'y en Windows no: no es un instalador');
assert.strictEqual(verificarActualizacion(aviso, pub, '', '.AppImage'), null, 'a Linux no se le hace bajar un .exe');
for (const archivo of ['.AppImage', '../CoverAO.AppImage', 'CoverAO.appimage', 'CoverAO.AppImage.sh']) {
  assert.strictEqual(verificarActualizacion(firmarActualizacion({ ...base, archivo }, privateKey), pub, '', '.AppImage'), null,
    `"${archivo}" no es un AppImage`);
}

// EL REEMPLAZO DEL APPIMAGE: el nuevo queda en el lugar del viejo, ejecutable, y sin temporales.
const fs = require('fs');
const os = require('os');
const path = require('path');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'coverao-appimage-'));
try {
  const viejo = path.join(dir, 'CoverAO.AppImage');
  const bajado = path.join(dir, 'bajado.AppImage');
  fs.writeFileSync(viejo, 'el viejo');
  fs.writeFileSync(bajado, 'el nuevo');
  reemplazarAppImage(bajado, viejo);
  assert.strictEqual(fs.readFileSync(viejo, 'utf8'), 'el nuevo', 'el AppImage es el bajado');
  if (process.platform !== 'win32') assert.ok(fs.statSync(viejo).mode & 0o100, 'y se puede ejecutar');
  assert.deepStrictEqual(fs.readdirSync(dir).sort(), ['CoverAO.AppImage', 'bajado.AppImage'], 'sin el temporal');
  // Si el rename falla (la carpeta sin permiso, un disco lleno), el viejo queda entero y el temporal no.
  const roto = { ...fs, renameSync: () => { throw new Error('sin permiso'); } };
  fs.writeFileSync(viejo, 'el viejo');
  assert.throws(() => reemplazarAppImage(bajado, viejo, roto), /sin permiso/);
  assert.strictEqual(fs.readFileSync(viejo, 'utf8'), 'el viejo', 'el viejo sigue entero');
  assert.ok(!fs.existsSync(`${viejo}.nueva`), 'y el temporal no quedó');
} finally { fs.rmSync(dir, { recursive: true, force: true }); }

console.log('test-actualizacion: OK');
