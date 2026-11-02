using System.Net;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// WebSocket endpoint for the web client (Fase 3). Upgrades HTTP requests to WebSocket and feeds each
/// connection into the SAME <see cref="PacketDispatcher"/> as the TCP server — the web client speaks
/// the identical AO byte protocol, just framed in WS binary messages. Old (TCP) and new (WS) clients
/// coexist on one world.
/// </summary>
public sealed class WebSocketGameServer
{
    private readonly int _port;
    private readonly PacketDispatcher _dispatcher;
    private readonly ConnectionGuard? _guard;
    private readonly long _authTimeoutMs;
    private readonly int _paquetesPorSegundo;
    private readonly int _rafagaDePaquetes;
    private readonly int _msPorMensaje, _rafagaMensajes, _msPorConsulta, _rafagaConsultas;
    private readonly int _maxInboundBytes;
    private readonly AdminConfig? _admin;   // baneo por IP en vivo (lo muta el panel)
    private readonly string[] _origenesPermitidos;   // dominios extra que pueden abrir el socket (Server.ini)
    private readonly bool _soloEscritorio;           // sólo entra la app de escritorio (Server.ini SoloEscritorio, ver Escritorio)
    private readonly string[] _proxysDeConfianza;    // Server.ini ProxyDeConfianza: de ésos, la IP sale de X-Forwarded-For

    public WebSocketGameServer(int port, PacketDispatcher dispatcher,
        ConnectionGuard? guard = null, long authTimeoutMs = 0, int maxInboundBytes = 0, AdminConfig? admin = null,
        int paquetesPorSegundo = 0, int rafagaDePaquetes = 200,
        int msPorMensaje = 0, int rafagaMensajes = 5, int msPorConsulta = 0, int rafagaConsultas = 10,
        string[]? origenesPermitidos = null, bool soloEscritorio = false, string[]? proxysDeConfianza = null)
    {
        _port = port;
        _dispatcher = dispatcher;
        _guard = guard;
        _authTimeoutMs = authTimeoutMs;
        _paquetesPorSegundo = paquetesPorSegundo;
        _rafagaDePaquetes = rafagaDePaquetes;
        _msPorMensaje = msPorMensaje; _rafagaMensajes = rafagaMensajes;
        _msPorConsulta = msPorConsulta; _rafagaConsultas = rafagaConsultas;
        _maxInboundBytes = maxInboundBytes;
        _admin = admin;
        _origenesPermitidos = origenesPermitidos ?? Array.Empty<string>();
        _soloEscritorio = soloEscritorio;
        _proxysDeConfianza = proxysDeConfianza ?? Array.Empty<string>();
    }

    /// <summary>
    /// LA DIRECCIÓN DEL JUGADOR. Si la conexión viene de un proxy de confianza (Server.ini <c>ProxyDeConfianza</c>), la IP es la
    /// ÚLTIMA de <c>X-Forwarded-For</c>: la que escribió ese proxy. Las anteriores las pudo poner el propio cliente, así que no se
    /// les cree. Si no, o si la cabecera no trae una IP, la del par TCP, como siempre.
    /// </summary>
    public static string DireccionDelCliente(string par, string? reenviadoPara, IReadOnlyCollection<string> proxysDeConfianza)
    {
        if (proxysDeConfianza.Count == 0 || string.IsNullOrWhiteSpace(reenviadoPara)) return par;
        if (!proxysDeConfianza.Contains(ConnectionGuard.NormalizeIp(par))) return par;
        string ultima = reenviadoPara.Split(',')[^1].Trim();
        if (!IPAddress.TryParse(ultima, out var ip)) return par;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:0" : $"{ip}:0";
    }

