using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ARRANQUE AMONTONADO (investigación del 06/10): los bots entran en la plaza de la cuna, y la gente de la plaza vetaba la cuna
/// por "lugar lleno". El veto es para donde se caza: donde nace algo a la vista.
/// </summary>
public class LaPlazaDeLaCunaTests
{
    private const int Cuna = 37;

    [Fact]
    public void En_la_plaza_de_la_cuna_no_nace_nada_a_la_vista_y_en_su_coto_si()
    {
        var datos = GameData.Load();
        if (!datos.MapSpawns.TryGetValue(Cuna, out var nacen)) return;
        bool Hostil(int f) => datos.Npc((short)f) is { Hostile: true };
        Assert.False(Bot.NaceAlgoALaVista(nacen, (57, 74), Hostil), "la plaza, donde entran todos");
        Assert.True(Bot.NaceAlgoALaVista(nacen, (74, 74), Hostil), "el coto de la cuna");
    }

    /// <summary>EL LUGAR SE DEJA POR LLENO sólo sin presa a la vista, con gente, y donde se caza.</summary>
    [Theory]
    [InlineData(false, 17, false, false)]   // la plaza con los 18 recién entrados
    [InlineData(false, 3, true, true)]
    [InlineData(true, 10, true, false)]     // ve una presa: no está lleno
    [InlineData(false, 2, true, false)]
    public void El_lugar_se_deja_por_lleno_solo_donde_se_caza(bool veAlgunaPresa, int personas, bool naceAlgo, bool lleno)
    {
        Assert.Equal(lleno, Bot.ElLugarEstaLleno(veAlgunaPresa, personas, naceAlgo));
    }
}
