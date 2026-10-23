// LA APP SE ACTUALIZA SOLA, SIN CAMBIAR DE VERSIÓN (el autor, 12/10: "siempre estaremos en la 1.0.0" hasta que el juego sea
// público). Las herramientas de siempre actualizan sólo si el número sube; acá cada instalador lleva la FECHA en que se armó
// (`compilacion.json`, la escribe armar.js) y el release publica `actualizacion.json`: esa fecha, el nombre del instalador, su
// SHA-256 y su tamaño, firmados con la clave del autor (la misma que firma el cliente, ver firma.js). La app baja el aviso, mira la
// firma y que la fecha sea MÁS NUEVA que la suya (así no se le puede volver a servir un instalador viejo firmado), baja el
// instalador y lo instala si su hash es el firmado. Quien entre a la cuenta de GitHub no puede colar una app: le falta la clave.
//
// Sin Electron adentro, para probarlo con `node test-actualizacion.js`. También lo usa armar.js (las novedades y la firma).
const crypto = require('crypto');

const FECHA = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/;
const NOMBRE = /^[A-Za-z0-9._-]{1,100}$/;

// CADA PLATAFORMA, SU AVISO Y SU ARCHIVO: Windows se actualiza con el instalador (`actualizacion.json`, un .exe) y Linux
// reemplazando el AppImage (`actualizacion-linux.json`, un .AppImage). La extensión se exige al verificar: el aviso de una no le
// sirve a la otra, y a Linux no se le puede hacer bajar un .exe ni a Windows algo que no sea su instalador.
const PLATAFORMAS = {
  win32: { aviso: 'actualizacion.json', extension: '.exe' },
  linux: { aviso: 'actualizacion-linux.json', extension: '.AppImage' },
};

const textoAFirmar = (a) => `actualizacion 1\nid ${a.id}\narchivo ${a.archivo}\nsha256 ${a.sha256}\ntamano ${a.tamano}\n`;

/** El aviso firmado de un instalador. */
function firmarActualizacion({ id, archivo, sha256, tamano }, clavePrivada) {
  const a = { version: 1, id, archivo, sha256, tamano };
  return { ...a, firma: crypto.sign(null, Buffer.from(textoAFirmar(a)), clavePrivada).toString('base64') };
}

/**
 * El aviso si está bien armado, la firma es de la clave pública y es más nuevo que `idLocal` (la fecha de la app que corre;
 * vacía = cualquiera es más nuevo). Si no, null. `extension` es la del archivo de esta plataforma (ver PLATAFORMAS).
 */
function verificarActualizacion(a, clavePublica, idLocal, extension = '.exe') {
  if (!a || a.version !== 1 || typeof a.firma !== 'string') return null;
  if (typeof a.id !== 'string' || !FECHA.test(a.id)) return null;
  if (typeof a.archivo !== 'string' || !NOMBRE.test(a.archivo) || !a.archivo.endsWith(extension)
      || a.archivo.length === extension.length) return null;
  if (typeof a.sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(a.sha256)) return null;
  if (!Number.isSafeInteger(a.tamano) || a.tamano <= 0) return null;
  if (idLocal && !(Date.parse(a.id) > Date.parse(idLocal))) return null;
  try {
    return crypto.verify(null, Buffer.from(textoAFirmar(a)), clavePublica, Buffer.from(a.firma, 'base64')) ? a : null;
  } catch { return null; }
}

// LAS NOVEDADES QUE TRAE LA APP: las de la sección más nueva del CHANGELOG, igual que las arma hosting/publish.ps1 para el
// data/updates.txt del server (ver ahí por qué se limpia el Markdown y por qué los títulos van como "== Título ==").
const limpiarMarkdown = (t) => t
  .replace(/\*\*([^*]+)\*\*/g, '$1')
  .replace(/(?<!\*)\*([^*]+)\*/g, '$1')
  .replace(/`([^`]+)`/g, '$1')
  .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
  .trim();

/** Las líneas de novedades de la sección más nueva del texto de un CHANGELOG.md. */
function novedadesDelChangelog(texto) {
  const lineas = [];
  let enSeccion = false;
  for (const l of String(texto).split(/\r?\n/)) {
    if (/^##\s/.test(l)) {
      if (enSeccion) break;
      if (/^##\s+\d/.test(l)) enSeccion = true;
      continue;
    }
    if (!enSeccion) continue;
    let m;
    if ((m = /^###\s+(.*)$/.exec(l))) { lineas.push(`== ${limpiarMarkdown(m[1])} ==`); continue; }
    if ((m = /^\s*-\s+(.*)$/.exec(l))) { lineas.push(limpiarMarkdown(m[1])); continue; }
    if (lineas.length > 0 && /^\s+\S/.test(l) && !/^\s*#/.test(l)) lineas[lineas.length - 1] += ' ' + limpiarMarkdown(l);
  }
  return lineas;
}

/**
 * INSTALAR EN LINUX: el AppImage es un solo archivo, y actualizarse es poner el nuevo en su lugar. Se copia al lado del que
 * corre (misma carpeta, mismo disco), se le da permiso de ejecución y se renombra encima: el rename es atómico, así que un
 * corte en el medio deja el viejo entero o el nuevo entero, nunca uno a medias. El que está corriendo no se entera: Linux
 * mantiene abierto el archivo viejo hasta que la app se cierra. `fsx` es `fs` (o uno de prueba). Si algo falla, tira, y el
 * temporal no queda.
 */
function reemplazarAppImage(nueva, destino, fsx = require('fs')) {
  const temporal = `${destino}.nueva`;
  try {
    fsx.copyFileSync(nueva, temporal);
    fsx.chmodSync(temporal, 0o755);
    fsx.renameSync(temporal, destino);
    return true;
  } catch (e) {
    try { fsx.rmSync(temporal, { force: true }); } catch { }
    throw e;
  }
}

module.exports = { PLATAFORMAS, firmarActualizacion, verificarActualizacion, novedadesDelChangelog, reemplazarAppImage };
