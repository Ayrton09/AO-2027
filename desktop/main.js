// EL ENVOLTORIO DE ESCRITORIO (decidido por el autor, 01/10 y 03/10): una ventana propia que CARGA LA PÁGINA DEL SERVER,
// no los archivos del cliente, así cada actualización del server sigue llegando sola, como en el navegador. Lo que suma:
// pantalla completa sin la cruz de Chrome, la música del ingreso sin esperar el clic, ícono propio, y el MENÚ PRINCIPAL (el autor,
// 05/10: "deberia abrirte el menu principal y deberia haber una opcion que diga servidores"): Jugar, Servidores, Opciones y Salir,
// con la música del inicio sonando hasta que se entra a un server.
const { app, BrowserWindow, Menu, ipcMain, shell, webFrameMain, safeStorage } = require('electron');
const fs = require('fs');
const path = require('path');

// El puerto web es StartPort + 3 (hosting/Server.ini.ejemplo): con el StartPort de siempre, 7666, el juego está en el 7669.
const SERVIDOR_POR_DEFECTO = 'http://localhost:7669/';
const MENU = path.join(__dirname, 'menu.html');
// Cuánto se espera la respuesta de un server de la lista antes de darlo por apagado.
const ESPERA_DE_LA_CONSULTA_MS = 4000;

const BARRA_DEL_TOP = `html { color-scheme: dark; scrollbar-color: #5c3f15 #120c06; }
  ::-webkit-scrollbar { width: 10px; height: 10px; } ::-webkit-scrollbar-track { background: #120c06; }
  ::-webkit-scrollbar-thumb { background: #5c3f15; border-radius: 5px; border: 2px solid #120c06; }
  ::-webkit-scrollbar-thumb:hover { background: #9a7326; }`;

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

// LAS OPCIONES DEL JUEGO, guardadas acá y puestas en cada server al entrar (el autor, 05/10: "las opciones deberían tener todas
// las opciones que tiene el juego"). Son las claves del `localStorage` del cliente web, con sus mismos valores: el menú las muestra
// y las cambia, el `preload` las copia a la página del server antes de que arranque el juego y le devuelve acá lo que se cambie
// adentro. Así valen para todos los servers. Sólo pasan éstas: el resto del `localStorage` de cada server es suyo.
const AJUSTES_DEL_JUEGO = new Set(['ao_musicalogin', 'ao_musica', 'ao_volmusica', 'ao_mute', 'ao_volefectos', 'ao_fpscap',
  'ao_escalado', 'ao_zoom', 'ao_ssmax', 'ao_nombres', 'ao_macro', 'ao_teclas']);
const soloAjustes = (o) => Object.fromEntries(Object.entries(o ?? {})
  .filter(([k, v]) => AJUSTES_DEL_JUEGO.has(k) && typeof v === 'string' && v.length < 4000));
// La música y los efectos al 20 %, y la de adentro del juego prendida (el autor, 05/10); en el navegador la de los mapas viene
// apagada.
config.ajustes = { ao_volmusica: '0.2', ao_volefectos: '0.2', ao_musica: '1', ...soloAjustes(config.ajustes) };
delete config.opciones;

// `--servidor=<dirección>` entra directo a ese server sin pasar por el menú ni tocar el guardado: un acceso directo por server.
const servidorDelAcceso = normalizar(process.argv.find((a) => a.startsWith('--servidor='))?.slice('--servidor='.length));
let servidor = servidorDelAcceso ?? config.servidor;

function abrirMenu(error) {
  ventana.loadFile(MENU, { query: { error: error ?? '' } });
}

