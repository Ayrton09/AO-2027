using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL TIRO SIN MUNICIÓN QUE SIRVA DESEQUIPA, como el original (Protocol.bas:2798-2807): con el arco y sin flechas, el aviso
/// 127 y afuera el arco y la munición; con un arma que no es de proyectil, afuera sólo la munición y sin aviso. Pasa de
/// verdad: gastar la última flecha deja el arco puesto y la ranura de munición vacía.
/// </summary>
public class ElArcoSinFlechasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_arco_sin_flechas_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Mapa = 15;
    private const byte X = 50, Y = 50;
    private const short ArcoSimple = 989, Flecha = 480, Daga = 15;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        foreach (var (cts, corriendo) in _loops)
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    /// <summary>Un arquero en (50,50) del 15 con el arma en la ranura 1 y, si se pide, flechas en la 2. Sin la cadencia del arco,
    /// para poder tirar seguido.</summary>
    private (Player Jugador, ClientSession Sesion, PacketDispatcher Dispatcher) Armar(short arma, int flechas)
    {
        string carpeta = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling,
            serverIni: ServerIni.Desde(IniFile.Parse("[INIT]\r\n[INTERVALOS]\r\nIntervaloFlechasCazadores=0\r\n")));
        var cts = new CancellationTokenSource();
        _loops.Add((cts, loop.RunAsync(cts.Token)));
        var p = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 100, MaxHp = 100, Level = 30, Stamina = 5000, MaxStamina = 5000,
        };
        p.Skills[GameLimits.SkillProyectiles] = 100;
        p.Inventory[1] = new InventorySlot { ObjIndex = arma, Amount = 1, Equipped = true };
        if (flechas > 0) p.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = (short)flechas, Equipped = true };
        var sesion = new ClientSession(new Transporte(), dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        world.Add(p);
        return (p, sesion, dispatcher);
    }

    /// <summary>Un tiro al aire, a dos baldosas.</summary>
    private static async Task Tirar(PacketDispatcher dispatcher, ClientSession sesion)
    {
        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 2), Y, (byte)GameLimits.SkillProyectiles }));
        await Task.Delay(300);
    }

    [Fact]
    public async Task Sin_flechas_el_tiro_saca_el_arco()
    {
        var (p, sesion, dispatcher) = Armar(ArcoSimple, flechas: 0);

        await Tirar(dispatcher, sesion);

        Assert.False(p.Inventory[1].Equipped);
        Assert.Equal(ArcoSimple, p.Inventory[1].ObjIndex);   // se lo saca, no se lo pierde
    }

    [Fact]
    public async Task Gastada_la_ultima_flecha_el_tiro_siguiente_saca_el_arco()
    {
        var (p, sesion, dispatcher) = Armar(ArcoSimple, flechas: 1);

        await Tirar(dispatcher, sesion);
        Assert.True(p.Inventory[2].IsEmpty);
        Assert.True(p.Inventory[1].Equipped);

        await Tirar(dispatcher, sesion);
        Assert.False(p.Inventory[1].Equipped);
    }

    [Fact]
    public async Task Con_un_arma_que_no_es_arco_se_saca_solo_la_municion()
    {
        var (p, sesion, dispatcher) = Armar(Daga, flechas: 50);

        await Tirar(dispatcher, sesion);

        Assert.True(p.Inventory[1].Equipped);
        Assert.False(p.Inventory[2].Equipped);
        Assert.Equal(50, p.Inventory[2].Amount);
    }

    /// <summary>El control: con el arco y flechas se tira, y no se saca nada.</summary>
    [Fact]
    public async Task Con_arco_y_flechas_no_se_saca_nada()
    {
        var (p, sesion, dispatcher) = Armar(ArcoSimple, flechas: 50);

        await Tirar(dispatcher, sesion);

        Assert.True(p.Inventory[1].Equipped);
        Assert.True(p.Inventory[2].Equipped);
        Assert.Equal(49, p.Inventory[2].Amount);
    }
}
