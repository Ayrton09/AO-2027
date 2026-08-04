using System.Linq;
using CoverAO.Server.Data;
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
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
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
        var data = LoadOrSkip();
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
        var data = LoadOrSkip();
        // "406,1,100-1081,1,10-1081,1,5-1180,1,5-993,1,5-1147,1,5-402,1,5" -> 7 drops.
        var dragon = data.Npcs.Values.First(n => n.Name.Contains("Legendario"));
        Assert.Equal(7, dragon.Drops!.Count);
        Assert.Equal(406, dragon.Drops[0].ObjIndex);
        Assert.All(dragon.Drops, d => Assert.True(d.ObjIndex > 0 && d.Amount > 0));
    }

    [Fact]
    public void Single_drop_npc_still_parses()
    {
        var data = LoadOrSkip();
        var bat = ByNameWithLoot(data, "Murciélago");   // "737,1,0.5"
        Assert.Single(bat.Drops!);
        Assert.Equal(737, bat.Drops![0].ObjIndex);
        Assert.Equal(1, bat.Drops[0].Amount);
    }

    [Fact]
    public void Every_npc_with_loot_has_at_least_one_usable_drop()
    {
        var data = LoadOrSkip();
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
