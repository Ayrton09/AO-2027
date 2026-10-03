using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL COTO QUE NO DA NADA SE DEJA COMO EL LLENO: ver <see cref="Bot.OtraBusquedaSinGanar"/>. Medido en `early9` y `early10` (30/09):
/// los bots de nivel 3 a 7 que salían de la cuna llena se quedaban en Lindos, con cuatro Águilas para dos o tres, hasta 28 minutos sin
/// subir de nivel.
/// </summary>
public class ElCotoQueNoDaNadaTests
{
    [Fact]
    public void La_cuenta_sigue_sin_ganar_y_vuelve_a_uno_si_gana_sube_o_cambia_de_mapa()
    {
        var c = Bot.OtraBusquedaSinGanar(default, 62, 100, 3);
        Assert.Equal(1, c.Seguidas);
        for (int i = 2; i <= Bot.BusquedasSinGanar; i++)
        {
            c = Bot.OtraBusquedaSinGanar(c, 62, 100, 3);
            Assert.Equal(i, c.Seguidas);
        }
        Assert.Equal(1, Bot.OtraBusquedaSinGanar(c, 62, 190, 3).Seguidas);   // le pegó a algo
        Assert.Equal(1, Bot.OtraBusquedaSinGanar(c, 62, 100, 4).Seguidas);   // subió, con la experiencia igual
        Assert.Equal(1, Bot.OtraBusquedaSinGanar(c, 37, 100, 3).Seguidas);   // se mudó
    }

    /// <summary>La primera búsqueda cuenta una aunque el mapa, la experiencia y el nivel coincidan con el vacío de arranque.</summary>
    [Fact]
    public void La_primera_busqueda_cuenta_una()
    {
        Assert.Equal(1, Bot.OtraBusquedaSinGanar(default, 0, 0, 0).Seguidas);
    }

    /// <summary>
    /// Y CONTRA LOS MAPAS DE VERDAD. Con la puerta del teleport cerrada, como arranca el servidor, Lindos es un pozo para el que llega
    /// desde la cuna: desde las Águilas a pie sólo se llega al 63 y al 64. Un nivel 3 parado ahí vuelve a elegir Lindos en cada
    /// búsqueda, y dejado como lleno también: eso es estar encerrado, y sale con la runa. Con la puerta abierta —la de (50,85), que el
    /// servidor deja así hasta que alguien la cierra— sale caminando a otro coto.
    /// </summary>
    [Fact]
    public void Con_la_puerta_cerrada_Lindos_es_un_pozo_y_el_que_no_gana_esta_encerrado()
    {
        if (Lindos(puertaAbierta: false) is not { } cerrada || Lindos(puertaAbierta: true) is not { } abierta) return;
        Assert.True(cerrada(new() { [37] = long.MaxValue })?.Mapa == 62, "con la cuna llena no eligió Lindos: el test no prueba nada");
        var mismo = cerrada(new() { [37] = long.MaxValue, [62] = long.MaxValue });
        Assert.Equal(62, mismo?.Mapa);
        Assert.True(Bot.LoUnicoEsQuedarseSinGanar(mismo?.Mapa, 62, noDaNada: true));
        Assert.False(Bot.LoUnicoEsQuedarseSinGanar(mismo?.Mapa, 62, noDaNada: false), "lleno de gente, sin otro coto, se queda");

        var otro = abierta(new() { [37] = long.MaxValue, [62] = long.MaxValue });
        Assert.True(otro is { } o && o.Mapa is not 62 and not 37, $"con la puerta abierta eligió el {otro?.Mapa}");
        Assert.False(Bot.LoUnicoEsQuedarseSinGanar(otro?.Mapa, 62, noDaNada: true));
    }

    /// <summary>La búsqueda de coto de un nivel 3 parado en (50,30) de Lindos, entre las Águilas, con los mapas llenos que se le den.</summary>
    private static Func<Dictionary<int, long>, (int Mapa, byte X, byte Y, int Saltos)?>? Lindos(bool puertaAbierta)
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0 || d.Npc(532) is null) return null;
        if (puertaAbierta)
            foreach (var (x, y) in new[] { (50, 85), (49, 85) }) d.SetTileBlocked(62, x, y, false);
        var mapas = new Navegante(d);
        const int vida = 30, nivel = 3;
        Func<int, bool> presa = f => Bot.EsPresaPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, bool> peligroso = f => Bot.EsPeligrosoPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, int> rinde = f => presa(f) ? (int)(d.Npc(f)?.GiveExp ?? 0) : 0;
        Func<int, bool> paga = f => d.Npc(f)?.GiveGld > 0;
        IEnumerable<int> Nacen(int m) => d.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>();
        Func<int, bool> seCaza = m => Bot.SeCazaAhi(d.IsPkMap(m), Nacen(m), peligroso);
        return llenos => Bot.CotoSinVolverAlLleno(mapas, 62, (50, 30), presa, paga, rinde, null,
                                                  m => seCaza(m) && !Bot.SigueLleno(llenos, m, 0), m => Bot.SigueLleno(llenos, m, 0),
                                                  m => Bot.SirveParaIrseDelLleno(Bot.SigueLleno(llenos, m, 0), Nacen(m), peligroso),
                                                  null, out _, out _);
    }

    /// <summary>
    /// DE UN POZO SE SALE CON LA RUNA RECIÉN COMPRADO O A LOS 90 S SIN GANAR (ver <see cref="Bot.SaleDelPozo"/>), y la cuenta del
    /// tiempo sin ganar vuelve a cero con cualquier cambio de experiencia, de nivel o de mapa (<see cref="Bot.LaUltimaGanancia"/>).
    /// </summary>
    [Fact]
    public void Del_pozo_se_sale_recien_comprado_o_a_los_noventa_segundos_sin_ganar()
    {
        Assert.True(Bot.SaleDelPozo(recienCompro: true, sinGanarMs: 0));
        Assert.False(Bot.SaleDelPozo(recienCompro: false, sinGanarMs: Bot.PozoSinGanarMs - 1));
        Assert.True(Bot.SaleDelPozo(recienCompro: false, sinGanarMs: Bot.PozoSinGanarMs));

        var g = Bot.LaUltimaGanancia(default, 62, 100, 5, 1_000);
        Assert.Equal(1_000, g.Desde);
        Assert.Equal(1_000, Bot.LaUltimaGanancia(g, 62, 100, 5, 50_000).Desde);
        Assert.Equal(50_000, Bot.LaUltimaGanancia(g, 62, 118, 5, 50_000).Desde);
        Assert.Equal(50_000, Bot.LaUltimaGanancia(g, 37, 100, 5, 50_000).Desde);
    }
}
