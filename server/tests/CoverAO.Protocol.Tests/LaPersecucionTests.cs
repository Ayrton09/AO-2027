using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// NI PERSIGUIENDO SE METE A LA VISTA DE LO PELIGROSO (30/09). En `cadena2` Elmo, nivel 13 con 102 de vida, persiguió en el 208 una
/// presa hacia donde nace el Señor de las sombras —que lo tenía a catorce baldosas y camina hacia lo que ve— y lo mató de dos golpes.
/// Ver <see cref="Bot.EntraALaVistaDelPeligro"/>.
/// </summary>
public class LaPersecucionTests
{
    private static readonly (byte X, byte Y)[] SenorDeLasSombras = { (50, 46) };

    [Fact]
    public void El_paso_que_entra_a_la_vista_de_lo_peligroso_no_se_da()
    {
        Assert.False(Bot.EntraALaVistaDelPeligro((40, 48), (41, 48), SenorDeLasSombras), "a nueve todavía no lo ve");
        Assert.True(Bot.EntraALaVistaDelPeligro((41, 48), (42, 48), SenorDeLasSombras), "a ocho ya lo ve");
    }

    [Fact]
    public void Adentro_de_su_vista_o_sin_nada_peligroso_se_persigue_como_siempre()
    {
        Assert.False(Bot.EntraALaVistaDelPeligro((45, 48), (46, 48), SenorDeLasSombras), "ya lo veía");
        Assert.False(Bot.EntraALaVistaDelPeligro((42, 48), (41, 48), SenorDeLasSombras), "alejarse vale siempre");
        Assert.False(Bot.EntraALaVistaDelPeligro((41, 48), (42, 48), Array.Empty<(byte, byte)>()));
    }
}
