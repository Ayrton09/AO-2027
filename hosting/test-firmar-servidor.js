const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const id = '2026-10-13T00:00:00.000Z';
const rid = 'linux-x64';

function firmarPaquete(t, archivos) {
  const raiz = fs.mkdtempSync(path.join(os.tmpdir(), 'coverao-firma-servidor-'));
  t.after(() => fs.rmSync(raiz, { recursive: true, force: true }));
  const paquete = path.join(raiz, 'paquete');
  fs.mkdirSync(paquete);
  for (const [nombre, contenido] of Object.entries(archivos)) {
    fs.writeFileSync(path.join(paquete, nombre), contenido);
  }
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ed25519');
  const clave = path.join(raiz, 'prueba.pem');
  fs.writeFileSync(clave, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
  const resultado = spawnSync(process.execPath, [path.join(__dirname, 'firmar-servidor.js'), paquete, rid,
    '--id', id, '--clave', clave], { encoding: 'utf8' });
  assert.ifError(resultado.error);
  return { paquete, publicKey, resultado };
}

test('el manifiesto conserva la firma v1 y excluye el .env del host', (t) => {
  const nombre = 'mapa con espacios.txt';
  const contenido = 'contenido de prueba';
  const { paquete, publicKey, resultado } = firmarPaquete(t, { [nombre]: contenido, '.env': 'VALOR=ficticio' });
  assert.equal(resultado.status, 0, resultado.stderr);
  const manifiesto = JSON.parse(fs.readFileSync(path.join(paquete, 'archivos.json'), 'utf8'));
  const hash = crypto.createHash('sha256').update(contenido).digest('hex');
  assert.equal(manifiesto.version, 1);
  assert.deepEqual(manifiesto.archivos, { [nombre]: hash });
  const textoV1 = Buffer.from(`servidor 1\nid ${id}\nrid ${rid}\n${nombre} ${hash}\n`, 'utf8');
  assert.ok(crypto.verify(null, textoV1, publicKey, Buffer.from(manifiesto.firma, 'base64')));
});

test('el firmante rechaza nombres con caracteres de control', (t) => {
  // U+0085 también se puede usar como parte de un nombre de archivo en Windows.
  const { paquete, resultado } = firmarPaquete(t, { 'mapa\u00851.txt': 'contenido de prueba' });
  assert.equal(resultado.status, 2, resultado.stdout + resultado.stderr);
  assert.match(resultado.stderr, /caracteres de control/);
  assert.equal(fs.existsSync(path.join(paquete, 'archivos.json')), false);
  assert.equal(fs.existsSync(path.join(paquete, 'compilacion.json')), false);
});
