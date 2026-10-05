// Sólo el menú (la página local, file://) habla con el proceso principal; la página del server, que viene de afuera, no recibe
// nada: es la misma que en el navegador. A ella sólo se le ponen las opciones del juego en su `localStorage` (abajo).
const { contextBridge, ipcRenderer } = require('electron');

if (location.protocol === 'file:') {
  contextBridge.exposeInMainWorld('escritorio', {
    listar: () => ipcRenderer.invoke('listar'),
    agregar: (texto, nombre) => ipcRenderer.invoke('agregar', texto, nombre),
    quitar: (direccion) => ipcRenderer.invoke('quitar', direccion),
    consultar: (direccion) => ipcRenderer.invoke('consultar', direccion),
    entrar: (direccion) => ipcRenderer.invoke('entrar', direccion),
    leerAjustes: () => ipcRenderer.invoke('leer-ajustes'),
    guardarAjustes: (cambios) => ipcRenderer.invoke('guardar-ajustes', cambios),
    estaEnPantallaCompleta: () => ipcRenderer.invoke('esta-en-pantalla-completa'),
    pantallaCompleta: (si) => ipcRenderer.invoke('pantalla-completa', si),
    salir: () => ipcRenderer.invoke('salir'),
  });
} else if (/^https?:$/.test(location.protocol)) {
  // LAS OPCIONES DEL MENÚ, EN EL JUEGO: se escriben en el `localStorage` de este server antes de que corra su página, que las lee
  // al arrancar (ver main.js, `AJUSTES_DEL_JUEGO`). Y lo que se cambie adentro vuelve al menú: cada tanto y al irse.
  try {
    for (const [k, v] of Object.entries(ipcRenderer.sendSync('ajustes-para-el-juego') ?? {})) localStorage.setItem(k, v);
    // Y la marca de que se juega desde acá: el ingreso suena con la música del mapa, porque la del inicio ya sonó en el menú.
    localStorage.setItem('ao_ingreso_con_mapa', '1');
  } catch { /* sin localStorage (modo privado): el juego arranca con lo suyo */ }
  const devolver = () => {
    try {
      const ajustes = {};
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        if (k && k.startsWith('ao_')) ajustes[k] = localStorage.getItem(k);
      }
      ipcRenderer.send('ajustes-del-juego', ajustes);
    } catch { /* nada que devolver */ }
  };
  window.addEventListener('pagehide', devolver);
  setInterval(devolver, 5000);

  // RECORDARME: la cuenta y la contraseña de ESTE server, guardadas cifradas por el juego de escritorio (ver main.js). La página
  // no dice de qué server es: el proceso principal lo saca de la dirección del marco que pregunta.
  contextBridge.exposeInMainWorld('coveraoEscritorio', {
    leer: () => ipcRenderer.invoke('recordar-leer'),
    guardar: (cuenta, clave) => ipcRenderer.invoke('recordar-guardar', String(cuenta ?? ''), String(clave ?? '')),
    olvidar: () => ipcRenderer.invoke('recordar-olvidar'),
    // Y lo que el juego le pide al juego de escritorio desde sus Opciones y su ingreso: volver al menú, salir y la pantalla completa.
    volverAlMenu: () => ipcRenderer.invoke('volver-al-menu'),
    salir: () => ipcRenderer.invoke('salir'),
    estaEnPantallaCompleta: () => ipcRenderer.invoke('esta-en-pantalla-completa'),
    pantallaCompleta: (si) => ipcRenderer.invoke('pantalla-completa', !!si),
  });
}
