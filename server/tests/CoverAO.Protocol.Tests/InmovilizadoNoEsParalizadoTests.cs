using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// UN JUGADOR INMOVILIZADO NO ESTÁ PARALIZADO. El original lleva las dos marcas (<c>flags.Paralizado</c> y
/// <c>flags.Inmovilizado</c>) con un solo contador; las dos impiden caminar, pero el /SALIR mira sólo la parálisis
/// (Protocol.bas:4975). El Inmovilizar de otro jugador prende sólo su marca (modHechizos.bas:161-162); el de una
/// criatura prende las dos (:218-220). Acá el jugador tenía un solo estado y el inmovilizado no podía salir.
/// </summary>
public class InmovilizadoNoEsParalizadoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_inmovilizado_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;                       // Desierto Rinkel: pk
    private const short Paralizar = 9, Inmovilizar = 24;
    private const int StEx = 10;                         // id, charIndex(2), código, vida(4), CharStat(2)

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public InmovilizadoNoEsParalizadoTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player Jugador, ClientSession Sesion) Jugador(string nombre, byte x, short hechizo = 0)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Mapa, x, 50), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, Class = 2,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, CombatMode = true,
            FactionStatus = Player.StatusCriminal,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        if (hechizo != 0) p.Spells[1] = hechizo;
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private async Task Castear(ClientSession sesion, Player blanco)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, blanco.Pos.X, (byte)(blanco.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    private async Task Salir(ClientSession sesion)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.Quit }));
        await Task.Delay(300);
    }

    [Fact]
    public async Task El_inmovilizado_por_otro_jugador_puede_salir()
    {
        var (quieto, sesion) = Jugador("Bruno", 52);
        var (_, sesionMago) = Jugador("Aldo", 50, Inmovilizar);
        await Castear(sesionMago, quieto);
        Assert.True(quieto.EstaSoloInmovilizado(_loop.Tick), "premisa: quedó inmovilizado");
        await Salir(sesion);
        Assert.True(quieto.Saliendo, "inmovilizado sin parálisis, el /SALIR arranca la cuenta");
    }

    [Fact]
    public async Task El_paralizado_no_puede_salir()
    {
        var (quieto, sesion) = Jugador("Bruno", 52);
        var (_, sesionMago) = Jugador("Aldo", 50, Paralizar);
        await Castear(sesionMago, quieto);
        Assert.True(quieto.IsParalyzed(_loop.Tick) && !quieto.EstaSoloInmovilizado(_loop.Tick), "premisa: quedó paralizado");
        await Salir(sesion);
        Assert.False(quieto.Saliendo);
    }

    // ---- las criaturas ------------------------------------------------------------------------------------------

    private static (World, Player, Npc) Escena(GameData d, short hechizo)
    {
        const short Cuna = 37;                               // se permite magia
        var world = new World();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        jugador.Pos = new WorldPos(Cuna, 50, 50);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        world.Add(jugador);
        var ficha = new NpcInfo(Index: 1, Name: "Chamán", Body: 1, Head: 1, Heading: 4, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Spells: new[] { (int)hechizo });
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(Cuna, 51, 50),
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4,
        };
        world.AddNpc(npc);
        return (world, jugador, npc);
    }

    [Fact]
    public void La_criatura_que_inmoviliza_tambien_paraliza()
    {
        var (world, jugador, _) = Escena(_datos, Inmovilizar);
        var sys = new NpcSystem(world, _datos);
        for (int i = 1; i <= 20; i++) sys.Update(i);
        Assert.True(jugador.IsParalyzed(20), "premisa: la criatura le tiró el hechizo");
        Assert.True(jugador.MarcaParalizado && jugador.MarcaInmovilizado);
        Assert.False(jugador.EstaSoloInmovilizado(20));
    }

    [Fact]
    public void La_criatura_paraliza_al_que_solo_estaba_inmovilizado()
    {
        var (world, jugador, _) = Escena(_datos, Paralizar);
        jugador.ParalyzedUntilTick = 1_000_000;
        jugador.MarcaParalizado = false;
        jugador.MarcaInmovilizado = true;
        var sys = new NpcSystem(world, _datos);
        for (int i = 1; i <= 20; i++) sys.Update(i);
        Assert.True(jugador.MarcaParalizado, "al que sólo estaba inmovilizado la criatura lo paraliza");
        Assert.True(jugador.MarcaInmovilizado, "y la inmovilización queda puesta");
        Assert.True(jugador.ParalyzedUntilTick < 1_000_000, "y el contador se reinicia");
    }

    [Fact]
    public void El_estado_del_jugador_lleva_las_dos_marcas()
    {
        var t = new Player { CharIndex = 1, Name = "Bruno", Account = "bruno", Hp = 10, MaxHp = 10, ParalyzedUntilTick = 100 };
        var quien = new Player { CharIndex = 2, Name = "Aldo", Account = "aldo" };
        int Bits() => ServerMessages.CharMsgStatus(t, quien, 50)[StEx] & 3;
        Assert.Equal(1, Bits());                                                   // paralizado
        t.MarcaParalizado = false; t.MarcaInmovilizado = true;
        Assert.Equal(2, Bits());                                                   // sólo inmovilizado
        t.MarcaParalizado = true;
        Assert.Equal(3, Bits());                                                   // las dos (la de una criatura)
        t.ParalyzedUntilTick = 0;
        Assert.Equal(0, Bits());
    }
}
