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

    public WebSocketGameServer(int port, PacketDispatcher dispatcher,
        ConnectionGuard? guard = null, long authTimeoutMs = 0, int maxInboundBytes = 0, AdminConfig? admin = null,
        int paquetesPorSegundo = 0, int rafagaDePaquetes = 200,
        int msPorMensaje = 0, int rafagaMensajes = 5, int msPorConsulta = 0, int rafagaConsultas = 10)
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
    }

    public async Task RunAsync(CancellationToken token)
    {
        // Toda interfaz, con caída a loopback — la MISMA decisión que toma el servidor de archivos.
        // Estaba cableado a loopback "para evitar el URL ACL de Windows", y eso rompía el hosteo entero: la
        // página del cliente se servía a todo el mundo (ese puerto sí abría en `+`) pero el WebSocket, que es
        // por donde se juega, no aceptaba a nadie de afuera. El que hosteaba veía a sus amigos entrar a la
        // pantalla de login y quedarse en "error de conexión", sin ninguna pista de por qué.
        var bind = HttpBind.Abrir(_port, "WebSocket", token);
        if (bind is null) return;
        var (listener, publico) = (bind.Value.Listener, bind.Value.Publico);
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
        catch (OperationCanceledException) { }
        // Las DOS formas en que `GetContextAsync()` se entera de que pararon el listener, y hacen falta las
        // dos: en Windows tira `HttpListenerException` y en Linux `ObjectDisposedException` ("Cannot access a
        // disposed object: 'listener'"). Faltando la segunda, apagar el servidor en Linux moria con excepcion
        // no manejada JUSTO DESPUES de "Apagando servidor (SIGTERM)...", asi que el `Task.WhenAll` de
        // Program.cs no volvia y el guardado de los conectados —que corre despues— nunca llegaba a correr.
        // Medido: el proceso salia en 1,4 s sin imprimir "Servidor detenido" y sin guardar a nadie.
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
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
        var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";
        string ip = ConnectionGuard.NormalizeIp(remote);   // IP canónica (igual forma que el ban del panel)
        if (_admin?.IsIpBanned(ip) == true)   // IP baneada desde el panel
        {
            ctx.Response.StatusCode = 403; ctx.Response.Close();
            Console.WriteLine($"[ws:{remote}] rechazado (IP baneada)");
            return;
        }
        if (_guard is not null && !_guard.TryAcquire(ip))   // cap por-IP/global
        {
            ctx.Response.StatusCode = 503; ctx.Response.Close();
            Console.WriteLine($"[ws:{remote}] rechazado (cap de conexiones)");
            return;
        }
        bool acquired = _guard is not null;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            Console.WriteLine($"[ws:{remote}] conectado");
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
            Console.Error.WriteLine($"[ws:{remote}] error: {ex.Message}");
        }
        finally
        {
            if (acquired) _guard!.Release(ip);   // falló el upgrade antes de crear la sesión → liberar acá
        }
    }
}