function crearVentana() {
  // EN PANTALLA COMPLETA, salvo que se haya apagado en Opciones o con F11 (el autor, 05/10: "la pantalla completa debería ser lo
  // default"). Es una preferencia aparte del lugar de la ventana: antes salía de cómo había quedado la ventana al cerrarse, y la
  // versión que todavía no arrancaba en pantalla completa dejaba guardado "en ventana" para siempre.
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
  if (config.pantallaCompleta !== false) ventana.setFullScreen(true);
  ventana.once('ready-to-show', () => ventana.show());

  // F11 alterna la pantalla completa, como en el navegador pero sin la cruz arriba; Ctrl+Shift+S vuelve al menú. F1 a F10 son
  // macros del juego: no se tocan.
  ventana.webContents.on('before-input-event', (e, input) => {
    if (input.type !== 'keyDown') return;
    if (input.key === 'F11') { ponerPantallaCompleta(!ventana.isFullScreen()); e.preventDefault(); }
    else if (input.control && input.shift && input.key.toLowerCase() === 's') { abrirMenu(); e.preventDefault(); }
    else if (input.control && input.shift && input.key.toLowerCase() === 'i') { ventana.webContents.toggleDevTools(); e.preventDefault(); }
    else if (input.alt && (input.key === 'ArrowLeft' || input.key === 'ArrowRight')) {
      if (input.key === 'ArrowLeft') atras();
      e.preventDefault();
    }
  });

  // EL "ATRÁS" (el botón lateral del mouse, Alt+Izquierda) NO RECORRE EL HISTORIAL: desde el juego vuelve al menú y en el menú a la
  // pantalla principal, como el Esc. Recorriendo el historial caía en lo que quedara atrás: la página de un server que no había
  // cargado, que es negra (el autor, 05/10: "al poner entrar y luego volver atrás se queda en negro todo").
  ventana.on('app-command', (e, comando) => {
    if (comando === 'browser-backward') { e.preventDefault(); atras(); }
    else if (comando === 'browser-forward') e.preventDefault();
  });
  // EL TOP DE UN SERVER VIEJO, CON LAS BARRAS OSCURAS: `estado.html` las trae desde el 05/10, y a la de un server de antes se le
  // ponen acá al cargar adentro del menú. Sólo el estilo de la barra: el resto de la página es del server.
  ventana.webContents.on('did-frame-finish-load', (_e, principal, proceso, rutina) => {
    if (principal) return;
    const marco = webFrameMain.fromId(proceso, rutina);
    if (!marco || !/\/estado\.html(\?|$)/.test(marco.url)) return;
    marco.executeJavaScript(`(() => { const s = document.createElement('style'); s.textContent = ${JSON.stringify(BARRA_DEL_TOP)};
      document.head.append(s); })()`).catch(() => {});
  });

  // Y cada vez que se carga el menú el historial arranca de cero.
  ventana.webContents.on('did-finish-load', () => {
    if (ventana.webContents.getURL().startsWith('file:')) ventana.webContents.navigationHistory.clear();
  });

  // Si el server no contesta, de vuelta al menú, en la lista y con el motivo.
  ventana.webContents.on('did-fail-load', (_e, codigo, descripcion, url, principal) => {
    if (!principal || codigo === -3 || url.startsWith('file:')) return;   // -3: la carga se reemplazó por otra
    abrirMenu(`No se pudo entrar a ${url} (${descripcion}).`);
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
    };
    guardarConfig(config);
  });

  if (servidorDelAcceso) ventana.loadURL(servidorDelAcceso); else abrirMenu();
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

// El menú es la única pantalla que habla con este proceso (ver preload.js).
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

/** El "atrás": del juego al menú; en el menú, a la pantalla principal (el mismo Esc que entiende menu.html). */
function atras() {
  if (!ventana.webContents.getURL().startsWith('file:')) return abrirMenu();
  ventana.webContents.executeJavaScript("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))").catch(() => {});
}

