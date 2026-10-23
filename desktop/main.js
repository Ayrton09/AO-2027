// EL ENVOLTORIO DE ESCRITORIO (decidido por el autor, 01/10 y 03/10): una ventana propia que CARGA LA PÁGINA DEL SERVER,
// no los archivos del cliente, así cada actualización del server sigue llegando sola, como en el navegador. Lo que suma:
// pantalla completa sin la cruz de Chrome, la música del ingreso sin esperar el clic, ícono propio, y el MENÚ PRINCIPAL (el autor,
// 05/10: "deberia abrirte el menu principal y deberia haber una opcion que diga servidores"): Jugar, Servidores, Opciones y Salir,
// con la música del inicio sonando hasta que se entra a un server.
const { app, BrowserWindow, Menu, ipcMain, shell, webFrameMain, safeStorage, session, net, protocol } = require('electron');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');
const firma = require('./firma');
const actualizacion = require('./actualizacion');

// LA APP NO ARRANCA EN MODO DEPURACIÓN. Con estos interruptores cualquiera se cuelga del juego desde afuera (el puerto de
// depuración de Chromium deja leer y cambiar la página entera); los fuses del paquete (package.json, `electronFuses`) cierran los de
// Node, y éstos los cierra la app al arrancar. Sin empaquetar (`npm start`) se permiten: es el desarrollo.
const DEPURACION = ['--remote-debugging-port', '--remote-debugging-pipe', '--remote-debugging-address', '--inspect', '--inspect-brk',
  '--inspect-port', '--js-flags'];
if (app.isPackaged && process.argv.some((a) => DEPURACION.some((d) => a === d || a.startsWith(d + '=')))) app.exit(1);

// SIN EMPAQUETAR, OTRA CARPETA DE DATOS (12/10): la de la app instalada lleva el mismo nombre, y una prueba con `npm start` le
// cambiaba la lista de servers, las opciones y la pantalla completa al juego que el autor usa. Va antes que nada que la lea.
if (!app.isPackaged) app.setPath('userData', path.join(app.getPath('appData'), 'coverao-escritorio-pruebas'));

// UNA SOLA VENTANA DEL JUEGO (auditoría del 12/10): con dos abiertas, cada una guardaba su copia del config.json encima de la
// otra y se perdían servers agregados, ajustes y cuentas recordadas. Abrir la app otra vez trae al frente la que ya está, y un
// acceso directo con `--servidor=` entra a ese server en ella (ver 'second-instance', abajo).
const unica = app.requestSingleInstanceLock();
if (!unica) app.quit();

// El puerto web es StartPort + 3 (hosting/Server.ini.ejemplo): con el StartPort de siempre, 7666, el juego está en el 7669.
const SERVIDOR_POR_DEFECTO = 'http://localhost:7669/';
const MENU = path.join(__dirname, 'menu.html');
// Cuánto se espera la respuesta de un server de la lista antes de darlo por apagado.
const ESPERA_DE_LA_CONSULTA_MS = 4000;
// Y cuánto se junta de lo que contesta: `api/publico.json` son los conectados y un top de 25 (EstadoPublico.cs), y
// `coverao-config.json` trae las novedades, que hosting/publish.ps1 saca de la última sección del CHANGELOG.md: el CHANGELOG
// entero pesa 63 KB (13/10).
const TOPE_DE_LA_CONSULTA = 1024 * 1024;
/** El JSON de una respuesta, o null si pasa del tope de la consulta. */
const jsonConTope = async (r) => {
  const cuerpo = await firma.juntar(r.body, TOPE_DE_LA_CONSULTA);
  return cuerpo ? JSON.parse(new TextDecoder().decode(cuerpo)) : null;
};

const BARRA_DEL_TOP = `html { color-scheme: dark; scrollbar-color: #5c3f15 #120c06; }
  ::-webkit-scrollbar { width: 10px; height: 10px; } ::-webkit-scrollbar-track { background: #120c06; }
  ::-webkit-scrollbar-thumb { background: #5c3f15; border-radius: 5px; border: 2px solid #120c06; }
  ::-webkit-scrollbar-thumb:hover { background: #9a7326; }`;

