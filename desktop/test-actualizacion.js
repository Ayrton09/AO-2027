// Los tests del aviso de actualización y de las novedades (actualizacion.js), sin Electron: `node desktop/test-actualizacion.js`.
const assert = require('assert');
const crypto = require('crypto');
const { PLATAFORMAS, firmarActualizacion, verificarActualizacion, novedadesDelChangelog, reemplazarAppImage,
  programarReaperturaAppImage } = require('./actualizacion');

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

// El contrato del ayudante también se comprueba en Windows, sin necesitar /bin/sh ni arrancar procesos Linux.
const { EventEmitter, once } = require('events');
const { spawn } = require('child_process');
const destinoConEspacios = path.resolve(os.tmpdir(), 'Cover AO; $(touch INYECTADO).AppImage');
let pedido, separado = false;
const ayudanteSimulado = new EventEmitter();
ayudanteSimulado.unref = () => { separado = true; };
const simularSpawn = (...args) => { pedido = args; return ayudanteSimulado; };
assert.strictEqual(programarReaperturaAppImage(destinoConEspacios, 123, simularSpawn), ayudanteSimulado);
assert.strictEqual(pedido[0], '/bin/sh');
assert.deepStrictEqual(pedido[1].slice(2), ['--', '123', destinoConEspacios], 'el PID y la ruta son datos posicionales');
assert.ok(!pedido[1][1].includes(destinoConEspacios), 'la ruta no se interpola en código');
assert.deepStrictEqual(pedido[2], { detached: true, stdio: 'ignore', cwd: path.dirname(destinoConEspacios) });
assert.ok(separado, 'el ayudante no retiene al proceso que debe salir');
assert.strictEqual(ayudanteSimulado.listenerCount('error'), 1, 'un fallo al arrancar no queda sin manejar');
const noDebeArrancar = () => { assert.fail('una entrada inválida no puede crear procesos'); };
for (const pid of [0, -1, 1.5, NaN, Infinity, '123', 0x80000000])
  assert.throws(() => programarReaperturaAppImage(destinoConEspacios, pid, noDebeArrancar), /PID inválido/);
for (const destino of ['', 'relativo.AppImage', null, `${destinoConEspacios}\0`])
  assert.throws(() => programarReaperturaAppImage(destino, 123, noDebeArrancar), /ruta absoluta válida/);

async function probarReaperturaLinux() {
  if (process.platform !== 'linux') return;
  const temporal = fs.mkdtempSync(path.join(os.tmpdir(), 'coverao-reapertura-'));
  const app = path.join(temporal, "Cover AO 'doble'; $(touch INYECTADO); `touch OTRO`.AppImage");
  let vieja, ayudante;
  const esperar = ms => new Promise(r => setTimeout(r, ms));
  const conPlazo = (promesa, ms) => {
    let timer;
    return Promise.race([promesa, new Promise((_, mal) => { timer = setTimeout(() => mal(new Error('timeout del ayudante QA')), ms); })])
      .finally(() => clearTimeout(timer));
  };
  try {
    fs.writeFileSync(app, `#!/bin/sh
printf 'una apertura\\n' >> "$0.aperturas"
printf '%s\\n' "$#" > "$0.argumentos"
pwd > "$0.cwd"
while IFS= read -r linea; do
  case "$linea" in NoNewPrivs:*) printf '%s\\n' "$linea" > "$0.nnp" ;; esac
done < /proc/self/status
`, { mode: 0o755 });
    vieja = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
    await once(vieja, 'spawn');
    const viejaTerminada = once(vieja, 'exit');
    ayudante = programarReaperturaAppImage(app, vieja.pid);
    const ayudanteTerminado = once(ayudante, 'exit');
    await esperar(250);
    assert.ok(!fs.existsSync(`${app}.aperturas`), 'no abre la nueva mientras el PID viejo sigue vivo');
    assert.strictEqual(vieja.exitCode, null);
    vieja.kill('SIGTERM');
    await conPlazo(viejaTerminada, 5000);
    assert.deepStrictEqual(await conPlazo(ayudanteTerminado, 5000), [0, null], 'el ayudante ejecuta la nueva después de salir la vieja');
    await esperar(150);
    assert.strictEqual(fs.readFileSync(`${app}.aperturas`, 'utf8'), 'una apertura\n', 'abre exactamente una instancia');
    assert.strictEqual(fs.readFileSync(`${app}.argumentos`, 'utf8').trim(), '0');
    assert.strictEqual(fs.readFileSync(`${app}.cwd`, 'utf8').trim(), fs.realpathSync(temporal), 'usa un cwd estable');
    assert.ok(!fs.existsSync(path.join(temporal, 'INYECTADO')) && !fs.existsSync(path.join(temporal, 'OTRO')), 'los metacaracteres no ejecutan código');
    const nnp = texto => texto.match(/^NoNewPrivs:\s*(\d+)/m)?.[1];
    assert.strictEqual(nnp(fs.readFileSync(`${app}.nnp`, 'utf8')), nnp(fs.readFileSync('/proc/self/status', 'utf8')),
      'reabrir conserva NoNewPrivs, sin agregar restricciones que impidan a FUSE montar el AppImage');
    console.log('test-actualizacion: reapertura Linux real OK');
  } finally {
    for (const hijo of [ayudante, vieja]) {
      if (hijo?.pid && hijo.exitCode === null && hijo.signalCode === null) {
        const termino = once(hijo, 'exit');
        hijo.kill('SIGTERM');
        await conPlazo(termino, 5000);
      }
    }
    fs.rmSync(temporal, { recursive: true, force: true });
  }
}

probarReaperturaLinux().then(() => console.log('test-actualizacion: OK')).catch(e => { console.error(e); process.exitCode = 1; });
