// El armador toma Electron de node_modules. Un directorio viejo no debe producir otra versión que la fijada.
function comprobarDependencias(lock, instalada) {
  for (const nombre of ['electron', 'electron-builder']) {
    const esperada = lock?.packages?.[`node_modules/${nombre}`]?.version;
    if (!esperada || instalada(nombre) !== esperada)
      throw new Error(`Las dependencias de escritorio no coinciden con package-lock.json (${nombre}). Ejecutá npm ci en desktop antes de armar.`);
  }
}
module.exports = { comprobarDependencias };