const archivoDeConfig = () => path.join(app.getPath('userData'), 'config.json');
const respaldoDeConfig = () => archivoDeConfig() + '.bak';

function leerConfig() {
  try { return JSON.parse(fs.readFileSync(archivoDeConfig(), 'utf8')); }
  catch (e) {
    if (e?.code === 'ENOENT') return {};
    // Si quedó cortado: la copia entera que guardarConfig escribe antes de pisarlo directo.
    try { return JSON.parse(fs.readFileSync(respaldoDeConfig(), 'utf8')); } catch { return {}; }
  }
}

function guardarConfig(config) {
  try {
    fs.mkdirSync(path.dirname(archivoDeConfig()), { recursive: true });
    // Primero a un archivo aparte y después se le cambia el nombre: un corte a mitad de camino deja entero el anterior.
    const temporal = archivoDeConfig() + '.tmp';
    const texto = JSON.stringify(config, null, 2);
    fs.writeFileSync(temporal, texto, { flush: true });
    // EL CAMBIO DE NOMBRE SE REINTENTA, y si no sale se escribe directo: un antivirus que revisa el archivo recién escrito lo
    // tiene abierto un momento y el cambio de nombre falla. La primera apertura del instalado (12/10) no guardó nada: ni los
    // ajustes ni la pantalla completa que se le tocó.
    for (let intento = 1; ; intento++) {
      try {
        fs.renameSync(temporal, archivoDeConfig());
        try { fs.rmSync(respaldoDeConfig(), { force: true }); } catch { }
        return;
      }
      catch (e) {
        if (intento >= 5) {
          console.error(`[config] no se pudo cambiar el nombre (${e?.code ?? e}): se escribe directo`);
          // Antes, una copia entera aparte: un corte en medio de la escritura directa deja ésa para leerConfig.
          fs.writeFileSync(respaldoDeConfig(), texto, { flush: true });
          fs.writeFileSync(archivoDeConfig(), texto, { flush: true });
          try { fs.rmSync(temporal, { force: true }); } catch { }
          return;
        }
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60 * intento);
      }
    }
  } catch (e) { console.error(`[config] no se pudo guardar: ${e?.message ?? e}`); }   // la próxima vez pregunta de nuevo
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
// EL SERVER EN EL QUE SE JUEGA: la ventana sólo navega adentro de su origen (ver 'will-navigate'). Vale recién cuando su cliente
// pasó la firma en esta sesión (cargarServidor, 'entrar'): el último de la lista o el de un acceso directo todavía no.
let servidor = null;

function abrirMenu(error) {
  ventana.loadFile(MENU, { query: { error: error ?? '' } });
}

