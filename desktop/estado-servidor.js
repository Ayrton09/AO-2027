// Una página de error del proxy también contesta HTTP. Sólo los datos del juego confirman disponibilidad.
const NO_DISPONIBLE = 'El servidor está apagado o no disponible. Intentá de nuevo más tarde.';
const versionValida = (v) => typeof v === 'string' && /^\d+\.\d+\.\d+$/.test(v);

function nombreValido(n) {
  if (typeof n !== 'string') return null;
  return n.replace(/[\u0000-\u001f\u007f]/g, '').replace(/\s+/g, ' ').trim().slice(0, 40) || null;
}

function esConfigDelJuego(c) {
  return !!c && versionValida(c.version) && Number.isInteger(c.wsPort) && c.wsPort > 0 && c.wsPort <= 65535;
}

async function consultarEstado(direccion, { pedir, leerJson, esperaMs }) {
  const inicio = performance.now();
  for (const ruta of ['api/publico.json', 'coverao-config.json']) {
    try {
      const r = await pedir(new URL(ruta, direccion), {
        cache: 'no-store', redirect: 'manual', signal: AbortSignal.timeout(esperaMs),
      });
      if (!r.ok) { await r.body?.cancel(); continue; }
      const datos = await leerJson(r);
      const publico = ruta === 'api/publico.json';
      const s = datos?.servidor;
      if (publico ? !(s && versionValida(s.version) && Number.isInteger(s.online) && s.online >= 0) : !esConfigDelJuego(datos))
        continue;
      return {
        enLinea: true, ping: Math.round(performance.now() - inicio),
        jugadores: publico ? s.online : null,
        version: publico ? s.version : datos.version,
        nombre: nombreValido(publico ? s.nombre : datos.nombre),
      };
    } catch { /* si la página pública está deshabilitada o no es JSON, se comprueba la configuración */ }
  }
  return { enLinea: false, ping: null, jugadores: null, version: null, nombre: null };
}

function recordarNombre(servidor, estado) {
  const nombre = estado?.enLinea ? nombreValido(estado.nombre) : null;
  // Una caída o una respuesta sin nombre no borra el último nombre confirmado.
  if (!servidor || !nombre || servidor.nombreDelServer === nombre) return false;
  servidor.nombreDelServer = nombre;
  return true;
}

module.exports = { NO_DISPONIBLE, nombreValido, esConfigDelJuego, consultarEstado, recordarNombre };
