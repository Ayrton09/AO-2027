using System.Runtime.InteropServices;   // PosixSignalRegistration — apagado ordenado ante SIGTERM
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

// CoverAO — servidor moderno (Fase 1, slice vertical).
// Habla el protocolo AO clasico byte-a-byte: es lo que permite compararlo contra el servidor VB6 original.
// (El CLIENTE VB6 igual no puede entrar: manda la clave ofuscada con SEncriptar y aca no se deshace. Medido;
// esta contado en docs/HOSTING.md.)
// Arquitectura: World (reemplaza UserList global) + GameLoop 40ms + EventBus + dispatch por sesion.

// Modo importador: `CoverAO.Server migrate <dataDir> [dbPath]` migra .chr/.cnt/Misc legacy → SQLite y sale.
if (args.Length > 0 && string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase))
{
    var mSrc = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "data");
    var mDst = args.Length > 2 ? args[2] : Path.Combine(mSrc, "coverao.db");
    return Migrator.Run(mSrc, mDst);
}

// Panel de admin: capturar la consola en un ring buffer (sin dejar de escribir en la consola real),
// para poder mostrarla en el panel web. Se instala ANTES de cualquier salida así queda todo.
var logRing = new CoverAO.Server.Net.LogRing(Console.Out);
Console.SetOut(logRing);
Console.SetError(logRing.EspejoDeError(Console.Error));   // los errores también van al panel y al archivo

// Server.ini: la perilla del que HOSTEA (puerto, límites, rates, y sobre todo QUIÉN ES GM). Se busca al lado
// del ejecutable — así viaja con la distribución portable— y es opcional: sin él, defaults y a andar.
// El puerto del ini lo pisa el argumento de línea de comandos, que es lo más específico.
string iniPath = Path.Combine(AppContext.BaseDirectory, "Server.ini");
var serverIni = CoverAO.Server.Data.ServerIni.Cargar(iniPath);

int port = serverIni.StartPort;
if (args.Length > 0 && int.TryParse(args[0], out var p)) port = p;

// LOS CUATRO PUERTOS, resueltos UNA sola vez y acá arriba.
//
// Salen todos de StartPort: +1 WebSocket, +2 panel de admin, +3 cliente web. El argumento numérico de la
// línea de comandos corre los cuatro; `--admin-port` y `--web-port` pisan uno solo.
//
// Se resuelven ANTES del banner para poder imprimirlos, que es lo que necesita quien hostea: con varios
// servidores en la misma máquina, saber cuál usa qué no puede depender de abrir cada Server.ini y sumar de
// a uno. Y se calculan UNA vez y se reusan más abajo a propósito — un mapa de puertos impreso que no fuera
// el mismo que se usa para escuchar sería peor que no imprimir nada.
bool sinAdmin = args.Any(a => string.Equals(a, "--no-admin", StringComparison.OrdinalIgnoreCase));
bool sinWeb = args.Any(a => string.Equals(a, "--no-web", StringComparison.OrdinalIgnoreCase));
int adminPort = port + 2;
int apIdx = Array.FindIndex(args, a => string.Equals(a, "--admin-port", StringComparison.OrdinalIgnoreCase));
if (apIdx >= 0 && apIdx + 1 < args.Length && int.TryParse(args[apIdx + 1], out var ap)) adminPort = ap;
int webPort = port + 3;
int wpIdx = Array.FindIndex(args, a => string.Equals(a, "--web-port", StringComparison.OrdinalIgnoreCase));
if (wpIdx >= 0 && wpIdx + 1 < args.Length && int.TryParse(args[wpIdx + 1], out var wp)) webPort = wp;
// El cliente web sólo se sirve si la instalación lo trae (lo empaqueta publish.ps1) o si se lo apunta con
// `--web-root`. Se resuelve acá y no más abajo para que el mapa no prometa un puerto que no va a abrir.
int wrIdx = Array.FindIndex(args, a => string.Equals(a, "--web-root", StringComparison.OrdinalIgnoreCase));
string? webRoot = (wrIdx >= 0 && wrIdx + 1 < args.Length) ? args[wrIdx + 1] : null;
if (webRoot is null)
{
    var empaquetado = Path.Combine(AppContext.BaseDirectory, "webclient");
    if (Directory.Exists(empaquetado)) webRoot = empaquetado;
}
bool habraWeb = !sinWeb && webRoot is not null && Directory.Exists(webRoot);

// FIXTURE OPT-IN: el mapa 15 se puebla con el fixture de test (NPCs de servicio, criaturas, arena, zonas,
// paredes) + la cuenta demo SÓLO con `--fixture` (o COVERAO_FIXTURE=1). Sin el flag, el server es el MUNDO
// REAL: el mapa 15 trae su fauna del .csm y nada de test, y no hay cuenta demo — como un server de verdad.
// Un jugador que llega caminando desde el mapa 13 (339 tile-exits llevan al 15) ya no se topa con Rugura,
// Merlin y los NPCs de test parados en medio del mundo. Los flows de paridad y el ws-smoke corren con el flag.
// Ver Game/TestFixture.cs.
bool fixtureMode = args.Any(a => string.Equals(a, "--fixture", StringComparison.OrdinalIgnoreCase))
    || string.Equals(Environment.GetEnvironmentVariable("COVERAO_FIXTURE"), "1", StringComparison.Ordinal);

// TLS opt-in (SEC-4): `--tls <cert.pfx> [password]` envuelve el transporte TCP en SslStream. Default plaintext
// (el protocolo AO clásico no tiene TLS; los flows/oráculo y el cliente VB6 corren en claro). Para el cliente
// WEB, lo estándar es terminar TLS en un reverse-proxy HTTPS y que el cliente use wss:// (auto en origen https).
System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCert = null;
int tlsIdx = Array.FindIndex(args, a => string.Equals(a, "--tls", StringComparison.OrdinalIgnoreCase));
if (tlsIdx >= 0 && tlsIdx + 1 < args.Length)
{
    string pfx = args[tlsIdx + 1];
    string? pw = (tlsIdx + 2 < args.Length && !args[tlsIdx + 2].StartsWith("--")) ? args[tlsIdx + 2] : null;
    try
    {
        tlsCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfx, pw);
        Console.WriteLine($"TLS habilitado en el transporte TCP (cert: {pfx}).");
    }
    catch (Exception ex) { Console.Error.WriteLine($"TLS: no se pudo cargar el cert '{pfx}': {ex.Message}. Sigo en plaintext."); }
}

// EL LOG A DISCO SE ABRE ACÁ, ANTES DEL BANNER, y no después de cargar el mundo como estaba.
//
// El banner es lo único que dice QUÉ SERVIDOR SOS: dónde está instalado, la configuración efectiva y la
// lista de staff. Abriendo el archivo más abajo, nada de eso llegaba al log — que arrancaba directo en
// "Spawn: 5404 NPCs" y salía idéntico en todas las instalaciones. Quien hostea varios servidores (cada uno
// en su carpeta, con sus mapas y su config) mira el log justo cuando algo anda mal, y un log que no dice de
// cuál es no ayuda. Lo único que sigue quedando afuera a propósito es el token del panel de admin.
//
// Un archivo por día, con purga a los N días. El ring buffer del panel vive en memoria y se pierde al cerrar
// la ventana: sin esto, el que hostea no puede averiguar nada de lo que pasó ayer.
string logsDir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
if (serverIni.LogDias > 0)
{
    logRing.AbrirArchivo(logsDir, serverIni.LogDias);
    Console.WriteLine($"Log en {logsDir} (uno por día, se conservan {serverIni.LogDias}).");
}

Console.WriteLine($"== CoverAO Server (moderno / .NET 10) — Fase 1 {(fixtureMode ? "[FIXTURE de test]" : "[mundo real]")} ==");
Console.WriteLine($"Instalado en: {AppContext.BaseDirectory}");
// Separadores ASCII a propósito. El resto del banner tiene acentos, pero ESTA es la línea que un host lee
// con `type` en una consola de Windows —codepage 850, no UTF-8— y ahí un `·` sale como un rombo negro.
// El archivo está bien (los bytes son \xc2\xb7); lo que se rompe es la lectura, que es lo único que importa.
Console.WriteLine($"Puertos: {port} juego (protocolo clasico)  |  {port + 1} WebSocket (cliente web)  |  " +
                  $"{(sinAdmin ? "sin panel de admin" : adminPort + " panel de admin (solo local)")}  |  " +
                  $"{(habraWeb ? webPort + " cliente web" : "sin cliente web (esta instalacion no lo trae)")}");
// SIN MENTIR SOBRE DE DONDE SALIO EL NUMERO. Si el puerto vino por linea de comandos, el Server.ini dice
// OTRA COSA, y el host que abre el archivo buscando ese numero no lo encuentra.
Console.WriteLine(port == serverIni.StartPort
    ? $"         Se configuran con StartPort={port} en el Server.ini: los otros tres salen de ahi (+1, +2, +3)."
    : $"         El {port} vino por linea de comandos y pisa al StartPort={serverIni.StartPort} del Server.ini; " +
      "los otros tres salen de ahi (+1, +2, +3).");
Console.WriteLine("         Con varios servidores en la misma maquina, separalos de a 10 (7666, 7676, 7686...).");
// Si el anuncio no es el puerto real, TIENE que verse. Un remapeo mal puesto se manifiesta como "el juego
// carga pero no conecta" del lado del jugador, y del lado del server no se nota nada: los logs quedan
// iguales porque el socket ni siquiera llega. Es la clase de cosa que se busca durante horas.
if (serverIni.PuertoWebSocketPublico > 0 && serverIni.PuertoWebSocketPublico != port + 1)
    Console.WriteLine($"         OJO: al cliente se le anuncia el WebSocket en el {serverIni.PuertoWebSocketPublico}, " +
                      $"no en el {port + 1} que abrio de verdad (PuertoWebSocketPublico del ini). " +
                      "Es para hostings que remapean puertos: si no es tu caso, sacalo.");

