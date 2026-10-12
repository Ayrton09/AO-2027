// Los tests de la firma del cliente (firma.js), sin Electron: `node desktop/test-firma.js`.
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { firmarCarpeta, verificarManifiesto, rutaDe, esLoFirmado, SINTETICAS } = require('./firma');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'coverao-firma-'));
try {
  fs.mkdirSync(path.join(dir, 'dist'));
  fs.writeFileSync(path.join(dir, 'index.html'), '<html>juego</html>');
  fs.writeFileSync(path.join(dir, 'dist', 'codec.js'), 'export const x = 1;');
  fs.writeFileSync(path.join(dir, 'LEEME.md'), 'notas internas');

  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  const pub = publicKey.export({ type: 'spki', format: 'pem' });
  const priv = privateKey.export({ type: 'pkcs8', format: 'pem' });
  const otra = crypto.generateKeyPairSync('ed25519').publicKey.export({ type: 'spki', format: 'pem' });

  const m = firmarCarpeta(dir, priv);
  assert.deepStrictEqual(Object.keys(m.archivos).sort(), ['dist/codec.js', 'index.html'], 'firma todo menos las notas .md');

  const archivos = verificarManifiesto(m, pub);
  assert.ok(archivos, 'la firma del autor pasa');
  assert.strictEqual(verificarManifiesto(m, otra), null, 'con otra clave no pasa');
  assert.strictEqual(verificarManifiesto(JSON.parse(JSON.stringify({ ...m, archivos: { ...m.archivos, 'trampa.js': m.archivos['index.html'] } })), pub),
    null, 'agregarle un archivo al manifiesto rompe la firma');
  assert.strictEqual(verificarManifiesto(null, pub), null);
  assert.strictEqual(verificarManifiesto({ version: 1, archivos: [], firma: '' }, pub), null);

  // LA FECHA DE LA FIRMA y el mínimo que puede pedir la app.
  assert.ok(m.version === 2 && /^\d{4}-\d{2}-\d{2}T/.test(m.fecha), 'el manifiesto lleva la fecha');
  assert.strictEqual(verificarManifiesto({ ...m, fecha: undefined }, pub), null, 'la versión 2 sin fecha no pasa');
  const vieja = firmarCarpeta(dir, priv, new Date('2026-10-01T00:00:00Z'));
  assert.ok(verificarManifiesto(vieja, pub), 'sin mínimo, una firma vieja pasa');
  assert.ok(verificarManifiesto(vieja, pub, '2026-10-01T00:00:00Z'), 'la firmada justo en el mínimo pasa');
  assert.strictEqual(verificarManifiesto(vieja, pub, '2026-10-12T00:00:00Z'), null, 'la firmada antes del mínimo no pasa');
  assert.strictEqual(verificarManifiesto({ ...vieja, fecha: '2026-10-20T00:00:00.000Z' }, pub, '2026-10-12T00:00:00Z'), null,
    'cambiarle la fecha rompe la firma');
  const texto1 = Object.keys(m.archivos).sort().map((r) => `${r} ${m.archivos[r]}\n`).join('');
  const v1 = { version: 1, archivos: m.archivos, firma: crypto.sign(null, Buffer.from(texto1), priv).toString('base64') };
  assert.ok(verificarManifiesto(v1, pub), 'la versión 1, sin fecha, pasa sin mínimo');
  assert.strictEqual(verificarManifiesto(v1, pub, '2026-10-12T00:00:00Z'), null, 'y con mínimo no pasa');
  assert.strictEqual(verificarManifiesto({ ...v1, version: 2, fecha: m.fecha }, pub), null, 'su firma no vale como de la versión 2');

  assert.ok(esLoFirmado(archivos, 'index.html', Buffer.from('<html>juego</html>')), 'el archivo tal cual pasa');
  assert.ok(!esLoFirmado(archivos, 'index.html', Buffer.from('<html>juego con trampa</html>')), 'cambiado no pasa');
  assert.ok(!esLoFirmado(archivos, 'trampa.js', Buffer.from('')), 'uno que no está en el manifiesto no pasa');
  assert.ok(!esLoFirmado(archivos, 'constructor', Buffer.from('')), 'ni una propiedad heredada');

  assert.strictEqual(rutaDe('http://srv:7669/'), 'index.html');
  assert.strictEqual(rutaDe('http://srv:7669/?mute'), 'index.html');
  assert.strictEqual(rutaDe('http://srv:7669/dist/src/codec/bytequeue.js?v=32'), 'dist/src/codec/bytequeue.js');
  assert.strictEqual(rutaDe('http://srv:7669/assets/mapas%20viejos/a.json'), 'assets/mapas viejos/a.json');
  assert.ok(SINTETICAS.has('coverao-config.json') && SINTETICAS.has('api/publico.json'));
  console.log('test-firma: OK');
} finally {
  fs.rmSync(dir, { recursive: true, force: true });
}
