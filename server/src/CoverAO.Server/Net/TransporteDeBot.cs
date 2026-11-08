using System.Threading.Channels;
using CoverAO.Protocol;

namespace CoverAO.Server.Net;

/// <summary>
/// El caño de bytes de un BOT: el tercer <see cref="ITransport"/>, al lado del TCP del cliente clásico y del
/// WebSocket del cliente web. No hay socket —el bot vive adentro de este proceso— pero los bytes recorren el
/// MISMO camino que los de un jugador: salen enmascarados con la clave de la sesión, los framea
/// <see cref="ClientSession"/> y los atiende el <see cref="PacketDispatcher"/> con todas sus validaciones,
/// sus intervalos y su anticheat. Eso es lo que asegura que un bot no pueda hacer nada que un jugador no
/// pueda hacer: no es una puerta de atrás al mundo, es un cliente más.
///
/// <para><b>El endpoint es <c>bot:&lt;nombre&gt;:0</c> y la forma importa.</b> <see cref="ConnectionGuard.IpOf"/>
/// corta en el ÚLTIMO <c>:</c> para sacar la "dirección", que es con lo que se cuentan las cuentas nuevas y
/// los intentos de login fallidos. Con <c>bot:Aldo</c> a secas los N bots compartirían la dirección
/// <c>bot</c>, y el cupo de cuentas nuevas por dirección frenaría a todos menos a los primeros — con el
/// mensaje equivocado, además. Con el puerto ficticio al final cada bot es su propia dirección, igual que dos
/// jugadores en dos máquinas.</para>
///
/// <para><b>Por qué el XOR igual se aplica.</b> Enmascarar y desenmascarar con la misma clave, adentro del
/// proceso, es una operación que se anula sola. Se hace lo mismo igual para que la sesión no tenga que
/// enterarse nunca de que del otro lado hay un bot: sin eso haría falta una rama "si es bot" en el camino por
/// donde pasa CADA byte de CADA jugador, que es el peor lugar posible para poner una excepción.</para>
/// </summary>
public sealed class TransporteDeBot : ITransport
{
    /// <summary>Lo que el bot todavía no le entregó a la sesión. Acotada: un bot que decide más rápido de lo
    /// que el servidor atiende espera, en vez de inflar memoria. Ninguna conducta se le acerca (el bot manda
    /// un paquete cada varios cientos de ms), así que llegar al tope significa que algo se trabó.</summary>
    private readonly Channel<byte[]> _haciaElServidor = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>El sobrante de un paquete que no entró entero en el buffer de la sesión. No pasa con ningún
    /// paquete del juego —el más largo que manda un bot son unas decenas de bytes contra un buffer de 4 KB—
    /// pero un transporte que pierde bytes cuando el buffer es chico es un transporte roto.</summary>
    private ReadOnlyMemory<byte> _resto;

    private volatile bool _cerrado;

    public string RemoteEndpoint { get; }

    /// <summary>La clave con la que se enmascara lo que sale del bot. Arranca en la de arranque del protocolo
    /// y pasa a la de la sesión cuando llega el <c>Logged</c>, exactamente como en un cliente de verdad.</summary>
    public volatile byte Clave = Crypto.DefaultKey;

    /// <summary>Cada paquete que el servidor le manda a este bot, ya frameado y completo.</summary>
    public event Action<byte[]>? Recibido;
    internal event Action<short, short, long>? VioAtaqueACriatura;
    internal event Action<short, long>? RecibioAtaqueDeJugador;

    internal void AvisarAtaqueACriatura(short atacante, short criatura, long cuando) => VioAtaqueACriatura?.Invoke(atacante, criatura, cuando);
    internal void AvisarAtaqueDeJugador(short atacante, long cuando) => RecibioAtaqueDeJugador?.Invoke(atacante, cuando);

    /// <summary>La sesión se cerró, con el motivo que dio el servidor.</summary>
    public event Action<string>? Cerrado;

    public TransporteDeBot(string nombre) => RemoteEndpoint = $"bot:{nombre}:0";

    /// <summary>Encola un paquete del bot hacia el servidor. false = la sesión ya está cerrada.</summary>
    public bool Mandar(byte[] paquete) => !_cerrado && _haciaElServidor.Writer.TryWrite(paquete);

    /// <summary>El bot se va por su cuenta. Equivale a que un cliente cierre el socket: la sesión lo ve como
    /// un peer que cerró y hace el mismo cierre ordenado (guardar el personaje, sacarlo del mundo).</summary>
    public void Terminar() => _haciaElServidor.Writer.TryComplete();

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        if (_resto.IsEmpty)
        {
            try { _resto = await _haciaElServidor.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return 0; }   // el bot se fue: para la sesión es el peer cerrando
        }
        int n = Math.Min(_resto.Length, buffer.Length);
        var origen = _resto.Span;
        byte clave = Clave;
        for (int i = 0; i < n; i++) buffer[i] = (byte)(origen[i] ^ clave);
        _resto = _resto[n..];
        return n;
    }

    public void Send(byte[] data)
    {
        // EL PAQUETE LLEGA ENTERO Y DE A UNO: el escritor de la sesión (ClientSession.BucleDeSalidaAsync) hace
        // una llamada por paquete. Por eso acá se puede mirar el id sin parsear el stream, y por eso el bot
        // puede leer la clave de sesión del `Logged` —igual que un cliente— sin necesitar un decodificador de
        // los ciento ocho paquetes que el servidor sabe mandar.
        if (data.Length >= 2 && data[0] == (byte)ServerPacketId.Logged) Clave = data[1];
        Recibido?.Invoke(data);
    }

    public void Close(string reason)
    {
        _cerrado = true;
        _haciaElServidor.Writer.TryComplete();
        Cerrado?.Invoke(reason);
    }
}
