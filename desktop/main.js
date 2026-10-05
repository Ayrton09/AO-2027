// EL ENVOLTORIO DE ESCRITORIO (decidido por el autor, 01/10 y 03/10): una ventana propia que CARGA LA PÁGINA DEL SERVER,
// no los archivos del cliente, así cada actualización del server sigue llegando sola, como en el navegador. Lo que suma:
// pantalla completa sin la cruz de Chrome, la música del ingreso sin esperar el clic, ícono propio y la lista de servers.
const { app, BrowserWindow, Menu, ipcMain, shell } = require('electron');
const fs = require('fs');
const path = require('path');

// El puerto web es StartPort + 3 (hosting/Server.ini.ejemplo): con el StartPort de siempre, 7666, el juego está en el 7669.
const SERVIDOR_POR_DEFECTO = 'http://localhost:7669/';
const ELEGIR = path.join(__dirname, 'elegir.html');
// Cuánto se espera la respuesta de un server de la lista antes de darlo por apagado.
const ESPERA_DE_LA_CONSULTA_MS = 4000;

const archivoDeConfig = () => path.join(app.getPath('userData'), 'config.json');

function leerConfig() {
  try { return JSON.parse(fs.readFileSync(archivoDeConfig(), 'utf8')); } catch { return {}; }
}

function guardarConfig(config) {
  try {
    fs.mkdirSync(path.dirname(archivoDeConfig()), { recursive: true });
    fs.writeFileSync(archivoDeConfig(), JSON.stringify(config, null, 2));
  } catch { /* sin config guardada, la próxima vez pregunta de nuevo */ }
}

/** Lo que escribió el jugador, como dirección: sin protocolo se toma http; cualquier otro protocolo no vale. */
function normalizar(texto) {
  let t = String(texto ?? '').trim();
  if (!t) return null;
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(t)) t = 'http://' + t;
  try {
    const u = new URL(t);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return null;
    return u.origin + (u.pathname.endsWith('/') ? u.pathname : u.pathname + '/');
  } catch { return null; }
}

let config = leerConfig();
// LA LISTA DE SERVERS (el autor, 03/10: "si alguien me pasa una IP yo pueda entrar y fue"). Se agregan por dirección y quedan
// guardados; no hay un central que los liste. El server elegido antes de la lista entra como el primero.
config.servidores = Array.isArray(config.servidores) ? config.servidores.filter((s) => normalizar(s?.direccion)) : [];
if (config.servidor && !config.servidores.some((s) => s.direccion === config.servidor))
  config.servidores.unshift({ direccion: config.servidor, nombre: '' });
let ventana = null;

// `--servidor=<dirección>` entra directo a ese server sin tocar el guardado: un acceso directo por server.
const servidorDelAcceso = normalizar(process.argv.find((a) => a.startsWith('--servidor='))?.slice('--servidor='.length));
let servidor = servidorDelAcceso ?? config.servidor;

function abrirElegir(error) {
  ventana.loadFile(ELEGIR, { query: { error: error ?? '' } });
}

function abrirServidor() {
  if (!servidor) return abrirElegir();
  ventana.loadURL(servidor);
}

