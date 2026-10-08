using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS MUERTES DEL 208 (ver <c>Bot.LaAmenazaDejaPelearParado</c> y el botín en <c>LevantarBotinAsync</c>): acorralado con algo que lo mata
/// de dos golpes a tres baldosas, no se queda peleando con lo que tiene pegado; y no se camina a un botín que lo deja al lado de algo
/// peligroso (<c>Bot.LoArrimaAlPeligro</c>).
/// </summary>
public class ElSenorDelDosCientosOchoTests
{
    private const short SenorDeLasSombras = 534;

    [Fact]
    public void Con_el_Senor_a_tres_baldosas_no_pelea_parado_con_el_Escorpion()
    {
        var senor = GameData.Load().Npc(SenorDeLasSombras);
        Assert.NotNull(senor);
        Assert.True(senor!.MaxHit * 2 >= 77, "el Señor tiene que matar de dos golpes a Elmo con 77 de vida");
        // Elmo en (57,51), el Escorpión pegado en (57,52), y el Señor, que lo mata de dos, a tres baldosas (60,51) o a una (58,51).
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)60, (byte)51) }, (57, 52)));
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)58, (byte)51) }, (57, 52)));
    }

    [Fact]
    public void Lo_pegado_que_tambien_mata_de_dos_no_tapa_al_Senor_a_tres_baldosas()
    {
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)57, (byte)52), ((byte)60, (byte)51) }, (57, 52)));
    }

    [Fact]
    public void Lejos_o_si_lo_que_mata_de_dos_es_lo_pegado_pelea_como_siempre()
    {
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)61, (byte)51) }, (57, 52)), "el Señor a cuatro baldosas");
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)57, (byte)52) }, (57, 52)), "lo pegado, solo");
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), Array.Empty<(byte, byte)>(), (57, 52)), "nada que mate de dos a tiro");
    }

    [Fact]
    public void El_botin_al_lado_del_peligro_no_se_va_a_buscar()
    {
        var peligro = new[] { ((byte)55, (byte)50) };
        Assert.True(Bot.LoArrimaAlPeligro((60, 51), (57, 51), peligro));    // como Elmo en early147
        Assert.False(Bot.LoArrimaAlPeligro((60, 51), (62, 51), peligro));   // alejándose
        Assert.False(Bot.LoArrimaAlPeligro((60, 51), (57, 51), Array.Empty<(byte, byte)>()));
    }
}
