using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace CoverAO.Server.Net;

/// <summary>
/// Servidor de archivos estáticos para el cliente web (distribución standalone): un HttpListener que sirve
/// la carpeta del cliente (index.html + dist + assets) para que el jugador abra una URL y juegue, sin
/// depender de un http.server externo. Deliberadamente SEPARADO del WebSocketGameServer para no tocar el
/// transporte de juego verificado. Sanea el path (sin traversal fuera del web root) y sólo atiende GET/HEAD.
///
/// COMPRESIÓN Y REVALIDACIÓN. En LAN da igual, pero hosteando de verdad no: el cliente baja 1,8 MB de JSON
/// antes de dibujar el primer frame —<c>grh.json</c> solo son 1,58 MB— más 75 KB por cada mapa que cruzás, y
/// hay 863 mapas. Sin nada de esto, cada recarga se los baja enteros otra vez.
///
///   - <b>gzip</b> para lo que comprime bien (JSON, JS, HTML, CSS, el .bin del minimapa). Los sprites y el
///     audio ya vienen comprimidos: gastar CPU en re-comprimirlos los deja iguales o más grandes.
///   - <b>ETag + 304.</b> El <c>no-cache</c> de antes obligaba a revalidar, pero sin ETag ni Last-Modified el
///     navegador no tenía con qué preguntar y el servidor no tenía cómo contestar "no cambió": revalidar era
///     bajar el archivo entero. Se conserva la semántica de revalidar SIEMPRE (que es lo que hace que el
///     cache-bust <c>?v=N</c> del index.html siga siendo la única fuente de verdad) y sólo se abarata la
///     respuesta cuando de verdad no cambió nada.
///
/// El gzip se cachea en memoria por (ruta, tamaño, fecha): comprimir 1,58 MB en cada request sería peor que
/// no comprimir. El cache se acota en bytes y se vacía entero al llenarse — es un cache de arranque, no un
/// CDN, y el juego entra en calor una sola vez.
/// </summary>
public sealed class StaticFileServer
{
    private readonly int _port;
    private readonly string _root;
    private readonly byte[] _config;

    /// <summary>Ruta sintética con la config que el cliente necesita ANTES de conectarse. Hoy es sólo el puerto
    /// del WebSocket: el cliente lo tenía cableado en 7667 y eso rompía a cualquier host que tocara
    /// <c>StartPort</c> en su Server.ini —los puertos salen todos de ahí— dejándolo golpeando un puerto de
    /// nadie, con un cartel que encima lo mandaba a revisar un servidor que estaba andando bien.
    /// El cliente la pide con <c>fetch</c> y si no está (servido por un http.server de desarrollo o por un
    /// nginx) se queda con su default. Por eso vale sintetizarla acá y no pedirle nada al que hostea.</summary>
    private const string RutaConfig = "coverao-config.json";

    /// <summary>La otra ruta sintética: quién está conectado y la tabla de posiciones, para
    /// <c>estado.html</c>. Va por acá y no por un archivo porque se arma del mundo vivo. Queda apagada —404,
    /// como cualquier ruta que no existe— cuando el host pone <c>PaginaPublica=0</c>.</summary>
    private const string RutaEstado = "api/publico.json";

