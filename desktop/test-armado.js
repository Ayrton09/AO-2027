const assert = require('node:assert/strict');
const { comprobarDependencias } = require('./dependencias-armado');
const lock = { packages: {
  'node_modules/electron': { version: '44.11.3' },
  'node_modules/electron-builder': { version: '26.18.0' },
} };
const instaladas = { electron: '44.11.3', 'electron-builder': '26.18.0' };
assert.doesNotThrow(() => comprobarDependencias(lock, n => instaladas[n]));
assert.throws(() => comprobarDependencias(lock, n => n === 'electron' ? '44.10.2' : instaladas[n]), /npm ci/);
assert.throws(() => comprobarDependencias(lock, n => n === 'electron-builder' ? '26.15.3' : instaladas[n]), /npm ci/);
assert.throws(() => comprobarDependencias({ packages: {} }, n => instaladas[n]), /npm ci/);
console.log('test-armado: se bloquea el armado con dependencias distintas de las fijadas');