// Que el host VEA qué leyó: sin esto, un Server.ini mal ubicado o con el nombre mal escrito falla en silencio
// y el operador se pasa la tarde preguntándose por qué no es GM.
// La CONFIGURACIÓN EFECTIVA se imprime siempre, haya archivo o no. Antes, sin archivo, se cortaba en "usando
// valores por defecto — nadie es GM" y no mostraba un solo valor. Con la configuración por variables de
// entorno (COVERAO_SECCION_CLAVE, para contenedores) eso pasó de incompleto a MENTIRA: un host que arranca su
// imagen con COVERAO_DIOSES_DIOS1 leía "nadie es GM" mientras el servidor lo tenía perfectamente cargado.
Console.WriteLine(serverIni.Ruta is null
    ? $"Server.ini: no hay archivo ({iniPath}) — vale lo que venga por variables de entorno y los defaults."
    : $"Server.ini: {serverIni.Ruta}");
Console.WriteLine($"            MaxUsers={(serverIni.MaxUsers > 0 ? serverIni.MaxUsers.ToString() : "sin tope")}, " +
                  $"exp x{serverIni.Exp}, oro x{serverIni.Oro}, multi-login={(serverIni.AllowMultiLogins ? "si" : "no")}, " +
                  $"crear personajes={(serverIni.PuedeCrearPersonajes ? "si" : "no")}, solo-GMs={(serverIni.ServerSoloGms ? "si" : "no")}");
Console.WriteLine($"            autosave={(serverIni.AutosaveMinutos > 0 ? serverIni.AutosaveMinutos + " min" : "off")}, " +
                  $"backup={(serverIni.BackupMinutos > 0 ? $"cada {serverIni.BackupMinutos} min (se conservan {serverIni.BackupsAConservar})" : "off")}, " +
                  $"log={(serverIni.LogDias > 0 ? serverIni.LogDias + " dias" : "solo consola")}");
if (serverIni.AvisoDeTasaVieja() is { } avisoDeTasa) Console.WriteLine("            " + avisoDeTasa);
// OPCIONES QUE ESTE Server.ini NO TIENE. Al actualizar, el armador CONSERVA el archivo del host a propósito
// —ahí están su puerto, sus rates y su lista de GMs—, así que las claves nuevas de la versión no aparecen
// solas. Sin esta línea, una versión puede traer una regla que cambia el juego y el que hostea nunca se
// entera de que existe ni de con qué valor está corriendo. Se listan sólo si hubo archivo: sin archivo es
// TODO default y decirlo no aporta nada.
if (serverIni.ClavesAusentes.Count > 0)
{
    Console.WriteLine($"            NUEVAS EN ESTA VERSION ({serverIni.ClavesAusentes.Count}), corriendo con su valor por " +
                      "defecto. Copialas de Server.ini.ejemplo si querés cambiarlas:");
    Console.WriteLine("            " + string.Join(", ", serverIni.ClavesAusentes));
}
Console.WriteLine(serverIni.CantidadStaff == 0
    ? "            STAFF: ninguno. Nadie va a poder usar los comandos GM — agregá un nombre a [Dioses]."
    : $"            STAFF ({serverIni.CantidadStaff}): " + string.Join(", ",
        serverIni.Staff.Select(s => $"{s.Nombre}[{NombreDeRango(s.Priv)}]")));
if (serverIni.Ruta is null && serverIni.CantidadStaff == 0)
    Console.WriteLine("            Copiá el Server.ini de ejemplo al lado del ejecutable y agregá tu personaje a [Dioses].");

static string NombreDeRango(byte priv) => priv switch
{
    16 => "Admin", 8 => "Dios", 4 => "SemiDios", 2 => "Consejero", 32 => "RoleMaster", _ => "?",
};

var gameData = GameData.Load();
// El mapa de la PRISIÓN arranca con la resurrección deshabilitada (VB6 General.bas:298, dentro de la carga
// inicial), así que a un preso no lo pueden sacar resucitándolo desde afuera. Es el único ResuSinEfecto que
// el original clava por código; los demás sólo los mueve /MODMAPINFO RESUSINEFECTO.
gameData.SetMapNoResu(CoverAO.Server.Game.Cities.Prision.Map, true);
var leveling = new Leveling(gameData.LevelExp, gameData.LifeMod);   // MODVIDA: HP/maná/stamina que se ganan por nivel
var dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
// EL SELLO DEL MUNDO: con qué versión se creó lo que hay en `data/`. Ver Build.SellarMundo para el porqué.
// Va ANTES de abrir la persistencia, que es lo que crea las subcarpetas: después de eso el directorio ya no
// estaría vacío y un mundo nuevo se confundiría con uno viejo sin sellar.
switch (CoverAO.Server.Build.SellarMundo(dataRoot, out string versionPrevia))
{
    case CoverAO.Server.SelloDelMundo.Distinta:
        Console.WriteLine($"MUNDO de la version {versionPrevia}; este servidor es la {CoverAO.Server.Build.Version}. " +
                          "Los personajes se abren igual (lo que no existia toma su valor por defecto). " +
                          "Si venis de una version MAS NUEVA, pará y restaurá un backup.");
        break;
    case CoverAO.Server.SelloDelMundo.SinSello:
        Console.WriteLine($"MUNDO sin sello de version: lo creó una version anterior a la {CoverAO.Server.Build.Version}. " +
                          "Queda sellado con ésta.");
        break;
}
IStore store = string.Equals(Environment.GetEnvironmentVariable("COVERAO_DB"), "sqlite", StringComparison.OrdinalIgnoreCase)
    ? new SqliteStore(Path.Combine(dataRoot, "coverao.db"))
    : new FileStore(dataRoot);
var db = new Persistence(store, leveling);
if (fixtureMode) db.SeedDemoIfEmpty();   // sin fixture no hay cuenta demo (cuenta_test/Rugura/…): como un server real
Console.WriteLine($"Persistencia en {dataRoot} ({(store is SqliteStore ? "SQLite (coverao.db)" : "archivos .chr/.cnt")}). COVERAO_DB=sqlite para SQLite.");


var world = new World();
world.LoadGuilds(db.LoadGuilds());       // restore persisted clans
world.LoadMailboxes(db.LoadMailboxes()); // restore pending mail
world.RecordUsuarios = db.LoadRecordUsuarios();   // el pico historico de conectados, que muestra el /ONLINE
var loop = new GameLoop();

