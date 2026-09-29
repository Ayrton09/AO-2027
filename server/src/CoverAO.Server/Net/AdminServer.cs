using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// Captura las líneas de la consola en un ring buffer para exponerlas en el panel de admin,
/// sin dejar de escribirlas en la consola real. Se instala con Console.SetOut().
/// </summary>
public sealed class LogRing : TextWriter
{
    private readonly TextWriter _inner;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly int _cap;
    public LogRing(TextWriter inner, int cap = 400) { _inner = inner; _cap = cap; }
    public override Encoding Encoding => _inner.Encoding;
    public override void WriteLine(string? s) { _inner.WriteLine(s); Push(s ?? ""); }
    public override void Write(string? s) => _inner.Write(s);
    public override void Write(char c) => _inner.Write(c);
    private void Push(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > _cap) _lines.TryDequeue(out _);
        EscribirAlArchivo(line);
    }
    public string[] Snapshot() => _lines.ToArray();

    /// <summary>Espejo para <c>Console.Error</c>: sigue escribiendo en stderr y además entra al ring y al
    /// archivo. Sin esto el log se perdía justo lo que más importa — todos los errores del server salen por
    /// <c>Console.Error</c>, que no pasa por el <c>Console.SetOut</c>.</summary>
    public TextWriter EspejoDeError(TextWriter innerErr) => new EspejoErr(this, innerErr);

    private sealed class EspejoErr : TextWriter
    {
        private readonly LogRing _ring;
        private readonly TextWriter _err;
        public EspejoErr(LogRing ring, TextWriter err) { _ring = ring; _err = err; }
        public override Encoding Encoding => _err.Encoding;
        public override void WriteLine(string? s) { _err.WriteLine(s); _ring.Push("[error] " + (s ?? "")); }
        public override void Write(string? s) => _err.Write(s);
        public override void Write(char c) => _err.Write(c);
    }

    // ---- Log a disco, con rotación diaria ----
    // El ring buffer vive en memoria: sirve para mirar el panel, pero se pierde entero al cerrar la ventana.
    // Para el que hostea eso significa no poder averiguar NADA de lo que pasó ayer — ni una caída, ni un abuso,
    // ni por qué se desconectó medio servidor. Un archivo por día, y se borran los de más de N días para que el
    // remedio no se coma el disco.
    private readonly object _archivoLock = new();
    private string? _dirLogs;
    private int _diasAConservar;
    private string _diaAbierto = "";
    private StreamWriter? _archivo;

    /// <summary>Empieza a volcar el log a <c>dir/server-AAAA-MM-DD.log</c>, conservando
    /// <paramref name="dias"/> días de historia (0 o menos = no escribir nada a disco).</summary>
    public void AbrirArchivo(string dir, int dias)
    {
        if (dias <= 0) return;
        lock (_archivoLock)
        {
            _dirLogs = dir;
            _diasAConservar = dias;
            Directory.CreateDirectory(dir);
            PurgarViejos();
        }
    }

    private void EscribirAlArchivo(string line)
    {
        if (_dirLogs is null) return;
        lock (_archivoLock)
        {
            if (_dirLogs is null) return;
            var ahora = DateTime.Now;
            string dia = ahora.ToString("yyyy-MM-dd");
            if (dia != _diaAbierto)
            {
                _archivo?.Dispose();
                try
                {
                    _archivo = new StreamWriter(Path.Combine(_dirLogs, $"server-{dia}.log"), append: true) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    // Sin archivo se sigue jugando: el log a disco es una comodidad del host, no el servidor.
                    _inner.WriteLine($"[log] no se pudo abrir el archivo del día: {ex.Message}");
                    _dirLogs = null; _archivo = null; return;
                }
                _diaAbierto = dia;
                PurgarViejos();
            }
            try { _archivo!.WriteLine($"{ahora:HH:mm:ss} {line}"); } catch { /* disco lleno: no tirar el server */ }
        }
    }

    /// <summary>Borra los <c>server-*.log</c> con más de N días. Se llama al abrir y en cada rotación.</summary>
    private void PurgarViejos()
    {
        if (_dirLogs is null) return;
        var corte = DateTime.Now.Date.AddDays(-_diasAConservar);
        foreach (var f in Directory.EnumerateFiles(_dirLogs, "server-*.log"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length != "server-9999-99-99".Length) continue;
            if (!DateTime.TryParseExact(stem[7..], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var fecha)) continue;
            if (fecha >= corte) continue;
            try { File.Delete(f); } catch { /* lo tiene abierto otro proceso: ya se irá */ }
        }
    }
}

