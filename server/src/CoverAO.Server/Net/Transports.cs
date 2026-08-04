using System.Net.Sockets;
using System.Net.WebSockets;

namespace CoverAO.Server.Net;

/// <summary>
/// The byte pipe under a <see cref="ClientSession"/>. The session's framing/XOR/dispatch logic is
/// transport-agnostic; this abstracts TCP (classic VB6 client) vs WebSocket (the web client).
/// A "receive" returns the next chunk of bytes — a TCP read or a WS binary message — which the
/// session's rolling buffer frames into AO packets regardless of source.
/// </summary>
public interface ITransport
{
    string RemoteEndpoint { get; }
    ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct); // bytes read; 0 = peer closed
    void Send(byte[] data);
    /// <summary>Cierra el transporte. El <paramref name="reason"/> es el mismo string que loguea la sesión; los
    /// transportes que pueden transmitirlo (WebSocket) se lo hacen llegar al cliente para que distinguya un
    /// cierre DELIBERADO del server (echado, baneado, /SALIR) de una caída de red, que sí amerita reconectar.</summary>
    void Close(string reason);
}

public sealed class TcpTransport : ITransport
{
    /// <summary>Tope de espera de una escritura al socket, en ms.
    ///
    /// <para>SIN ESTO EL SERVIDOR SE CUELGA ENTERO. <c>Send</c> lo llama el hilo ÚNICO del game loop (todo
    /// <c>Broadcast</c>/<c>BroadcastToObservers</c> sale de ahí), y el default de <c>WriteTimeout</c> es
    /// INFINITO. Un cliente que deja de leer del socket —notebook suspendida, red saturada, o alguien que lo
    /// hace a propósito— llena la ventana TCP; el buffer de envío del kernel se llena; y <c>Write</c> bloquea
    /// para siempre. El mundo se congela para TODOS los jugadores, y no queda nada en el log.</para>
    ///
    /// <para>Con el tope, esa escritura falla con <c>IOException</c>, <c>ClientSession.Send</c> la atrapa y
    /// cierra ESA sesión: se cae el cliente lento, no el servidor. 3 s es holgado para un buffer de 64 KB en
    /// cualquier conexión que funcione, y corto para que el freeze sea un hipo y no una caída.</para>
    ///
    /// <para>MITIGA, no elimina: el arreglo de fondo es una cola de salida por sesión con un solo flush por
    /// tick, que además ahorraría un syscall por paquete. Esto acota el daño sin rearquitectar el transporte.</para></summary>
    public const int SendTimeoutMs = 3000;

    private readonly TcpClient _client;
    private readonly Stream _stream;   // NetworkStream (plaintext) o SslStream (TLS opt-in)
    private readonly object _gate = new();

    public string RemoteEndpoint { get; }

    // stream != null → transporte sobre TLS (SslStream ya handshakeado); null → plaintext (client.GetStream()).
    public TcpTransport(TcpClient client, Stream? stream = null)
    {
        _client = client;
        _stream = stream ?? client.GetStream();
        RemoteEndpoint = "tcp:" + (client.Client.RemoteEndPoint?.ToString() ?? "?");
        // NetworkStream y SslStream lo soportan; si algún stream no, seguimos sin tope (no peor que antes).
        try { _stream.WriteTimeout = SendTimeoutMs; } catch (InvalidOperationException) { }
    }

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
        => await _stream.ReadAsync(buffer, ct);

    public void Send(byte[] data) { lock (_gate) _stream.Write(data, 0, data.Length); }

    // El cliente VB6 clásico no tiene por dónde recibir un motivo: TCP a secas. Se ignora.
    public void Close(string reason) { try { _client.Close(); } catch { /* ignore */ } }
}

public sealed class WebSocketTransport : ITransport
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string RemoteEndpoint { get; }

    public WebSocketTransport(WebSocket ws, string remote)
    {
        _ws = ws;
        RemoteEndpoint = "ws:" + remote;
    }

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), ct);
            if (r.MessageType == WebSocketMessageType.Close) return 0;
            total += r.Count;
            if (r.EndOfMessage || total >= buffer.Length) return total;
        }
    }

    public void Send(byte[] data)
    {
        // Mismo problema que en TCP (ver TcpTransport.SendTimeoutMs) y el doble: acá se bloqueaba en DOS
        // lugares sin tope — el semáforo, que se comparte con el hilo de I/O, y el propio SendAsync. Los dos
        // corren en el hilo del game loop, así que un cliente web que deja de leer congelaba el mundo entero.
        // Con tope, el que no drena se lleva su propia conexión abortada y el loop sigue.
        if (!_sendLock.Wait(TcpTransport.SendTimeoutMs)) { try { _ws.Abort(); } catch { } return; }
        try
        {
            using var cts = new CancellationTokenSource(TcpTransport.SendTimeoutMs);
            _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cts.Token).GetAwaiter().GetResult();
        }
        catch { try { _ws.Abort(); } catch { } }   // peer cerrado, o no drenó a tiempo
        finally { _sendLock.Release(); }
    }

    /// <summary>Cierra mandando el MOTIVO en el frame de cierre. El cliente web lo lee en <c>ev.reason</c> y
    /// con eso decide si reconectar: un Abort pelado sólo le deja un 1006 y no puede distinguir "me echaron"
    /// de "se cortó la red", así que se reconectaba SIEMPRE — dos pestañas con el mismo personaje se expulsaban
    /// en bucle infinito, y el /SALIR se deshacía solo. No se espera el close del peer (CloseOutputAsync) ni se
    /// bloquea el game-loop.</summary>
    public void Close(string reason)
    {
        try
        {
            if (_ws.State == WebSocketState.Open) { _ = CerrarConMotivoAsync(reason); return; }
        }
        catch { /* ignore */ }
        try { _ws.Abort(); } catch { /* ignore */ }
    }

    /// <summary>Gracia que se le da al peer para devolver su frame de cierre antes de abortar el socket.</summary>
    private const int GraciaCierreMs = 2_000;

    /// <summary>Manda el close frame y DESPUÉS aborta. El abort al final no es opcional: `CloseOutputAsync` no
    /// espera la respuesta del peer, así que un cliente que no la manda dejaría el socket medio abierto con un
    /// `ReceiveAsync` pendiente para siempre (el timeout de auth no lo cubre: sólo corre antes de loguear).
    /// Con el Abort a secas que había antes esto no pasaba, pero tampoco llegaba el motivo.</summary>
    private async Task CerrarConMotivoAsync(string reason)
    {
        // El motivo del close frame tiene un tope de 123 bytes UTF-8; los nuestros son cortos, pero se recorta por
        // las dudas — pasarse tira ArgumentException y nos dejaría la conexión colgada en vez de cerrada.
        var motivo = reason.Length > 100 ? reason[..100] : reason;
        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, motivo, CancellationToken.None); }
        catch { /* el peer ya se fue: igual hay que abortar */ }
        await Task.Delay(GraciaCierreMs);
        try { _ws.Abort(); } catch { /* ignore */ }
    }
}