// Estado de administración en caliente (eventos exp/oro/drop, autosave, pausa, solo-GMs, cadencia,
// mensajes automáticos, bans por IP); lo muta el panel. Las IPs baneadas se persisten en data/banned-ips.txt.
var admin = new CoverAO.Server.Game.AdminConfig();
// El centinela anti-macro arranca como diga el Server.ini (apagado por defecto, como el original). En vivo
// lo conmuta /CENTINELAACTIVADO, que escribe en este mismo campo.
admin.CentinelActive = serverIni.Centinela;
var bannedIpsPath = Path.Combine(dataRoot, "banned-ips.txt");
try { if (File.Exists(bannedIpsPath)) admin.LoadBannedIps(File.ReadAllLines(bannedIpsPath)); }
catch (Exception ex) { Console.Error.WriteLine($"No se pudo leer {bannedIpsPath}: {ex.Message}"); }
// La lista la mutan dos caminos —el panel de admin y los comandos GM /BANIP y /UNBANIP—, así que la
// persistencia se engancha acá, donde se conoce la ruta, y no en cada uno (el VB6 la graba en los dos casos).
admin.PersistBannedIps = () =>
{
    try
    {
        var dir = Path.GetDirectoryName(bannedIpsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        CoverAO.Server.Data.EscrituraAtomica.Escribir(bannedIpsPath,
            string.Concat(admin.BannedIpList().Select(l => l + Environment.NewLine)),
            System.Text.Encoding.UTF8);
    }
    catch (Exception ex) { Console.Error.WriteLine($"No se pudo guardar {bannedIpsPath}: {ex.Message}"); }
};
// Relectura desde disco, para el /BANIPRELOAD (VB6 BanIpGuardar + BanIpCargar).
admin.ReloadBannedIpsFrom = () =>
{
    try
    {
        if (File.Exists(bannedIpsPath)) admin.LoadBannedIps(File.ReadAllLines(bannedIpsPath));
        return admin.BannedIpList().Length;
    }
    catch (Exception ex) { Console.Error.WriteLine($"No se pudo releer {bannedIpsPath}: {ex.Message}"); return -1; }
};

// Contenido del login (VB6 History.bas): MOTD (data/motd.txt) + novedades (data/updates.txt), o el default.
var serverContent = CoverAO.Server.Game.ServerContent.Load(dataRoot, gameData.SourceDir);   // 2do arg: Motd.dat/ACTUALIZACIONES.dat reales del install

// gmsPerseguibles sólo en fixture: el protagonista demo (Rugura) es Dios, y el gate fiel AdminPerseguible
// dejaría al laboratorio sin PvE — ninguna criatura lo perseguiría ni lo golpearía. Ver NpcSystem.
// EL FIXTURE NO TOMA LA ESPERA DE RESPAWN DE PRODUCCION: sus criaturas son utileria que los flows matan y
// vuelven a necesitar dentro de la misma tanda de golpes. Con los 30 s de produccion se quedaban sin blanco.
var npcSystem = new NpcSystem(world, gameData, gmsPerseguibles: fixtureMode, ini: fixtureMode ? null : serverIni);
// Regla de la casa: ¿el familiar sale a buscar pelea solo? Apagada = como el original (te sigue y nada más).
npcSystem.FamiliarBuscaEnemigo = serverIni.FamiliarBusca;
npcSystem.ParalisisDeNpcTicks = serverIni.IntervaloParalizado;   // [INTERVALOS] IntervaloParalizado
npcSystem.IntervaloNpcApropiado = serverIni.IntervaloNpcApropiado;   // [TIMERS] IntervaloOwnedNpc

// Regla de la casa: ¿los pases del pirata de Rinkel se pueden usar? Apagada = como el install, donde el
// pirata te los cobra y después no te los acepta porque su `Desde` apunta a un mapa sin transportador.
if (serverIni.PasesDeRinkel)
{
    gameData.ArreglarPasesDeRinkel();
    Console.WriteLine("Regla de la casa: los pases del pirata de Rinkel salen de su puerto (PasesDeRinkel=1).");
}

// El fixture de test (NPCs + arena + zonas seguras + paredes en el mapa 15) SÓLO en modo --fixture; en
// producción el mapa 15 es mundo real. Se puebla ANTES que el mundo real para no correrle los char index
// (belt-and-braces: los flows resuelven los NPCs dinámicamente). Ver Game/TestFixture.cs.
if (fixtureMode)
    TestFixture.Populate(world, npcSystem, gameData);

// El mundo real: la población y los objetos que cada mapa trae en su .csm (ver WorldPopulator, que es donde
// vive la lógica — acá adentro no se podía testear y por eso se comió el truncamiento del nº de mapa a byte).
int spawnedNpcs, spawnedMaps, spawnedObjs;
{
    // En modo fixture el mapa del laboratorio se puebla SÓLO con el fixture: su propia fauna del .csm queda
    // afuera. Ver WorldPopulator.SpawnNpcs — un escorpión del mapa 15 se paraba en el punto de aparición.
    var (npcs, maps) = WorldPopulator.SpawnNpcs(npcSystem, gameData, fixtureMode ? TestFixture.FixtureMap : null);
    Console.WriteLine($"Spawn: {npcs} NPCs poblados desde MapSpawns.json en {maps} mapa(s) (mapa 1 = Ullathorpe).");
    var (objs, doors) = WorldPopulator.PlaceObjects(world, gameData);
    Console.WriteLine($"Spawn: {objs} objetos de mundo poblados desde MapObjects.json ({doors} puertas).");
    spawnedNpcs = npcs; spawnedMaps = maps; spawnedObjs = objs;

    // EL SUELO DE LOS JUGADORES, y va DESPUÉS del .csm a propósito (ver WorldPopulator.RestoreSuelo). Regla de
    // la casa apagada por defecto: sin ella el suelo vive en RAM y se repuebla del mapa, como el original.
    if (serverIni.PersistirSuelo)
    {
        var (puestos, descartados) = WorldPopulator.RestoreSuelo(world, db.LoadSuelo());
        Console.WriteLine($"Suelo: {puestos} objetos restaurados de lo que dejaron los jugadores" +
                          (descartados > 0 ? $" ({descartados} descartados, el mapa ya tenía algo en ese tile)." : "."));
    }
}

// Tick systems: day/night clock + NPC AI + slow HP/mana/stamina regeneration for living players.
var lastPeriod = DayNight.PeriodAt(0);
var lastWeather = Weather.At(0);
var regenRng = new Random();   // RandomNumber del regen de stamina (VB6 RecStamina)
// El handler del tick necesita al dispatcher (para el trabajo continuo), pero el dispatcher se construye más
// abajo porque a su vez necesita cosas que se arman acá. Se declara antes y se asigna después: la lambda lo
// captura por referencia y para cuando el loop corra ya está.
PacketDispatcher? dispatcher = null;

// Guardar a los conectados son DOS mitades, y una sola de las dos tiene que estar adentro del game loop:
// serializar lee unos cien campos del Player vivo (hacerlo desde otro hilo devuelve un personaje partido al
// medio), escribir es disco. Esto último es lo que se saca afuera.
List<(string nombre, string ini)> SerializarConectados(string quien)
{
    var pendientes = new List<(string, string)>();
    foreach (var pl in world.Players)
    {
        try { pendientes.Add((pl.Name, Persistence.SerializarPersonaje(pl))); }
        catch (Exception ex) { Console.Error.WriteLine($"[{quien}] no se pudo serializar '{pl.Name}': {ex.Message}"); }
    }
    return pendientes;
}

// Try POR JUGADOR: un solo .chr que falle (disco lleno, archivo tomado por el antivirus) no puede impedir que
// se guarden los demás, y el mensaje tiene que decir DE QUIÉN fue el error.
int EscribirGuardados(List<(string nombre, string ini)> pendientes, string quien)
{
    int ok = 0, fallidos = 0;
    foreach (var (nombre, ini) in pendientes)
    {
        try { db.GuardarPersonajeSerializado(nombre, ini); ok++; }
        catch (Exception ex) { fallidos++; Console.Error.WriteLine($"[{quien}] no se pudo guardar '{nombre}': {ex.Message}"); }
    }
    if (ok > 0 || fallidos > 0)
        Console.WriteLine($"[{quien}] {ok} personajes guardados" + (fallidos > 0 ? $", {fallidos} FALLARON" : ""));
    return ok;
}

// Backups del almacén (data/backups/coverao-AAAAMMDD-HHMMSS).
//
// La foto se saca AFUERA del hilo del loop. Antes corría adentro, con el argumento de que así nadie escribía el
// mundo mientras se copiaba — pero eso ya lo garantiza la escritura atómica del almacén (temporal + rename),
// archivo por archivo, y lo que cuesta se midió sobre el Charfile del servidor original: 9.918 personajes son
// CINCO SEGUNDOS de mundo congelado, cada backup, para siempre.
long ultimoBackupTick = 0;
string backupsDir = Path.Combine(dataRoot, "backups");
Task tareaBackup = Task.CompletedTask, tareaAutosave = Task.CompletedTask;
int backupEnCurso = 0;

string HacerBackup(string motivo)
{
    // Uno por vez: el timer vuelve a saltar mientras el anterior todavía copia, y dos recorridas simultáneas del
    // mismo directorio se pisan el destino.
    if (Interlocked.Exchange(ref backupEnCurso, 1) == 1)
    {
        string ocupado = $"[backup] {motivo}: salteado, el anterior sigue en curso";
        Console.WriteLine(ocupado);
        return ocupado;
    }
    // Guardar a los conectados primero: si no, el snapshot sale con el estado del último save y el backup
    // miente sobre lo que había. Es la diferencia entre restaurar la partida y restaurar la de hace una hora.
    var pendientes = SerializarConectados("backup");
    // Por lo mismo que los personajes: si los clanes o el correo quedaron sucios, el snapshot saldría con lo
    // de hace unos segundos.
    dispatcher?.VolcarPersistenciaDiferida(forzar: true);
    var cuando = DateTime.Now;

    tareaBackup = Task.Run(() =>
    {
        try
        {
            EscribirGuardados(pendientes, "backup");
            string ruta = Backups.Crear(store, backupsDir, cuando, dataRoot);
            int podados = Backups.Podar(backupsDir, serverIni.BackupsAConservar);
            Console.WriteLine($"[backup] {motivo}: {Path.GetFileName(ruta)}" + (podados > 0 ? $" ({podados} viejo(s) borrado(s))" : ""));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[backup] FALLÓ: {ex.Message}"); }
        finally { Interlocked.Exchange(ref backupEnCurso, 0); }
    });
    return $"[backup] {motivo}: en curso";
}

// `--perf`: cada 10 s imprime lo que cuesta una vuelta del loop. El presupuesto es TickMs (40 ms) y pasarse no
// tira ningún error —el loop resincroniza y el mundo corre más lento—, así que sin esto un servidor ahogado se
// ve como "lag del cliente". Es la herramienta con la que se midieron los índices por tile y por mapa.
bool perfMode = args.Any(a => string.Equals(a, "--perf", StringComparison.OrdinalIgnoreCase));

loop.OnTickHandler = tick =>
{
    if (perfMode && tick % (10_000 / GameLoop.TickMs) == 0 && tick > 0)
    {
        var (prom, max, excedidas, total) = loop.EstadisticasDeTick();
        Console.WriteLine($"[perf] tick {total}: promedio {prom:F3} ms, peor {max:F3} ms, " +
                          $"{excedidas}/256 pasadas de {GameLoop.TickMs} ms · {world.OnlineCount} jugadores, " +
                          $"{world.NpcCount} NPCs · GC {GC.GetTotalAllocatedBytes() / 1048576} MB acumulados");
    }
    // Clanes y correo: se escriben acá, agrupados, y no una vez por acción de clan (ver
    // PacketDispatcher.VolcarPersistenciaDiferida). Es barato: si no hay nada sucio, sale en el primer if.
    dispatcher?.VolcarPersistenciaDiferida();
    // Las fogatas que cumplieron su plazo (regla de la casa `MinutosDeFogata`; apagada, no hace nada).
    dispatcher?.ApagarFogatasVencidas(tick);
    // Autosave periódico (config del panel, "MinutosGuardarUsuarios" del VB6). Corre aunque esté pausado.
    if (admin.AutosaveMinutes > 0 && (tick - admin.LastAutosaveTick) * GameLoop.TickMs >= (long)admin.AutosaveMinutes * 60_000)
    {
        admin.LastAutosaveTick = tick;
        // Serializar acá, escribir afuera: medido en este equipo son 0,75 ms por .chr, o sea que con cien
        // conectados el autosave se comía dos ticks enteros y el mundo se trababa cada quince minutos.
        var pendientes = SerializarConectados("autosave");
        if (pendientes.Count > 0) tareaAutosave = Task.Run(() => EscribirGuardados(pendientes, "autosave"));
    }
    // Backup periódico del almacén entero (Server.ini MinutosBackup). Se guarda a los conectados PRIMERO, o el
    // snapshot sale con el estado del último save y el backup miente sobre lo que había.
    if (serverIni.BackupMinutos > 0 && (tick - ultimoBackupTick) * GameLoop.TickMs >= (long)serverIni.BackupMinutos * 60_000)
    {
        ultimoBackupTick = tick;
        HacerBackup(motivo: "automático");
    }
    // Mensajes automáticos programados (frmMain del VB6: broadcast periódico rotativo). Sólo con jugadores online.
    if (admin.AutoMsgMinutes > 0 && world.OnlineCount > 0)
    {
        var autoMsgs = admin.AutoMessages;
        if (autoMsgs.Length > 0 && (tick - admin.LastAutoMsgTick) * GameLoop.TickMs >= (long)admin.AutoMsgMinutes * 60_000)
        {
            admin.LastAutoMsgTick = tick;
            var msg = autoMsgs[admin.AutoMsgIndex % autoMsgs.Length];
            admin.AutoMsgIndex = (admin.AutoMsgIndex + 1) % autoMsgs.Length;
            world.Broadcast(ServerMessages.ConsoleMsg(msg, 0));
            Console.WriteLine($"[auto-msg] {msg}");
        }
    }
    // EL CENTINELA ANTI-MACRO, si el host lo prendió. Es el `Call modCentinela.CallUserAttention` que el
    // original tiene COMENTADO en esta misma línea de su loop (mMainLoop.bas:21): el ruido y el destello que
    // le llaman la atención al que está trabajando. Con el interruptor apagado no hace nada.
    dispatcher?.CentinelaLlamarAtencion(Environment.TickCount64);
    // Condenas de /CARCEL (VB6 PurgarPenas, Admin.bas:138): una vez por minuto se descuenta un minuto a cada
    // preso y, al llegar a 0, sale libre en Libertad con LocaleMsg 121.
    // La vuelta de minuto del centinela (el otro `Call` comentado del original, frmMain.frm:749). Va en su
    // PROPIA cadencia y no dentro del bloque de abajo porque `centinela_flow` la acelera con
    // `--relojes-de-test`, y las condenas de /CARCEL y el IdleLimit tienen que seguir contando minutos de
    // verdad aunque el centinela vaya a un segundo.
    if (tick % PacketDispatcher.TicksEntreVueltas == 0) dispatcher?.PasarMinutoCentinela();
    if (tick % (60_000 / GameLoop.TickMs) == 0)
    {
        foreach (var pl in world.Players)
        {
            if (pl.JailMinutesLeft <= 0) continue;
            pl.JailMinutesLeft--;
            if (pl.JailMinutesLeft > 0) continue;
            dispatcher?.WarpOtroJugador(pl, Cities.Libertad);     // VB6 PurgarPenas (Admin.bas:152): WarpUserChar
            pl.Connection?.Send(ServerMessages.LocaleMsg(121));   // "Has cumplido tu condena"
            Console.WriteLine($"[carcel] {pl.Name} cumplió su condena y sale libre");
        }
        // IdleLimit del Server.ini (VB6 Counters.IdleCount + TimerCheckIdleUser): echar al que no manda un
        // paquete hace N minutos. Es lo que le devuelve la ranura al host cuando alguien deja el cliente
        // abierto toda la noche. Se mide sobre el ÚLTIMO BYTE RECIBIDO, no sobre el tráfico que le mandamos:
        // el server le manda clima y día/noche a todo el mundo, y eso no es actividad DEL JUGADOR.
        if (serverIni.IdleLimitMinutos > 0)
        {
            long limiteMs = (long)serverIni.IdleLimitMinutos * 60_000;
            long ahora = Environment.TickCount64;
            foreach (var pl in world.Players.ToArray())
            {
                var con = pl.Connection;
                // El que ya no tiene conexión está esperando su cuenta de salida: no hay a quién echar.
                if (con is null || pl.SinConexion || ahora - con.UltimaActividadMs < limiteMs) continue;
                Console.WriteLine($"[idle] {pl.Name} desconectado por inactividad ({serverIni.IdleLimitMinutos} min)");
                if (dispatcher is not null) dispatcher.EcharPorInactividad(pl);   // el 33 y la salida de siempre
                else con.Close("desconectado por inactividad");
            }
        }
    }
    var period = DayNight.PeriodAt(tick);
    if (period != lastPeriod)
    {
        lastPeriod = period;
        world.Broadcast(ServerMessages.ConsoleMsg(DayNight.Announce(period), 0)); // announce the transition
    }
    // El reloj de clima NO corre si un GM lo forzó con /LLUVIA: en el original no hay reloj, el clima es una
    // global que sólo mueve ese comando (Weather.Forzar), y un tick que la pisara haría que el /LLUVIA se
    // deshiciera solo a los pocos segundos.
    var weather = Weather.At(tick);
    if (weather != lastWeather && !Weather.Forzado)
    {
        lastWeather = weather;
        world.Broadcast(ServerMessages.RainToggle(Weather.Clima(weather)));           // client shows/hides rain
        world.Broadcast(ServerMessages.ConsoleMsg(Weather.Announce(weather), 0));
    }
    // LA PAUSA DEL SERVIDOR (VB6 EnPausa) frena sólo el TIMER_AI (mMainLoop.bas:77): la IA de las criaturas. El resto
    // del mundo —la vida, el hambre, el veneno, la cuenta de salida, el clima— es de otros timers y sigue corriendo.
    npcSystem.Update(tick, pausado: admin.Paused);
    // Cuenta regresiva de SALIDA (VB6 General.bas:1341, el mismo timer de 1 s): a cada jugador que pidió
    // /SALIR —o al que se le cortó la conexión en un mapa inseguro— se le baja el contador avisándole con
    // LocaleMsg(491), y al llegar a 0 se le manda el Disconnect y se le cierra la conexión, que es el cierre
    // DEFINITIVO: al que ya no tenía conexión lo saca del mundo igual. Cancelar es volver a mandar /SALIR (lo
    // maneja el dispatcher).
    if (tick % (1000 / GameLoop.TickMs) == 0)
        foreach (var pl in world.Players.Where(x => x.Saliendo).ToArray())
        {
            pl.SalirSegundos--;
            if (pl.SalirSegundos >= 1) { pl.Connection?.Send(ServerMessages.LocaleMsg(491, pl.SalirSegundos.ToString())); continue; }
            pl.Saliendo = false;
            pl.Connection?.Send(ServerMessages.Disconnect());
            pl.Connection?.Close("salida solicitada");
        }
    // Cuenta regresiva del comando GM (VB6 General.bas:1991, dentro del timer de 1 s): mientras queden segundos manda
    // LocaleMsg(434) con el número que falta y al llegar al final LocaleMsg(435) ("¡YA!"). Va al mapa que
    // eligió el comando o a todo el server si es 0, y el original limpia `mapasegundos` al terminar.
    if (world.CountdownRemaining > 0 && tick % (1000 / GameLoop.TickMs) == 0)   // una vez por segundo
    {
        byte quedan = world.CountdownRemaining;
        var pkt = quedan > 1
            ? ServerMessages.LocaleMsg(434, (quedan - 1).ToString())
            : ServerMessages.LocaleMsg(435);
        if (world.CountdownMap > 0) world.BroadcastToMap((short)world.CountdownMap, pkt);
        else world.Broadcast(pkt);
        world.CountdownRemaining = (byte)(quedan - 1);
        if (world.CountdownRemaining == 0) world.CountdownMap = 0;
    }
    // HambreYSed (VB6 General.bas:1183): la sed baja 10 cada IntervaloSed y el hambre cada IntervaloHambre,
    // las dos con piso en 0. Los intervalos se cuentan en VUELTAS del loop, no en milisegundos (ver
    // Survival.cs y fase5/unidades-del-loop.md), y el contador es POR USUARIO: se reinicia al disparar.
    // Y SÓLO AL USUARIO COMÚN: el original sale de entrada si no tiene el bit User (`If Not .flags.Privilegios
    // And PlayerType.User Then Exit Sub`, General.bas:1184). Un GM no se deshidrata ni pasa hambre.
    foreach (var pl in world.Players)
    {
        if (!pl.IsAlive) continue;
        bool hcChanged = false;
        bool comun = (pl.Privileges & 1) != 0;   // PlayerType.User
        if (comun && pl.Thirst > 0 && Survival.Cumplio(ref pl.ThirstCounter, Survival.ThirstIntervalTicks))
        { pl.Thirst = Survival.Decay(pl.Thirst); hcChanged = true; }
        if (comun && pl.Hunger > 0 && Survival.Cumplio(ref pl.HungerCounter, Survival.HungerIntervalTicks))
        { pl.Hunger = Survival.Decay(pl.Hunger); hcChanged = true; }
        if (hcChanged) pl.Connection?.Send(ServerMessages.UpdateHungerAndThirst(pl.Thirst, pl.Hunger));
        // TRABAJO CONTINUO (VB6 mMainLoop.bas:178, `If .flags.Trabajando Then DoTrabajar iUserIndex`): mientras
        // la faena esté prendida, el jugador pesca/mina/tala solo. El propio TickTrabajo se gatea por el
        // intervalo de 700 ms, así que llamarlo en cada vuelta del loop es lo mismo que hace el original.
        if (pl.Laborando) dispatcher?.TickTrabajo(pl.Connection as ClientSession, pl, Environment.TickCount64);
        // EfectoParalisisUser (VB6 General.bas:978, llamado desde mMainLoop.bas:160 en CADA vuelta): al paralizado
        // se lo SUELTA si su captor lo abandona. Es lo que evita que te dejen clavado y se vayan.
        //
        // El original lo aplica sólo a las clases SIN MANÁ (`If .Stats.MaxMAN = 0`), que son las que no tienen
        // Remover Parálisis para salvarse solas; el mago se aguanta los 6 segundos completos. Tres desenlaces:
        // captor desconectado o muerto → libre; captor fuera del rango de visión → la parálisis se recorta a
        // IntervaloParalizadoReducido (37 ticks ≈ 1500 ms, tal como dice el comentario de la fuente).
        //
        // La rama gemela del original para NPCs (`ParalizedByNpcIndex`) no se porta porque acá NINGUNA criatura
        // paraliza jugadores: el port no tiene NPCs lanzadores de hechizos, así que ese campo no tendría quién
        // lo prendiera.
        if (pl.MaxMana == 0 && pl.ParalyzedByCharIndex != 0 && pl.IsParalyzed(tick))
        {
            bool suelto = false;
            if (!world.TryGet(pl.ParalyzedByCharIndex, out var captor) || captor is null || !captor.IsAlive)
                suelto = true;                                   // se desconectó o lo mataron
            else if (pl.ParalyzedUntilTick - tick > 37 && !World.InView(pl.Pos, captor.Pos))
                pl.ParalyzedUntilTick = tick + 37;               // se alejó: te quedan 1500 ms y listo
            if (suelto)
            {
                pl.ParalyzedUntilTick = 0;
                pl.ParalyzedByCharIndex = 0;
                if (pl.ClientParalyzed) { pl.ClientParalyzed = false; pl.Connection?.Send(ServerMessages.ParalizeOK()); }
            }
        }
    }
    // Efectos por vuelta. El original los corre en CADA pasada del loop (mMainLoop.bas:155-215), cada uno con
    // su propio contador; el port los tenía todos amontonados en un bloque cada 3 segundos, lo que aplanaba
    // cadencias que en la fuente van de 0,08 s (energía descansando) a 64 s (curación de pie). Ver
    // fase5/unidades-del-loop.md.
    {
        foreach (var pl in world.Players)
        {
            if (!pl.IsAlive) continue;
            // Avisos GRANULARES, como el original: un paquete de 3-5 bytes por stat que cambió, en vez del
            // UpdateUserStats de ~26. Éste es el bucle más caliente que hay (corre por jugador cada 40 ms).
            bool cambioHp = false, cambioMana = false, cambioSta = false;
            if (pl.ExpireEffects(tick))   // se venció un boost temporal de poción
            {
                // VB6 DuracionPociones (General.bas:1166): manda los DOS atributos aunque haya cambiado uno
                // solo — el propio autor lo comenta ("No me gusta que ambos... pero bueno"). Es paridad.
                pl.Connection?.Send(ServerMessages.UpdateDexterity(pl.Agility));
                pl.Connection?.Send(ServerMessages.UpdateStrenght(pl.Strength));
            }
            if (!pl.IsParalyzed(tick) && pl.ClientParalyzed) // paralysis wore off → toggle the client flag back
            {
                pl.ClientParalyzed = false;
                pl.ParalyzedByCharIndex = 0;
                pl.Connection?.Send(ServerMessages.ParalizeOK());
            }
            if (!pl.IsBlinded(tick) && pl.ClientBlind) // blindness wore off → tell the client
            {
                pl.ClientBlind = false;
                pl.Connection?.Send(ServerMessages.BlindNoMore());
            }
            // Invisibilidad por hechizo vencida → volver visible (el /INVISIBLE de GM deja InvisibleUntilTick=0,
            // así que no la toca: su invisibilidad es permanente hasta re-togglear).
            dispatcher?.VencerInvisibilidad(pl, tick);
            // Metamorfosis vencida → vuelve a su forma (regla de la casa MetamorfosisDelDruida; con la regla
            // apagada nadie se transforma nunca y este chequeo es un `!= 0` que siempre da falso, y con la
            // regla en permanente —el default— el tick de vencimiento no lo alcanza ninguna vuelta).
            dispatcher?.VencerMetamorfosis(pl, tick);
            // DoPermanecerOculto (VB6 Trabajo.bas:37): el ocultamiento vencido revela al jugador (LocaleMsg 307).
            // No pisa la invisibilidad por hechizo/GM: si Invisible sigue activo, no se emite SetInvisible.
            if (pl.OcultoUntilTick != 0 && tick >= pl.OcultoUntilTick)
            {
                pl.OcultoUntilTick = 0;
                if (pl.Oculto)
                {
                    pl.Oculto = false;
                    if (!pl.Invisible)
                    {
                        world.BroadcastToObservers(pl.Pos, ServerMessages.SetInvisible(pl.CharIndex, false), pl.CharIndex);
                        pl.Connection?.Send(ServerMessages.SetInvisible(pl.CharIndex, false));
                        pl.Connection?.Send(ServerMessages.LocaleMsg(307));
                    }
                }
            }
            // EfectoFrio (VB6 General.bas:854, llamado desde mMainLoop.bas:164). Gate del original: estar
            // DESNUDO —sin armadura equipada— y ser usuario común, no GM. Cada IntervaloFrio (50 vueltas = 2 s)
            // saca el 5% del máximo: en un mapa de NIEVE se lo saca a la VIDA y PUEDE MATAR; en cualquier otro
            // terreno se lo saca a la ENERGÍA. Los 95 mapas nevados del install no gateaban nada hasta ahora
            // porque el transcode se salteaba el campo `terrain` del header de cada .csm.
            if (!pl.IsGm && Equipment.EquippedOfCategory(pl, gameData, 2) is null
                && Survival.Cumplio(ref pl.ColdCounter, Survival.ColdIntervalTicks))
            {
                if (gameData.IsSnowMap(pl.Pos.Map))
                {
                    pl.Connection?.Send(ServerMessages.LocaleMsg(46));
                    pl.Hp -= Survival.ColdBite(pl.MaxHp);
                    if (pl.Hp < 1) { pl.Hp = 0; PacketDispatcher.KillPlayer(world, gameData, pl, "el frio"); }
                    cambioHp = true;
                }
                else
                {
                    pl.Stamina = Math.Max(0, pl.Stamina - Survival.ColdBite(pl.MaxStamina));
                    cambioSta = true;
                }
            }
            // EL RELOJ DEL PORTAL DE LUM (VB6 PasarSegundotelep, General.bas:1855). En el original cuelga de
            // un timer de UN SEGUNDO aparte del loop; acá se cuenta en vueltas como todo lo demás.
            if (pl.CreoTeleport && ++pl.PortalTickCounter >= Survival.TicksPorSegundo)
            {
                pl.PortalTickCounter = 0;
                PacketDispatcher.PasoDelPortalDeLum(world, gameData, pl);
            }
            if (PacketDispatcher.EfectoVeneno(world, gameData, pl, tick, regenRng)) cambioHp = true;   // y el UpdateHP
            // EfectoIncinerado (VB6 FileIO.bas:2010, llamado desde mMainLoop.bas:170). NO es veneno: pega
            // RandomNumber(45, 65) por golpe de fuego —contra el 1 a 5 del veneno— y avisa con LocaleMsg 48.
            // El original excluye a los GMs (`Privilegios And PlayerType.User`).
            //
            // SON DOS RELOJES DISTINTOS Y ESA ES LA GRACIA. El contador `Counters.Incinerado` mide la DURACIÓN
            // y avanza `DeltaTick` —que es (ms transcurridos / 40), o sea ~1 por vuelta— en CADA vuelta, se
            // pegue o no; a las 50 vueltas (2 s) el fuego se apaga con el LocaleMsg 389. El DAÑO, en cambio,
            // sale cuando lo deja la cadencia de GOLPE, y se la CONSUME: mientras te quemás no podés pegar.
            //
            // Acá el fuego duraba CINCUENTA GOLPES de uno cada 20 s: dieciséis minutos y ~2.700 de daño, contra
            // los ~110 de dos golpes del original. El 50 estaba leído como "golpes" cuando es "vueltas".
            //
            // Y AL APAGARSE EL CONTADOR QUEDA EN UN NÚMERO AL AZAR entre -100 y 100 ("Invi variable :D", dice
            // la fuente). Como el hechizo NO lo resetea al prender el fuego (modHechizos.bas:1150 sólo toca el
            // flag), la quemadura SIGUIENTE dura `50 - ese número`: entre 0 y 6 s, y una de cada cuatro se
            // apaga en el acto sin hacer daño. Es del original y se porta igual.
            if (pl.Incinerado && !pl.IsGm)
            {
                if (pl.IncineradoCounter < 50)
                {
                    pl.IncineradoCounter++;
                    // La cadencia sale del ini y NO del 0 del fixture: ese apagado existe para que los flows
                    // manden PAQUETES en ráfaga, y esto no lo manda un cliente. Con 0 el fuego pegaría en cada
                    // vuelta y mataría a cualquiera en dos segundos.
                    if (CombatTiming.PermiteAtacar(pl, Environment.TickCount64, serverIni.IntervaloAtacar))
                    {
                        pl.Connection?.Send(ServerMessages.LocaleMsg(48));
                        world.BroadcastToObservers(pl.Pos, ServerMessages.PlayWave(78, pl.Pos.X, pl.Pos.Y));
                        pl.Hp -= regenRng.Next(45, 66);                   // RandomNumber(45, 65), inclusivo
                        if (pl.Hp < 1) { pl.Hp = 0; PacketDispatcher.KillPlayer(world, gameData, pl, "el fuego"); }
                        cambioHp = true;
                    }
                }
                else
                {
                    pl.IncineradoCounter = regenRng.Next(-100, 101);      // RandomNumber(-100, 100), inclusivo
                    pl.Incinerado = false;
                    pl.Connection?.Send(ServerMessages.LocaleMsg(389));   // se apagó
                }
            }
            // Sanar (VB6 General.bas + mMainLoop.bas:186-206): la curación natural es LENTA de pie (64 s) y
            // rápida descansando (4 s), y devuelve RandomNumber(2, 5% de la energía máxima) avisando con
            // LocaleMsg 389. Sólo si no hay hambre ni sed (mMainLoop.bas:184 envuelve Sanar+RecStamina).
            if (pl.Hunger > 0 && pl.Thirst > 0 && pl.Hp < pl.MaxHp
                && Survival.Cumplio(ref pl.HpCounter, pl.Resting ? Survival.HealRestingIntervalTicks : Survival.HealIntervalTicks))
            {
                pl.Hp = Math.Min(pl.MaxHp, pl.Hp + Survival.HealAmount(pl.MaxStamina, (lo, hi) => regenRng.Next(lo, hi + 1)));
                pl.Connection?.Send(ServerMessages.LocaleMsg(389));
                cambioHp = true;   // VB6 Sanar: WriteUpdateHP
            }
            // Maná: en el VB6 NO hay regen pasiva — sólo vuelve MEDITANDO (Sanar no toca el maná). Antes acá se
            // regalaban 5/tick caminando/peleando, debilitando la gestión de recurso del mago. La cadencia de
            // meditar se mantiene en 3 s: es la que verificaron los flujos, y DoMeditar tiene su propia lógica
            // de suerte por skill que no es parte de esta corrección de unidades.
            // Y cuánto vuelve NO es una constante: el original tira los dados CADA VUELTA con una suerte que
            // sale de la skill Meditar (1 de 35 con la skill en 0, 1 de 5 con la skill en 100) y devuelve un
            // porcentaje del maná MÁXIMO (Trabajo.bas:2094-2103). Acá había un +20 fijo cada 3 s: le regalaba
            // maná al que recién empieza, se lo escatimaba al mago grande, y dejaba a la skill Meditar sin
            // ningún efecto — o sea que entrenarla no servía para nada.
            if (pl.Meditating && pl.Mana < pl.MaxMana
                && regenRng.Next(1, Survival.SuerteAlMeditar(pl.Skills[GameLimits.SkillMeditar]) + 1) == 1)
            {
                pl.Mana = Math.Min(pl.MaxMana, pl.Mana + Survival.ManaAlMeditar(pl.MaxMana, gameData.PorcentajeRecuperoMana));
                cambioMana = true;   // VB6: WriteUpdateMana
                dispatcher?.MeditarSubeSkill(pl);   // Trabajo.bas:2113, adentro del mismo `If res = 1`
            }
            // RecStamina (VB6 General.bas:1093): cada 50 vueltas (2 s), descanses o no —el original le pasa ese
            // 50 literal en las dos ramas—, y con un corte que el port no tenía: no recupera NADA mientras
            // estás trabajando.
            if (pl.Hunger > 0 && pl.Thirst > 0 && pl.Stamina < pl.MaxStamina && !pl.Laborando
                && Survival.RecuperaEnergia(Equipment.EstaDesnudo(pl, gameData), pl.Mounted)
                && Survival.Cumplio(ref pl.StaCounter, Survival.StaminaIntervalTicks))
            {
                pl.Stamina = Math.Min(pl.MaxStamina, pl.Stamina + Survival.StaminaAmount(
                    pl.MaxStamina, pl.Skills[GameLimits.SkillSupervivencia], (lo, hi) => regenRng.Next(lo, hi + 1)));
                cambioSta = true;   // VB6 RecStamina: WriteUpdateSta
            }
            // El descanso TERMINA SOLO cuando la vida y la energía están llenas (VB6 mMainLoop.bas:220): apaga
            // el flag, manda RestOK y avisa. El port dejaba al jugador descansando para siempre.
            if (pl.Resting && pl.Hp == pl.MaxHp && pl.Stamina == pl.MaxStamina)
            {
                pl.Resting = false;
                pl.Connection?.Send(ServerMessages.RestOK());
                pl.Connection?.Send(ServerMessages.ConsoleMsg("Has terminado de descansar.", 0));
            }
            if (cambioHp) pl.Connection?.Send(ServerMessages.UpdateHP((short)pl.Hp));
            if (cambioMana) pl.Connection?.Send(ServerMessages.UpdateMana((short)pl.Mana));
            if (cambioSta) pl.Connection?.Send(ServerMessages.UpdateSta((short)pl.Stamina));
        }
    }
};

// Anti-cheat de cadencia (VB6 IntervaloParaCaminar/ParaGolpe): activo en producción, DESACTIVADO en fixture
// (modo test — los flows/oráculo mandan paquetes en ráfaga y se procesan en el mismo tick). Valores tipo AO.
long walkIntervalMs = fixtureMode ? 0 : 130;
// La cadencia de golpe sale del `[INTERVALOS] IntervaloUserPuedeAtacar` del Server.ini (VB6 FileIO.bas:1495),
// que en este install vale 1000. Estaba cableada en 1200: se peleaba un 20% más lento que el original.
// En --fixture va en 0, como la caminata: los flows mandan paquetes tan rápido como pueden.
long attackIntervalMs = fixtureMode ? 0 : serverIni.IntervaloAtacar;
admin.WalkIntervalMs = walkIntervalMs;       // el panel puede ajustar la cadencia en vivo (el dispatcher lee de admin)
admin.AttackIntervalMs = attackIntervalMs;
// Hardening de red (SEC-3): cap de conexiones global/por-IP, timeout de auth, tope de buffer por conexión,
// rate-limit de login por IP (anti brute-force / DoS por PBKDF2). Valores generosos para no molestar a
// jugadores/tests legítimos pero que cortan floods/fuerza bruta.
// Los topes salen del Server.ini si el host los puso; si no, los de siempre. MaxUsers del original es el
// cupo de JUGADORES, no de sockets: acá se usa como tope global de conexiones porque es el que el host
// entiende ("cuánta gente entra al servidor"), y 0 = sin tope, como en el ini.
var connGuard = new ConnectionGuard(
    maxTotal: serverIni.MaxUsers > 0 ? serverIni.MaxUsers : 4000,
    // Sockets por IP: el original clava 10 (SecurityIp.bas LIMITECONEXIONESxIP). Acá había 64, y cada socket
    // es una fuente más de paquetes, de log y de PBKDF2 contra el host. Con --fixture quedan los 64: los
    // flows y las sondas entran de a muchos desde 127.0.0.1.
    maxPerIp: serverIni.LimiteConexionesPorIp > 0 ? serverIni.LimiteConexionesPorIp : (fixtureMode ? 64 : 10));
var loginThrottle = new LoginThrottle(maxFails: 20, windowMs: 60_000, blockMs: 300_000);
// Cuentas NUEVAS por IP: tres cada diez minutos. Es otro contador porque el de arriba cuenta FALLOS y crear
// una cuenta con nombre libre no falla nunca (ver PacketDispatcher.CreacionBloqueada).
// EL CUPO DE CUENTAS NUEVAS SE APAGA CON --fixture, igual que los topes por IP, salvo que el ini lo escriba.
// El barrido del oraculo crea UNA cuenta por accion desde 127.0.0.1 —son ciento quince— y con el cupo puesto
// se quedaba sin poder crear a partir de la cuarta: el barrido entero salia <sin-login> y sin medir.
int cupoDeCuentas = fixtureMode && !serverIni.InitEscrita("CuentasNuevasPorIp") ? 0 : serverIni.CuentasNuevasPorIp;
var creacionDeCuentas = cupoDeCuentas > 0 ? new LoginThrottle(maxFails: cupoDeCuentas, windowMs: 600_000, blockMs: 600_000) : null;
// Registro de economía: un archivo por día en data/logs, aparte del log general, para reconstruir de dónde
// salió el oro de alguien cuando haga falta. Se conserva lo mismo que el log (o 30 días si el log va a consola).
using var economia = new RegistroDeEconomia(logsDir, serverIni.LogDias > 0 ? serverIni.LogDias : 30);
// Y el de trampas: cada detección del anticheat, con cuenta, personaje, IP y el número que le tocó.
using var trampas = new RegistroDeEconomia(logsDir, serverIni.LogDias > 0 ? serverIni.LogDias : 30, "trampas");
const long authTimeoutMs = 30_000;   // 30 s para autenticarse o se cierra la conexión
const int maxInboundBytes = 64 * 1024;
// El token de cancelación se crea ANTES del dispatcher porque el comando GM /APAGAR necesita el mismo
// callback de apagado que usa el botón del panel de admin (ver PacketDispatcher.GmTurnOffServer).
using var cts = new CancellationTokenSource();

// AVISARLE A LA GENTE ANTES DE CORTAR, y por UN solo lugar: todo apagado pasa por acá.
//
// El `/APAGAR` del GM avisa (GmTurnOffServer), pero los caminos que usa el HOST —Ctrl+C, cerrar la ventana,
// `systemctl stop`, el archivo `detener`— cortaban en silencio: al jugador se le cae la conexión sin una
// palabra y el cliente web se queda reintentando solo, que desde el otro lado se ve igual que quedarse sin
// internet.
//
// El aviso NO puede ir abajo, con el guardado: MEDIDO — para cuando `Task.WhenAll` de los listeners vuelve,
// las sesiones ya están cerradas y el mensaje no sale por ningún socket (la sonda recibía el cierre y nada
// más). Tiene que ir ANTES de cancelar, con las conexiones vivas, y hay que darle un respiro a la cola de
// salida para que lo escriba: 300 ms es imperceptible al apagar y es la diferencia entre avisar y no avisar.
var yaAviso = new bool[1];
void PedirApagadoOrdenado()
{
    bool avisar = false;
    lock (yaAviso) { if (!yaAviso[0]) { yaAviso[0] = true; avisar = true; } }
    if (avisar)
    {
        try
        {
            world.Broadcast(ServerMessages.ConsoleMsg("El servidor se está apagando. ¡Hasta la próxima!", 1));
            Thread.Sleep(300);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[apagado] no se pudo avisar: {ex.Message}"); }
    }
    cts.Cancel();
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nApagando servidor...");
    PedirApagadoOrdenado();
};
// SEÑALES DE APAGADO. `ctx.Cancel = true` frena la terminación por default, y después se ESPERA a que el
// guardado termine: si el handler vuelve enseguida, el sistema mata el proceso mientras el hilo principal
// todavía está escribiendo los .chr, y el jugador pierde igual lo que se quería salvar.
//
// Hay que registrar las TRES, porque cada forma de apagar manda una distinta:
//   SIGTERM  `systemctl stop` en Linux, `docker stop`, y el apagado/cierre de sesión de Windows.
//   SIGINT   Ctrl+C (además del CancelKeyPress de arriba, que cubre el caso normal).
//   SIGHUP   CERRAR LA VENTANA de la consola en Windows —.NET mapea ahí el CTRL_CLOSE_EVENT— y también la
//            caída de la terminal en Linux (una sesión SSH que se corta).
//
// SIGHUP FALTABA, y "cerrar la ventana" es textualmente lo que HOSTING.md y el LEEME le dicen al host que
// haga. O sea que el camino más documentado era el único que NO pasaba por el apagado ordenado: cada cierre
// costaba a cada jugador conectado todo lo hecho desde el último autosave (hasta 15 minutos).
var apagadoListo = new ManualResetEventSlim(false);
void PedirApagado(PosixSignalContext ctx, string quien)
{
    ctx.Cancel = true;
    Console.WriteLine($"\nApagando servidor ({quien})...");
    PedirApagadoOrdenado();
    // Tope por si el apagado se traba: preferimos morir tarde a colgarnos para siempre.
    apagadoListo.Wait(TimeSpan.FromSeconds(30));
}
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => PedirApagado(ctx, "SIGTERM"));
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => PedirApagado(ctx, "SIGINT"));
using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => PedirApagado(ctx, "SIGHUP / ventana cerrada"));

