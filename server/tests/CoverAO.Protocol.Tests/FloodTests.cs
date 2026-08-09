using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL TECHO DE PAQUETES, ejercido por el camino de verdad: bytes que entran por el transporte y salen por el
/// bucle de recepción.
///
/// <para><c>LimiteDeCadenciaTests</c> prueba el cubo de fichas, y seguiría verde aunque nadie lo llamara nunca.
/// Eso ya pasó en este repo —tests que verifican que el CAMPO se carga y no que el comportamiento ocurra—, así
/// que acá se mide lo único que le importa al servidor: si un cliente que inunda TERMINA DESCONECTADO, y si uno
/// que juega normal NO.</para>
/// </summary>
public class FloodTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_flood_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;

    // Un paquete de UN byte que el despachador conoce y que sin personaje no hace nada: lo que interesa es que
    // pase por la puerta de cadencia, no lo que haga después.
    private static readonly byte Inofensivo = (byte)ClientPacketId.RequestSkills;

    public FloodTests()
    {
        Directory.CreateDirectory(_root);
        var data = new GameData();
        var world = new World();
        var leveling = new Leveling(data.LevelExp);
        var db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(world, new GameLoop(world), db, data, leveling);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Entrega <paramref name="total"/> paquetes en tandas de <paramref name="porTanda"/>, esperando
    /// <paramref name="msEntreTandas"/> entre una y otra, y después se queda callado (un cliente conectado que
    /// no manda nada). Los bytes van cifrados con la clave inicial, igual que los de la red.
    ///
    /// <para>La señal que miran los tests es <see cref="Entregados"/>, no el motivo del cierre. Motivo llega
    /// TARDE a propósito: desde que la salida tiene cola, <c>Close</c> no baja el socket en el acto —lo baja el
    /// escritor cuando termina de drenar—, así que leerlo apenas vuelve el bucle es una carrera. La primera
    /// versión de estos tests hacía eso y el MISMO sabotaje daba rojo una corrida y verde la siguiente.</para>
    ///
    /// <para>Cuántos paquetes alcanzó a pedir el bucle, en cambio, es sincrónico y no miente: el bucle lee
    /// <c>!_closed</c> en su condición, así que en cuanto la sesión se cierra deja de pedir. Si cortó, el
    /// contador queda en la tanda donde cortó; si no cortó, llega al total.</para></summary>
    private sealed class TransporteQueEntrega : ITransport
    {
        private readonly int _total, _porTanda, _msEntreTandas;
        private int _entregados;
        public string RemoteEndpoint => "cliente";
        public string? Motivo;
        public int Entregados => _entregados;
        public int Total => _total;

        public TransporteQueEntrega(int total, int porTanda, int msEntreTandas = 0)
        { _total = total; _porTanda = porTanda; _msEntreTandas = msEntreTandas; }

        public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
        {
            if (_entregados >= _total) return 0;   // el cliente se fue; el bucle termina y el test lee el contador
            if (_msEntreTandas > 0 && _entregados > 0) await Task.Delay(_msEntreTandas, ct);
            int n = Math.Min(Math.Min(_porTanda, buffer.Length), _total - _entregados);
            for (int i = 0; i < n; i++) buffer[i] = (byte)(Inofensivo ^ Crypto.DefaultKey);
            _entregados += n;
            return n;
        }

        public void Send(byte[] data) { }
        public void Close(string reason) { Motivo ??= reason; }   // el PRIMER motivo es el que explica el cierre
    }

    [Fact]
    public async Task Al_que_inunda_se_lo_desconecta()
    {
        // 4000 paquetes de un byte en tandas de 1000: lo que sale de un `for` en la consola del navegador.
        // Con el techo andando, la sesión se cae DENTRO de la primera tanda y el bucle no pide las otras tres.
        var t = new TransporteQueEntrega(total: 4000, porTanda: 1000);
        var sesion = new ClientSession(t, _dispatcher, paquetesPorSegundo: 50, rafagaDePaquetes: 200);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sesion.ReceiveLoopAsync(cts.Token);

        Assert.Equal(1000, t.Entregados);   // cortó en la primera tanda; sin techo habría pedido las 4000
        // El motivo llega por el escritor, más tarde que el corte. Se lo espera con paciencia acotada porque es
        // lo que le dice al operador POR QUÉ se le fue un jugador; el corte en sí ya quedó probado arriba.
        EsperaDelEscritor.Hasta(() => t.Motivo is not null);
        Assert.Contains("flood", t.Motivo ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task El_que_juega_normal_no_se_cae()
    {
        // 3 paquetes cada 100 ms = 30 por segundo, con el techo en 50. Lo único que cambia respecto del caso de
        // arriba es el RITMO, que es justo lo que la puerta tiene que distinguir.
        //
        // El total (90) queda por debajo de la ráfaga (200) a propósito: aunque la máquina esté cargada y las
        // esperas se aplasten a cero, no alcanza para vaciar el cubo. Así el verde no depende del reloj.
        var t = new TransporteQueEntrega(total: 90, porTanda: 3, msEntreTandas: 100);
        var sesion = new ClientSession(t, _dispatcher, paquetesPorSegundo: 50, rafagaDePaquetes: 200);
        // EL PLAZO ES UNA RED CONTRA EL CUELGUE, no algo que se esté afirmando. Son 30 tandas de 100 ms = 3 s
        // de trabajo nominal, y suelto tarda exactamente eso. Estaba en 15 s y **se agotó corriendo los 588
        // tests en paralelo**: con el pool saturado cada `Task.Delay(100)` se estira, el token cancela el
        // bucle a mitad y `Entregados` queda corto — o sea, un rojo por el reloj de la máquina y no por el
        // techo de cadencia, que es lo único que este test juzga.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await sesion.ReceiveLoopAsync(cts.Token);   // termina al quedarse callado, no por el techo

        Assert.Equal(t.Total, t.Entregados);   // el bucle siguió pidiendo hasta el final: nunca se lo cortó
    }

    [Fact]
    public async Task Sin_techo_configurado_no_se_desconecta_a_nadie()
    {
        // El default del constructor es 0 = sin límite. Importa que sea así: el techo lo enciende el Server.ini,
        // y un default que desconecta rompería a cualquiera que instancie una sesión sin pensarlo.
        var t = new TransporteQueEntrega(total: 4000, porTanda: 1000);
        var sesion = new ClientSession(t, _dispatcher);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sesion.ReceiveLoopAsync(cts.Token);

        Assert.Equal(t.Total, t.Entregados);   // las 4000 entraron: sin techo no se echa a nadie
    }
}
