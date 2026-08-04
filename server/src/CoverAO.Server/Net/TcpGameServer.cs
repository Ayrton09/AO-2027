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
    private readonly int _maxInboundBytes;
    private readonly X509Certificate2? _tlsCert;
    private readonly AdminConfig? _admin;   // baneo por IP en vivo (lo muta el panel)

    public TcpGameServer(int port, PacketDispatcher dispatcher,
        ConnectionGuard? guard = null, long authTimeoutMs = 0, int maxInboundBytes = 0,
        X509Certificate2? tlsCert = null, AdminConfig? admin = null)
    {
        _port = port;
        _dispatcher = dispatcher;
        _guard = guard;
        _authTimeoutMs = authTimeoutMs;
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
        if (_admin?.IsIpBanned(ip) == true)   // IP baneada desde el panel: se rechaza antes de gastar recursos
        {
            Console.WriteLine($"[tcp:{remote}] rechazado (IP baneada)");
            try { client.Close(); } catch { }
            return;
        }
        if (_guard is not null && !_guard.TryAcquire(ip))   // cap por-IP/global: rechaza el flood
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
                await ssl.AuthenticateAsServerAsync(_tlsCert, clientCertificateRequired: false, checkCertificateRevocation: false);
                stream = ssl;
            }
            var transport = new TcpTransport(client, stream);
            session = new ClientSession(transport, _dispatcher, _authTimeoutMs, _maxInboundBytes,
                onClose: acquired ? () => _guard!.Release(ip) : null);
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
}