// APAGAR UN SERVIDOR QUE NO TIENE VENTANA: un archivo `detener` al lado del ejecutable.
//
// Los dos caminos de arriba necesitan una consola. En Linux no importa —`systemctl stop` manda SIGTERM de
// verdad—, pero en WINDOWS un servidor que corre de fondo no tiene ventana, y ahí no hay a quién mandarle el
// WM_CLOSE. MEDIDO el 2026-08-02 sobre este ejecutable con el mundo real cargado:
//
//     taskkill /PID <n>  (sin /F)   salió en  87 ms, sin "Apagando" ni "Servidor detenido"
//     Stop-Process -Force           salió en  24 ms, idéntico
//
// O sea que el "apagado suave" de Windows terminaba siendo un kill, y cada reinicio le costaba a cada
// jugador conectado todo lo hecho desde el último autosave (hasta 15 minutos por defecto). Un host que
// reinicia para actualizar hace eso seguido.
//
// El archivo no depende de consola, de puerto ni de secreto, y anda igual en los dos sistemas. El permiso
// queda donde corresponde: quien puede escribir en la carpeta del servidor puede apagarlo, que es
// exactamente quien es dueño de esos personajes. Se borra ANTES de apagar para que no quede pidiendo
// apagado en el próximo arranque. Medido igual: 841 ms, con el guardado hecho.
// SE ACEPTA TAMBIEN `detener.txt`. El Explorador de Windows viene con las extensiones conocidas OCULTAS,
// asi que quien sigue el LEEME al pie de la letra —"crea un archivo vacio llamado detener"— con el menu
// derecho > Nuevo > Documento de texto termina con `detener.txt` y ve el nombre "detener" en pantalla. El
// servidor lo ignoraba y el host no tenia forma de darse cuenta de por que no se apagaba.
string[] nombresDeApagado = { "detener", "detener.txt" };
string? PedidoDeApagadoEnDisco() =>
    nombresDeApagado.Select(n => Path.Combine(AppContext.BaseDirectory, n)).FirstOrDefault(File.Exists);
