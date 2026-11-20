using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Botín de NPCs (campo <c>RandomDrop</c> de NPCs.dat). El parser viejo partía sólo por ',' y trataba el
/// resto como la chance, así que en las entradas MULTIDROP —separadas por '-'— la chance quedaba basura
/// (0) y los 38 NPCs con varios drops, bosses incluidos, no soltaban NADA. Estos tests fijan el formato
/// del VB6 (MODULO_NPCs.bas:1027: N drops = cantidad de '-' + 1, cada uno "objIndex,amount,probTirar")
/// contra la data REAL transcodeada, para que la regresión no pueda volver sin romper la suite.
/// </summary>
public class NpcDropTests
{
    private static GameData CargarDatos(ServerIni? ini = null)
    {
        var data = GameData.Load(ini);
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    /// <summary>El NPC con ese nombre QUE TIENE botín: varios nombres se repiten en NPCs.dat (hay 4 "Lobo",
    /// índices 2/133/501/545) y sólo una de las variantes trae RandomDrop.</summary>
    private static NpcInfo ByNameWithLoot(GameData data, string name) =>
        data.Npcs.Values.First(n => n.Name == name && n.Drops is { Count: > 0 });

    [Fact]
    public void Multidrop_npc_parses_every_entry()
    {
        var data = CargarDatos();
        // Lobo (índice 501): "414,1,100-976,1,0.8-578,1,1.5" -> 3 drops (antes: 0, la chance quedaba en basura).
        var lobo = ByNameWithLoot(data, "Lobo");
        Assert.Equal(3, lobo.Drops!.Count);
        Assert.Equal(414, lobo.Drops[0].ObjIndex);
        Assert.Equal(1, lobo.Drops[0].Amount);
        Assert.Equal(100, lobo.Drops[0].ProbTirar);   // ProbTirar 100 = "dropea siempre" en el VB6
        Assert.Equal(976, lobo.Drops[1].ObjIndex);
        Assert.Equal(578, lobo.Drops[2].ObjIndex);
    }

    [Fact]
    public void Boss_with_seven_drops_parses_all_of_them()
    {
        var data = CargarDatos();
        // "406,1,100-1081,1,10-1081,1,5-1180,1,5-993,1,5-1147,1,5-402,1,5" -> 7 drops.
        var dragon = data.Npcs.Values.First(n => n.Name.Contains("Legendario"));
        Assert.Equal(7, dragon.Drops!.Count);
        Assert.Equal(406, dragon.Drops[0].ObjIndex);
        Assert.All(dragon.Drops, d => Assert.True(d.ObjIndex > 0 && d.Amount > 0));
    }

    [Theory]
    [InlineData("Oso Pardo", 415, 100)]
    [InlineData("Tortuga Gigante", 404, 100)]
    [InlineData("Lobo", 414, 100)]
    [InlineData("Lobo", 976, 0.8)]
    [InlineData("Lobo", 578, 1.5)]
    [InlineData("Murciélago", 737, 0.5)]
    [InlineData("Medusa", 1147, 0.15)]
    [InlineData("Tortuga Gigante", 738, 0.3)]
    public void El_botin_usa_los_porcentajes_de_la_ficha(string criatura, int objeto, double porcentaje)
    {
        var datos = CargarDatos();
        var npc = ByNameWithLoot(datos, criatura);
        var drop = npc.Drops!.Single(d => d.ObjIndex == objeto);
        Assert.Equal(porcentaje, drop.ProbTirar);
        Assert.True(drop.Cae(new AzarFijo((porcentaje - 0.01) / 100), 1));
        if (porcentaje < 100) Assert.False(drop.Cae(new AzarFijo(porcentaje / 100), 1));
    }

    [Theory]
    [InlineData(0, 50, 0, false)]
    [InlineData(0.8, 1, 0.01, false)]
    [InlineData(0.8, 2, 0.01, true)]
    [InlineData(5, 20, 0.999, true)]
    [InlineData(100, 1, 0.999, true)]
    public void El_evento_multiplica_la_probabilidad_del_drop(double porcentaje, int multiplicador, double valor, bool cae)
    {
        Assert.Equal(cae, new NpcDrop(976, 1, porcentaje).Cae(new AzarFijo(valor), multiplicador));
    }

    private sealed class AzarFijo(double valor) : Random
    {
        public override double NextDouble() => valor;
    }

    [Fact]
    public void Los_ajustes_admiten_decimales_y_tiradas_duplicadas()
    {
        var ini = ServerIni.Desde(IniFile.Parse("""
            [DROPSNPC]
            501.976 = 1,5 ; Carisma
            511.1081.2 = 2.75
            511.1081 = 100
            538.415 = 1
            539.404 = 65
            538.738 = 101
            539.738 = NaN
            511.1081.0 = 0
            511.1081.3 = 0
            501.737 = 100
            """));
        var datos = CargarDatos(ini);
        var lobo = datos.Npc(501)!.Drops!;
        Assert.Equal(1.5, lobo.Single(d => d.ObjIndex == 976).ProbTirar);
        Assert.DoesNotContain(lobo, d => d.ObjIndex == 737);
        Assert.Equal([100, 2.75], datos.Npc(511)!.Drops!.Where(d => d.ObjIndex == 1081).Select(d => d.ProbTirar));
        Assert.Equal(1, datos.Npc(538)!.Drops!.Single(d => d.ObjIndex == 415).ProbTirar);
        Assert.Equal(65, datos.Npc(539)!.Drops!.Single(d => d.ObjIndex == 404).ProbTirar);
        Assert.Equal(0.6, datos.Npc(538)!.Drops!.Single(d => d.ObjIndex == 738).ProbTirar);
        Assert.Equal(0.3, datos.Npc(539)!.Drops!.Single(d => d.ObjIndex == 738).ProbTirar);
    }

    [Fact]
    public void Los_ajustes_del_ini_determinan_que_objeto_cae_al_matar()
    {
        var ini = ServerIni.Desde(IniFile.Parse("""
            [INIT]
            DropGlobal=0
            [DROPSNPC]
            501.414=0
            501.976=100
            501.578=0
            """));
        var datos = CargarDatos(ini);
        var mundo = new World();
        var leveling = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        using var db = new Persistence(almacen, leveling);
        var despacho = new PacketDispatcher(mundo, new GameLoop(), db, datos, leveling, serverIni: ini);
        var jugador = new Player
        {
            CharIndex = mundo.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(1, 50, 50),
        };
        var sesion = new ClientSession(new Transporte(), despacho) { Player = jugador };
        var lobo = new Npc
        {
            CharIndex = mundo.AllocateCharIndex(), Pos = new WorldPos(1, 51, 50),
            Template = datos.Npc(501)!, Hp = 0, ExpCount = 0,
        };
        mundo.AddNpc(lobo);

        despacho.NpcKilledByPlayer(sesion, jugador, lobo);

        Assert.Equal((short)976, Assert.Single(mundo.SueloNoFijo()).ObjIndex);
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    [Fact]
    public void Single_drop_npc_still_parses()
    {
        var data = CargarDatos();
        var bat = ByNameWithLoot(data, "Murciélago");   // "737,1,0.5"
        Assert.Single(bat.Drops!);
        Assert.Equal(737, bat.Drops![0].ObjIndex);
        Assert.Equal(1, bat.Drops[0].Amount);
    }

    [Fact]
    public void Every_npc_with_loot_has_at_least_one_usable_drop()
    {
        var data = CargarDatos();
        var conLoot = data.Npcs.Values.Where(n => n.Drops is { Count: > 0 }).ToList();
        // La data trae 102 NPCs con campo RandomDrop, pero 30 lo tienen en "0" = sin botín (el VB6 los gatea
        // igual: `If val(RandomDrop) > 0 Then`). Quedan 72 con loot real, y ninguno con entradas inválidas.
        Assert.True(conLoot.Count >= 70, $"esperaba ~72 NPCs con botín, hay {conLoot.Count}");
        Assert.All(conLoot, n => Assert.All(n.Drops!, d =>
        {
            Assert.True(d.ObjIndex > 0, $"{n.Name}: objIndex inválido");
            Assert.True(d.Amount > 0, $"{n.Name}: amount inválido");
        }));
        // Y los multidrop deben ser varios de verdad (el bug los dejaba en 1 o en 0).
        var multi = conLoot.Where(n => n.Drops!.Count > 1).ToList();
        Assert.True(multi.Count >= 35, $"esperaba ~38 NPCs multidrop, hay {multi.Count}");
    }
}
