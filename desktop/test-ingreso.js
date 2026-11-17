// Los ingresos reales del menú y del acceso directo, con respuestas locales ficticias y sin Electron.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const actualizacion = require('./actualizacion');
const fuente = fs.readFileSync(require.resolve('./main.js'), 'utf8');
const funciones = fuente.slice(fuente.indexOf('async function prepararIngreso('), fuente.indexOf('function crearVentana()'));
const menu = fuente.slice(fuente.indexOf("delMenu('entrar',"), fuente.indexOf("delMenu('leer-ajustes',"));

async function probar(desdeMenu, version, fallaConsulta = false, errorFirma = null) {
  let firmas = 0, cargas = 0, entrar;
  const contexto = vm.createContext({
    URL, AbortSignal, actualizacion, console: { error() {} },
    app: { getVersion: () => '1.0.31' }, ESPERA_DE_LA_CONSULTA_MS: 100,
    sinCaptura: async () => { if (fallaConsulta) throw new Error('sin respuesta'); return { ok: true }; },
    jsonConTope: async () => ({ versionClienteRequerida: version }),
    prepararFirma: async () => { firmas++; return errorFirma; },
    abrirMenu() {}, ventana: { loadURL() { cargas++; } },
    normalizar: x => x, consultar: async () => ({ enLinea: true }),
    config: { servidores: [] }, guardarConfig() {},
    delMenu: (_, handler) => { entrar = handler; }, servidor: null,
  });
  vm.runInContext(funciones + '\n' + menu, contexto);
  const error = await (desdeMenu ? entrar(null, 'http://ejemplo.test/') : contexto.cargarServidor('http://ejemplo.test/'));
  const compatible = version === '1.0.31' && !fallaConsulta;
  assert.equal(firmas, compatible ? 1 : 0, 'una versión incompatible se frena antes de verificar la firma');
  assert.equal(cargas, compatible && !errorFirma ? 1 : 0, 'no se carga el juego sin versión y firma aceptadas');
  if (!compatible) assert.equal(typeof error, 'string');
  else assert.equal(error, errorFirma);
}

(async () => {
  for (const desdeMenu of [false, true]) {
    for (const version of ['1.0.31', '1.0.30', '1.0.32', null, '']) await probar(desdeMenu, version);
    await probar(desdeMenu, '1.0.31', true);
    await probar(desdeMenu, '1.0.31', false, 'firma inválida');
  }
  console.log('test-ingreso: menú y acceso directo exigen versión y firma antes de cargar');
})().catch(e => { console.error(e); process.exitCode = 1; });