ipcMain.handle('entrar', async (_e, direccion) => {
  const elegido = normalizar(direccion);
  if (!elegido) return 'Esa dirección no es válida.';
  // SI NO RESPONDE, NO SE SALE DEL MENÚ: el aviso va en la lista y la música sigue. Antes se intentaba cargar, fallaba y el menú se
  // volvía a abrir entero con el error, con la música desde el principio y la página fallida en el historial.
  if (!(await consultar(elegido)).enLinea) return `${elegido.replace(/^https?:\/\//, '').replace(/\/$/, '')} no responde: ¿está prendido?`;
  servidor = config.servidor = elegido;
  if (!config.servidores.some((s) => s.direccion === elegido)) config.servidores.push({ direccion: elegido, nombre: '' });
  guardarConfig(config);
  ventana.loadURL(servidor);
  return null;
});

ipcMain.handle('leer-ajustes', () => ({ ...config.ajustes }));

ipcMain.handle('guardar-ajustes', (_e, cambios) => {
  config.ajustes = { ...config.ajustes, ...soloAjustes(cambios) };
  guardarConfig(config);
});

ipcMain.handle('esta-en-pantalla-completa', () => ventana.isFullScreen());

/** La pantalla completa, y queda como preferencia para la próxima vez. */
function ponerPantallaCompleta(si) {
  ventana.setFullScreen(!!si);
  config.pantallaCompleta = !!si;
  guardarConfig(config);
  return ventana.isFullScreen();
}

ipcMain.handle('pantalla-completa', (_e, si) => ponerPantallaCompleta(si));

// La página del server (ver preload.js): al cargar pide los ajustes —sincrónico, para tenerlos antes que el juego— y después
// devuelve lo que se haya cambiado adentro.
ipcMain.on('ajustes-para-el-juego', (e) => { e.returnValue = { ...config.ajustes }; });

ipcMain.on('ajustes-del-juego', (_e, ajustes) => {
  const nuevos = soloAjustes(ajustes);
  if (Object.entries(nuevos).every(([k, v]) => config.ajustes[k] === v)) return;
  config.ajustes = { ...config.ajustes, ...nuevos };
  guardarConfig(config);
});

ipcMain.handle('salir', () => app.quit());

ipcMain.handle('volver-al-menu', () => abrirMenu());

// RECORDARME (el autor, 05/10): la cuenta y la contraseña de cada server, la contraseña cifrada con `safeStorage` (en Windows,
// DPAPI: sólo la lee este usuario de Windows en esta PC). El server se saca de la dirección del marco que pregunta —nunca de lo
// que diga la página—, y sólo del marco principal: un server lee únicamente lo suyo. Sin cifrado disponible no se guarda la
// contraseña, sólo la cuenta.
const servidorDelPedido = (e) => {
  const marco = e.senderFrame;
  if (!marco || marco.parent || !/^https?:/.test(marco.url)) return null;
  try { return new URL(marco.url).origin; } catch { return null; }
};
config.recordados = config.recordados && typeof config.recordados === 'object' ? config.recordados : {};

ipcMain.handle('recordar-leer', (e) => {
  const origen = servidorDelPedido(e);
  const r = origen && config.recordados[origen];
  if (!r) return null;
  let clave = '';
  try { if (r.clave && safeStorage.isEncryptionAvailable()) clave = safeStorage.decryptString(Buffer.from(r.clave, 'base64')); } catch { }
  return { cuenta: r.cuenta, clave };
});

ipcMain.handle('recordar-guardar', (e, cuenta, clave) => {
  const origen = servidorDelPedido(e);
  if (!origen || typeof cuenta !== 'string' || !cuenta || cuenta.length > 64 || typeof clave !== 'string' || clave.length > 256) return;
  const cifrada = clave && safeStorage.isEncryptionAvailable() ? safeStorage.encryptString(clave).toString('base64') : '';
  config.recordados[origen] = { cuenta, clave: cifrada };
  guardarConfig(config);
});

ipcMain.handle('recordar-olvidar', (e) => {
  const origen = servidorDelPedido(e);
  if (!origen || !config.recordados[origen]) return;
  delete config.recordados[origen];
  guardarConfig(config);
});

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  crearVentana();
});

app.on('window-all-closed', () => app.quit());
