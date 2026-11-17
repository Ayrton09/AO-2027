const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const vm = require('node:vm');
const estadoServidor = require('./estado-servidor');
const fuente = fs.readFileSync(require.resolve('./main.js'), 'utf8');

async function probar() {
  let modo = 'encendido';
  const config = { version: '1.0.31', wsPort: 7667, nombre: 'Reino de prueba' };
  const server = http.createServer((req, res) => {
    const publico = req.url.startsWith('/api/');
    if (modo === 'lento') { res.writeHead(200); res.write('{'); return; }
    if (['502', '404', '302'].includes(modo)) { res.writeHead(Number(modo), { Location: '/error' }); res.end('proxy apagado'); return; }
    if (modo === 'html') { res.end('<html>Servidor apagado</html>'); return; }
    if (modo === 'otro-json') { res.end('{"ok":true}'); return; }
    if (modo === 'sin-publica' && publico) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify(publico ? { servidor: { nombre: config.nombre, version: config.version, online: 3 } } : config));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const direccion = `http://127.0.0.1:${server.address().port}/`;
  const consultar = () => estadoServidor.consultarEstado(direccion, { pedir: fetch, leerJson: r => r.json(), esperaMs: 150 });
  try {
    const guardado = { direccion };
    const inicial = await consultar();
    assert.equal(inicial.enLinea, true);
    assert.equal(inicial.jugadores, 3);
    assert.equal(estadoServidor.recordarNombre(guardado, inicial), true);
    for (modo of ['502', '404', '302', 'html', 'otro-json', 'lento']) {
      const caido = await consultar();
      assert.equal(caido.enLinea, false, modo);
      assert.equal(caido.ping, null, modo);
      assert.equal(estadoServidor.recordarNombre(guardado, caido), false);
      assert.equal(guardado.nombreDelServer, 'Reino de prueba', modo);
    }
    modo = 'sin-publica';
    const privado = await consultar();
    assert.equal(privado.enLinea, true);
    assert.equal(privado.jugadores, null);
    assert.equal(privado.nombre, 'Reino de prueba');
    assert.equal(estadoServidor.recordarNombre(guardado, { enLinea: true, nombre: null }), false);
    const recargado = JSON.parse(JSON.stringify(guardado));
    assert.equal(recargado.nombreDelServer, 'Reino de prueba');
    modo = 'encendido'; config.nombre = 'Reino renombrado';
    assert.equal(estadoServidor.recordarNombre(recargado, await consultar()), true);
    assert.equal(recargado.nombreDelServer, 'Reino renombrado');
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
  assert.equal((await consultar()).enLinea, false, 'el socket cerrado también figura apagado');

  // El handler real del IPC conserva el nombre al caer y guarda un cambio de nombre confirmado.
  let consultarIpc, estado = { enLinea: false, nombre: null }, guardados = 0;
  const configLocal = { servidores: [{ direccion: 'http://prueba/', nombreDelServer: 'Mi servidor' }] };
  const contexto = vm.createContext({
    estadoServidor, config: configLocal, normalizar: x => x, consultar: async () => estado,
    guardarConfig: () => { guardados++; }, delMenu: (_, fn) => { consultarIpc = fn; },
  });
  vm.runInContext(fuente.slice(fuente.indexOf("delMenu('consultar',"), fuente.indexOf('/** El "atrás"')), contexto);
  await consultarIpc(null, 'http://prueba/');
  assert.equal(configLocal.servidores[0].nombreDelServer, 'Mi servidor');
  assert.equal(guardados, 0);
  estado = { enLinea: true, nombre: 'Nombre actualizado' };
  await consultarIpc(null, 'http://prueba/');
  assert.equal(configLocal.servidores[0].nombreDelServer, 'Nombre actualizado');
  assert.equal(guardados, 1);

  // Una caída durante la descarga no se presenta como un cliente sin firma. La firma inválida sigue bloqueada.
  const firmaReal = require('./firma');
  const claves = require('node:crypto').generateKeyPairSync('ed25519');
  let bajar = async () => { throw new Error('conexión cerrada'); };
  const contextoFirma = vm.createContext({
    VERIFICAR: true, CLAVE_PUBLICA: claves.publicKey, FIRMADO_DESDE: '', estadoServidor,
    URL, bajar: (...args) => bajar(...args), firma: firmaReal, nombreDe: () => 'Reino de prueba',
    bloqueados: new Set(), firmados: new Map(), console,
  });
  vm.runInContext(fuente.slice(fuente.indexOf('async function prepararFirma('), fuente.indexOf('/** Un archivo del server')), contextoFirma);
  assert.equal(await contextoFirma.prepararFirma('http://prueba/'), estadoServidor.NO_DISPONIBLE);
  bajar = async () => ({ r: { ok: false, status: 502 }, cuerpo: null });
  assert.equal(await contextoFirma.prepararFirma('http://prueba/'), estadoServidor.NO_DISPONIBLE);
  bajar = async () => ({ r: { ok: false, status: 404 }, cuerpo: null });
  assert.match(await contextoFirma.prepararFirma('http://prueba/'), /sin la firma/);
  bajar = async () => ({ r: { ok: true, status: 200 }, cuerpo: Buffer.from('{"firma":"incorrecta"}') });
  assert.match(await contextoFirma.prepararFirma('http://prueba/'), /sin la firma/);
  bajar = async () => ({ r: { ok: true, status: 200 }, cuerpo: Buffer.from('<html>no es una firma</html>') });
  assert.match(await contextoFirma.prepararFirma('http://prueba/'), /firma.*inválida/);

  // La segunda consulta del interceptor puede fallar después de aprobar el ingreso o al recargar.
  let interceptado, motivoMenu, modificados = 0;
  let errorFirma = estadoServidor.NO_DISPONIBLE;
  const contextoIntercepcion = vm.createContext({
    URL, Response, estadoServidor, firma: firmaReal,
    protocol: { handle: (_, fn) => { interceptado = fn; } },
    firmados: new Map([['http://prueba', { 'index.html': 'a'.repeat(64) }]]), bloqueados: new Set(),
    prepararFirma: async () => errorFirma,
    abrirMenu: motivo => { motivoMenu = motivo; }, clienteModificado: () => { modificados++; },
  });
  vm.runInContext(fuente.slice(fuente.indexOf('function interceptar('), fuente.indexOf('/** Los dos ingresos')), contextoIntercepcion);
  contextoIntercepcion.interceptar('http');
  assert.equal((await interceptado({ url: 'http://prueba/', method: 'GET' })).status, 502);
  assert.equal(motivoMenu, estadoServidor.NO_DISPONIBLE);
  assert.equal(modificados, 0);
  assert.equal(contextoIntercepcion.bloqueados.size, 0);
  errorFirma = 'firma inválida';
  assert.equal((await interceptado({ url: 'http://prueba/', method: 'GET' })).status, 403);
  assert.equal(modificados, 1, 'una firma inválida de verdad conserva su rechazo');
  console.log('test-estado-servidor: estados reales HTTP, caídas, nombre persistido y errores de firma separados');
}
probar().catch(e => { console.error(e); process.exitCode = 1; });
