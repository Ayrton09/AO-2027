// Sólo la pantalla de elegir server (la local, file://) habla con el proceso principal; la página del server, que viene de
// afuera, no recibe nada: es la misma que en el navegador.
const { contextBridge, ipcRenderer } = require('electron');

if (location.protocol === 'file:') {
  contextBridge.exposeInMainWorld('escritorio', {
    elegirServidor: (texto) => ipcRenderer.invoke('elegir-servidor', texto),
  });
}
