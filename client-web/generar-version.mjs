// La versión que viaja en los cinco paquetes de ingreso sale del mismo VERSION que el release.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';

export function versionEnBytes(version) {
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('VERSION debe tener formato X.Y.Z.');
  const bytes = version.split('.').map(Number);
  if (bytes.some((v) => !Number.isInteger(v) || v < 0 || v > 255) || bytes.join('.') !== version)
    throw new Error('Cada parte de VERSION debe ser un byte sin ceros a la izquierda.');
  return bytes;
}

const version = readFileSync(new URL('../VERSION', import.meta.url), 'utf8').trim();
const bytes = versionEnBytes(version);
const build = readFileSync(new URL('../server/src/CoverAO.Server/Build.cs', import.meta.url), 'utf8');
if (build.match(/public const string Version = "([^"]+)";/)?.[1] !== version)
  throw new Error('Build.Version del servidor no coincide con VERSION.');
for (const archivo of ['./package.json', '../desktop/package.json']) {
  if (JSON.parse(readFileSync(new URL(archivo, import.meta.url), 'utf8')).version !== version)
    throw new Error(`${archivo} no coincide con VERSION.`);
}
mkdirSync(new URL('./dist/src/', import.meta.url), { recursive: true });
writeFileSync(new URL('./dist/src/version.js', import.meta.url),
  `// Generado desde /VERSION al compilar.\nexport const VERSION_CLIENTE = Object.freeze(${JSON.stringify(bytes)});\n`);
