using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// Async TCP acceptor. Replaces the VB6 WSAAsyncSelect model (a subclassed hidden window receiving
/// socket messages) with a straightforward accept loop; each connection gets its own receive task.
/// TLS opcional (SEC-4): con un cert configurado, cada conexión se envuelve en un SslStream (autenticación
/// de servidor) antes de framear los paquetes AO. Default plaintext (compatibilidad con el protocolo AO
/// clásico, los flows y el oráculo). El handshake corre en la tarea por-conexión, no bloquea el accept.
/// </summary>
public sealed class TcpGameServer
{
    private readonly int _port;
    private readonly PacketDispatcher _dispatcher;
    private readonly List<ClientSession> _sessions = new();
    private readonly ConnectionGuard? _guard;
    private readonly long _authTimeoutMs;
    private readonly int _paquetesPorSegundo;
    private readonly int _rafagaDePaquetes;
    private readonly int _msPorMensaje, _rafagaMensajes, _msPorConsulta, _rafagaConsultas;
    private readonly int _maxInboundBytes;
    private readonly X509Certificate2? _tlsCert;
    private readonly AdminConfig? _admin;   // baneo por IP en vivo (lo muta el panel)

    public TcpGameServer(int port, PacketDispatcher dispatcher,
        ConnectionGuard? guard = null, long authTimeoutMs = 0, int maxInboundBytes = 0,
        X509Certificate2? tlsCert = null, AdminConfig? admin = null,
        // AL FINAL a propósito: los argumentos van por POSICIÓN y meterlos en el medio corre todo lo que
        // viene detrás. Acá el compilador avisó (tipos distintos), pero con dos int seguidos no lo haría.
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
        _tlsCert = tlsCert;
        _admin = admin;
    }

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"Escuchando en 0.0.0.0:{_port} (protocolo AO clasico{(_tlsCert is not null ? ", TLS" : "")})");

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            Console.WriteLine("Listener detenido.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        string ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        // LOS RECHAZOS AL ACEPTAR, en el orden del original (wskapiAO.bas:501-552): el servidor lleno y la IP bloqueada
        // se avisan con un cartel antes de cortar; el tope por IP corta callado. Acá los tres cortaban callados.
        var reserva = _guard?.Reservar(ip) ?? ConnectionGuard.Reserva.Hecha;
        if (reserva == ConnectionGuard.Reserva.ServidorLleno)
        {
            Console.WriteLine($"[tcp:{remote}] rechazado (servidor lleno)");
            await AvisarYCortar(client, ConnectionGuard.AvisoServidorLleno);
            return;
        }
        if (_admin?.IsIpBanned(ip) == true)   // IP baneada desde el panel
        {
            if (reserva == ConnectionGuard.Reserva.Hecha) _guard!.Release(ip);
            Console.WriteLine($"[tcp:{remote}] rechazado (IP baneada)");
            await AvisarYCortar(client, ConnectionGuard.AvisoIpBloqueada);
            return;
        }
        if (reserva == ConnectionGuard.Reserva.TopeDeLaIp)   // cap por-IP: rechaza el flood
        {
            Console.WriteLine($"[tcp:{remote}] rechazado (cap de conexiones)");
            try { client.Close(); } catch { }
            return;
        }
        bool acquired = _guard is not null;
        ClientSession? session = null;   // declarada afuera para que el finally la pueda sacar del registro
        try
        {
            Stream? stream = null;
            if (_tlsCert is not null)   // TLS opt-in: handshake de servidor antes de framear
            {
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                // CON PLAZO: el de entrar al juego arranca recién en la sesión, después del handshake, y un socket que nunca mandaba
                // el saludo de TLS dejaba su lugar del cupo tomado para siempre (auditoría del 12/10).
                using var plazo = CancellationTokenSource.CreateLinkedTokenSource(token);
                plazo.CancelAfter(TimeSpan.FromSeconds(15));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tlsCert, ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, plazo.Token);
                stream = ssl;
            }
            var transport = new TcpTransport(client, stream);
            session = new ClientSession(transport, _dispatcher, _authTimeoutMs, _maxInboundBytes,
                onClose: acquired ? () => _guard!.Release(ip) : null,
                paquetesPorSegundo: _paquetesPorSegundo, rafagaDePaquetes: _rafagaDePaquetes,
                msPorMensaje: _msPorMensaje, rafagaMensajes: _rafagaMensajes,
                msPorConsulta: _msPorConsulta, rafagaConsultas: _rafagaConsultas);
            acquired = false;   // el slot lo libera el onClose de la sesión
            lock (_sessions) _sessions.Add(session);
            Console.WriteLine($"[{session.RemoteEndpoint}] conectado{(_tlsCert is not null ? " (TLS)" : "")}");
            await session.ReceiveLoopAsync(token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tcp:{remote}] error: {ex.Message}");
            try { client.Close(); } catch { }
        }
        finally
        {
            if (acquired) _guard!.Release(ip);
            // SACAR LA SESIÓN DEL REGISTRO. Antes sólo había un Add y ningún Remove, así que CADA conexión que
            // el server aceptó alguna vez quedaba viva para siempre — y con ella su buffer de entrada (hasta
            // 64 KB), el TcpClient, y el objeto Player entero, porque session.Player tampoco se libera. Crecía
            // con cada login/logout Y con cada escaneo de puertos. Era la memoria que sube con los días.
            if (session is not null) { lock (_sessions) _sessions.Remove(session); }
        }
    }

    /// <summary>
    /// El cartel del rechazo (<c>WriteSendMsgBox</c> + <c>FlushBuffer</c> + cerrar el socket) y el corte. Después de
    /// escribir se cierra el envío y se vacía lo que el cliente ya mandó —su login—, hasta que corte o pase un
    /// segundo: cerrar con datos sin leer sale como RST, y el RST se puede llevar el cartel antes de que lo lean.
    /// Con TLS el cartel no puede ir antes del handshake, y ahí se corta callado.
    /// </summary>
    private async Task AvisarYCortar(TcpClient client, string aviso)
    {
        try
        {
            if (_tlsCert is null)
            {
                var stream = client.GetStream();
                using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await stream.WriteAsync(ServerMessages.SendMsgBox(aviso), plazo.Token);
                client.Client.Shutdown(SocketShutdown.Send);
                var basura = new byte[1024];
                while (await stream.ReadAsync(basura, plazo.Token) > 0) { }
            }
        }
        catch { /* el que se va no espera respuesta */ }
        finally { try { client.Close(); } catch { } }
    }
}
