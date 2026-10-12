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
