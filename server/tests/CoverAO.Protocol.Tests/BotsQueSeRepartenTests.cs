using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS BOTS SE REPARTEN (el tester, 27/11): en un mapa cazan los que caben por las presas que nacen ahí (<c>CupoDeCazadores</c>), y el
/// hogar se muda a la ciudad con menos bots (<c>CiudadParaMudarse</c>).
/// </summary>
public sealed class BotsQueSeRepartenTests
{
    [Fact]
    public void El_cupo_es_uno_cada_seis_presas_y_al_menos_uno()
    {
        Assert.Equal(1, Bot.CupoDeCazadores(0));
        Assert.Equal(1, Bot.CupoDeCazadores(11));
        Assert.Equal(2, Bot.CupoDeCazadores(12));
        Assert.Equal(4, Bot.CupoDeCazadores(24));
    }

    [Fact]
    public void A_otro_mapa_no_va_si_ya_esta_el_cupo_y_en_el_suyo_se_quedan_los_mas_fuertes()
    {
        var dos = new[] { (20, "Ana"), (18, "Beto") };
        Assert.True(Bot.SobranCazadores(esElSuyo: false, cupo: 2, dos, 25, "Zoe"));
        Assert.False(Bot.SobranCazadores(false, 3, dos, 15, "Zoe"));
        // En el suyo: con cupo 2, el de nivel 19 tiene uno que le gana y se queda; el de 17, dos, y se va.
        Assert.False(Bot.SobranCazadores(true, 2, dos, 19, "Zoe"));
        Assert.True(Bot.SobranCazadores(true, 2, dos, 17, "Zoe"));
        // Entre iguales decide el nombre, así no se van los dos.
        Assert.True(Bot.SobranCazadores(true, 1, new[] { (20, "Ana") }, 20, "Beto"));
        Assert.False(Bot.SobranCazadores(true, 1, new[] { (20, "Beto") }, 20, "Ana"));
    }

    [Fact]
    public void Se_muda_a_la_ciudad_con_menos_bots_si_en_la_suya_hay_tres_mas()
    {
        var candidatas = new[] { (20, (byte)5), (218, (byte)9), (112, (byte)12) };
        var bots = new Dictionary<byte, int> { [5] = 10, [9] = 7, [12] = 2 };
        Assert.Equal((112, (byte)12), Bot.CiudadParaMudarse(candidatas, 5, bots, _ => false, "Ana"));
        // Con dos de diferencia se queda.
        Assert.Null(Bot.CiudadParaMudarse(candidatas, 5, new Dictionary<byte, int> { [5] = 4, [9] = 2, [12] = 2 }, _ => false, "Ana"));
        // El que ya está en la que menos tiene no se mueve.
        Assert.Null(Bot.CiudadParaMudarse(candidatas, 12, bots, _ => false, "Ana"));
        // Empatadas, la de su misma tierra.
        var empate = new Dictionary<byte, int> { [5] = 9 };
        Assert.Equal((218, (byte)9), Bot.CiudadParaMudarse(candidatas, 5, empate, m => m == 218, "Ana"));
    }
}