    /// <summary>Por dónde entra el juego EN ESTE MISMO PUERTO.
    ///
    /// <para>El WebSocket tiene además su propio puerto (StartPort+1), y sigue estando: los dos caminos
    /// terminan en el mismo <see cref="WebSocketGameServer"/>. Lo que cambia es que el cliente ya no necesita
    /// el otro, y con eso el que hostea abre UN puerto en su router en vez de dos.</para>
    ///
    /// <para>Eso no es comodidad. Con dos puertos, olvidarse del segundo no da ningún error visible: la
    /// página abre, el login aparece, y el socket muere contra un puerto cerrado. Del lado del servidor no se
    /// registra nada, porque esa conexión nunca llega. Es el defecto que hizo falta un aviso de arranque
    /// especial para explicar —y por el mismo camino se resuelve el HTTPS, porque una página servida por
    /// <c>https://</c> abre su socket <c>wss://</c> sobre esa misma dirección sin configurar nada.</para></summary>
    private const string RutaWs = "/ws";

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json", [".txt"] = "text/plain; charset=utf-8", [".wasm"] = "application/wasm",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".bmp"] = "image/bmp",
        [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".wav"] = "audio/wav", [".mid"] = "audio/midi",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
    };

    /// <summary>Lo que vale la pena comprimir. Todo lo demás (PNG, MP3, OGG, WOFF2…) ya viene comprimido y
    /// pasarle gzip encima cuesta CPU para no ahorrar un byte — a veces para agrandarlo.</summary>
    private static readonly HashSet<string> Comprimibles = new(StringComparer.OrdinalIgnoreCase)
    { ".html", ".htm", ".js", ".mjs", ".css", ".json", ".map", ".txt", ".svg", ".bin", ".wasm" };

    /// <summary>Debajo de esto el gzip no paga: el overhead del formato se come el ahorro y encima suma una
    /// pasada de CPU por request.</summary>
    private const int MinimoParaComprimir = 1024;

    private readonly ConcurrentDictionary<string, (long Len, long Mtime, byte[] Gz)> _cacheGz = new();

    /// <summary>
    /// LOS PEDIDOS EN VUELO POR BLOQUE DE IP, con un tope. Cada respuesta se escribía en un hilo del pool, de forma
    /// sincrónica y sin límite: unas pocas conexiones que pedían archivos grandes y no los leían dejaban al pool sin hilos, y
    /// con él al game loop (auditoría del 13/10). Ahora la escritura es asíncrona (no retiene un hilo) y una dirección no
    /// tiene más de <see cref="PedidosPorIp"/> a la vez. Un proxy en la misma máquina no cuenta: junta a todos los jugadores
    /// y es él el que absorbe al lento.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _enVuelo = new();
    private const int PedidosPorIp = 32;
    private long _bytesEnCache;
    private const long TopeCacheBytes = 64L * 1024 * 1024;

    private readonly int _wsPort;
    private readonly Func<string>? _estadoPublico;
    private readonly Func<HttpListenerContext, CancellationToken, Task>? _juegoPorWebSocket;
    private readonly bool _soloEscritorio;   // el navegador ve la página de descarga en vez del juego (ver Escritorio)

    /// <param name="wsPort">El puerto que abrió DE VERDAD el <see cref="WebSocketGameServer"/>.</param>
    /// <param name="codigoFuente">URL del código de ESTE servidor (Server.ini [INIT] CodigoFuente). Viaja por
    /// el mismo canal que el puerto y el cliente la muestra en el menú: es como un host cumple la cláusula de
    /// red de la AGPL sin tener que armar nada aparte. Vacía = el cliente muestra el aviso sin enlace.</param>
    /// <param name="wsPortPublico">El puerto que se le ANUNCIA al cliente, cuando no es el real. 0 = el real.
    ///
    /// Los dos se separan por los hostings que REMAPEAN puertos: si por afuera entrás por el 25000 y adentro
    /// el WebSocket está en el 7667, anunciar el interno deja al jugador bajando la página bien y despues
    /// fallando el socket contra un puerto que no está abierto. Se ve como "carga pero no conecta".
    ///
    /// El REAL se sigue usando para todo lo demás —incluido deducir el puerto del juego en el cartel de
    /// "Como entrar"—, porque eso describe la máquina, no lo que ve el de afuera.</param>
    /// <param name="estadoPublico">De dónde sale <c>/api/publico.json</c>. <c>null</c> = la página pública está
    /// apagada y esa ruta no existe.</param>
    /// <param name="juegoPorWebSocket">Qué hacer con un pedido de WebSocket a <see cref="RutaWs"/>: es
    /// <c>WebSocketGameServer.AceptarAsync</c>. Va como delegado y no como referencia al otro servidor para
    /// que este archivo siga sin saber nada del transporte de juego. <c>null</c> = <c>/ws</c> no existe acá y
    /// el cliente usa el puerto aparte, como antes.</param>
    /// <param name="alcanceDelHechizo">Si el cliente marca el alcance del hechizo al apuntar (Server.ini
    /// <c>AlcanceDelHechizo</c>). Viaja en el mismo JSON: es del server, no de cada jugador.</param>
    /// <param name="destinoDeLosPortales">Si cada portal dice a dónde lleva (Server.ini <c>DestinoDeLosPortales</c>).</param>
    /// <param name="lluviaBajoTecho">Si llueve también bajo techo y en las cuevas (Server.ini <c>LluviaBajoTecho</c>).</param>
    /// <param name="novedades">Las NOVEDADES del server (las que manda al entrar, <c>SendUpdate</c>), para que la pantalla de
    /// ingreso las muestre ANTES de entrar, como las Noticias del frmConnect del original. Viajan en el mismo JSON: son las
    /// mismas para todos y se cargan al arrancar.</param>
    /// <param name="version">La versión del server, que el panel de novedades muestra en su cabecera.</param>
    /// <param name="soloEscritorio">Server.ini <c>SoloEscritorio</c>: el que no viene de la app ve la página de descarga.</param>
    /// <param name="descargaEscritorio">Server.ini <c>DescargaEscritorio</c>: el enlace de esa página, por el mismo JSON.</param>
    public StaticFileServer(int port, string root, int wsPort, string codigoFuente = "", int wsPortPublico = 0,
                            Func<string>? estadoPublico = null,
                            Func<HttpListenerContext, CancellationToken, Task>? juegoPorWebSocket = null,
                            bool alcanceDelHechizo = true, bool destinoDeLosPortales = true,
                            IReadOnlyList<string>? novedades = null, string version = "", bool lluviaBajoTecho = false,
                            bool soloEscritorio = false, string descargaEscritorio = "", string compilacion = "")
    {
        _soloEscritorio = soloEscritorio;
        _port = port;
        _wsPort = wsPort;
        _estadoPublico = estadoPublico;
        _juegoPorWebSocket = juegoPorWebSocket;
        _root = Path.GetFullPath(root);
        int anunciado = wsPortPublico > 0 ? wsPortPublico : wsPort;
        // `wsPath` es lo que hace que el cliente entre por ESTA misma dirección. `wsPort` se sigue publicando
        // —y sigue siendo el real— para el cliente que no conozca la ruta: un index.html viejo, o una página
        // servida por un http.server de desarrollo que no pasa por acá.
        string ruta = juegoPorWebSocket is not null ? System.Text.Json.JsonSerializer.Serialize(RutaWs) : "null";
        _config = Encoding.UTF8.GetBytes(
            $"{{\"wsPort\":{anunciado},\"wsPath\":{ruta}," +
            $"\"codigoFuente\":{System.Text.Json.JsonSerializer.Serialize(codigoFuente)}," +
            $"\"alcanceHechizo\":{(alcanceDelHechizo ? "true" : "false")}," +
            $"\"destinoPortales\":{(destinoDeLosPortales ? "true" : "false")}," +
            $"\"lluviaBajoTecho\":{(lluviaBajoTecho ? "true" : "false")}," +
            $"\"version\":{System.Text.Json.JsonSerializer.Serialize(version)}," +
            $"\"descarga\":{System.Text.Json.JsonSerializer.Serialize(descargaEscritorio)}," +
            // La fecha de la compilación del server (Data/Actualizador.cs): el cliente que reconecta y la ve cambiada se recarga.
            $"\"compilacion\":{System.Text.Json.JsonSerializer.Serialize(compilacion)}," +
            $"\"novedades\":{System.Text.Json.JsonSerializer.Serialize(novedades ?? Array.Empty<string>())}}}");
    }

    /// <summary>El JSON que se baja el cliente, tal cual. Existe para poder testear QUÉ se le anuncia sin
    /// levantar un servidor HTTP: lo que importa de este archivo es su contenido, no su transporte.</summary>
    public string ConfigJson => Encoding.UTF8.GetString(_config);

    public async Task RunAsync(CancellationToken token)
    {
        var bind = HttpBind.Abrir(_port, "Cliente web", token);   // toda interfaz, con caída a loopback (ver HttpBind)
        if (bind is null) return;
        var (listener, publico) = (bind.Value.Listener, bind.Value.Publico);
        Console.WriteLine($"== Cliente web: http://localhost:{_port}/ (raíz: {_root}) ==");
        HttpBind.AvisarSiSoloLocal(publico, _port);
        HttpBind.ImprimirComoEntrar(_port, _wsPort, publico);
        try
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                // EL JUEGO, POR ESTE MISMO PUERTO. Va antes que el disco: `/ws` no es un archivo, y sin este
                // desvío el upgrade termina como un 404 — el cliente vería "no conecta" con el servidor sano.
                if (_juegoPorWebSocket is not null && ctx.Request.IsWebSocketRequest
                    && string.Equals(ctx.Request.Url?.AbsolutePath, RutaWs, StringComparison.OrdinalIgnoreCase))
                {
                    var pedido = ctx;
                    _ = Task.Run(() => _juegoPorWebSocket(pedido, token), token);
                    continue;
                }
                _ = Task.Run(() => AtenderAsync(ctx, token), token);
            }
        }
        finally { try { listener.Stop(); } catch { } }
    }

    private async Task AtenderAsync(HttpListenerContext ctx, CancellationToken token)
    {
        var dir = ctx.Request.RemoteEndPoint?.Address;
        string? bloque = dir is null || IPAddress.IsLoopback(dir) ? null : ConnectionGuard.Bloque(dir.ToString());
        if (bloque is not null && _enVuelo.AddOrUpdate(bloque, 1, (_, n) => n + 1) > PedidosPorIp)
        {
            Soltar(bloque);
            try { ctx.Response.StatusCode = 503; ctx.Response.Close(); } catch { }
            return;
        }
        try { await ServeAsync(ctx, token); }
        finally { if (bloque is not null) Soltar(bloque); }
    }

    private void Soltar(string bloque)
    {
        if (_enVuelo.AddOrUpdate(bloque, 0, (_, n) => n - 1) <= 0) _enVuelo.TryRemove(new KeyValuePair<string, int>(bloque, 0));
    }

    private async Task ServeAsync(HttpListenerContext ctx, CancellationToken token)
    {
        try
        {
            var req = ctx.Request; var res = ctx.Response;
            if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD") { res.StatusCode = 405; res.Close(); return; }
            string rel = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/").TrimStart('/');
            if (rel.Length == 0) rel = "index.html";
            // LA PÁGINA DEL JUEGO, SÓLO PARA LA APP (ver Escritorio): al navegador se le sirve la de descarga en la misma
            // dirección. Lo que sale depende de la cabecera, así que `variante` la suma al Vary de la respuesta.
            bool variante = _soloEscritorio && string.Equals(rel, "index.html", StringComparison.OrdinalIgnoreCase);
            if (variante) rel = Escritorio.RutaAServir(rel, true, Escritorio.EsLaApp(req.Headers[Escritorio.Cabecera]));
            // La config es SINTÉTICA y gana sobre el disco: si quedara un archivo viejo con ese nombre en la
            // carpeta del cliente, publicaría un puerto que no es el que este proceso abrió.
            if (string.Equals(rel, RutaConfig, StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "application/json; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";   // el puerto cambia entre arranques: nunca cachear
                res.ContentLength64 = _config.Length;
                if (req.HttpMethod == "GET") await res.OutputStream.WriteAsync(_config, token);
                res.OutputStream.Close();
                return;
            }
            // Quién está conectado y la tabla de posiciones. También sintética y también gana sobre el disco,
            // por el mismo motivo: un archivo viejo con ese nombre publicaría una foto que no es la de este
            // servidor. El armador ya cachea, así que pedirla en un bucle no cuesta más que pedirla una vez.
            if (string.Equals(rel, RutaEstado, StringComparison.OrdinalIgnoreCase))
            {
                if (_estadoPublico is null) { res.StatusCode = 404; res.Close(); return; }
                var cuerpo = Encoding.UTF8.GetBytes(_estadoPublico());
                res.ContentType = "application/json; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = cuerpo.Length;
                if (req.HttpMethod == "GET") await res.OutputStream.WriteAsync(cuerpo, token);
                res.OutputStream.Close();
                return;
            }
            string full = Path.GetFullPath(Path.Combine(_root, rel));
            // Anti path-traversal: el archivo resuelto debe quedar dentro del web root.
            if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            { res.StatusCode = 403; res.Close(); return; }
            if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
            var info = new FileInfo(full);
            if (!info.Exists) { res.StatusCode = 404; res.Close(); return; }

            // ETag de (tamaño, fecha de modificación): cambia el archivo, cambia el ETag, y el navegador se
            // baja el nuevo. No hay forma de que sirva contenido viejo.
            long mtime = info.LastWriteTimeUtc.Ticks;
            string etag = $"\"{info.Length:x}-{mtime:x}\"";
            res.Headers["ETag"] = etag;
            res.Headers["Cache-Control"] = "no-cache";      // revalidar siempre; el 304 hace que salga barato
            res.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R");
            if (string.Equals(req.Headers["If-None-Match"], etag, StringComparison.Ordinal))
            { res.StatusCode = 304; res.Close(); return; }

            string ext = Path.GetExtension(full);
            res.ContentType = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
            // LA PÁGINA DEL JUEGO NO SE DEJA METER EN UN MARCO AJENO: un sitio la mostraba adentro y guiaba los clics del
            // jugador (auditoría del 13/10). Sólo la del juego: estado.html sí va en un marco, el de la app y el del juego.
            if (string.Equals(Path.GetFileName(full), "index.html", StringComparison.OrdinalIgnoreCase))
            {
                res.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
                res.Headers["X-Frame-Options"] = "SAMEORIGIN";
            }

            bool aceptaGzip = (req.Headers["Accept-Encoding"] ?? "").Contains("gzip", StringComparison.OrdinalIgnoreCase);
            if (aceptaGzip && info.Length >= MinimoParaComprimir && Comprimibles.Contains(ext))
            {
                byte[] cuerpo = GzipDe(full, info.Length, mtime);
                res.Headers["Content-Encoding"] = "gzip";
                res.Headers["Vary"] = variante ? $"Accept-Encoding, {Escritorio.Cabecera}" : "Accept-Encoding";   // el proxy de por medio no puede servirle esto a un cliente sin gzip
                res.ContentLength64 = cuerpo.Length;
                if (req.HttpMethod == "GET") await res.OutputStream.WriteAsync(cuerpo, token);
            }
            else
            {
                // STREAMEADO, no File.ReadAllBytes: este es el camino de los archivos grandes (los MP3 de
                // hasta 8 MB no se comprimen) y cargarlos enteros por request era amplificación de memoria —
                // N descargas paralelas eran N archivos completos en el heap. Así viajan de a bloques y la
                // contrapresión del socket manda.
                if (variante) res.Headers["Vary"] = Escritorio.Cabecera;
                res.ContentLength64 = info.Length;
                if (req.HttpMethod == "GET")
                {
                    await using var fs = File.OpenRead(full);
                    await fs.CopyToAsync(res.OutputStream, token);
                }
            }
            res.OutputStream.Close();
        }
        catch (Exception ex) { LogDeRed.Linea($"[web] error: {ex.Message}", error: true); try { ctx.Response.Abort(); } catch { } }
    }

    /// <summary>
    /// El archivo comprimido, cacheado por (tamaño, fecha). Si el archivo cambió, se recomprime. UNA SOLA VEZ: los pedidos
    /// del mismo archivo que llegan juntos —un arranque, varios jugadores que entran a la vez— esperan la compresión del
    /// primero en vez de hacer cada uno la suya (auditoría del 13/10).
    /// </summary>
    internal byte[] GzipDe(string full, long len, long mtime)
    {
        if (_cacheGz.TryGetValue(full, out var e) && e.Len == len && e.Mtime == mtime) return e.Gz;
        var clave = (full, len, mtime);
        // Adentro, otra mirada al caché: el que llegó tarde, con la del primero ya terminada y fuera de la lista, no la repite.
        var compresion = _comprimiendo.GetOrAdd(clave, _ => new Lazy<byte[]>(() =>
            _cacheGz.TryGetValue(full, out var c) && c.Len == len && c.Mtime == mtime ? c.Gz : Comprimir(full, len, mtime)));
        try { return compresion.Value; }
        finally { _comprimiendo.TryRemove(new KeyValuePair<(string, long, long), Lazy<byte[]>>(clave, compresion)); }
    }

    /// <summary>Las compresiones en curso, por (ruta, tamaño, fecha): ver <see cref="GzipDe"/>.</summary>
    private readonly ConcurrentDictionary<(string, long, long), Lazy<byte[]>> _comprimiendo = new();

    /// <summary>Cuántas veces se comprimió un archivo: para las pruebas.</summary>
    internal int Compresiones => _compresiones;
    private int _compresiones;

    private byte[] Comprimir(string full, long len, long mtime)
    {
        Interlocked.Increment(ref _compresiones);
        var crudo = File.ReadAllBytes(full);
        using var ms = new MemoryStream(crudo.Length / 3);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(crudo);
        var comprimido = ms.ToArray();

        // Cache de arranque, no CDN: al llenarse se vacía entero en vez de andar desalojando por LRU. El
        // cliente pide sus assets una vez y sigue jugando.
        long previo = _cacheGz.TryGetValue(full, out var v) ? v.Gz.Length : 0;
        if (Interlocked.Add(ref _bytesEnCache, comprimido.Length - previo) > TopeCacheBytes)
        {
            _cacheGz.Clear();
            Interlocked.Exchange(ref _bytesEnCache, comprimido.Length);
        }
        _cacheGz[full] = (len, mtime, comprimido);
        return comprimido;
    }
}