function crearVentana() {
  const lugar = config.ventana ?? {};
  ventana = new BrowserWindow({
    width: lugar.width ?? 1280, height: lugar.height ?? 800, x: lugar.x, y: lugar.y,
    minWidth: 800, minHeight: 600,
    title: 'CoverAO', icon: path.join(__dirname, 'build', 'icon.png'),
    backgroundColor: '#000000', autoHideMenuBar: true, show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true, sandbox: true, spellcheck: false,
      // La música del ingreso suena sin esperar un clic: en el navegador, sin gesto del usuario, el `play()` se rechaza.
      autoplayPolicy: 'no-user-gesture-required',
      // Minimizado sigue andando igual que en primer plano: el navegador frena los timers de una pestaña escondida.
      backgroundThrottling: false,
    },
  });
  if (lugar.maximizada) ventana.maximize();
  if (lugar.pantallaCompleta) ventana.setFullScreen(true);
  ventana.once('ready-to-show', () => ventana.show());

  // F11 alterna la pantalla completa, como en el navegador pero sin la cruz arriba; Ctrl+Shift+S vuelve a la lista de
  // servers. F1 a F10 son macros del juego: no se tocan.
  ventana.webContents.on('before-input-event', (e, input) => {
    if (input.type !== 'keyDown') return;
    if (input.key === 'F11') { ventana.setFullScreen(!ventana.isFullScreen()); e.preventDefault(); }
    else if (input.control && input.shift && input.key.toLowerCase() === 's') { abrirElegir(); e.preventDefault(); }
    else if (input.control && input.shift && input.key.toLowerCase() === 'i') { ventana.webContents.toggleDevTools(); e.preventDefault(); }
  });

  // Si el server no contesta, de vuelta a la lista, con el motivo.
  ventana.webContents.on('did-fail-load', (_e, codigo, descripcion, url, principal) => {
    if (!principal || codigo === -3 || url.startsWith('file:')) return;   // -3: la carga se reemplazó por otra
    abrirElegir(`No se pudo entrar a ${url} (${descripcion}).`);
  });

  // Los enlaces a otros sitios (el código fuente, la página de un clan) se abren en el navegador, no en la ventana del juego.
  const mismoServidor = (url) => servidor && url.startsWith(new URL(servidor).origin);
  ventana.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  ventana.webContents.on('will-navigate', (e, url) => {
    if (url.startsWith('file:') || mismoServidor(url)) return;
    e.preventDefault();
    if (/^https?:/i.test(url)) shell.openExternal(url);
  });

  ventana.on('close', () => {
    config.ventana = {
      ...ventana.getNormalBounds(),
      maximizada: ventana.isMaximized(),
      pantallaCompleta: ventana.isFullScreen(),
    };
    guardarConfig(config);
  });

  abrirServidor();
}

/**
 * CÓMO ESTÁ UN SERVER DE LA LISTA: si contesta, cuántos hay conectados, qué versión corre y cuánto tarda en contestar. Se pregunta
 * desde acá y no desde la página porque la página es local (file://) y el server no tiene por qué dejarla leer. Lo que se pide
 * es `api/publico.json`, lo mismo que muestra su página de estado; si el host la apagó (`PaginaPublica=0`) contesta 404 y el
 * server igual figura en línea, sin jugadores.
 */
async function consultar(direccion) {
  const inicio = performance.now();
  try {
    const r = await fetch(new URL('api/publico.json', direccion), {
      cache: 'no-store', signal: AbortSignal.timeout(ESPERA_DE_LA_CONSULTA_MS),
    });
    const ping = Math.round(performance.now() - inicio);
    if (!r.ok) return { enLinea: true, ping, jugadores: null, version: null };
    const estado = await r.json();
    return {
      enLinea: true, ping,
      jugadores: Number.isFinite(estado?.servidor?.online) ? estado.servidor.online : null,
      version: typeof estado?.servidor?.version === 'string' ? estado.servidor.version : null,
    };
  } catch {
    return { enLinea: false, ping: null, jugadores: null, version: null };
  }
}

// La pantalla de la lista es la única que habla con este proceso (ver preload.js).
ipcMain.handle('listar', () => ({ servidores: config.servidores, ultimo: config.servidor ?? null, porDefecto: SERVIDOR_POR_DEFECTO }));

ipcMain.handle('agregar', (_e, texto, nombre) => {
  const direccion = normalizar(texto);
  if (!direccion) return { error: 'Esa dirección no es válida. Probá con algo como localhost:7669 o https://mi-server.com.' };
  const ya = config.servidores.find((s) => s.direccion === direccion);
  if (ya) { if (nombre?.trim()) ya.nombre = nombre.trim(); }
  else config.servidores.push({ direccion, nombre: String(nombre ?? '').trim() });
  guardarConfig(config);
  return { direccion };
});

ipcMain.handle('quitar', (_e, direccion) => {
  config.servidores = config.servidores.filter((s) => s.direccion !== direccion);
  if (config.servidor === direccion) delete config.servidor;
  guardarConfig(config);
});

ipcMain.handle('consultar', (_e, direccion) => (normalizar(direccion) ? consultar(normalizar(direccion)) : null));

ipcMain.handle('entrar', (_e, direccion) => {
  const elegido = normalizar(direccion);
  if (!elegido) return 'Esa dirección no es válida.';
  servidor = config.servidor = elegido;
  if (!config.servidores.some((s) => s.direccion === elegido)) config.servidores.push({ direccion: elegido, nombre: '' });
  guardarConfig(config);
  ventana.loadURL(servidor);
  return null;
});

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  crearVentana();
});

app.on('window-all-closed', () => app.quit());