// LA FIRMA DEL CLIENTE (ver firma.js): antes de entrar a un server se baja su `firma.json` y se verifica con la clave pública que
// trae la app; después, cada archivo que pide la página pasa por `interceptar` y tiene que ser el firmado. Sin empaquetar se juega
// con el cliente de desarrollo, que no está firmado: `COVERAO_VERIFICAR=1` prueba la verificación igual.
const VERIFICAR = app.isPackaged || process.env.COVERAO_VERIFICAR === '1';
const CLAVE_PUBLICA = (() => { try { return fs.readFileSync(path.join(__dirname, 'clave-publica.pem'), 'utf8'); } catch { return null; } })();
// EL CLIENTE MÁS VIEJO QUE SE ACEPTA: con una fecha ISO (por ejemplo '2026-10-12T00:00:00Z'), un cliente firmado antes no corre,
// ni uno de la versión 1 de la firma, que no tiene fecha. null: sin tope.
const FIRMADO_DESDE = null;
const firmados = new Map();   // origen del server -> { ruta: hash } de su manifiesto verificado
const bloqueados = new Set();   // origen de un server con el cliente modificado: no se le pide nada hasta volver a entrar
const CABECERA = 'X-CoverAO-Escritorio';
const sinCaptura = (url, init = {}) => {
  const encabezados = new Headers(init.headers ?? {});
  encabezados.set(CABECERA, app.getVersion());
  return net.fetch(url, { ...init, headers: encabezados, bypassCustomProtocolHandlers: true });
};
const nombreDe = (direccion) => direccion.replace(/^https?:\/\//, '').replace(/\/$/, '');

// LO QUE SE ESPERA AL VERIFICAR: si pasa ESPERA_SIN_DATOS_MS sin que llegue nada, o ESPERA_TOTAL_MS desde que se pidió, el pedido
// se corta, y un archivo más grande que TOPE_DEL_ARCHIVO no se junta en memoria. El más grande del cliente es
// `assets/music/8.mp3`, de 10,5 MB (12/10): en los dos minutos pide unos 90 KB/s.
const ESPERA_SIN_DATOS_MS = 15000;
const ESPERA_TOTAL_MS = 120000;
const TOPE_DEL_ARCHIVO = 64 * 1024 * 1024;

/**
 * Un archivo del server para verificarlo, SIN SEGUIR REDIRECCIONES y con las esperas y el tope de arriba. Devuelve la respuesta y
 * su cuerpo entero, que es null si la respuesta no es 2xx. Tira si el server redirige, deja de mandar, tarda o pasa del tope.
 */
async function bajar(url, init = {}) {
  const corte = new AbortController();
  const aTiempo = firma.conPlazos(ESPERA_SIN_DATOS_MS, ESPERA_TOTAL_MS);
  try {
    const r = await aTiempo(sinCaptura(url, { ...init, redirect: 'error', signal: corte.signal }));
    if (!r.ok || !r.body) { corte.abort(); return { r, cuerpo: r.ok ? Buffer.alloc(0) : null }; }
    const cuerpo = await firma.juntar(r.body, TOPE_DEL_ARCHIVO, aTiempo);
    if (!cuerpo) throw new Error('el archivo pasa del tope');
    return { r, cuerpo };
  } catch (e) { corte.abort(); throw e; }
}

/** Baja y verifica la firma del cliente de ese server. Devuelve el motivo para no entrar, o null. */
async function prepararFirma(direccion) {
  if (!VERIFICAR) return null;
  if (!CLAVE_PUBLICA) return 'Esta versión del juego no trae la clave para verificar el cliente: bajate la última.';
  try {
    const { cuerpo } = await bajar(new URL(firma.ARCHIVO, direccion).href, { cache: 'no-store' });
    const manifiesto = cuerpo ? JSON.parse(cuerpo.toString('utf8')) : null;
    const archivos = firma.verificarManifiesto(manifiesto, CLAVE_PUBLICA, FIRMADO_DESDE);
    if (!archivos) {
      return firma.verificarManifiesto(manifiesto, CLAVE_PUBLICA)
        ? `${nombreDe(direccion)} tiene un cliente viejo, que este juego ya no acepta: no se puede entrar.`
        : `${nombreDe(direccion)} tiene un cliente sin la firma del juego: no se puede entrar.`;
    }
    bloqueados.delete(new URL(direccion).origin);
    firmados.set(new URL(direccion).origin, archivos);
    console.log(`[firma] ${nombreDe(direccion)}: cliente firmado, ${Object.keys(archivos).length} archivos`);
    return null;
  } catch {
    return `No se pudo verificar el cliente de ${nombreDe(direccion)}.`;
  }
}

/** Un archivo del server que no es el firmado: no se juega ahí, de vuelta al menú con el motivo, y el server queda bloqueado. */
function clienteModificado(origen, rel) {
  if (bloqueados.has(origen)) return;
  firmados.delete(origen);
  bloqueados.add(origen);
  console.error(`[firma] ${origen}/${rel} no es el firmado`);
  abrirMenu(`El cliente de ${nombreDe(origen)} fue modificado (${rel}): no se puede jugar ahí.`);
}

function interceptar(esquema) {
  protocol.handle(esquema, async (pedido) => {
    const origen = new URL(pedido.url).origin;
    if (bloqueados.has(origen)) return new Response(null, { status: 403 });
    const rel = firma.rutaDe(pedido.url);
    // LA PÁGINA DEL JUEGO VUELVE A LEER LA FIRMA: si el server se actualizó con el juego abierto, el cliente recarga y trae
    // archivos nuevos, que con el manifiesto de antes parecerían cambiados. Cada carga de la página toma el del server. La de un
    // server que todavía no se verificó (un enlace del Top) también pasa por la firma: sin ella, no carga.
    if (rel === 'index.html' && pedido.method === 'GET') {
      const yaVerificado = firmados.has(origen);
      const error = await prepararFirma(origen + '/');
      if (error) { if (yaVerificado) clienteModificado(origen, rel); return new Response(null, { status: 403 }); }
    }
    const archivos = firmados.get(origen);
    // A UN SERVER VERIFICADO SÓLO SE LE PIDE CON GET (o HEAD): el juego no manda otra cosa y su server contesta 405 a todo lo
    // demás. Un formulario POST con target=_top desde el Top de otro server pasaba por acá sin firma y la ventana mostraba lo
    // que contestara (auditoría del 13/10).
    if (archivos && pedido.method !== 'GET' && pedido.method !== 'HEAD') return new Response(null, { status: 405 });
    // Lo que arma el server en el momento pasa sin firma, pero como JSON que no se ejecuta (ver firma.cabecerasSinteticas).
    if (firma.SINTETICAS.has(rel)) {
      try {
        const r = await net.fetch(pedido, { bypassCustomProtocolHandlers: true });
        return new Response(r.body, { status: r.status, headers: firma.cabecerasSinteticas(r.headers) });
      } catch { return new Response(null, { status: 502 }); }
    }
    // Lo que no es de un server verificado (la consulta de la lista, un enlace) y el HEAD pasan derecho.
    if (!archivos || pedido.method !== 'GET') {
      return net.fetch(pedido, { bypassCustomProtocolHandlers: true });
    }
    // Sin las preguntas de caché ni de pedazos de la página: para mirarlo hace falta el archivo entero (el caché de la red sigue
    // andando por debajo).
    const encabezados = new Headers(pedido.headers);
    for (const h of ['if-none-match', 'if-modified-since', 'range']) encabezados.delete(h);
    let r, cuerpo;
    try { ({ r, cuerpo } = await bajar(pedido.url, { headers: encabezados })); }
    catch { return new Response(null, { status: 502 }); }
    // LO QUE NO ES 2xx (un 404, un error del server) llega a la página sin el cuerpo ni las cabeceras del server: sólo el código.
    if (!r.ok) return new Response(null, { status: r.status });
    if (!firma.esLoFirmado(archivos, rel, cuerpo)) { clienteModificado(origen, rel); return new Response(null, { status: 403 }); }
    return new Response(cuerpo, { status: r.status, headers: firma.cabecerasDeLoFirmado(r.headers) });
  });
}

/** Entra al server: primero su firma, después la página. */
async function cargarServidor(direccion) {
  const error = await prepararFirma(direccion);
  if (error) { console.error(`[firma] ${error}`); abrirMenu(error); return error; }
  servidor = direccion;
  ventana.loadURL(direccion);
  return null;
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
      // LAS HERRAMIENTAS DE DESARROLLO, SÓLO SIN EMPAQUETAR (`npm start`): en el juego instalado no se abren. No es una barrera
      // (el mismo juego se abre en el navegador, que las tiene); es que el juego de escritorio no las ofrezca.
      devTools: !app.isPackaged,
    },
  });
  if (lugar.maximizada) ventana.maximize();
  if (config.pantallaCompleta !== false) ventana.setFullScreen(true);
  // Y otra vez al mostrarla, si no quedó: la primera apertura del instalado (la que lanza el instalador al terminar, 12/10) salió
  // en ventana aunque no había ninguna preferencia guardada.
  ventana.once('ready-to-show', () => {
    ventana.show();
    if (config.pantallaCompleta !== false && !ventana.isFullScreen()) ventana.setFullScreen(true);
  });

  // F11 alterna la pantalla completa, como en el navegador pero sin la cruz arriba; Ctrl+Shift+S vuelve al menú. F1 a F10 son
  // macros del juego: no se tocan.
  ventana.webContents.on('before-input-event', (e, input) => {
    if (input.type !== 'keyDown') return;
    if (input.key === 'F11') { ponerPantallaCompleta(!ventana.isFullScreen()); e.preventDefault(); }
    else if (input.control && input.shift && input.key.toLowerCase() === 's') { abrirMenu(); e.preventDefault(); }
    else if (!app.isPackaged && input.control && input.shift && input.key.toLowerCase() === 'i') {
      ventana.webContents.toggleDevTools(); e.preventDefault();
    }
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
  // Adentro sólo se navega al MISMO ORIGEN del server, no a uno que empiece igual, y a una página local sólo desde el menú. Con
  // la firma, además, a una PÁGINA FIRMADA de un origen verificado: el server al que se le encontró el cliente cambiado ya no lo
  // está, y una ruta que el server escribe sin firma (coverao-config.json) no es una página (ver firma.esPaginaFirmada).
  const mismoServidor = (url) => { try { return !!servidor && new URL(url).origin === new URL(servidor).origin; } catch { return false; } };
  const verificado = (url) => !VERIFICAR || firma.esPaginaFirmada(firmados.get(new URL(url).origin), url);
  ventana.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  ventana.webContents.on('will-navigate', (e, url) => {
    if (url.startsWith('file:') ? ventana.webContents.getURL().startsWith('file:') : mismoServidor(url) && verificado(url)) return;
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

  if (servidorDelAcceso) cargarServidor(servidorDelAcceso); else abrirMenu();
}

/**
 * CÓMO ESTÁ UN SERVER DE LA LISTA: si contesta, cuántos hay conectados, qué versión corre y cuánto tarda en contestar. Se pregunta
 * desde acá y no desde la página porque la página es local (file://) y el server no tiene por qué dejarla leer. Lo que se pide
 * es `api/publico.json`, lo mismo que muestra su página de estado; si el host la apagó (`PaginaPublica=0`) contesta 404 y el
 * server igual figura en línea, sin jugadores. Una redirección no se sigue: el server figura en línea, sin datos.
 */
async function consultar(direccion) {
  const inicio = performance.now();
  try {
    const r = await fetch(new URL('api/publico.json', direccion), {
      cache: 'no-store', redirect: 'manual', signal: AbortSignal.timeout(ESPERA_DE_LA_CONSULTA_MS),
    });
    const ping = Math.round(performance.now() - inicio);
    if (!r.ok) return { enLinea: true, ping, jugadores: null, version: null };
    const estado = await jsonConTope(r);
    return {
      enLinea: true, ping,
      jugadores: Number.isFinite(estado?.servidor?.online) ? estado.servidor.online : null,
      version: typeof estado?.servidor?.version === 'string' ? estado.servidor.version : null,
    };
  } catch {
    return { enLinea: false, ping: null, jugadores: null, version: null };
  }
}

// LA COMPILACIÓN DE ESTA APP (la escribe armar.js al armar el instalador): su fecha, que decide si un instalador del release es
// más nuevo, y las novedades del CHANGELOG de ese momento. Sin empaquetar no está: sin fecha y sin novedades propias.
const COMPILACION = (() => {
  try {
    const c = JSON.parse(fs.readFileSync(path.join(__dirname, 'compilacion.json'), 'utf8'));
    return { id: typeof c.id === 'string' ? c.id : '', novedades: Array.isArray(c.novedades) ? c.novedades.map(String) : [] };
  } catch { return { id: '', novedades: [] }; }
})();

/**
 * LAS NOVEDADES DE UN SERVER: las que manda al entrar, publicadas en su `coverao-config.json` (ver StaticFileServer.cs), con la
 * versión que corre. null si no contesta. Como `consultar`, se pide desde acá y no desde la página.
 */
async function novedadesDe(direccion) {
  try {
    const r = await fetch(new URL('coverao-config.json', direccion), {
      cache: 'no-store', redirect: 'manual', signal: AbortSignal.timeout(ESPERA_DE_LA_CONSULTA_MS),
    });
    if (!r.ok) return null;
    const c = await jsonConTope(r);
    if (!Array.isArray(c?.novedades)) return null;
    return {
      lineas: c.novedades.filter((l) => typeof l === 'string').slice(0, 400).map((l) => l.slice(0, 2000)),
      version: typeof c.version === 'string' ? c.version.slice(0, 40) : null,
    };
  } catch { return null; }
}

// LA ACTUALIZACIÓN (ver actualizacion.js): al abrir se baja el aviso del release; si es del autor y más nuevo que esta compilación,
// se baja el instalador y, si es el firmado, el menú ofrece instalarlo. Si no se toca, se instala al cerrar. Sin empaquetar no
// se busca: `COVERAO_ACTUALIZAR=1` lo prueba igual.
const DESCARGAS = 'https://github.com/Ayrton09/CoverAO/releases/latest/download/';
const BUSCAR_ACTUALIZACION = app.isPackaged || process.env.COVERAO_ACTUALIZAR === '1';
const TOPE_DEL_INSTALADOR = 512 * 1024 * 1024;
let lista = null;        // { ruta, id } del instalador ya bajado y verificado
let instalando = false;

async function buscarActualizacion() {
  if (!BUSCAR_ACTUALIZACION || !CLAVE_PUBLICA) return;
  try {
    const r = await net.fetch(DESCARGAS + 'actualizacion.json', { cache: 'no-store', bypassCustomProtocolHandlers: true });
    if (!r.ok) return;
    const aviso = actualizacion.verificarActualizacion(await r.json(), CLAVE_PUBLICA, COMPILACION.id);
    if (!aviso) {
      // Ya es la última: lo bajado antes (la que se acaba de instalar) sobra, que son 100 MB.
      fs.rmSync(path.join(app.getPath('userData'), 'actualizacion'), { recursive: true, force: true });
      console.log('[actualizacion] ya es la última');
      return;
    }
    const dir = path.join(app.getPath('userData'), 'actualizacion');
    fs.mkdirSync(dir, { recursive: true });
    const ruta = path.join(dir, aviso.archivo);
    if (!fs.existsSync(ruta) || fs.statSync(ruta).size !== aviso.tamano || !(await esElFirmado(ruta, aviso.sha256))) {
      if (aviso.tamano > TOPE_DEL_INSTALADOR) return;
      const ri = await net.fetch(DESCARGAS + aviso.archivo, { cache: 'no-store', bypassCustomProtocolHandlers: true });
      if (!ri.ok || !ri.body) return;
      const temporal = ruta + '.bajando';
      const hash = crypto.createHash('sha256');
      const salida = fs.createWriteStream(temporal);
      // UN ERROR DEL DISCO (lleno, un antivirus que traba el archivo) corta la bajada y borra lo bajado. Sin escuchador era una
      // excepción suelta, y la espera del 'drain' no volvía nunca.
      const roto = new Promise((_, mal) => salida.on('error', mal));
      roto.catch(() => {});
      let total = 0;
      try {
        for await (const pedazo of ri.body) {
          total += pedazo.byteLength;
          if (total > aviso.tamano) { salida.destroy(); fs.rmSync(temporal, { force: true }); return; }
          hash.update(pedazo);
          if (!salida.write(pedazo)) await Promise.race([new Promise((listo) => salida.once('drain', listo)), roto]);
        }
        await Promise.race([new Promise((listo, mal) => salida.end((e) => (e ? mal(e) : listo()))), roto]);
      } catch (e) {
        salida.destroy();
        try { fs.rmSync(temporal, { force: true }); } catch { }
        throw e;
      }
      if (total !== aviso.tamano || hash.digest('hex') !== aviso.sha256) {
        fs.rmSync(temporal, { force: true });
        console.error('[actualizacion] el instalador bajado no es el firmado');
        return;
      }
      fs.renameSync(temporal, ruta);
    }
    lista = { ruta, id: aviso.id };
    console.log(`[actualizacion] lista la del ${aviso.id}`);
    if (ventana && !ventana.isDestroyed()) ventana.webContents.send('actualizacion-lista');
  } catch (e) { console.error(`[actualizacion] ${e?.message ?? e}`); }
}

/** ¿El archivo es el del hash? */
function esElFirmado(ruta, sha256) {
  return new Promise((listo) => {
    const hash = crypto.createHash('sha256');
    fs.createReadStream(ruta).on('data', (d) => hash.update(d)).on('error', () => listo(false))
      .on('end', () => listo(hash.digest('hex') === sha256));
  });
}

/** Corre el instalador en silencio; `--updated` le dice que es una actualización y `--force-run` que abra la app al terminar. */
function instalar(reabrir) {
  if (!lista || instalando) return false;
  instalando = true;
  // Sin empaquetar (`npm start`, `COVERAO_ACTUALIZAR=1`) se baja y se verifica, pero no se instala: sería pisar el juego
  // instalado en esta PC con una prueba.
  if (!app.isPackaged) { console.log(`[actualizacion] sin empaquetar: no se instala ${lista.ruta}`); return true; }
  const instalador = spawn(lista.ruta, ['/S', '--updated', ...(reabrir ? ['--force-run'] : [])], { detached: true, stdio: 'ignore' });
  // Si no arranca (un antivirus lo sacó), queda en el registro: sin escuchador era una excepción suelta al cerrar.
  instalador.on('error', (e) => console.error(`[actualizacion] no arrancó el instalador: ${e?.message ?? e}`));
  instalador.unref();
  return true;
}

app.on('quit', () => instalar(false));

// RECIÉN ACTUALIZADA (el autor, 12/10: "si sacamos una nueva version de la app al actualizarse deberian aparecer las
// novedades"): la primera vez que abre una compilación distinta de la última que se abrió, el menú muestra solo las novedades
// que trae. La primera instalación no cuenta: no hay una anterior.
let recienActualizada = !!COMPILACION.id && !!config.compilacionVista && config.compilacionVista !== COMPILACION.id;
// Sólo la primera instancia: la segunda sale por el lock (arriba) y no guarda su copia del config encima.
if (unica && COMPILACION.id && config.compilacionVista !== COMPILACION.id) { config.compilacionVista = COMPILACION.id; guardarConfig(config); }

ipcMain.handle('recien-actualizada', () => { const si = recienActualizada; recienActualizada = false; return si; });

// El menú es la única pantalla que habla con este proceso (ver preload.js).
ipcMain.handle('novedades', async (_e, direccion) => {
  if (direccion === null) return { lineas: COMPILACION.novedades, version: null, de: null, propias: true };
  const d = normalizar(direccion);
  const delServer = d ? await novedadesDe(d) : null;
  if (delServer?.lineas.length) return { ...delServer, de: nombreDe(d) };
  return { lineas: COMPILACION.novedades, version: null, de: null, sinRespuesta: d ? nombreDe(d) : null };
});

ipcMain.handle('actualizacion', () => !!lista);

ipcMain.handle('actualizar-ahora', () => { if (instalar(true)) app.quit(); });

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
  // SIN LA FIRMA, TAMPOCO SE SALE DEL MENÚ: el motivo va en la lista, como el del server apagado.
  const sinFirma = await prepararFirma(elegido);
  if (sinFirma) return sinFirma;
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

app.on('second-instance', (_e, argv) => {
  if (!ventana) return;
  if (ventana.isMinimized()) ventana.restore();
  ventana.show();
  ventana.focus();
  const otro = normalizar(argv.find((a) => a.startsWith('--servidor='))?.slice('--servidor='.length));
  if (otro) cargarServidor(otro);
});

app.whenReady().then(() => {
  if (!unica) return;
  Menu.setApplicationMenu(null);
  // LA CABECERA DE LA APP en cada pedido, la página y el WebSocket del juego incluidos (ver `Escritorio.cs` en el server): con
  // `SoloEscritorio=1` el server sólo deja jugar a quien la manda, y un navegador no puede ponérsela.
  session.defaultSession.webRequest.onBeforeSendHeaders((detalles, listo) => {
    detalles.requestHeaders['X-CoverAO-Escritorio'] = app.getVersion();
    listo({ requestHeaders: detalles.requestHeaders });
  });
  // NINGÚN PERMISO (cámara, micrófono, portapapeles, notificaciones): ni el menú ni el juego piden ninguno, y sin estos dos
  // Electron se los da a cualquier página que los pida.
  session.defaultSession.setPermissionRequestHandler((_contenido, _permiso, responder) => responder(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  // NINGUNA DESCARGA DEL NAVEGADOR: ni el menú ni el juego bajan archivos (la actualización de la app baja el suyo por su cuenta,
  // ver buscarActualizacion), y un enlace del Top de cualquier server de la lista bajaba lo que quisiera a Descargas, sin
  // preguntar y sin tope.
  session.defaultSession.on('will-download', (e) => e.preventDefault());
  if (VERIFICAR) { interceptar('http'); interceptar('https'); }
  crearVentana();
  buscarActualizacion();
});

app.on('window-all-closed', () => app.quit());