_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        var pedidoDeApagado = PedidoDeApagadoEnDisco();
        if (pedidoDeApagado is not null)
        {
            try { File.Delete(pedidoDeApagado); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Si no se puede borrar, el próximo arranque se apaga solo y parece embrujado.
                Console.Error.WriteLine($"[apagado] no pude borrar '{pedidoDeApagado}' ({ex.Message}). " +
                                        "Borralo a mano o el servidor se va a apagar apenas arranque.");
            }
            Console.WriteLine("\nApagando servidor (pedido con el archivo 'detener')...");
            PedirApagadoOrdenado();
            return;
        }
        // Una vez por segundo: el costo es nulo y un apagado no necesita reaccionar más rápido. Va fuera del
        // tick del juego a propósito — el loop tiene un presupuesto de 40 ms y no se le mete un stat() ahí.
        try { await Task.Delay(1000, cts.Token); }
        catch (OperationCanceledException) { return; }
    }
});

serverIni.AplicarA(admin);   // rates y modo mantenimiento del ini → config viva del panel
serverIni.AplicarRelojes();  // duración del día y del ciclo de lluvia (en minutos en el ini, en vueltas acá)

// `--relojes-de-test`: día y clima en cadencia de SEGUNDOS. NO es para jugar — con esto el mundo amanece y
// anochece mientras cruzás una plaza, que es exactamente el defecto que se corrigió alargándolos a 24 y 18
// minutos. Existe porque `daynight_flow` y `weather_flow` prueban una cosa que los tests unitarios no pueden:
// que la TRANSICIÓN salga por el socket (el ConsoleMsg del período, el RainToggle con su byte de clima). Para
// verla hay que esperarla, y un flow no puede quedarse 6 minutos mirando. La aritmética del reloj ya está
// cubierta, y determinísticamente, en DayNightTests/WeatherTests.
//
// Deliberadamente NO va atado a `--fixture`: el fixture es lo que usa cualquiera para probar el juego con la
// cuenta demo, y ahí el reloj tiene que ser el jugable.
if (args.Any(a => string.Equals(a, "--relojes-de-test", StringComparison.OrdinalIgnoreCase)))
{
    DayNight.PeriodTicks = 75;                    // 3 s por período → un día de 12 s
    Weather.CicloTicks = 150;                     // 6 s de ciclo…
    Weather.LluviaPorciento = 50;                 // …con 3 s de lluvia y 3 despejado
    // El "minuto" del centinela, a TRES segundos: su primer paso tarda una vuelta y el plazo para contestar
    // son dos, o sea tres minutos de reloj por corrida. `centinela_flow` prueba justo lo que un test unitario
    // no puede —que el NPC aparezca, hable por el socket y encarcele al que no contesta—, y para eso hay que
    // esperarlo. Con el flag ausente vale el minuto de verdad.
    //
    // TRES y no uno: con un segundo el plazo entero son dos, y una sonda que lee tres sockets no alcanza a
    // contestar — medido, encarcelaba a los dos antes de que el flow mandara la clave. El caso "no contesta"
    // daba verde por la razón equivocada y el caso "contesta" no se podía probar.
    PacketDispatcher.TicksEntreVueltas = 3_000 / GameLoop.TickMs;
    Console.WriteLine("[test] relojes de día/noche, clima y centinela acelerados a segundos (--relojes-de-test)");
}
dispatcher = new PacketDispatcher(world, loop, db, gameData, leveling, walkIntervalMs, attackIntervalMs, loginThrottle, admin, serverContent, npcSystem, _ => PedirApagadoOrdenado(), fixtureMode, serverIni, creacionDeCuentas, economia, trampas);
// Las fogatas que quedaron prendidas del arranque anterior arrancan su plazo acá: el suelo se restaura mucho
// más arriba y la lista de vencimientos es del dispatcher, que recién existe ahora.
dispatcher.AnotarFogatasDelSueloRestaurado();
var server = new TcpGameServer(port, dispatcher, connGuard, authTimeoutMs, maxInboundBytes, tlsCert, admin,
    paquetesPorSegundo: serverIni.MaxPaquetesPorSegundo, rafagaDePaquetes: serverIni.RafagaDePaquetes,
    msPorMensaje: serverIni.MsEntreMensajes, rafagaMensajes: serverIni.RafagaDeMensajes,
    msPorConsulta: serverIni.MsEntreConsultas, rafagaConsultas: serverIni.RafagaDeConsultas);
