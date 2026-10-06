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
}
