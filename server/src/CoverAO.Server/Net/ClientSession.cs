using System.Threading.Channels;
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
    private readonly object _inboundGate = new();
    private byte _inboundKey = Crypto.DefaultKey;
    private volatile bool _closed;
    private int _cerrando;   // 0/1 para el Interlocked de Close: el check-and-set de _closed no es atómico
    // Hardening SEC-3: timeout de autenticación, tope de bytes bufferizados y callback de cierre (libera el
    // slot del ConnectionGuard). 0 = desactivado.
    private readonly long _authTimeoutMs;
    private readonly int _maxInboundBytes;
    private readonly Action? _onClose;
    private volatile bool _authed;

    /// <summary>Una verificación de contraseña (PBKDF2) ya está corriendo para esta sesión. Sirve para dos
    /// cosas: que un cliente no compre N hashes de 25 ms mandando N paquetes de login seguidos, y que dos
    /// verificaciones en vuelo no terminen las dos en un SpawnIntoWorld. Se toma con Interlocked porque el
    /// que la pide es el hilo de la sesión y el que la suelta es el del game loop.</summary>
    private int _verificando;
    public bool TomarVerificacion() => Interlocked.CompareExchange(ref _verificando, 1, 0) == 0;
    public void SoltarVerificacion() => Interlocked.Exchange(ref _verificando, 0);

    public string RemoteEndpoint { get; }
    /// <summary>La sesión ya se cerró. La mira el trabajo diferido (verificación de contraseña) antes de
    /// tocar el mundo: entre que arrancó y que terminó, el cliente se pudo haber ido.</summary>
    public bool Cerrada => _closed;
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
        _ = Task.Run(BucleDeSalidaAsync);   // el escritor arranca con la sesión y muere con ella
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
            // `!_closed` en la condición, y no sólo el token: desde que la salida tiene cola, `Close` NO baja
            // el socket en el acto (lo baja el escritor al terminar de drenar). Sin este chequeo, una sesión
            // ya cerrada seguía leyendo del socket hasta medio segundo más, que es tiempo de sobra para que
            // entre otro paquete.
            while (!token.IsCancellationRequested && !_closed)
            {
                int read = await _transport.ReceiveAsync(chunk, token);
                if (read == 0) break; // peer closed
                UltimaActividadMs = Environment.TickCount64;   // IdleLimit del Server.ini (VB6 Counters.IdleCount)

                // Decrypt this chunk with the current session key, then append. Bajo candado: el hilo del game
                // loop también toca el buffer cuando reanuda lo retenido por una verificación de contraseña.
                int bufferizados;
                lock (_inboundGate)
                {
                    for (int i = 0; i < read; i++)
                        _inbound.Add((byte)(chunk[i] ^ _inboundKey));
                    bufferizados = _inbound.Count;
                }

                // Tope de buffer: un cliente que hace drip-feed de un paquete gigante (o nunca completa uno)
                // no puede inflar la memoria por conexión indefinidamente.
                if (_maxInboundBytes > 0 && bufferizados > _maxInboundBytes) { Close("buffer overflow"); break; }

                DrainInbound();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[{RemoteEndpoint}] recv error: {ex.Message}"); }
        finally { Close("receive loop ended"); }
    }

    /// <summary>Despacha lo que haya en el buffer. Lo llaman DOS hilos —el de la sesión, cuando llegan bytes,
    /// y el del game loop, cuando termina una verificación de contraseña y hay que reanudar—, así que el
    /// buffer va bajo candado y el drenaje es de a uno.</summary>
    private void DrainInbound()
    {
        lock (_inboundGate)
        {
            // NADA se despacha por una sesión ya cerrada. El bucle de abajo lo chequea DESPUÉS de cada
            // paquete, que alcanzaba mientras `Close` bajaba el socket sincrónicamente: lo que llegara después
            // no llegaba. Ahora el socket se baja un instante más tarde, así que la puerta también tiene que
            // estar ACÁ, a la entrada. Es el mismo riesgo que explica el chequeo de más abajo: un paquete
            // atendido después del cierre corre sobre un Player que ya se está guardando.
            if (_closed) return;
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
                if (_closed) { if (offset > 0) _inbound.RemoveRange(0, offset); return; }
                int consumed = reader.Consumed;
                if (consumed <= 0) { Close("zero-length packet"); return; }
                offset += consumed;

                // FRENO POR VERIFICACIÓN EN VUELO. El paquete que acabamos de despachar pudo arrancar una
                // verificación de contraseña, que corre FUERA de este hilo (ver PacketDispatcher.VerificarCuenta).
                // Lo que venga detrás en el mismo segmento TCP tiene que esperarla: un cliente que manda
                // ConnectAccount y LoginExistingChar seguidos vería el segundo llegar antes de que el primero
                // haya terminado. Los bytes se quedan en el buffer y los drena `ReanudarInbound` cuando la
                // verificación cierra — sin esto, el orden que el jugador ve depende de cuánto tarda un hash.
                if (Volatile.Read(ref _verificando) == 1) break;
            }
            if (offset > 0) _inbound.RemoveRange(0, offset);
        }
    }

    /// <summary>Reanuda el despacho de lo que quedó retenido mientras corría una verificación de contraseña.
    ///
    /// La llama el hilo del GAME LOOP (desde la continuación de la verificación), y por eso el drenaje se va a
    /// un hilo del pool en vez de correr acá: `DrainInbound` toma el candado del buffer y despacha paquetes,
    /// dos cosas que pueden tardar lo que tarde un handler. Bloquear el loop esperando ese candado sería
    /// exactamente el problema que este cambio viene a sacar. Además es el hilo donde el despacho corría
    /// siempre: el de la sesión, que también es del pool.</summary>
    public void ReanudarInbound()
    {
        if (_closed) return;
        _ = Task.Run(DrainInbound);
    }

    // ── COLA DE SALIDA ──────────────────────────────────────────────────────────────────────────────────
    // Send hacía `_stream.Write` DIRECTO, y el que lo llama casi siempre es el hilo del game loop (todo
    // broadcast sale de ahí). Un `Write` a un socket TCP bloquea cuando el cliente dejó de leer y se llenan
    // el buffer de envío y la ventana del par: con el `SendTimeoutMs = 3000` que le pusimos, UN cliente que
    // se conecta y no lee más congelaba el mundo entero hasta 3 segundos. No hace falta ni un exploit: basta
    // con dejar el proceso suspendido.
    //
    // Ahora Send sólo encola —una operación sin bloqueo— y un escritor por sesión baja los bytes al socket.
    // El orden se conserva: la cola es FIFO y hay un solo lector. Si el cliente se atrasa tanto que llena la
    // cola, se lo desconecta: descartar paquetes sueltos no es una opción, porque el protocolo AO no tiene
    // reenvío y un hueco deja al cliente con un estado que no existe.
    private readonly Channel<byte[]> _salida = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private volatile string _motivoDeCierre = "";

    public void Send(byte[] packet)
    {
        if (_closed) return;
        // TryWrite NO espera: con la cola llena devuelve false en vez de bloquear al que llama.
        if (!_salida.Writer.TryWrite(packet)) Close("cola de salida saturada (cliente lento)");
    }

    /// <summary>El único que escribe al socket. Corre en su propia tarea, fuera del game loop.</summary>
    private async Task BucleDeSalidaAsync()
    {
        try
        {
            while (await _salida.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_salida.Reader.TryRead(out var paquete))
                {
                    try { _transport.Send(paquete); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[{RemoteEndpoint}] send error: {ex.Message}");
                        Close("send error");
                        return;
                    }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[{RemoteEndpoint}] bucle de salida: {ex.Message}"); }
        // La cola se vació y se cerró: recién ahí baja el socket. Es lo que hace que el mensaje de rechazo
        // —que se manda justo antes de Close— llegue al cliente en vez de perderse con el socket.
        finally { try { _transport.Close(_motivoDeCierre); } catch { /* ignore */ } }
    }

    public void Close(string reason)
    {
        // Interlocked y no "if (_closed) { _closed = true; }": ese check-and-set NO es atómico, y hay dos
        // hilos que pueden entrar a la vez — el callback del timeout de auth (pool) y el finally del receive
        // loop. Los dos pasaban, y OnDisconnect se encolaba DOS veces: doble SaveCharacter y doble Remove.
        if (Interlocked.Exchange(ref _cerrando, 1) != 0) return;
        _closed = true;
        _motivoDeCierre = reason;
        var charIndex = Player?.CharIndex;
        // El socket NO se baja acá: se cierra la cola y el escritor lo baja cuando termina de drenar, así el
        // último mensaje (el motivo del rechazo) llega. El cierre forzado a los 500 ms es la red: si el
        // cliente dejó de leer, el escritor está trabado en un Write y sólo lo destraba cerrarle el socket.
        _salida.Writer.TryComplete();
        _ = Task.Delay(500).ContinueWith(_ => { try { _transport.Close(reason); } catch { /* ignore */ } });
        try { _onClose?.Invoke(); } catch { /* ignore */ }   // libera el slot del ConnectionGuard
        _dispatcher.OnDisconnect(this, reason);
        if (charIndex is short ci)
            Console.WriteLine($"[{RemoteEndpoint}] closed ({reason}) char={ci}");
        else
            Console.WriteLine($"[{RemoteEndpoint}] closed ({reason})");
    }
}
