// LA FIRMA DEL CLIENTE (decisión del autor, 12/10). Cada server sirve el cliente, como siempre, pero firmado por el autor: al
// empaquetar, `firmar.js` anota el SHA-256 de cada archivo del cliente en `firma.json` y lo firma con la clave privada del autor
// (Ed25519, guardada fuera del repo). La app trae la clave pública y, antes de dejar correr un archivo del server, mira que su
// contenido sea el firmado. Un cliente cambiado (por el que hostea o en el camino) no corre: así las actualizaciones siguen
// llegando solas por cada server y nadie puede servir un cliente con trampas a los que juegan con la app.
//
// Lo usan el proceso principal de la app (main.js) y la herramienta de firma (firmar.js). Sin Electron adentro: se prueba con
// `node test-firma.js`.
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const ARCHIVO = 'firma.json';

// Lo que el server arma en el momento (ver StaticFileServer.cs): no es un archivo del cliente, así que ni se firma ni se mira.
const SINTETICAS = new Set(['coverao-config.json', 'api/publico.json', ARCHIVO]);

const hashDe = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');

// Lo que se firma: cada ruta con su hash, ordenadas, una por renglón. Igual de los dos lados, sin depender de cómo se
// serialice el JSON. DESDE LA VERSIÓN 2 va adelante la fecha de la firma, así la app puede no aceptar un cliente firmado antes
// de un mínimo; la versión 1 no la tiene.
const textoAFirmar = (archivos, fecha) => (fecha === undefined ? '' : `version 2\nfecha ${fecha}\n`)
  + Object.keys(archivos).sort().map((r) => `${r} ${archivos[r]}\n`).join('');
const FECHA = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$/;

/** Los archivos de la carpeta del cliente, con su ruta relativa en `/`, sin la firma misma ni las notas `.md`. */
function archivosDe(dir) {
  const salida = [];
  const recorrer = (sub) => {
    for (const e of fs.readdirSync(path.join(dir, sub), { withFileTypes: true })) {
      const rel = sub ? `${sub}/${e.name}` : e.name;
      if (e.isDirectory()) recorrer(rel);
      else if (rel !== ARCHIVO && !rel.toLowerCase().endsWith('.md')) salida.push(rel);
    }
  };
  recorrer('');
  return salida;
}

/** El manifiesto firmado de la carpeta `dir` con la clave privada (PEM), con la fecha de hoy (o `cuando`). */
function firmarCarpeta(dir, clavePrivada, cuando = new Date()) {
  const archivos = {};
  for (const rel of archivosDe(dir)) archivos[rel] = hashDe(fs.readFileSync(path.join(dir, rel)));
  const fecha = cuando.toISOString();
  const firma = crypto.sign(null, Buffer.from(textoAFirmar(archivos, fecha)), clavePrivada).toString('base64');
  return { version: 2, fecha, archivos, firma };
}

/**
 * Los archivos del manifiesto si la firma es de la clave pública (PEM); null si no lo es o el manifiesto está mal armado. Con
 * `firmadoDesde` (una fecha ISO), tampoco pasa uno firmado antes ni uno de la versión 1, que no tiene fecha.
 */
function verificarManifiesto(manifiesto, clavePublica, firmadoDesde = null) {
  if (!manifiesto || (manifiesto.version !== 1 && manifiesto.version !== 2) || typeof manifiesto.firma !== 'string') return null;
  const fecha = manifiesto.version === 2 ? manifiesto.fecha : undefined;
  if (manifiesto.version === 2 && (typeof fecha !== 'string' || !FECHA.test(fecha))) return null;
  if (firmadoDesde != null && !(fecha !== undefined && Date.parse(fecha) >= Date.parse(firmadoDesde))) return null;
  const archivos = manifiesto.archivos;
  if (!archivos || typeof archivos !== 'object' || Array.isArray(archivos)) return null;
  if (!Object.values(archivos).every((h) => typeof h === 'string' && /^[0-9a-f]{64}$/.test(h))) return null;
  try {
    const ok = crypto.verify(null, Buffer.from(textoAFirmar(archivos, fecha)), clavePublica, Buffer.from(manifiesto.firma, 'base64'));
    return ok ? archivos : null;
  } catch { return null; }
}

