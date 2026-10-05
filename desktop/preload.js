// Sólo la pantalla de la lista de servers (la local, file://) habla con el proceso principal; la página del server, que viene de
// afuera, no recibe nada: es la misma que en el navegador.
const { contextBridge, ipcRenderer } = require('electron');

if (location.protocol === 'file:') {
  contextBridge.exposeInMainWorld('escritorio', {
    listar: () => ipcRenderer.invoke('listar'),
    agregar: (texto, nombre) => ipcRenderer.invoke('agregar', texto, nombre),
    quitar: (direccion) => ipcRenderer.invoke('quitar', direccion),
    consultar: (direccion) => ipcRenderer.invoke('consultar', direccion),
    entrar: (direccion) => ipcRenderer.invoke('entrar', direccion),
  });
}