    public async Task RunAsync(CancellationToken token)
    {
        // Toda interfaz, con caída a loopback — la MISMA decisión que toma el servidor de archivos.
        // Estaba cableado a loopback "para evitar el URL ACL de Windows", y eso rompía el hosteo entero: la
        // página del cliente se servía a todo el mundo (ese puerto sí abría en `+`) pero el WebSocket, que es
        // por donde se juega, no aceptaba a nadie de afuera. El que hosteaba veía a sus amigos entrar a la
        // pantalla de login y quedarse en "error de conexión", sin ninguna pista de por qué.
        var bind = HttpBind.Abrir(_port, "WebSocket", token);
        using var listener = bind.Listener;
        bool publico = bind.Publico;
        Console.WriteLine($"WebSocket escuchando en ws://{(publico ? "0.0.0.0" : "localhost")}:{_port}/ (cliente web)");
        // OPCIONAL cuando el juego ya entra por el puerto del cliente web: este puerto queda de compatibilidad
        // para un cliente que no conozca `/ws`, así que si cae a loopback no hay nada que avisar ni reservar.
        HttpBind.AvisarSiSoloLocal(publico, _port, opcional: HttpBind.ElJuegoEntraPorElPuertoWeb);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync();
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                _ = Task.Run(() => AceptarAsync(ctx, token), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // Las DOS formas en que `GetContextAsync()` se entera de que pararon el listener, y hacen falta las
        // dos: en Windows tira `HttpListenerException` y en Linux `ObjectDisposedException` ("Cannot access a
        // disposed object: 'listener'"). Faltando la segunda, apagar el servidor en Linux moria con excepcion
        // no manejada JUSTO DESPUES de "Apagando servidor (SIGTERM)...", asi que el `Task.WhenAll` de
        // Program.cs no volvia y el guardado de los conectados —que corre despues— nunca llegaba a correr.
        // Medido: el proceso salia en 1,4 s sin imprimir "Servidor detenido" y sin guardar a nadie.
        catch (HttpListenerException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Toma un pedido HTTP ya aceptado, lo sube a WebSocket y lo mete en el mundo.
    ///
    /// <para>Es público porque este servidor no es el único que puede recibir ese pedido: el servidor del
    /// cliente web atiende <c>/ws</c> en SU puerto y se lo pasa acá. Así el jugador baja la página y juega
    /// por la MISMA dirección, que es lo que reduce a uno los puertos que hay que abrir en el router — el
    /// defecto más caro que tenía el hosteo era justamente que fueran dos y que faltar uno se viera como
    /// "carga pero no conecta".</para>
    ///
    /// <para>El camino es el mismo pase lo que pase: el ban por IP, el cupo de conexiones y la sesión salen
    /// de acá, no del llamador. Duplicar esos tres controles en el otro servidor era garantizar que un día
    /// uno de los dos se quedara sin alguno.</para></summary>
    public async Task AceptarAsync(HttpListenerContext ctx, CancellationToken token)
    {
        var remote = DireccionDelCliente(ctx.Request.RemoteEndPoint?.ToString() ?? "?", ctx.Request.Headers["X-Forwarded-For"],
                                         _proxysDeConfianza);
        string ip = ConnectionGuard.NormalizeIp(remote);   // IP canónica (igual forma que el ban del panel)
        // ANTES del cupo de conexiones, y el orden importa: `Reservar` TOMA la ranura, así que rechazando
        // después habría que acordarse de devolverla — y un rechazo que filtra un slot convierte el guardia en
        // el arma. Acá el drive-by se corta sin gastar nada.
        if (!OrigenAceptable(ctx.Request))
        {
            ctx.Response.StatusCode = 403; ctx.Response.Close();
            // El Origin lo escribe el cliente: al log va recortado, que si no un pedido con una cabecera enorme era una línea enorme
            // en el disco por cada intento (auditoría del 12/10).
            string ajeno = ctx.Request.Headers["Origin"] ?? "";
            LogDeRed.Linea($"[ws:{remote}] rechazado (Origin ajeno: {(ajeno.Length > 100 ? ajeno[..100] + "…" : ajeno)})");
            return;
        }
        // SÓLO LA APP DE ESCRITORIO, si el server es para ella (ver Escritorio). También antes del cupo.
        if (_soloEscritorio && !Escritorio.EsLaApp(ctx.Request.Headers[Escritorio.Cabecera]))
        {
            ctx.Response.StatusCode = 403; ctx.Response.Close();
            LogDeRed.Linea($"[ws:{remote}] rechazado (no es el juego de escritorio)");
            return;
        }
        // LOS RECHAZOS AL ACEPTAR, en el orden del original (wskapiAO.bas:501-552): el servidor lleno y la IP bloqueada
        // se avisan con un cartel antes de cortar; el tope por IP corta callado. Acá los tres cortaban callados.
        var reserva = _guard?.Reservar(ip) ?? ConnectionGuard.Reserva.Hecha;
        if (reserva == ConnectionGuard.Reserva.ServidorLleno)
        {
            LogDeRed.Linea($"[ws:{remote}] rechazado (servidor lleno)");
            await AvisarYCortar(ctx, remote, ConnectionGuard.AvisoServidorLleno);
            return;
        }
        if (_admin?.IsIpBanned(ip) == true)   // IP baneada desde el panel
        {
            if (reserva == ConnectionGuard.Reserva.Hecha) _guard!.Release(ip);
            LogDeRed.Linea($"[ws:{remote}] rechazado (IP baneada)");
            await AvisarYCortar(ctx, remote, ConnectionGuard.AvisoIpBloqueada);
            return;
        }
        if (reserva == ConnectionGuard.Reserva.TopeDeLaIp)   // cap por-IP
        {
            ctx.Response.StatusCode = 503; ctx.Response.Close();
            LogDeRed.Linea($"[ws:{remote}] rechazado (cap de conexiones)");
            return;
        }
        bool acquired = _guard is not null;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            LogDeRed.Linea($"[ws:{remote}] conectado");
            var session = new ClientSession(new WebSocketTransport(wsCtx.WebSocket, remote), _dispatcher,
                _authTimeoutMs, _maxInboundBytes, onClose: acquired ? () => _guard!.Release(ip) : null,
                paquetesPorSegundo: _paquetesPorSegundo, rafagaDePaquetes: _rafagaDePaquetes,
                msPorMensaje: _msPorMensaje, rafagaMensajes: _rafagaMensajes,
                msPorConsulta: _msPorConsulta, rafagaConsultas: _rafagaConsultas);
            acquired = false;   // el slot lo libera ahora el onClose de la sesión
            await session.ReceiveLoopAsync(token);
        }
        catch (Exception ex)
        {
            LogDeRed.Linea($"[ws:{remote}] error: {ex.Message}", error: true);
        }
        finally
        {
            if (acquired) _guard!.Release(ip);   // falló el upgrade antes de crear la sesión → liberar acá
        }
    }

    /// <summary>El cartel del rechazo y el corte. Hay que subir a WebSocket para mandarlo: el cartel es un paquete del
    /// juego, y un 503 no lo lee nadie. Sale como cualquier paquete y el cierre lleva el mismo texto como motivo.</summary>
    private static async Task AvisarYCortar(HttpListenerContext ctx, string remote, string aviso)
    {
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            var transporte = new WebSocketTransport(wsCtx.WebSocket, remote);
            transporte.Send(ServerMessages.SendMsgBox(aviso));
            transporte.Close(aviso);
        }
        catch { /* el que se va no espera respuesta */ }
    }

    /// <summary>
    /// ¿DE DÓNDE VIENE ESTE SOCKET? El navegador declara en <c>Origin</c> qué página lo abrió.
    ///
    /// <para><b>Qué evita.</b> Sin esto, cualquier página que un jugador abra puede abrirle sockets a este
    /// servidor desde su navegador, sin que él se entere. No se roba nada —sin credenciales no se entra— pero
    /// sí alcanza para hacer daño con la máquina de la víctima: gastarle el cupo de conexiones por IP, y
    /// disparar intentos de login fallidos hasta que el <c>LoginThrottle</c> le bloquee <b>su</b> IP. O sea,
    /// dejar afuera del juego a alguien que no hizo nada.</para>
    ///
    /// <para><b>Sin <c>Origin</c> se ACEPTA, y no es un agujero:</b> esa cabecera la pone el navegador y sólo
    /// el navegador. Un cliente que no es una página —el arnés, una herramienta, un cliente nativo— no la
    /// manda, y rechazarlo por eso cortaría todo lo que no sea un browser sin ganar nada: quien controla su
    /// propio cliente puede escribir el <c>Origin</c> que se le antoje. La cabecera sirve contra la página de
    /// un tercero abierta en el navegador de la víctima, que es justo el caso donde el atacante NO la puede
    /// falsear.</para>
    ///
    /// <para><b>Se compara sólo el HOST, sin el puerto</b>, porque los puertos son legítimamente distintos:
    /// la página se sirve en <c>StartPort+3</c> y el WebSocket suelto vive en <c>StartPort+1</c>. Exigiendo
    /// puerto igual, el camino de compatibilidad quedaba roto para cualquier navegador.</para>
    ///
    /// <para>Y hay escape para el que sirva el cliente desde OTRO dominio: <c>OrigenesPermitidos</c> en el
    /// <c>Server.ini</c>, separados por coma.</para>
    /// </summary>
    private bool OrigenAceptable(HttpListenerRequest req)
    {
        string origen = req.Headers["Origin"] ?? "";
        if (origen.Length == 0) return true;                       // no es un navegador
        if (!Uri.TryCreate(origen, UriKind.Absolute, out var uri)) return false;   // basura: no pasa

        // El host de ESTE pedido, sin el puerto. `Url.Host` ya viene sin puerto; `UserHostName` lo trae.
        string propio = req.Url?.Host ?? (req.UserHostName ?? "").Split(':')[0];
        if (string.Equals(uri.Host, propio, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var permitido in _origenesPermitidos)
            if (string.Equals(uri.Host, permitido, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