/** La ruta de un pedido dentro del cliente: sin el `?v=` del cache-bust, y la raíz o una carpeta es su `index.html`. */
function rutaDe(url) {
  let rel = decodeURIComponent(new URL(url).pathname).replace(/^\/+/, '');
  if (rel === '' || rel.endsWith('/')) rel += 'index.html';
  return rel;
}

/** ¿Lo que llegó para esa ruta es lo firmado? Una ruta que no está en el manifiesto no pasa: no se agregan archivos. */
const esLoFirmado = (archivos, rel, bytes) => Object.prototype.hasOwnProperty.call(archivos, rel) && archivos[rel] === hashDe(bytes);

// LA CLAVE PRIVADA CON CONTRASEÑA (auditoría del 12/10): en el disco va cifrada, así una copia del archivo (un backup, la carpeta
// sincronizada, otro usuario de la PC) no alcanza para firmar un cliente con trampas.
/** ¿El PEM de la clave privada está cifrado? */
const estaCifrada = (pem) => /-----BEGIN ENCRYPTED PRIVATE KEY-----/.test(pem);

/** La clave privada del PEM; si está cifrada, con la contraseña. Tira si falta o no es la contraseña. */
function cargarPrivada(pem, contrasena) {
  if (!estaCifrada(pem)) return crypto.createPrivateKey(pem);
  if (!contrasena) throw new Error('la clave está cifrada y falta la contraseña');
  try { return crypto.createPrivateKey({ key: pem, format: 'pem', passphrase: contrasena }); }
  catch { throw new Error('esa no es la contraseña de la clave'); }
}

/** El PEM de la clave privada cifrado con la contraseña. */
const cifrarPrivada = (clave, contrasena) => clave.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase: contrasena });

/** La clave pública (PEM) de una privada: para comparar dos claves sin mirar la privada. */
const publicaDe = (clave) => crypto.createPublicKey(clave).export({ type: 'spki', format: 'pem' });

// LO QUE LA APP BAJA DE UN SERVER, CON PLAZOS Y TOPE (auditorías del 12/10 y del 13/10): la firma, cada archivo del cliente y las
// consultas de la lista. Un server no puede colgar la app mandando de a un byte, ni llenarle la memoria con una respuesta enorme.

/**
 * Un reloj para una bajada de a pedazos: cada espera se corta a los `sinDatosMs`, y todas juntas a los `totalMs` desde que se
 * armó el reloj. Devuelve la función que envuelve cada espera (una promesa) y tira si vence.
 */
function conPlazos(sinDatosMs, totalMs) {
  const limite = Date.now() + totalMs;
  return (promesa) => {
    const queda = limite - Date.now();
    let reloj;
    const vencida = new Promise((_, mal) => {
      reloj = setTimeout(() => mal(new Error(queda < sinDatosMs ? 'el archivo tarda demasiado' : 'el server dejó de mandar')),
        Math.max(0, Math.min(sinDatosMs, queda)));
    });
    return Promise.race([promesa, vencida]).finally(() => clearTimeout(reloj));
  };
}

/** El cuerpo entero de una respuesta (su `body`), o null si pasa de `tope` bytes: ahí deja de leerlo. `aTiempo`: ver conPlazos. */
async function juntar(cuerpo, tope, aTiempo = (promesa) => promesa) {
  if (!cuerpo) return Buffer.alloc(0);
  const lector = cuerpo.getReader();
  const partes = [];
  for (let total = 0; ;) {
    const { done, value } = await aTiempo(lector.read());
    if (done) return Buffer.concat(partes);
    if ((total += value.byteLength) > tope) { lector.cancel().catch(() => {}); return null; }
    partes.push(Buffer.from(value));
  }
}

module.exports = { ARCHIVO, SINTETICAS, hashDe, firmarCarpeta, verificarManifiesto, rutaDe, esLoFirmado, estaCifrada, cargarPrivada,
  cifrarPrivada, publicaDe, conPlazos, juntar };
