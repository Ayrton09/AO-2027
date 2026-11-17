import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { versionEnBytes } from './generar-version.mjs';
import { VERSION_CLIENTE } from './dist/src/version.js';

const version = readFileSync(new URL('../VERSION', import.meta.url), 'utf8').trim();
assert.equal(VERSION_CLIENTE.join('.'), version);
assert.deepEqual(versionEnBytes('1.2.255'), [1, 2, 255]);
for (const mala of ['1.0.256', '256.0.0', '-1.0.0', '1.0', '01.0.0', '1.0.0-beta'])
  assert.throws(() => versionEnBytes(mala));
const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
assert.match(html, /import\s*\{\s*VERSION_CLIENTE\s*\}\s*from\s*['"]\.\/dist\/src\/version\.js/);
assert.doesNotMatch(html, /const\s+VERSION_CLIENTE\s*=/);
console.log('check-version: el cliente anuncia la versión publicada y rechaza versiones que no entran en el protocolo');
