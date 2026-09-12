using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA MOCHILA DEL BOT SIGUE A LA DEL SERVIDOR CUANDO COME Y TOMA.
///
/// <para>Contra el despachador de verdad: el personaje usa la botella con el mismo paquete que manda un cliente, y
/// lo que el servidor contesta lo lee <see cref="SentidosDelBot"/> por el transporte del bot. Si el servidor
/// cambiara la forma de avisar lo consumido, esto se pone rojo antes de que un bot vuelva a contar botellas que ya
/// se tomó.</para>
/// </summary>
public class MochilaDelBotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_mochila_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public MochilaDelBotTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(new Dictionary<int, long>());
        _dispatcher = new PacketDispatcher(new World(), _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        // Usar un objeto corre adentro del loop, así que el loop tiene que girar de verdad.
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    [Theory]
    [InlineData((short)43)]   // Botella de Agua
    [InlineData((short)26)]   // Pollo
    public async Task Lo_que_consume_en_el_servidor_se_descuenta_en_su_mochila(short obj)
    {
        if (_datos.Obj(obj) is not ({ IsDrink: true } or { IsFood: true })) return;   // sin la data del juego no hay qué probar

        var transporte = new TransporteDeBot("Aldo");
        var sentidos = new SentidosDelBot();
        transporte.Recibido += sentidos.Recibir;
        var sesion = new ClientSession(transporte, _dispatcher);
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50), Hunger = 0, Thirst = 0 };
        p.Inventory[2] = new InventorySlot { ObjIndex = obj, Amount = 4 };
        sesion.Player = p;
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, obj, 4, false, 0f, 1));   // lo que sabe desde el login

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.UseItem).WriteByte(2).ToArray()));

        for (int i = 0; i < 250 && sentidos.CuantoTiene(o => o == obj) == 4; i++) await Task.Delay(20);
        Assert.Equal(3, p.Inventory[2].Amount);   // el servidor la consumió
        Assert.Equal(3, sentidos.CuantoTiene(o => o == obj));
        sesion.Close("fin del test");
    }

    /// <summary>
    /// EL ÚLTIMO NIVEL DE NOVATO YA CUENTA COMO "ESTOY POR PERDERLO". Al pasar de
    /// <see cref="GameLimits.NewbieMaxLevel"/> el servidor le saca de una todo lo de novato —siete de los ocho ítems
    /// del kit de nacimiento—, así que el bot tiene que salir a comprar lo suyo ANTES, mientras todavía tiene con qué.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]     // último nivel de novato: ya va de compras
    [InlineData(16, true)]     // ya se lo sacaron
    [InlineData(40, true)]
    public void En_el_ultimo_nivel_de_novato_ya_se_compra_lo_suyo(int nivel, bool seprepara)
    {
        Assert.Equal(seprepara, Bot.PorDejarDeSerNovato(nivel));
    }

    /// <summary>Y lo que el servidor le va a sacar es REAL: el kit de nacimiento de una clase cualquiera está lleno de
    /// objetos que el port trata como de novato, que es lo que hace falta para que la regla de arriba sirva.</summary>
    [Fact]
    public void El_kit_de_nacimiento_esta_lleno_de_cosas_que_se_pierden_al_dejar_de_ser_novato()
    {
        var datos = GameData.Load();
        var kit = datos.NewbieKit(2);
        if (kit.Count == 0) return;                       // sin la data del juego no hay kit que mirar

        int deNovato = kit.Count(it => datos.Obj((short)it.ObjIndex) is { IsNewbie: true });

        Assert.True(deNovato >= 4, $"el kit trae {deNovato} objetos de novato: la regla no tendría nada que proteger");
    }

    /// <summary>
    /// LO QUE COMPRÓ SE LO PONE. Con un escudo peor puesto y uno mejor en la mochila, el bot se cambia: si no, juntar
    /// plata para comprarse equipo mejor no sirve de nada. Es el caso de Ciro en `seguir1`, con el Escudo de Hierro +2
    /// guardado y el Escudo de Tortuga encima.
    /// </summary>
    [Fact]
    public void Se_cambia_por_lo_mejor_que_tiene_en_la_mochila()
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[]
        {
            (4, 2, true),      // el Escudo de Tortuga, puesto
            (12, 8, false),    // el Escudo de Hierro +2, guardado
        };

        Assert.Equal((byte)12, Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>
    /// Y NO SE CAMBIA POR ALGO PEOR NI POR ALGO IGUAL: el servidor saca lo puesto al poner lo nuevo, así que con dos
    /// que valen lo mismo el bot se pasaría la vida cambiándoselas.
    /// </summary>
    [Theory]
    [InlineData(8, 2, false)]    // lo de la mochila es peor
    [InlineData(8, 8, false)]    // valen lo mismo
    [InlineData(2, 8, true)]     // lo de la mochila es mejor
    public void Solo_se_cambia_por_algo_mejor(int puesto, int guardado, bool seCambia)
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[] { (4, puesto, true), (12, guardado, false) };

        Assert.Equal(seCambia ? (byte?)12 : null, Bot.QueSePoneDeEso(mochila));
    }

    /// <summary>Sin nada puesto se pone lo mejor que tenga, que es como empieza cualquiera.</summary>
    [Fact]
    public void Sin_nada_puesto_se_pone_lo_mejor_de_la_mochila()
    {
        var mochila = new (byte Ranura, int Valor, bool Puesto)[] { (3, 1, false), (7, 5, false), (9, 3, false) };

        Assert.Equal((byte)7, Bot.QueSePoneDeEso(mochila));
        Assert.Null(Bot.QueSePoneDeEso(System.Array.Empty<(byte, int, bool)>()));
    }

    /// <summary>
    /// NO SE COMPRA NI SE PONE LO QUE SU RAZA TIENE PROHIBIDO. Contra la data de verdad: la Armadura de Cazador (360)
    /// lleva <c>RazasProhibidas=2,5</c> —números del obj.dat de Imperium, que <c>RaceForbidden</c> traduce— y es la que
    /// Ciro, enano, se compró en `seguir3` y no se pudo poner, quedándose con defensa 3 en vez de 24.
    /// </summary>
    [Fact]
    public void No_se_pone_la_armadura_que_su_raza_tiene_prohibida()
    {
        var datos = GameData.Load();
        if (datos.Obj(360) is not { } cazador) return;                 // sin la data del juego no hay nada que mirar
        byte prohibida = 0, permitida = 0;
        for (byte r = 1; r <= 6; r++)
            if (cazador.RaceForbidden(r)) prohibida = prohibida == 0 ? r : prohibida;
            else permitida = permitida == 0 ? r : permitida;
        Assert.True(prohibida != 0 && permitida != 0,
            "la Armadura de Cazador dejó de tener razas prohibidas: el test no prueba nada");

        Assert.False(Bot.PuedeUsarlo(cazador, clase: 8, raza: prohibida, nivel: 40));
        Assert.True(Bot.PuedeUsarlo(cazador, clase: 8, raza: permitida, nivel: 40));
    }

    /// <summary>Y las otras dos condiciones siguen valiendo: la clase prohibida y el nivel mínimo.</summary>
    [Fact]
    public void Tampoco_lo_que_su_clase_prohibe_ni_lo_que_le_queda_grande()
    {
        var datos = GameData.Load();
        if (datos.Obj(1000) is not { } escudo) return;                  // Escudo de Hierro +2
        byte prohibida = 0, permitida = 0;
        for (byte c = 1; c <= 18; c++)
            if (escudo.ClassForbidden(c)) prohibida = prohibida == 0 ? c : prohibida;
            else permitida = permitida == 0 ? c : permitida;
        Assert.True(prohibida != 0 && permitida != 0, "el Escudo de Hierro +2 dejó de tener clases prohibidas");

        Assert.False(Bot.PuedeUsarlo(escudo, clase: prohibida, raza: 1, nivel: 40));
        Assert.True(Bot.PuedeUsarlo(escudo, clase: permitida, raza: 1, nivel: 40));
        Assert.False(Bot.PuedeUsarlo(null, clase: permitida, raza: 1, nivel: 40));
    }
}
