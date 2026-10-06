using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ORO QUE SE CAE AL MORIR (regla de la casa <c>OroQueSeCaeAlMorir</c>, pedido del tester, 06/10): con el tope o menos
/// encima, todo al piso; con más, nada; en 0, nunca. Es la regla del AO clásico que el CoverAO de 2021 dejó comentada.
/// </summary>
public class OroQueSeCaeAlMorirTests
{
    private static readonly GameData Datos = GameData.Load();

    private static ServerIni Ini(string texto = "") => ServerIni.Desde(IniFile.Parse("[INIT]\n" + texto), "test.ini");

    /// <summary>El mapa 15 en (50,50) es inseguro y fuera de zona segura: ahí se cae el inventario.</summary>
    private static (World, Player) Muerto(int oro, int tope, byte mapa = 15)
    {
        var world = new World { OroQueSeCaeAlMorir = tope, OroPorMonton = 10_000 };
        var p = new Player { CharIndex = 1, Name = "Probador", Account = "cuenta_test", Pos = new WorldPos(mapa, 50, 50), Gold = oro };
        world.Add(p);
        PacketDispatcher.KillPlayer(world, Datos, p, "test");
        return (world, p);
    }

    private static long OroEnElPiso(World world, short mapa) =>
        world.GroundOnMap(mapa).Where(g => g.ObjIndex == GameLimits.ObjOro).Sum(g => (long)g.Amount);

    [Fact]
    public void La_premisa_el_mapa_de_la_prueba_es_inseguro()
    {
        if (Datos.ItemCount == 0) return;
        Assert.True(Datos.IsPkMap(15));
        Assert.False(Datos.IsSafeZone(15, 50, 50));
    }

    [Fact]
    public void Con_el_tope_o_menos_se_cae_todo_en_montones()
    {
        if (Datos.ItemCount == 0) return;
        var (world, p) = Muerto(35_000, 100_000);
        Assert.Equal(0, p.Gold);
        Assert.Equal(35_000, OroEnElPiso(world, 15));
        Assert.Equal(4, world.GroundOnMap(15).Count(g => g.ObjIndex == GameLimits.ObjOro));   // 10.000 x 3 + 5.000
    }

    [Fact]
    public void Justo_el_tope_tambien_se_cae()
    {
        if (Datos.ItemCount == 0) return;
        var (_, p) = Muerto(100_000, 100_000);
        Assert.Equal(0, p.Gold);
    }

    [Fact]
    public void Con_mas_del_tope_no_se_cae_nada()
    {
        if (Datos.ItemCount == 0) return;
        var (world, p) = Muerto(100_001, 100_000);
        Assert.Equal(100_001, p.Gold);
        Assert.Equal(0, OroEnElPiso(world, 15));
    }

    [Fact]
    public void Apagada_no_se_cae_nada()
    {
        if (Datos.ItemCount == 0) return;
        var (_, p) = Muerto(500, 0);
        Assert.Equal(500, p.Gold);
    }

    [Fact]
    public void En_un_mapa_seguro_no_se_cae()
    {
        if (Datos.ItemCount == 0) return;
        short seguro = (short)Enumerable.Range(1, 400).First(m => Datos.MapExists((short)m) && !Datos.IsPkMap((short)m));
        var (_, p) = Muerto(500, 100_000, (byte)seguro);
        Assert.Equal(500, p.Gold);
    }

    [Fact]
    public void El_ini_la_trae_apagada_y_se_elige_el_tope()
    {
        Assert.Equal(0, Ini().OroQueSeCaeAlMorir);
        Assert.Equal(100_000, Ini("OroQueSeCaeAlMorir=100000\n").OroQueSeCaeAlMorir);
        Assert.Equal(0, Ini("OroQueSeCaeAlMorir=-5\n").OroQueSeCaeAlMorir);
        Assert.Contains("OroQueSeCaeAlMorir", ServerIni.ClavesConocidas);
    }
}