/// <summary>Datos fijos del server para el panel (se calculan al arrancar).</summary>
public readonly record struct ServerInfo(int Maps, int Npcs, int Objects, string Version, bool Fixture, string Base = "");

/// <summary>
/// Panel de administración web del servidor (reemplaza el frmMain del server VB6, rediseñado).
/// Un HttpListener en loopback sirve la UI (admin.html) + una API JSON de estado y comandos:
/// monitoreo (usuarios online, récord, uptime, día/noche, clima, log), y acciones (echar, echar a
/// todos, broadcast, guardar chars, backup, apagar). Protegido por token (impreso al arrancar).
/// </summary>
public sealed class AdminServer
{
    private readonly int _port;
    private readonly string _token;
    private readonly World _world;
    private readonly GameLoop _loop;
    private readonly Persistence _db;
    private readonly ServerInfo _info;
    private readonly Action<bool> _shutdown;   // (backup) => apagar
    private readonly LogRing? _log;
    private readonly AdminConfig _admin;
    /// <summary>El Server.ini vivo. Es la ÚNICA autoridad de rango, así que dar o sacar staff desde el panel
    /// se hace escribiéndolo a él y recargando — nunca tocando el personaje.</summary>
    private readonly Data.ServerIni? _ini;
    private readonly Func<string, string>? _backup;   // (motivo) => mensaje; lo provee Program, corre en el loop
    private readonly string _html;
    private int _peak;
    private HashSet<string>? _banned;          // cuentas baneadas (cache; se llena lazy y se sincroniza en ban/unban)
    private readonly object _banLock = new();

    public AdminServer(int port, string token, World world, GameLoop loop, Persistence db,
        ServerInfo info, Action<bool> shutdown, LogRing? log, AdminConfig admin,
        Func<string, string>? backup = null, Data.ServerIni? ini = null)
    {
        _port = port; _token = token; _world = world; _loop = loop; _db = db; _info = info; _shutdown = shutdown; _log = log; _admin = admin;
        _ini = ini;
        _backup = backup;
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin.html");
        _html = File.Exists(path) ? File.ReadAllText(path) : "<h1>admin.html no encontrado</h1>";
    }

    /// <summary>Dónde se deja el token cuando la salida está redirigida y no hay consola donde imprimirlo.
    /// Va al lado del ejecutable y no adentro de <c>data/</c>: esa carpeta es la que entra en los backups y la
    /// que se copia para mudar un servidor.</summary>
    internal static string ArchivoDelToken() =>
        Path.Combine(AppContext.BaseDirectory, "admin-token.txt");

