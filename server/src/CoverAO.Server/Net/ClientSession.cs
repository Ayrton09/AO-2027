using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// One connected client. Owns the socket and the inbound rolling buffer, and adapts the socket to
/// <see cref="IConnection"/> for the game layer. This is where transport lives — deliberately
/// separate from the <see cref="Player"/> domain entity (the VB6 code welded the byte queues into
/// the Type User; we don't).
///
/// Framing: the protocol has NO packet length prefix. We accumulate bytes, then repeatedly try to
/// parse one message from the front; a <see cref="NotEnoughDataException"/> means "wait for more".
///
/// Inbound XOR: client-&gt;server bytes are XOR'd with a one-byte key (Crypto). Key starts at 13 and
/// switches to the per-session <c>redundance</c> after we send <c>Logged</c>. The exact bootstrap is
/// marked for parity-harness verification against a real client.
/// </summary>
public sealed class ClientSession : IConnection
{
    private readonly ITransport _transport;
    private readonly PacketDispatcher _dispatcher;
    private readonly List<byte> _inbound = new(1024);
    private byte _inboundKey = Crypto.DefaultKey;
    private volatile bool _closed;
    private int _cerrando;   // 0/1 para el Interlocked de Close: el check-and-set de _closed no es atómico
    // Hardening SEC-3: timeout de autenticación, tope de bytes bufferizados y callback de cierre (libera el
    // slot del ConnectionGuard). 0 = desactivado.
    private readonly long _authTimeoutMs;
    private readonly int _maxInboundBytes;
    private readonly Action? _onClose;
    private volatile bool _authed;

    public string RemoteEndpoint { get; }
    public Player? Player { get; set; }

    public ClientSession(ITransport transport, PacketDispatcher dispatcher,
        long authTimeoutMs = 0, int maxInboundBytes = 0, Action? onClose = null)
    {
        _transport = transport;
        _dispatcher = dispatcher;
        RemoteEndpoint = transport.RemoteEndpoint;
        _authTimeoutMs = authTimeoutMs;
        _maxInboundBytes = maxInboundBytes;
        _onClose = onClose;
    }

    /// <summary>Switch the inbound XOR key once the session key is negotiated (after Logged).</summary>
    public void SetInboundKey(byte key) => _inboundKey = key;

    /// <summary>Marca la sesión como autenticada (entró al mundo): desarma el timeout de auth. Lo llama
    /// SpawnIntoWorld al bindear el Player.</summary>
    public void MarkAuthed() => _authed = true;

    /// <summary><c>Environment.TickCount64</c> del último byte RECIBIDO. El VB6 lleva lo mismo como un contador
    /// de minutos (<c>Counters.IdleCount</c>, reseteado en cada paquete del usuario) para el <c>IdleLimit</c>
    /// del Server.ini. Acá se guarda el instante y el loop hace la resta — más barato y sin deriva.</summary>
    public long UltimaActividadMs { get; private set; } = Environment.TickCount64;

    public async Task ReceiveLoopAsync(CancellationToken token)
    {
        var chunk = new byte[4096];
        // Timeout de autenticación: cierra la conexión si no entró al mundo en N ms (anti slowloris / sockets
        // pre-auth colgados). No usa un CTS ligado al receive para no matar a un cliente que SÍ autenticó y
        // luego quedó legítimamente en silencio; sólo cierra si al vencer el plazo aún no está autenticado.
        if (_authTimeoutMs > 0)
            _ = Task.Delay((int)_authTimeoutMs, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !_authed && !_closed) Close("auth timeout");
            }, TaskScheduler.Default);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await _transport.ReceiveAsync(chunk, token);
                if (read == 0) break; // peer closed
                UltimaActividadMs = Environment.TickCount64;   // IdleLimit del Server.ini (VB6 Counters.IdleCount)

                // Decrypt this chunk with the current session key, then append.
                for (int i = 0; i < read; i++)
                    _inbound.Add((byte)(chunk[i] ^ _inboundKey));

                // Tope de buffer: un cliente que hace drip-feed de un paquete gigante (o nunca completa uno)
                // no puede inflar la memoria por conexión indefinidamente.
                if (_maxInboundBytes > 0 && _inbound.Count > _maxInboundBytes) { Close("buffer overflow"); break; }

                DrainInbound();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[{RemoteEndpoint}] recv error: {ex.Message}"); }
        finally { Close("receive loop ended"); }
    }

    private void DrainInbound()
    {
        // Framing: parseamos varios paquetes sobre UNA sola copia del buffer, avanzando un offset
        // (ByteQueueReader lee de un ReadOnlyMemory, así que Slice es zero-copy), y recortamos el
        // prefijo consumido UNA vez al final. Antes se hacía ToArray()+RemoveRange(0,consumed) POR
        // paquete -> O(n²) en copias: un flood de paquetes de 1 byte saturaba un core (DoS pre-auth).
        if (_inbound.Count == 0) return;
        ReadOnlyMemory<byte> buf = _inbound.ToArray();
        int offset = 0;
        while (offset < buf.Length)
        {
            var reader = new ByteQueueReader(buf.Slice(offset));
            try
            {
                _dispatcher.HandleOne(this, reader);
            }
            catch (NotEnoughDataException)
            {
                break; // incomplete packet; wait for more bytes
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{RemoteEndpoint}] dispatch error: {ex.Message}");
                Close("dispatch error");
                return;
            }

            // Un paquete pudo CERRAR la sesión (DesconectarCuenta -> Close()), que corre sincrónico en este
            // mismo hilo y encola el guardado del personaje + su baja del mundo. Si seguimos despachando lo que
            // venga DETRÁS en el mismo segmento TCP, esos paquetes se procesan DESPUÉS del guardado sobre un
            // Player ya huérfano pero con efecto real (mandar el oro a un mulo y guardarlo intacto en el .chr:
            // impresora de oro). Cortamos acá: la sesión se está cerrando, no hay nada más que atender.
            if (_closed) return;
            int consumed = reader.Consumed;
            if (consumed <= 0) { Close("zero-length packet"); return; }
            offset += consumed;
        }
        if (offset > 0) _inbound.RemoveRange(0, offset);
    }

    public void Send(byte[] packet)
    {
        if (_closed) return;
        try
        {
            _transport.Send(packet);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{RemoteEndpoint}] send error: {ex.Message}");
            Close("send error");
        }
    }

    public void Close(string reason)
    {
        // Interlocked y no "if (_closed) { _closed = true; }": ese check-and-set NO es atómico, y hay dos
        // hilos que pueden entrar a la vez — el callback del timeout de auth (pool) y el finally del receive
        // loop. Los dos pasaban, y OnDisconnect se encolaba DOS veces: doble SaveCharacter y doble Remove.
        if (Interlocked.Exchange(ref _cerrando, 1) != 0) return;
        _closed = true;
        var charIndex = Player?.CharIndex;
        try { _transport.Close(reason); } catch { /* ignore */ }
        try { _onClose?.Invoke(); } catch { /* ignore */ }   // libera el slot del ConnectionGuard
        _dispatcher.OnDisconnect(this, reason);
        if (charIndex is short ci)
            Console.WriteLine($"[{RemoteEndpoint}] closed ({reason}) char={ci}");
        else
            Console.WriteLine($"[{RemoteEndpoint}] closed ({reason})");
    }
}
