using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS HECHIZOS QUE NO SALEN. El bot clickea la cabeza de la criatura (<see cref="Bot.ClicEnLaCabeza"/>) y el servidor sólo
/// elige blanco adentro del área jugable (LookatTile e InMapBounds, desde la fila <see cref="GameLimits.MinYBorder"/>): a la
/// criatura parada en la primera fila el clic no la elige, el hechizo se corta sin cobrar, y el bot —que tomaba por lanzado lo
/// que había mandado— no le pegaba nunca con el arma. Visto en `pelea_a`: un Nigromante siete minutos con el maná clavado,
/// tomando pociones. Acá se mira con el servidor de verdad que el bot sabe lo mismo que el servidor.
/// </summary>
public class LosHechizosQueNoSalenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_nosalen_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15, MisilMagico = 92;
    private const byte X = 50;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public LosHechizosQueNoSalenTests()
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

    /// <summary>Un mago con todo de sobra y en modo combate, que sabe el Misil Mágico (Target 3: jugador y si no, criatura).</summary>
    private (Player Mago, ClientSession Sesion) Mago(byte x, byte y)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Kael", Account = "kael",
            Pos = new WorldPos(Mapa, x, y), Heading = 2, Hp = 50, MaxHp = 100, Level = 30, Class = 2, CombatMode = true,
            FactionStatus = Player.StatusCriminal, Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = MisilMagico;
        _world.Add(p);
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        return (p, sesion);
    }

    private Npc Criatura(byte x, byte y)
    {
        var ficha = new NpcInfo(Index: 904, Name: "Bicho de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1,
            MaxHit: 1, Def: 0, GiveExp: 10, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 500, Pos = new WorldPos(Mapa, x, y), Hostile = true };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task Castear(ClientSession sesion, byte x, byte y)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, x, y, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);
    }

    /// <summary>En la primera fila: el clic en la cabeza cae afuera, no sale nada y no se cobra. Y el bot lo sabe.</summary>
    [Fact]
    public async Task A_la_criatura_de_la_primera_fila_el_clic_en_la_cabeza_no_la_elige()
    {
        Assert.Equal(3, _datos.Spell(MisilMagico)!.Target);   // premisa: el que corta en silencio cuando no hay blanco
        byte fila = (byte)GameLimits.MinYBorder;
        var (mago, sesion) = Mago(X, (byte)(fila + 1));
        var bicho = Criatura(X, fila);

        await Castear(sesion, X, Bot.ClicEnLaCabeza(fila));

        Assert.Equal(500, bicho.Hp);
        Assert.Equal(5000, mago.Mana);
        Assert.False(Bot.ElClicLoElige(fila));
    }

    /// <summary>Una fila más abajo, el mismo clic la elige, le pega y cobra. Y el bot también lo sabe.</summary>
    [Fact]
    public async Task Una_fila_mas_abajo_el_clic_en_la_cabeza_la_elige()
    {
        byte fila = (byte)(GameLimits.MinYBorder + 1);
        var (mago, sesion) = Mago(X + 1, fila);
        var bicho = Criatura(X, fila);

        await Castear(sesion, X, Bot.ClicEnLaCabeza(fila));

        Assert.True(bicho.Hp < 500);
        Assert.True(mago.Mana < 5000);
        Assert.True(Bot.ElClicLoElige(fila));
    }

    /// <summary>Y cualquier otro motivo: tres hechizos seguidos sin cobrar y pasa al arma.</summary>
    [Fact]
    public void Tres_hechizos_seguidos_sin_cobrar_y_pasa_al_arma()
    {
        Assert.False(Bot.DejaDeLanzarle(2));
        Assert.True(Bot.DejaDeLanzarle(3));
    }
}