// Cuantos servidores HTTP van a reportar como quedaron: el WebSocket siempre, el cliente web solo si esta
// instalacion lo trae. Del aviso de loopback depende que el host sepa que URL reservar.
CoverAO.Server.Net.HttpBind.PuertosHttpEsperados = habraWeb ? 2 : 1;
// Con cliente web, el juego entra por SU puerto (/ws) y el del WebSocket suelto queda de compatibilidad.
CoverAO.Server.Net.HttpBind.ElJuegoEntraPorElPuertoWeb = habraWeb;
var wsServer = new WebSocketGameServer(port + 1, dispatcher, connGuard, authTimeoutMs, maxInboundBytes, admin,
    paquetesPorSegundo: serverIni.MaxPaquetesPorSegundo, rafagaDePaquetes: serverIni.RafagaDePaquetes,
    msPorMensaje: serverIni.MsEntreMensajes, rafagaMensajes: serverIni.RafagaDeMensajes,
    msPorConsulta: serverIni.MsEntreConsultas, rafagaConsultas: serverIni.RafagaDeConsultas,
    // El Origin que declara el navegador: sin este chequeo, cualquier pagina que abra un jugador
    // puede abrirle sockets a este servidor desde SU maquina.
    origenesPermitidos: serverIni.OrigenesPermitidos); // web client (Fase 3), same world