    public async Task RunAsync(CancellationToken token)
    {
        // `soloLocal: true` a propósito y para siempre: el panel puede apagar el server, kickear a todos,
        // banear y bajarse un backup. No se publica ni con urlacl — desde afuera se llega por túnel SSH.
        // Va por HttpBind igual que los otros dos por el CORTE AL APAGAR: sin él, `GetContextAsync()` deja el
        // proceso colgado para siempre en el apagado (ver el comentario largo en HttpBind.Abrir).
        var bind = HttpBind.Abrir(_port, "Panel admin", token, soloLocal: true);
        if (bind is null) return;
        var listener = bind.Value.Listener;
        // La URL con el token va DIRECTO a la consola real, no por Console.Out — que desde que existe el log a
        // disco está redirigido al LogRing y termina en `data/logs/server-AAAA-MM-DD.log`. Quien lea ese
        // archivo tiene control total del panel (shutdown, kickAll, ban, banIp, backup), y es justo el archivo
        // que un host manda "para que le miren el problema". El token es de sesión: se regenera en cada
        // arranque, así que perderlo de vista no cuesta nada — basta con volver a mirar la consola.
        // CON LA CODIFICACION DE LA CONSOLA, no la de .NET. Un StreamWriter recien hecho escribe en UTF-8, y
        // la consola de Windows en castellano muestra CP850: esta linea -la UNICA que lleva el token, o sea
        // justo la que los documentos le dicen al host que mire- salia con el acento roto
        // ("administraci?n") mientras todo el resto de la consola se veia bien.
        //
        // Y OJO CON "LA CONSOLA REAL": `Console.OpenStandardOutput()` es la consola sólo si nadie la
        // redirigió. Corriendo como servicio —`systemd`, `nohup`, un envoltorio que hace `> server.log`— esa
        // salida ES UN ARCHIVO, y el token terminaba ahí igual que si lo hubiéramos escrito en el log: medido
        // el 2026-09-03 arrancando el server con la salida redirigida. Justamente el host que corre como
        // servicio es el que después manda ese archivo para que le miren el problema.
        //
        // Con la salida redirigida no hay dónde imprimirlo, así que se lo deja en un archivo AL LADO DEL
        // EJECUTABLE y la línea visible dice dónde. Se reescribe en cada arranque; el token es de sesión, así
        // que el que quede de una corrida vieja no sirve para nada.
        if (!Console.IsOutputRedirected)
        {
            var consolaReal = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
            consolaReal.WriteLine($"== Panel de administración: http://localhost:{_port}/?token={_token} ==");
            Console.WriteLine($"== Panel de administración: http://localhost:{_port}/ (el token va sólo a la consola, no al log) ==");
        }
        else
        {
            string dondeQuedo = ArchivoDelToken();
            string aviso;
            try
            {
                // Por el helper y no con un WriteAllText: la regla de la casa es que ningún archivo que
                // sobreviva al proceso se escriba truncando (lo fija `GuardadoConLectorAjenoTests`, que me
                // puso rojo el build en cuanto lo intenté). Un token a medio escribir deja al host sin panel.
                Data.EscrituraAtomica.Escribir(dondeQuedo,
                    $"http://localhost:{_port}/?token={_token}{Environment.NewLine}", Encoding.UTF8);
                aviso = $"la salida está redirigida, así que el token quedó en {dondeQuedo}";
            }
            catch (Exception ex)
            {
                // Sin el archivo no hay forma de entrar al panel, y callarlo dejaría al host buscando un token
                // que no existe. No es motivo para no arrancar: el juego anda igual sin panel.
                aviso = $"la salida está redirigida y el token NO se pudo guardar en {dondeQuedo} ({ex.Message}); el panel queda inaccesible";
            }
            Console.WriteLine($"== Panel de administración: http://localhost:{_port}/ ({aviso}) ==");
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => HandleAsync(ctx), token);
            }
        }
        finally { try { listener.Stop(); } catch { } }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request; var res = ctx.Response;
            string path = req.Url?.AbsolutePath ?? "/";
            // HEAD: los encabezados y nada más. `HttpListener` no deja escribir cuerpo en la respuesta a un HEAD
            // —tira "Bytes to be written to the stream exceed the Content-Length"—, y así preguntan los monitores
            // de disponibilidad: cada sondeo dejaba una línea roja en el log del panel, que parecía una falla.
            if (req.HttpMethod == "HEAD")
            {
                res.StatusCode = path is "/" or "/admin" or "/index.html" ? 200 : 405;
                res.Close();
                return;
            }
            // La UI (shell) es pública; la API exige token (el server sólo escucha en loopback igual).
            if (path == "/" || path == "/admin" || path == "/index.html")
            {
                await WriteText(res, _html, "text/html; charset=utf-8");
                return;
            }
            // SÓLO POR CABECERA. El `?token=` de la query se aceptaba además, y ese es el camino por el que
            // un secreto se escapa sin que nadie lo toque: queda en el historial del navegador, en la barra de
            // direcciones y en la línea de request de cualquier proxy o log intermedio. La URL con el token
            // sigue siendo la forma de ENTRAR —es lo que imprime la consola—, pero el panel la limpia apenas
            // la lee y de ahí en más manda `X-Admin-Token`.
            // En tiempo constante: el panel es loopback, pero un `!=` de strings corta en el primer byte distinto y
            // no cuesta nada no dejar ese reloj.
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(req.Headers["X-Admin-Token"] ?? ""), Encoding.UTF8.GetBytes(_token)))
            {
                res.StatusCode = 401; await WriteText(res, "{\"error\":\"token invalido\"}", "application/json");
                return;
            }
            if (path == "/api/state") { await WriteText(res, BuildState(), "application/json"); return; }
            if (path == "/api/cmd" && req.HttpMethod == "POST")
            {
                using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                string body = await sr.ReadToEndAsync();
                await WriteText(res, HandleCommand(body), "application/json");
                return;
            }
            res.StatusCode = 404; await WriteText(res, "{\"error\":\"not found\"}", "application/json");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[admin] error: {ex.Message}"); try { ctx.Response.Abort(); } catch { } }
    }

    private static async Task WriteText(HttpListenerResponse res, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentType = contentType; res.ContentLength64 = bytes.Length;
        res.Headers["Cache-Control"] = "no-store";
        await res.OutputStream.WriteAsync(bytes);
        res.OutputStream.Close();
    }

    private string BuildState()
    {
        long tick = _loop.Tick;
        var players = _world.Players.ToArray();   // ConcurrentDictionary → iteración segura
        int online = players.Length;
        if (online > _peak) _peak = online;
        var period = DayNight.PeriodAt(tick);
        var weather = Weather.At(tick);

        var playerList = players
            .OrderByDescending(p => p.Privileges).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                charIndex = p.CharIndex,
                name = p.Name,
                account = p.Account,
                level = (int)p.Level,
                clase = GameLimits.NombreDeClase(p.Class),
                map = (int)p.Pos.Map, x = (int)p.Pos.X, y = (int)p.Pos.Y,
                hp = p.Hp, maxHp = p.MaxHp,
                gm = p.IsGm, priv = (int)p.Privileges,
                trampas = p.Trampas,
                guild = p.GuildName,
                faction = (int)p.FactionStatus,
                dead = !p.IsAlive,
            });

        long nextSaveSec = _admin.AutosaveMinutes > 0
            ? Math.Max(0, (long)_admin.AutosaveMinutes * 60 - (tick - _admin.LastAutosaveTick) * GameLoop.TickMs / 1000) : -1;
        long nextAutoMsgSec = _admin.AutoMsgMinutes > 0
            ? Math.Max(0, (long)_admin.AutoMsgMinutes * 60 - (tick - _admin.LastAutoMsgTick) * GameLoop.TickMs / 1000) : -1;

        var state = new
        {
            stats = new
            {
                online, peak = _peak,
                uptimeSec = tick * GameLoop.TickMs / 1000,
                tick,
                period = DayNight.Announce(period),
                weather = Weather.Announce(weather),
                maps = _info.Maps, npcs = _info.Npcs, objects = _info.Objects,
                version = _info.Version, fixture = _info.Fixture,
            },
            config = new
            {
                paused = _admin.Paused, onlyGms = _admin.OnlyGms,
                expMult = _admin.ExpMult, goldMult = _admin.GoldMult, dropMult = _admin.DropMult,
                // La TASA del server (Exp/Oro del ini) va aparte de los eventos: se multiplican, no se pisan.
                tasaExp = _ini?.Exp ?? 1, tasaOro = _ini?.Oro ?? 1,
                autosaveMin = _admin.AutosaveMinutes, nextSaveSec,
                walkMs = _admin.WalkIntervalMs, attackMs = _admin.AttackIntervalMs,
                autoMsgMin = _admin.AutoMsgMinutes, autoMessages = _admin.AutoMessages, nextAutoMsgSec,
            },
            // LA BASE (fase5/postgres/DISENO.md, 3.5): dónde está, si responde, cuántos pedidos esperan y desde cuándo el más
            // viejo. Con la base caída el juego sigue y lo que se guarda espera en memoria: esto es lo que lo muestra.
            baseDeDatos = new
            {
                donde = _info.Base,
                caida = _db.Cola.CaidaDesde is not null,
                caidaHaceSec = _db.Cola.CaidaDesde is { } caida ? (long)(DateTime.UtcNow - caida).TotalSeconds : 0,
                pendientes = _db.Cola.Pendientes,
                esperandoHaceSec = _db.Cola.EsperandoDesde is { } desde ? (long)(DateTime.UtcNow - desde).TotalSeconds : 0,
            },
            banned = BannedList(),
            bannedIps = _admin.BannedIpList(),
            // El STAFF que hay hoy, leído del ini vivo. `iniEscribible` es lo que le dice al panel si puede
            // ofrecer el botón: un server arrancado sin Server.ini corre con defaults y no hay dónde escribir.
            staff = _ini is null ? Array.Empty<object>()
                : _ini.Staff.OrderByDescending(s => s.Priv).ThenBy(s => s.Nombre, StringComparer.OrdinalIgnoreCase)
                      .Select(s => (object)new { name = s.Nombre, priv = (int)s.Priv, rango = Data.ServerIni.NombreDelRango(s.Priv) })
                      .ToArray(),
            iniEscribible = _ini?.Ruta is not null,
            players = playerList,
            log = _log?.Snapshot() ?? Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(state);
    }

    private string HandleCommand(string body)
    {
        JsonElement cmd;
        try { cmd = JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return Fail("body invalido"); }
        // `null`, `[]`, `5` y `"x"` son JSON válido y pasan el catch de arriba, pero TryGetProperty sobre algo
        // que no es un objeto tira InvalidOperationException — que ya no la agarra nadie acá, sube al handler
        // y le ABORTA la respuesta al panel en vez de contestarle el error que promete.
        if (cmd.ValueKind != JsonValueKind.Object) return Fail("body invalido");
        string action = cmd.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        int charIndex = cmd.TryGetProperty("charIndex", out var ci) && ci.TryGetInt32(out var civ) ? civ : 0;
        string message = cmd.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        bool backup = cmd.TryGetProperty("backup", out var b) && b.ValueKind == JsonValueKind.True;
        string name = cmd.TryGetProperty("name", out var nm) ? nm.GetString()?.Trim() ?? "" : "";
        int GetInt(string prop, int def) => cmd.TryGetProperty(prop, out var v) && v.TryGetInt32(out var iv) ? iv : def;
        string GetStr(string prop) => cmd.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        string[] GetStrArray(string prop) => cmd.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();

        switch (action)
        {
            case "kick":
                _loop.Enqueue(() =>
                {
                    if (_world.TryGet((short)charIndex, out var p) && p is not null)
                    { Console.WriteLine($"[admin] echando a {p.Name} (char {charIndex})"); p.Connection?.Close("expulsado por el administrador"); }
                });
                return Ok($"expulsando char {charIndex}");
            case "kickAll":
                _loop.Enqueue(() =>
                {
                    int n = 0;
                    foreach (var p in _world.Players.ToArray()) if (!p.IsGm) { p.Connection?.Close("servidor: expulsion masiva"); n++; }
                    Console.WriteLine($"[admin] expulsion masiva: {n} usuarios no-GM");
                });
                return Ok("expulsando a todos los no-GM");
            case "broadcast":
                if (string.IsNullOrWhiteSpace(message)) return Fail("mensaje vacio");
                _loop.Enqueue(() => { _world.Broadcast(ServerMessages.ConsoleMsg(message, 0)); Console.WriteLine($"[admin] broadcast: {message}"); });
                return Ok("mensaje enviado a todos");
            case "save":
                _loop.Enqueue(() =>
                {
                    int n = 0; foreach (var p in _world.Players.ToArray()) { _db.SaveCharacter(p); n++; }
                    Console.WriteLine($"[admin] guardados {n} personajes");
                });
                return Ok("guardando personajes");
            case "backup":
                // Copia del almacén ENTERO (cuentas, personajes, clanes, correo), no sólo un save de los online.
                if (_backup is null) return Fail("backup no disponible");
                _loop.Enqueue(() => _backup("manual (panel)"));
                return Ok("haciendo backup del mundo");
            case "shutdown":
                _loop.Enqueue(() =>
                {
                    if (backup) { foreach (var p in _world.Players.ToArray()) _db.SaveCharacter(p); Console.WriteLine("[admin] guardando a los conectados antes de apagar"); }
                    _world.Broadcast(ServerMessages.ConsoleMsg("El servidor se está apagando. ¡Hasta la próxima!", 0));
                    Console.WriteLine($"[admin] APAGANDO el servidor ({(backup ? "guardando" : "sin guardar")})");
                });
                _ = Task.Run(async () => { await Task.Delay(800); _shutdown(backup); });
                return Ok($"apagando ({(backup ? "guardando" : "sin guardar")})");

            // --- Eventos y mantenimiento (frmMain/frmServidor del VB6) ---
            case "setEvent":
                _admin.ExpMult = GetInt("exp", _admin.ExpMult);
                _admin.GoldMult = GetInt("gold", _admin.GoldMult);
                _admin.DropMult = GetInt("drop", _admin.DropMult);
                Console.WriteLine($"[admin] eventos: exp x{_admin.ExpMult}, oro x{_admin.GoldMult}, drop x{_admin.DropMult}");
                return Ok($"eventos: exp x{_admin.ExpMult}, oro x{_admin.GoldMult}, drop x{_admin.DropMult}");
            case "setAutosave":
                _admin.AutosaveMinutes = GetInt("minutes", 0);
                _admin.LastAutosaveTick = _loop.Tick;   // reinicia el countdown
                return Ok(_admin.AutosaveMinutes > 0 ? $"autosave cada {_admin.AutosaveMinutes} min" : "autosave desactivado");
            case "togglePause":
            {
                // El botón "Pausar el servidor" del original (frmServidor.frm:390, Command21_Click).
                bool pausado = _admin.Paused = !_admin.Paused;
                _loop.Enqueue(() => PacketDispatcher.AvisarPausa(_world, pausado));
                Console.WriteLine($"[admin] servidor {(pausado ? "PAUSADO" : "reanudado")}");
                return Ok(pausado ? "servidor pausado" : "servidor reanudado");
            }
            case "toggleOnlyGms":
                _admin.OnlyGms = !_admin.OnlyGms;
                Console.WriteLine($"[admin] modo solo-GMs: {(_admin.OnlyGms ? "ON" : "OFF")}");
                return Ok(_admin.OnlyGms ? "solo GMs pueden entrar (mantenimiento)" : "entrada abierta a todos");
            case "setIntervals":   // cadencia anti-cheat en vivo (VB6 IntervaloParaCaminar/ParaGolpe)
                _admin.WalkIntervalMs = GetInt("walkMs", (int)_admin.WalkIntervalMs);
                _admin.AttackIntervalMs = GetInt("attackMs", (int)_admin.AttackIntervalMs);
                Console.WriteLine($"[admin] cadencia: caminar {_admin.WalkIntervalMs}ms, golpe {_admin.AttackIntervalMs}ms");
                return Ok($"cadencia: caminar {_admin.WalkIntervalMs}ms, golpe {_admin.AttackIntervalMs}ms");
            case "setAutoMessages":   // mensajes automáticos programados (broadcast rotativo)
                _admin.AutoMessages = GetStrArray("messages");
                _admin.AutoMsgMinutes = GetInt("minutes", 0);
                _admin.LastAutoMsgTick = _loop.Tick;   // reinicia el countdown
                _admin.AutoMsgIndex = 0;
                Console.WriteLine($"[admin] mensajes automáticos: {_admin.AutoMessages.Length} cada {_admin.AutoMsgMinutes} min");
                return Ok(_admin.AutoMsgMinutes > 0 && _admin.AutoMessages.Length > 0
                    ? $"{_admin.AutoMessages.Length} mensaje(s) cada {_admin.AutoMsgMinutes} min"
                    : "mensajes automáticos desactivados");

            // --- STAFF: dar o sacar rango de GM (el 2.30 del informe) ---
            //
            // Escribe el Server.ini y lo recarga. NO toca el personaje, y eso es lo importante: el rango se
            // deriva del ini en CADA login (`AplicarPrivilegiosDeStaff`), así que guardarlo en el `.chr` sería
            // reabrir el agujero que esa función cerró — un `data/` copiado a otra máquina llevándose los
            // privilegios adentro.
            //
            // Toma efecto en el PRÓXIMO login del personaje. Al que está conectado no se le cambia el rango en
            // caliente a propósito: la mitad del estado de staff (el status de facción pisado, el override que
            // guarda lo civil debajo) se arma al entrar al mundo, y pisarlo a mitad de sesión es justo lo que
            // dejaba la facción congelada. Si hace falta ya, se lo kickea y vuelve a entrar.
            case "setStaff":
            {
                if (_ini is null) return Fail("este server no tiene Server.ini: el rango no se puede escribir");
                if (string.IsNullOrWhiteSpace(name)) return Fail("falta el nombre del personaje");
                byte priv = (byte)Math.Clamp(GetInt("priv", 0), 0, 255);
                if (priv != 1 && Data.ServerIni.NombreDelRango(priv).Length == 0) return Fail($"rango desconocido: {priv}");
                if (!_ini.GuardarStaff(name, priv)) return Fail("no se pudo escribir el Server.ini");
                string comoQueda = priv == 1 ? "jugador común" : Data.ServerIni.NombreDelRango(priv);
                return Ok($"{name} queda como {comoQueda}. Toma efecto en su próximo login.");
            }

            // --- Bans (frmServidor: Unban All; + baneo individual) ---
            case "ban":
                if (string.IsNullOrWhiteSpace(name)) return Fail("falta el nombre de cuenta");
                return SetBanned(name, true);
            case "unban":
                if (string.IsNullOrWhiteSpace(name)) return Fail("falta el nombre de cuenta");
                return SetBanned(name, false);
            case "unbanAll":
            {
                int n = 0;
                // Se lee para no reescribir las que no estaban baneadas, y se desbanea adentro de la base
                // (Persistence.CambiarCuenta): guardar la leída pisaría lo que haya cambiado en el medio.
                try { foreach (var acct in _db.AllAccountNames().ToArray()) if (_db.LoadAccount(acct) is { Banned: true } && _db.CambiarCuenta(acct, c => c.Banned = false) is not null) n++; }
                catch (Exception ex) { return Fail("error: " + ex.Message); }
                lock (_banLock) _banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"[admin] unban masivo: {n} cuentas");
                return Ok($"{n} cuentas desbaneadas");
            }

            // --- Bans por IP (frmServidor: lista de IPs baneadas + "Unban All") ---
            case "banIp":
            {
                string ip = ConnectionGuard.NormalizeIp(GetStr("ip"));
                if (string.IsNullOrWhiteSpace(ip) || ip == "?") return Fail("falta una IP válida");
                bool nuevo = _admin.BanIp(ip); SaveBannedIps();
                _loop.Enqueue(() => KickIp(ip));
                Console.WriteLine($"[admin] IP {ip} BANEADA");
                return Ok(nuevo ? $"IP {ip} baneada" : $"la IP {ip} ya estaba baneada");
            }
            case "banPlayerIp":   // banear la IP del jugador online (charIndex) y echarlo
            {
                string? ep = _world.TryGet((short)charIndex, out var pl) && pl is not null ? pl.Connection?.RemoteEndpoint : null;
                string ip = ConnectionGuard.NormalizeIp(ep ?? "");
                if (string.IsNullOrWhiteSpace(ip) || ip == "?") return Fail("no se pudo determinar la IP del jugador");
                _admin.BanIp(ip); SaveBannedIps();
                _loop.Enqueue(() => KickIp(ip));
                Console.WriteLine($"[admin] IP {ip} BANEADA (char {charIndex})");
                return Ok($"IP {ip} baneada");
            }
            case "unbanIp":
            {
                string ip = ConnectionGuard.NormalizeIp(GetStr("ip"));
                if (string.IsNullOrWhiteSpace(ip)) return Fail("falta la IP");
                bool había = _admin.UnbanIp(ip); SaveBannedIps();
                Console.WriteLine($"[admin] IP {ip} desbaneada");
                return Ok(había ? $"IP {ip} desbaneada" : $"la IP {ip} no estaba baneada");
            }
            case "unbanAllIps":
            {
                int n = _admin.ClearBannedIps(); SaveBannedIps();
                Console.WriteLine($"[admin] unban masivo de IPs: {n}");
                return Ok($"{n} IP(s) desbaneadas");
            }
            default:
                return Fail($"accion desconocida: {action}");
        }
    }

    private string SetBanned(string account, bool banned)
    {
        // Adentro de la base, no leída y guardada entera: eso pisaría lo que cambió en el medio (una trampa, un personaje
        // nuevo) y, peor, un baneo que se cruzara con el hash migrado de un login se podía perder.
        var acc = _db.CambiarCuenta(account, c => c.Banned = banned);
        if (acc is null) return Fail($"la cuenta '{account}' no existe");
        lock (_banLock) { _banned ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (banned) _banned.Add(acc.Name); else _banned.Remove(acc.Name); }
        if (banned)   // echar de inmediato a los personajes online de esa cuenta
            _loop.Enqueue(() => { foreach (var p in _world.Players.ToArray()) if (string.Equals(p.Account, account, StringComparison.OrdinalIgnoreCase)) p.Connection?.Close("cuenta baneada"); });
        Console.WriteLine($"[admin] cuenta {acc.Name} {(banned ? "BANEADA" : "desbaneada")}");
        return Ok($"cuenta {acc.Name} {(banned ? "baneada" : "desbaneada")}");
    }

    /// <summary>Echa (en el GameLoop) a todo jugador online cuya IP normalizada coincida con <paramref name="ip"/>.</summary>
    private void KickIp(string ip)
    {
        foreach (var p in _world.Players.ToArray())
            if (ConnectionGuard.NormalizeIp(p.Connection?.RemoteEndpoint ?? "") == ip)
                p.Connection?.Close("IP baneada");
    }

    /// <summary>Guarda la lista de IPs baneadas en la base (por la cola; se lee al arrancar el server).</summary>
    private void SaveBannedIps() => _admin.PersistBannedIps?.Invoke();

    private string[] BannedList()
    {
        lock (_banLock)
        {
            if (_banned is null)
            {
                _banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var nm in _db.AllAccountNames()) { var ac = _db.LoadAccount(nm); if (ac is { Banned: true }) _banned.Add(ac.Name); } }
                catch (Exception ex) { Console.Error.WriteLine($"[admin] scan de bans: {ex.Message}"); }
            }
            return _banned.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static string Ok(string msg) => JsonSerializer.Serialize(new { ok = true, msg });
    private static string Fail(string msg) => JsonSerializer.Serialize(new { ok = false, msg });
}
