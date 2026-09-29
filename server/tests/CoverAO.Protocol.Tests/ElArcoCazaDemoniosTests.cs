using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ARCO CAZA DEMONIOS (VB6 CalcularDaño, SistemaCombate.bas:309-316 y :359-361): contra un demonio pega su tirada, contra
/// cualquier otra criatura y contra una persona, 1. El original compara contra la ficha 1, que en esta data es el Aldeano;
/// apuntarlo a los demonios de verdad es una regla de la casa, decisión del autor del 2026-09-27.
/// </summary>
public class ElArcoCazaDemoniosTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_arco_demonios_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();

    private const short Mapa = 15;                   // Desierto Rinkel: pk
    private const short Flecha = 480;
    private const byte X = 50, Y = 50;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    [Fact]
    public void La_tirada_del_arco_segun_contra_quien_tira()
    {
        Assert.Equal((16L, 18L), Combat.TiradaDelArcoCazaDemonios(16, 18, fichaDeLaCriatura: 98));
        Assert.Equal((16L, 18L), Combat.TiradaDelArcoCazaDemonios(16, 18, fichaDeLaCriatura: 572));
        Assert.Equal((1L, 1L), Combat.TiradaDelArcoCazaDemonios(16, 18, fichaDeLaCriatura: 1));      // el Aldeano del original
        Assert.Equal((1L, 1L), Combat.TiradaDelArcoCazaDemonios(16, 18, fichaDeLaCriatura: 501));    // un Lobo
        Assert.Equal((1L, 1L), Combat.TiradaDelArcoCazaDemonios(16, 18, fichaDeLaCriatura: null));   // una persona
    }

    /// <summary>LA DATA: el 666 es el arco, los tres "demonios" lo son, y la ficha 1 del original es el Aldeano.</summary>
    [Fact]
    public void Los_demonios_del_arco_son_los_demonios_de_la_data()
    {
        Assert.Equal("Arco Caza Demonios", _datos.Obj(Combat.ArcoCazaDemonios)?.Name);
        Assert.All(Combat.DemoniosDelArco, f => Assert.Contains("Demonio", _datos.Npc((short)f)?.Name ?? ""));
        Assert.Equal("Aldeano", _datos.Npc(1)?.Name);
    }

    /// <summary>
    /// Y POR EL SERVIDOR: el mismo arquero, con el mismo arco y la misma flecha, a un demonio y a una criatura que no lo es, las
    /// dos sin defensa. Al demonio le saca más del doble SIEMPRE: con el arco de 14 a 18 y la flecha de 4 a 6, el peor golpe
    /// al demonio es (3*18 + 1) = 55 contra el mejor a la otra, (3*7 + 2) = 23, por el mismo modificador de clase. Pedir el
    /// triple fallaba por azar: 55 no llega a 69.
    /// </summary>
    [Fact]
    public async Task Tirando_por_el_servidor_al_demonio_le_saca_mucho_mas()
    {
        int alDemonio = await VidaQueLeSaca(ficha: 98);
        int aOtra = await VidaQueLeSaca(ficha: 900);
        Assert.True(alDemonio > 0 && aOtra > 0, $"no pegó: demonio {alDemonio}, otra {aOtra}");
        Assert.True(alDemonio > aOtra * 2, $"al demonio le sacó {alDemonio} y a la otra {aOtra}");
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    /// <summary>Cuánta vida le saca el primer disparo que acierta.</summary>
    private async Task<int> VidaQueLeSaca(short ficha)
    {
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        string carpeta = Path.Combine(_root, ficha.ToString());
        Directory.CreateDirectory(carpeta);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        using var cts = new CancellationTokenSource();
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            var p = new Player
            {
                CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Class = 10, Level = 40,
                Pos = new WorldPos(Mapa, X, Y), Heading = 2, Hp = 500, MaxHp = 500, MinHit = 1, MaxHit = 2,
                Stamina = 100_000, MaxStamina = 100_000, CombatMode = true,
            };
            p.Skills[GameLimits.SkillProyectiles] = 100;
            p.Inventory[1] = new InventorySlot { ObjIndex = Combat.ArcoCazaDemonios, Amount = 1, Equipped = true };
            p.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = 500, Equipped = true };
            var sesion = new ClientSession(new Transporte(), dispatcher);
            sesion.Player = p;
            p.Connection = sesion;
            world.Add(p);

            var plantilla = new NpcInfo(Index: ficha, Name: "Blanco", Body: 1, Head: 1, Heading: 3, MaxHp: 1_000_000, MinHit: 0,
                MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
            var blanco = new Npc { CharIndex = world.AllocateCharIndex(), Template = plantilla, Hp = 1_000_000, Pos = new WorldPos(Mapa, X + 2, Y) };
            world.AddNpc(blanco);

            // La tirada de acierto puede fallar: se tira hasta que la vida cambia, o un rato.
            for (int i = 0; i < 60 && blanco.Hp == 1_000_000; i++)
            {
                dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, (byte)(X + 2), Y, (byte)GameLimits.SkillProyectiles }));
                await Task.Delay(300);
            }
            return 1_000_000 - blanco.Hp;
        }
        finally
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
    }
}
