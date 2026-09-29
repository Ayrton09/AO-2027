using System.Linq;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Subcomandos GM no implementados. Antes el <c>default</c> de HandleGMCommands cerraba la conexión, así
/// que un GM que tipeaba cualquiera de los ~81 comandos sin portar (/MOD, /CI, /CARCEL…) quedaba EXPULSADO
/// — una regresión, porque el VB6 no tiene <c>Case Else</c> y simplemente los ignora. Ahora se consumen los
/// argumentos con la tabla de layouts (los subcomandos NO llevan length-prefix: sin consumirlos el stream
/// se desincroniza) y se responde "no implementado" sin cortar. Estos tests fijan las dos mitades.
/// </summary>
public class GmCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_gm_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;
    private readonly ClientSession _session;

    private sealed class FakeTransport : ITransport
    {
        public string RemoteEndpoint => "test";
        public volatile bool Closed;   // lo escribe el escritor de salida, que es otro hilo
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { Closed = true; CloseReason = reason; }
        public string? CloseReason { get; private set; }
    }

    private readonly FakeTransport _transport = new();

    public GmCommandTests()
    {
        Directory.CreateDirectory(_root);
        var data = new GameData();
        var world = new World();
        var leveling = new Leveling(data.LevelExp);
        var db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(world, new GameLoop(), db, data, leveling);
        _session = new ClientSession(_transport, _dispatcher);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Manda GMCommands(92) + subcomando + args y devuelve cuántos bytes consumió el handler.</summary>
    private int Consumed(byte sub, byte[] args)
    {
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(sub);
        var bytes = w.ToArray().Concat(args).ToArray();
        var reader = new ByteQueueReader(bytes);
        _dispatcher.HandleOne(_session, reader);
        return reader.Consumed;
    }

    [Fact]
    public void Unimplemented_gm_command_does_not_close_the_connection()
    {
        // /CI (CreateItem, 53) = Integer + Integer. No está implementado todavía.
        var args = new ByteQueueWriter().WriteInteger(100).WriteInteger(5).ToArray();
        Consumed(53, args);
        Assert.False(_transport.Closed, "un comando GM no implementado NO debe cerrar la conexión");
    }

    [Theory]
    // (subcomando, args, bytes de args) — layouts sacados de los Handle* del VB6.
    [InlineData((byte)53, "ii")]     // CreateItem
    [InlineData((byte)17, "ssb")]    // Jail  /CARCEL
    [InlineData((byte)20, "sbss")]   // EditChar /MOD
    [InlineData((byte)29, "s")]      // Execute /EJECUTAR
    [InlineData((byte)37, "bbbb")]   // IPToNick
    [InlineData((byte)86, "sl")]     // DARPUN
    [InlineData((byte)91, "bb")]     // CuentaRegresiva
    public void Unimplemented_gm_command_consumes_exactly_its_arguments(byte sub, string layout)
    {
        var w = new ByteQueueWriter();
        foreach (char t in layout)
        {
            switch (t)
            {
                case 's': w.WriteAsciiString("x"); break;
                case 'i': w.WriteInteger(7); break;
                case 'l': w.WriteLong(7); break;
                default: w.WriteByte(1); break;
            }
        }
        var args = w.ToArray();
        int consumed = Consumed(sub, args);
        // 1 (id) + 1 (subcomando) + args: si consumiera de menos, los bytes sobrantes desincronizan el stream.
        Assert.Equal(2 + args.Length, consumed);
        Assert.False(_transport.Closed);
    }

    [Fact]
    public void Truly_unknown_selector_still_closes_to_protect_the_stream()
    {
        Consumed(200, Array.Empty<byte>());   // 200 no existe en eGMCommands (1..91)
        // Lo que protege al servidor es que la SESIÓN se cierre en el acto: desde ese instante no se despacha
        // ni un paquete más ni se manda un byte más. Antes esto se medía por el socket, que se bajaba
        // sincrónicamente adentro de Close; desde que la salida tiene cola, el socket lo baja el escritor
        // cuando termina de drenar —así el último mensaje llega al cliente en vez de perderse con él—, o el
        // cierre forzado si el cliente dejó de leer. O sea que el socket cae un instante después, y por eso
        // se comprueban las DOS cosas: la sesión ya, el socket enseguida.
        Assert.True(_session.Cerrada, "un selector desconocido sí debe cerrar: no sabemos cuántos bytes ocupa");
        // EL PLAZO SALE DE LA CONSTANTE DEL PRODUCTO, no de un número lindo. El socket lo baja el escritor
        // cuando drena, y si el escritor no llegó a correr lo baja la red de `Close`, que espera
        // `SendTimeoutMs`. Con 5 s cableados quedaban 2 s de margen sobre ese plazo, y corriendo los 588
        // tests en paralelo el pool a veces no le da un hilo a la continuación en ese rato: el test daba rojo
        // en la suite y verde suelto (3 de 3). No es lentitud del producto — es un plazo mal elegido.
        EsperaDelEscritor.Hasta(() => _transport.Closed);
        Assert.True(_transport.Closed, "y el socket tiene que terminar bajando");
    }
}
