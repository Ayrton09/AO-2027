using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT RODEADO (investigación del 06/10, ronda 46): con las cuatro baldosas pegadas ocupadas, `SalirDelMapaAsync` probaba cada salida
/// chocando con cada criatura; Bruma y Gonzo murieron así en el 195, rodeados de Lobos.
/// </summary>
public class ElBotRodeadoTests
{
    private static readonly (byte X, byte Y) Aca = (18, 8);

    private static IReadOnlySet<(byte X, byte Y)> Ocupadas(params (byte, byte)[] b) => new HashSet<(byte X, byte Y)>(b);

    [Fact]
    public void Las_vecinas_tapadas_son_las_cuatro_pegadas_que_estan_ocupadas()
    {
        var tapadas = Bot.VecinasTapadas(Aca, Ocupadas((18, 7), (19, 8), (19, 9), (25, 25)));
        Assert.Equal(new[] { ((byte)18, (byte)7), ((byte)19, (byte)8) }, tapadas.OrderBy(t => t).ToArray());
    }

    [Fact]
    public void Con_las_cuatro_ocupadas_esta_rodeado()
    {
        var tapadas = Bot.VecinasTapadas(Aca, Ocupadas((18, 7), (19, 8), (18, 9), (17, 8)));
        Assert.True(Bot.EstaRodeado(Aca, tapadas, (_, _) => true));
    }

    [Fact]
    public void Con_un_hueco_no_esta_rodeado()
    {
        var tapadas = Bot.VecinasTapadas(Aca, Ocupadas((18, 7), (19, 8), (18, 9)));
        Assert.False(Bot.EstaRodeado(Aca, tapadas, (_, _) => true));
    }

    /// <summary>Contra la pared también: la pared no es salida.</summary>
    [Fact]
    public void Contra_la_pared_con_las_otras_ocupadas_esta_rodeado()
    {
        var tapadas = Bot.VecinasTapadas(Aca, Ocupadas((19, 8), (18, 9), (17, 8)));
        Assert.True(Bot.EstaRodeado(Aca, tapadas, (x, y) => !(x == 18 && y == 7)));
    }

    /// <summary>Sin nadie pegado no está rodeado, aunque sea un rincón: el viaje lo resuelve como siempre.</summary>
    [Fact]
    public void Sin_nadie_pegado_no_esta_rodeado()
    {
        Assert.False(Bot.EstaRodeado(Aca, Bot.VecinasTapadas(Aca, Ocupadas()), (_, _) => false));
    }

    /// <summary>CONTRA UNA SOLA, LA CUENTA DE SIEMPRE; contra cuatro Lobos con 84 de vida (Gonzo en la ronda 46), no gana aunque contra
    /// uno sí.</summary>
    [Fact]
    public void Contra_cuatro_Lobos_no_gana_aunque_contra_uno_si()
    {
        var datos = CoverAO.Server.Data.GameData.Load();
        if (datos.Npc(501) is not { } lobo) return;
        int? golpes = 3;
        Assert.Equal(Bot.LaPeleaSeGana(golpes, 84, lobo), Bot.LaPeleaSeGanaContraTodas(new[] { (golpes, lobo) }, 84));
        Assert.True(Bot.LaPeleaSeGana(golpes, 84, lobo), "la premisa: contra un Lobo gana");
        Assert.False(Bot.LaPeleaSeGanaContraTodas(new[] { (golpes, lobo), (golpes, lobo), (golpes, lobo), (golpes, lobo) }, 84));
    }

    [Fact]
    public void Sin_medida_cuenta_los_dos_golpes_de_todas()
    {
        var datos = CoverAO.Server.Data.GameData.Load();
        if (datos.Npc(501) is not { } lobo) return;
        int? sinMedida = null;
        Assert.True(Bot.LaPeleaSeGanaContraTodas(new[] { (sinMedida, lobo), (sinMedida, lobo) }, 4 * lobo.MaxHit + 1));
        Assert.False(Bot.LaPeleaSeGanaContraTodas(new[] { (sinMedida, lobo), (sinMedida, lobo) }, 4 * lobo.MaxHit));
    }

    /// <summary>RODEADO Y CON LA VIDA BAJA, PRIMERO LA POCIÓN; sin rodear, con la vida alta o sin pociones, pega.</summary>
    [Theory]
    [InlineData(true, 0.4, 50, true)]
    [InlineData(false, 0.4, 50, false)]
    [InlineData(true, 0.9, 50, false)]
    [InlineData(true, 0.4, 0, false)]
    public void Rodeado_con_la_vida_baja_primero_la_pocion(bool rodeado, double salud, int pociones, bool seCura)
    {
        Assert.Equal(seCura, Bot.RodeadoSeCuraPrimero(rodeado, salud, pociones));
    }
}
