using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS PIELES Y EL SASTRE (pedido del tester, 25/11, punto 1): los demás publican las pieles en lotes de 100 en vez de guardarlas en el
/// banco, el Sastre se las compra, y con la skill de las túnicas y los gorros fabrica una prenda por vez para la subasta.
/// </summary>
public sealed class PielesYSastreTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private const short Vestimentas = 31, TunicaRm5 = 1091, GorroAncestral = 1202, LoboInvernal = 1145;

    [Fact]
    public void Las_tres_pieles_del_sastre()
    {
        Assert.True(Bot.EsPielDeSastre(GameLimits.SkinWolfObj));
        Assert.True(Bot.EsPielDeSastre(GameLimits.SkinBrownBearObj));
        Assert.True(Bot.EsPielDeSastre(GameLimits.SkinPolarBearObj));
        Assert.False(Bot.EsPielDeSastre(LoboInvernal));
    }

    [Fact]
    public void Las_pieles_del_sastre_no_van_al_banco()
    {
        var d = Datos.Value;
        var mochila = new List<(byte, short, short, bool)> { (1, (short)GameLimits.SkinWolfObj, 80, false), (2, LoboInvernal, 3, false) };
        var guarda = Bot.LoQueGuarda(d, mochila, trabajaPieles: false, tiraConArco: false, _ => false);
        Assert.DoesNotContain(guarda, g => g.Obj == GameLimits.SkinWolfObj);
        Assert.Contains(guarda, g => g.Obj == LoboInvernal);
    }

    /// <summary>Las Vestimentas valen 50 y se las compra el mercader; las túnicas y los gorros valen 0 y van a la subasta.</summary>
    [Fact]
    public void Las_vestimentas_al_mercader_y_lo_demas_a_la_subasta()
    {
        var d = Datos.Value;
        Assert.False(Bot.EsDeSastreriaParaVender(d, Vestimentas));
        Assert.True(Bot.EsDeSastreriaParaVender(d, TunicaRm5));
        Assert.True(Bot.EsDeSastreriaParaVender(d, GorroAncestral));
    }

    /// <summary>La Túnica de Absorción Mágica (RM +5) lleva 300 de lobo (10), 120 de oso pardo (15) y 180 de oso polar (20): 8.400, más
    /// la mitad.</summary>
    [Fact]
    public void El_precio_sale_de_las_pieles()
    {
        var d = Datos.Value;
        Assert.Equal(12_600, Bot.ValorDeSastreria(d, TunicaRm5));
        Assert.Equal(12_600, Bot.ValorDeMercado(d, TunicaRm5));
        Assert.Equal(50, Bot.ValorDeMercado(d, Vestimentas));
    }

    [Fact]
    public void La_prenda_que_sabe_y_no_tiene_y_la_que_lleva_menos_pieles()
    {
        var d = Datos.Value;
        Assert.Null(Bot.ElegirPrendaDeSastreria(d, 65, _ => 0));   // con 65 sólo sabe las Vestimentas
        short? primera = Bot.ElegirPrendaDeSastreria(d, 100, _ => 0);
        Assert.Equal(GorroAncestral, primera);                     // 155 pieles, la que menos lleva
        Assert.NotEqual(GorroAncestral, Bot.ElegirPrendaDeSastreria(d, 100, o => o == GorroAncestral ? 1 : 0));
    }

    [Fact]
    public void El_sastre_le_compra_el_lote_de_pieles()
    {
        var (_, final) = Bot.PrecioDeSubasta(10 * Bot.PielesPorLote);
        Assert.Equal(650, final);
        var lote = new ServerMessages.FilaDeSubasta(1, (short)GameLimits.SkinWolfObj, (short)Bot.PielesPorLote, 0, final, "Gonzo", 3_600, "");
        Assert.True(Bot.LeConvieneElLote(lote, 10, "Petra", 5_000, Bot.PielesPorLote));
    }
}
