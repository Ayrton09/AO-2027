// Los tests de la firma del cliente (firma.js), sin Electron: `node desktop/test-firma.js`.
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { firmarCarpeta, verificarManifiesto, rutaDe, esLoFirmado, SINTETICAS, estaCifrada, cargarPrivada, cifrarPrivada,
  publicaDe, conPlazos, juntar } = require('./firma');

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

  // LA CLAVE CON CONTRASEÑA.
  const cifrada = cifrarPrivada(privateKey, 'una contraseña larga');
  assert.ok(estaCifrada(cifrada) && !estaCifrada(priv), 'el PEM cifrado se reconoce');
  assert.strictEqual(publicaDe(cargarPrivada(cifrada, 'una contraseña larga')), pub, 'con la contraseña da la misma clave');
  assert.throws(() => cargarPrivada(cifrada, 'otra contraseña'), /no es la contraseña/);
  assert.throws(() => cargarPrivada(cifrada, undefined), /falta la contraseña/);
  assert.ok(verificarManifiesto(firmarCarpeta(dir, cargarPrivada(cifrada, 'una contraseña larga')), pub), 'y firma igual');
  assert.strictEqual(publicaDe(cargarPrivada(priv)), pub, 'la de antes, sin cifrar, se abre sin contraseña');

  // firmar.js, sin consola: la contraseña va en las variables.
  const k = path.join(dir, 'clave.pem');
  fs.writeFileSync(k, priv);
  const firmar = (extra, env = {}) => spawnSync(process.execPath, [path.join(__dirname, 'firmar.js'), ...extra, '--clave', k],
    { env: { ...process.env, COVERAO_FIRMA_CLAVE: undefined, COVERAO_FIRMA_CLAVE_NUEVA: undefined, ...env }, encoding: 'utf8' });
  assert.strictEqual(firmar(['--poner-clave'], { COVERAO_FIRMA_CLAVE_NUEVA: 'corta' }).status, 5, 'una contraseña corta no va');
  assert.ok(!estaCifrada(fs.readFileSync(k, 'utf8')), 'y la clave quedó como estaba');
  assert.strictEqual(firmar(['--poner-clave'], { COVERAO_FIRMA_CLAVE_NUEVA: 'la contraseña nueva' }).status, 0);
  const enDisco = fs.readFileSync(k, 'utf8');
  assert.ok(estaCifrada(enDisco), '--poner-clave la deja cifrada');
  assert.strictEqual(publicaDe(cargarPrivada(enDisco, 'la contraseña nueva')), pub, 'y es la misma clave');
  const cliente = path.join(dir, 'dist');
  assert.strictEqual(firmar([cliente]).status, 3, 'sin la contraseña ni consola, no firma');
  assert.strictEqual(firmar([cliente], { COVERAO_FIRMA_CLAVE: 'otra' }).status, 3, 'con otra contraseña, tampoco');
  // Con la buena la abre y firma; no la escribe porque no es la clave de la app (desktop/clave-publica.pem).
  assert.strictEqual(firmar([cliente], { COVERAO_FIRMA_CLAVE: 'la contraseña nueva' }).status, 4, 'con la buena, la abre');
  const copia = path.join(dir, 'copia.pem');
  assert.strictEqual(firmar(['--copia', copia]).status, 0);
  assert.strictEqual(fs.readFileSync(copia, 'utf8'), enDisco, 'la copia de una cifrada es el mismo archivo');
  assert.strictEqual(firmar(['--copia', copia]).status, 1, 'la copia no pisa un archivo');
  console.log('test-firma: OK');
} finally {
  fs.rmSync(dir, { recursive: true, force: true });
}

// LO QUE SE BAJA DEL SERVER: el tope y los plazos (conPlazos y juntar).
(async () => {
  const flujo = (pedazos, cadaMs = 0) => new ReadableStream({
    async pull(c) {
      if (cadaMs) await new Promise((listo) => setTimeout(listo, cadaMs));
      if (pedazos.length) c.enqueue(pedazos.shift()); else c.close();
    },
  });
  const b = (t) => Buffer.from(t);
  assert.deepStrictEqual(await juntar(flujo([b('ab'), b('cd')]), 4), b('abcd'), 'justo en el tope pasa');
  assert.strictEqual(await juntar(flujo([b('ab'), b('cde')]), 4), null, 'pasado el tope, null');
  assert.deepStrictEqual(await juntar(null, 4), Buffer.alloc(0), 'sin cuerpo, vacío');
  assert.deepStrictEqual(await juntar(flujo([b('x'), b('y')], 20), 100, conPlazos(1000, 10000)), b('xy'), 'a tiempo pasa');
  await assert.rejects(juntar(flujo([b('x')], 300), 100, conPlazos(100, 10000)), /dejó de mandar/, 'el plazo sin datos');
  // Un server que gotea: cada pedazo llega mucho antes del plazo sin datos, pero el total vence.
  const goteo = flujo(Array.from({ length: 100 }, () => b('x')), 20);
  await assert.rejects(juntar(goteo, 1000, conPlazos(1000, 200)), /tarda demasiado/, 'el plazo total');
  console.log('test-firma (bajadas): OK');
})().catch((e) => { console.error(e); process.exit(1); });