// Ejemplo de suscripcion al event bus (decoupling de reglas y efectos).
world.Events.Subscribe<PlayerLoggedIn>(e =>
    Console.WriteLine($"[evento] {e.Player.Name} entro al mundo (char {e.Player.CharIndex})."));
// Cada muerte con su causa: es lo único que dice qué mata a los personajes en una corrida larga. Sin tildes, como la
// de arriba: redirigida a un archivo, la consola del host las rompe.
world.Events.Subscribe<PlayerDied>(e =>
    Console.WriteLine($"[evento] {e.Player.Name} murio en el mapa {e.Player.Pos.Map} ({e.Player.Pos.X},{e.Player.Pos.Y}), " +
                      $"nivel {e.Player.Level}, energia {e.Player.Stamina}/{e.Player.MaxStamina}: {e.Cause}."));

// Panel de administración web (reemplaza el frmMain del server VB6, rediseñado + responsive). Escucha en
// loopback (port+2 por defecto) y exige un token. Flags: --no-admin (desactivar), --admin-port <p>,
// --admin-token <t> (por defecto uno aleatorio, impreso al arrancar).
Task adminTask = Task.CompletedTask;
if (!sinAdmin)
{
    // adminPort ya está resuelto arriba, junto con los otros tres: es el MISMO valor que se imprimió.
    int atIdx = Array.FindIndex(args, a => string.Equals(a, "--admin-token", StringComparison.OrdinalIgnoreCase));
    string adminToken = (atIdx >= 0 && atIdx + 1 < args.Length) ? args[atIdx + 1]
        : Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    var serverInfo = new CoverAO.Server.Net.ServerInfo(spawnedMaps, spawnedNpcs, spawnedObjs, CoverAO.Server.Build.Version, fixtureMode);
    var adminServer = new CoverAO.Server.Net.AdminServer(adminPort, adminToken, world, loop, db, serverInfo, _ => PedirApagadoOrdenado(), logRing, admin, bannedIpsPath, HacerBackup, serverIni);
    adminTask = adminServer.RunAsync(cts.Token);
}

