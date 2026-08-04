using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Fija que el fixture de test es OPT-IN: en producción el mapa 15 es un mapa real del mundo (sólo su fauna del
/// .csm), y sólo con TestFixture.Populate aparecen el comerciante, el banquero, el entrenador, la arena, las
/// zonas seguras y las paredes de test. Sin este candado, un jugador que camina hasta el mapa 15 (339 tile-exits
/// llevan a él desde el mapa 13) se topa con Rugura, Merlin y los NPCs de test parados en medio del mundo.
/// </summary>
public class TestFixtureTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    private static bool EsDeFixture(Npc n) =>
        n.IsMerchant || n.IsBanker || n.IsReviver || n.IsWorkbench || n.IsTrainer
        || n.FactionEnlist != 0 || n.FactionConvert != 0;

    [Fact]
    public void Produccion_deja_el_mapa_15_como_mundo_real()
    {
        // Sin TestFixture.Populate: el mapa 15 tiene EXACTAMENTE su fauna del .csm y ningún NPC de test.
        var data = LoadOrSkip();
        var world = new World();
        var sys = new NpcSystem(world, data);
        WorldPopulator.SpawnNpcs(sys, data);   // el mundo real, sin el fixture

        var enMapa15 = world.Npcs.Where(n => n.Pos.Map == 15).ToList();
        Assert.Equal(data.MapSpawns[15].Count, enMapa15.Count);         // sólo las criaturas del .csm (28)
        Assert.DoesNotContain(enMapa15, EsDeFixture);                   // ni comerciante, ni banquero, ni entrenador…

        // Y ninguna de las modificaciones de terreno del fixture: el mapa 15 es abierto y sin zonas propias.
        Assert.False(data.IsBlockedTile(15, 50, 45), "la pared de fixture (50,45) no debe existir en producción");
        Assert.False(data.IsBlockedTile(15, 55, 35), "el corral de fixture no debe existir en producción");
        Assert.False(data.IsArena(15, 41, 41), "la arena de fixture no debe existir en producción");
        Assert.False(data.IsSafeZone(15, 47, 51), "la zona segura de fixture no debe existir en producción");
    }

    [Fact]
    public void El_fixture_puebla_el_mapa_15_con_los_NPCs_de_test()
    {
        // Con TestFixture.Populate: aparecen los NPCs de servicio y las modificaciones de terreno que los flows
        // esperan, ADEMÁS de la fauna real del .csm (el fixture se puebla antes, el mundo real después).
        var data = LoadOrSkip();
        var world = new World();
        var sys = new NpcSystem(world, data);
        TestFixture.Populate(world, sys, data);
        WorldPopulator.SpawnNpcs(sys, data);

        var enMapa15 = world.Npcs.Where(n => n.Pos.Map == 15).ToList();
        Assert.True(enMapa15.Count > data.MapSpawns[15].Count, "el fixture agrega NPCs por encima de la fauna del .csm");

        // Los servicios que los flows doble-clickean por coordenada.
        Assert.Contains(enMapa15, n => n.IsBanker);
        Assert.Contains(enMapa15, n => n.IsReviver);
        Assert.Contains(enMapa15, n => n.IsTrainer);
        Assert.Contains(enMapa15, n => n.IsMerchant);
        Assert.Equal(4, enMapa15.Count(n => n.IsWorkbench));                        // herrero/carpintero/sastre/alquimista
        Assert.All(new byte[] { 4, 5, 6 }, f => Assert.Contains(enMapa15, n => n.FactionEnlist == f)); // Caos/Armada/Milicia

        // Y el terreno de test que ejercen blocking_flow / arena / safezone.
        Assert.True(data.IsBlockedTile(15, 50, 45));
        Assert.True(data.IsArena(15, 41, 41));
        Assert.True(data.IsSafeZone(15, 47, 51));
    }
}
