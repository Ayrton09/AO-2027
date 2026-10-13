// Los tests del aviso de actualización y de las novedades (actualizacion.js), sin Electron: `node desktop/test-actualizacion.js`.
const assert = require('assert');
const crypto = require('crypto');
const { firmarActualizacion, verificarActualizacion, novedadesDelChangelog } = require('./actualizacion');

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
console.log('test-actualizacion: OK');
