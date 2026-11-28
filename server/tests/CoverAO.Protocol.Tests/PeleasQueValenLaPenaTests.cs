using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS PELEAS CON GENTE QUE NO SALE A BUSCAR (28/11). En las rondas 199 a 206, 155 de 157 muertes fueron entre bots: 60 en el Río Otoren
/// (98), al lado de la entrada a Rinkel, y 19 de un nivel 15 o 16 contra uno que le sacaba de 7 a 13 niveles. No sale a buscar pelea en
/// la puerta de un pueblo, cargado, ni contra uno mucho más débil. Si le pegan, se defiende igual.
/// </summary>
public sealed class PeleasQueValenLaPenaTests
{
    [Fact]
    public void No_sale_a_buscar_al_que_tiene_siete_niveles_menos()
    {
        Assert.True(Bot.EsDemasiadoDebil(18, 25));
        Assert.False(Bot.EsDemasiadoDebil(19, 25));
        Assert.False(Bot.EsDemasiadoDebil(null, 25));
        Assert.False(Bot.EsDemasiadoDebil(SentidosDelBot.NivelEscondido, 25));
    }

    [Fact]
    public void Cargado_no_sale_a_buscar_pelea()
    {
        Assert.False(Bot.VaCargado(19));
        Assert.True(Bot.VaCargado(20));
    }

    [Fact]
    public void Las_muertes_del_Rio_Otoren_son_en_la_puerta_de_Rinkel_y_el_coto_del_18_no()
    {
        var d = GameData.Load();
        List<(int X, int Y)> Puertas(int m) => d.MapExits.Where(e => e.Key.Map == m && !d.IsPkMap(e.Value.DestMap))
            .Select(e => (e.Key.X, e.Key.Y)).ToList();
        var delRio = Puertas(98);
        Assert.Contains(d.MapExits, e => e.Key.Map == 98 && e.Value.DestMap == 20);
        foreach (var (x, y) in new[] { (89, 69), (87, 69), (89, 65), (86, 76), (90, 58), (90, 49) })
            Assert.True(Bot.DistanciaALaPuertaDeUnPueblo(delRio, ((byte)x, (byte)y)) <= Bot.RadioDeLaPuerta, $"({x},{y})");
        Assert.True(Bot.DistanciaALaPuertaDeUnPueblo(Puertas(18), (77, 62)) > Bot.RadioDeLaPuerta);
    }
}
