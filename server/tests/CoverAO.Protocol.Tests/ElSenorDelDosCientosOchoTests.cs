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
        Assert.False(Bot.LaAmenazaDejaPelearParado(senor, esLoPegado: false, pasosDeLaAmenaza: 3, vidaAhora: 77));
        Assert.False(Bot.LaAmenazaDejaPelearParado(senor, esLoPegado: false, pasosDeLaAmenaza: 1, vidaAhora: 77));
    }

    [Fact]
    public void Lejos_o_si_la_amenaza_es_lo_pegado_o_si_no_lo_mata_de_dos_pelea_como_siempre()
    {
        var senor = GameData.Load().Npc(SenorDeLasSombras)!;
        Assert.True(Bot.LaAmenazaDejaPelearParado(senor, esLoPegado: false, pasosDeLaAmenaza: 4, vidaAhora: 77));
        Assert.True(Bot.LaAmenazaDejaPelearParado(senor, esLoPegado: true, pasosDeLaAmenaza: 1, vidaAhora: 77));
        Assert.True(Bot.LaAmenazaDejaPelearParado(senor, esLoPegado: false, pasosDeLaAmenaza: 2, vidaAhora: senor.MaxHit * 2 + 1));
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