// Servidor de archivos estáticos del cliente web (distribución standalone): sirve la carpeta del cliente
// para que el jugador abra una URL y juegue sin http.server externo. Se activa sólo si hay web root (por
// defecto <exe>/webclient, empaquetado por publish.ps1) o con --web-root <dir>. Flags: --no-web, --web-port.
// `habraWeb`, `webRoot` y `webPort` ya están resueltos arriba, con los otros tres puertos: son los MISMOS
// valores que se imprimieron en el banner.
Task webTask = Task.CompletedTask;
if (habraWeb)
{
    // `port + 1` es el puerto del WebSocketGameServer. Se le pasa para que se lo publique al cliente en vez
    // de que éste lo tenga cableado: los puertos salen todos de StartPort, y con el 7667 a mano un host que
    // cambiara StartPort servía un cliente que no podía conectarse a su propio servidor.
    // La página pública (estado.html): conectados + tabla de posiciones, del mundo vivo y del almacén. No
    // toca el juego —ni un paquete nuevo— y se apaga entera con PaginaPublica=0, que deja la ruta en 404.
    // `EsStaff` sale del ini VIVO y no del personaje: el rango se deriva en cada login y el .chr no manda.
    Func<string>? estadoPublico = serverIni.PaginaPublica
        ? new CoverAO.Server.Net.EstadoPublico(world, store, loop, serverIni.EsStaff, CoverAO.Server.Build.Version).Json
        : null;
    // Y el JUEGO entra por este mismo puerto, en /ws. El WebSocket sigue teniendo el suyo (port+1) y los dos
    // caminos terminan en el mismo aceptador; lo que cambia es que el cliente ya no necesita el otro, así que
    // el que hostea abre UN puerto en el router en vez de dos. Con dos, olvidarse del segundo no daba ningún
    // error visible: la página abría, el login aparecía, y el socket moría contra un puerto cerrado.
    webTask = new CoverAO.Server.Net.StaticFileServer(
        webPort, webRoot!, port + 1, serverIni.CodigoFuente, serverIni.PuertoWebSocketPublico,
        estadoPublico, wsServer.AceptarAsync).RunAsync(cts.Token);
}

// UNA COPIA AL ARRANCAR. El backup periodico sale recien a los MinutosBackup exactos y su contador se
// reinicia en cada arranque, asi que quien reinicia seguido —que es lo que hace cualquiera mientras ajusta
// el Server.ini para ser GM— podia jugar toda la tarde y no tener NUNCA una copia, con el LEEME
// prometiendole "una cada media hora". Esta es la barata y la que de verdad salva: guarda el estado con el
// que ARRANCA esta sesion, antes de que pase nada.
//
// Va en segundo plano para no demorar el arranque -en el Charfile del servidor original la copia son unos
// segundos- y todavia no hay nadie conectado, asi que no congela a nadie. `HacerBackup` ya se protege de
// correr dos veces a la vez.
if (serverIni.BackupMinutos > 0) _ = Task.Run(() => HacerBackup(motivo: "al arrancar"));

var loopTask = loop.RunAsync(cts.Token);

// LOS BOTS. Apagados salvo que el host los pida (clave `Bots` del Server.ini), y NUNCA con --fixture: un bot
// caminando cerca le mete a un flow de paridad apariciones, movimientos y desapariciones que el flow no pidió,
// y con 218 flows eso serían rojos intermitentes imposibles de rastrear. Se avisa en vez de apagarlos callado.
if (serverIni.Bots > 0)
{
    if (fixtureMode)
        Console.WriteLine($"BOTS: {serverIni.Bots} pedidos, pero --fixture los apaga (contaminarian los flows).");
    else
        CoverAO.Server.Net.PobladoDeBots.Arrancar(serverIni.Bots, dispatcher, serverIni, dataRoot,
            authTimeoutMs, maxInboundBytes, gameData, cts.Token);
}

try
{
    await Task.WhenAll(server.RunAsync(cts.Token), wsServer.RunAsync(cts.Token), adminTask, webTask);
}
// Se atrapa CUALQUIER excepción, no sólo la cancelación: si un listener se cae por otra cosa —el caso
// concreto es arrancar un segundo servidor con el mismo StartPort, que muere con SocketException porque
// `listener.Start()` está fuera del try de TcpGameServer— el proceso terminaba con un stack trace crudo
// SALTEÁNDOSE el guardado de abajo. Justo cuando hay jugadores conectados es cuando no se puede omitir.
catch (OperationCanceledException) { }
catch (Exception ex) { Console.Error.WriteLine($"[apagado] un listener falló: {ex.Message}"); }

loop.Stop();
try { await loopTask; } catch (OperationCanceledException) { } catch (Exception ex) { Console.Error.WriteLine($"[apagado] game loop: {ex.Message}"); }

// GUARDAR AL APAGAR. Los otros dos caminos de apagado —el /APAGAR del GM y el botón del panel— guardan a
// todos los conectados a mano antes de disparar el shutdown; éste, que es el que usa CUALQUIER host (Ctrl+C
// en la consola, `systemctl stop`, cerrar la ventana), no lo hacía: el `SaveCharacter` del OnDisconnect se
// ENCOLA en el game loop, y para cuando las sesiones se caen el loop ya está parado, así que la tarea muere
// sin correr. Resultado: todo lo que hiciste desde el último autosave se perdía. Va acá, después de parar el
// loop, que es cuando nadie más muta el mundo.
//
// Antes que nada hay que esperar al autosave y al backup que puedan estar escribiendo en segundo plano: si no,
// una foto de hace unos milisegundos aterriza DESPUÉS del guardado final y lo pisa. Y un backup cortado a la
// mitad deja el almacén copiado a medias.
try { await Task.WhenAll(tareaAutosave, tareaBackup); }
catch (Exception ex) { Console.Error.WriteLine($"[apagado] guardado en segundo plano: {ex.Message}"); }
// Y las escrituras de los que se DESCONECTARON hace un momento, que también salieron del loop: sin esperarlas,
// el que se fue un segundo antes de bajar el servidor pierde la partida.
db.EsperarEscriturasPendientes();

int guardados = 0;
foreach (var pl in world.Players.ToArray())
{
    try { db.SaveCharacter(pl); guardados++; }
    catch (Exception ex) { Console.Error.WriteLine($"[save] {pl.Name}: {ex.Message}"); }
}
if (guardados > 0) Console.WriteLine($"Guardados {guardados} personaje(s) conectado(s).");
// Clanes y correo van diferidos (una escritura cada 3 s en vez de una por accion): si quedo algo sin
// volcar, un cierre ORDENADO no lo puede perder.
dispatcher?.VolcarPersistenciaDiferida(forzar: true);
Console.WriteLine("Servidor detenido. Hasta la proxima.");
// Recién ACÁ se libera el handler de señal: hasta este punto el sistema tiene que esperarnos, o nos mata a
// mitad de escribir los .chr y el guardado de arriba no sirve de nada.
apagadoListo.Set();
return 0;
