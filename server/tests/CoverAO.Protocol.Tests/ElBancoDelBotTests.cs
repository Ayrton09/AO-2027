using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BANCO DEL BOT (Bots.Banco.cs). Con <c>OroQueSeCaeAlMorir</c> el mid perdía todo lo que llevaba encima al morir en un lugar
/// inseguro: deja en la bóveda lo que no es de las compras y retira cuando una compra lo necesita.
/// </summary>
public class ElBancoDelBotTests
{
    /// <summary>EL PANEL DEL ORO DICE LO QUE HAY EN LA BÓVEDA; la bóveda de ítems lo manda en cero y no se lee. Mientras no lo
    /// vio, el banco es -1 y la plata es sólo la de encima.</summary>
    [Fact]
    public void El_panel_del_oro_dice_lo_que_hay_en_la_boveda()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UpdateGold(300));
        Assert.Equal(-1, s.Banco);
        Assert.Equal(300, s.Plata);

        s.Recibir(ServerMessages.BankInit(goliath: false));
        Assert.Equal(-1, s.Banco);
        Assert.Equal(0, s.VecesQueVioElBanco);

        s.Recibir(ServerMessages.BankInit(goliath: true, 5000, 2));
        Assert.Equal(5000, s.Banco);
        Assert.Equal(5300, s.Plata);
        Assert.Equal(1, s.VecesQueVioElBanco);

        s.MovioAlBanco(-2000);
        Assert.Equal(3000, s.Banco);
    }

    /// <summary>Sin saber lo que hay, un movimiento no inventa un saldo.</summary>
    [Fact]
    public void Sin_haber_visto_el_panel_un_movimiento_no_inventa_el_saldo()
    {
        var s = new SentidosDelBot();
        s.MovioAlBanco(1000);
        Assert.Equal(-1, s.Banco);
    }

    /// <summary>DEPOSITA PASADO EL NOVATO lo que sobra del bolsillo, SÓLO si eso se caería al morir: con la regla apagada o con más
    /// que el tope encima no se cae nada. Y sin saber lo que guardó, pasa a mirar.</summary>
    [Theory]
    [InlineData(15, 50_000, 0, 8_000, 100_000, false)]   // el novato no
    [InlineData(16, 50_000, 0, 8_000, 100_000, true)]
    [InlineData(16, 9_999, 0, 8_000, 100_000, false)]    // sobran 1999
    [InlineData(16, 10_000, 0, 8_000, 100_000, true)]    // sobran 2000
    [InlineData(16, 100_000, 0, 8_000, 100_000, true)]   // con el tope justo se cae todo
    [InlineData(16, 162_370, 0, 8_878, 100_000, false)]  // Milo en la ronda 42: a salvo con más que el tope
    [InlineData(16, 50_000, 0, 8_000, 0, false)]         // la regla apagada
    [InlineData(16, 100, -1, 8_000, 100_000, true)]      // no sabe lo que guardó
    [InlineData(16, 100, -1, 8_000, 0, true)]
    public void Deposita_pasado_el_novato_lo_que_se_caeria(int nivel, int oro, int banco, int bolsillo, int tope, bool deposita)
    {
        Assert.Equal(deposita, Bot.ValeDepositar(nivel, oro, banco, bolsillo, tope));
    }

    /// <summary>LA COMPRA NECESITA LO GUARDADO si hay algo guardado y lo de encima no alcanza para las compras, o si es por gustos.</summary>
    [Theory]
    [InlineData(0, 100, 8_000, true, false)]       // nada guardado
    [InlineData(-1, 100, 8_000, true, false)]      // no sabe
    [InlineData(50_000, 100, 8_000, false, true)]  // le robaron el bolsillo (murió)
    [InlineData(50_000, 9_000, 8_000, false, false)]
    [InlineData(50_000, 9_000, 8_000, true, true)] // el equipo se eligió con lo guardado
    [InlineData(2_928, 34_656, 8_878, true, false)] // Elmo en la ronda 42: lo de encima paga
    public void La_compra_necesita_lo_guardado(int banco, int oro, int bolsillo, bool soloGustos, bool necesita)
    {
        Assert.Equal(necesita, Bot.NecesitaElBanco(banco, oro, bolsillo, soloGustos));
    }

    /// <summary>EL QUE REVIVE SIN NADA MIRA LA BÓVEDA (<see cref="Bot.MiraLaBoveda"/>): Osmar en `confm1`, nivel 23, en Rinkel con 0
    /// encima y 40.328 guardados que no sabía.</summary>
    [Theory]
    [InlineData(23, 0, -1, true, true)]        // Osmar
    [InlineData(23, 0, 40_328, true, false)]   // ya lo sabe: la compra retira
    [InlineData(23, 0, 0, true, false)]        // ya sabe que no hay
    [InlineData(23, 500, -1, true, false)]     // tiene con qué comprar
    [InlineData(23, 0, -1, false, false)]      // sin banquero en el mapa
    [InlineData(10, 0, -1, true, false)]       // novato
    public void Sin_plata_encima_mira_lo_que_guardo(int nivel, int oro, int banco, bool hayBanquero, bool mira)
    {
        Assert.Equal(mira, Bot.MiraLaBoveda(nivel, oro, banco, hayBanquero));
    }

    /// <summary>EL BOLSILLO, con los precios de la data: lo de dos viajes de compras del mid. Tiene que quedar MUY por debajo de lo que
    /// el mid lleva encima (25 mil de mediana en las corridas del 01/10 al 06/10), si no depositar no le saca nada del riesgo.</summary>
    [Fact]
    public void El_bolsillo_del_mid_es_mucho_menos_que_lo_que_lleva()
    {
        var datos = GameData.Load();
        if (datos.Npcs.Count == 0) return;
        int bolsillo = Bot.LoDeLasCompras(
            Bot.ReservaParaLasPociones(0, Bot.PrecioDeLaPocionEn(datos)),
            Bot.ReservaParaLoQueFalta(Bot.CostoPorPuntoEn(datos, esBebida: false), 0, Bot.ComidaQueSeLleva(20))
            + Bot.ReservaParaLoQueFalta(Bot.CostoPorPuntoEn(datos, esBebida: true), 0, Bot.BebidaQueSeLleva(20)),
            5 * Bot.PrecioDelAntidotoEn(datos), 0);
        Assert.InRange(bolsillo, 1_000, 12_000);
    }

    [Fact]
    public void Lo_de_las_compras_es_el_doble_de_llenar_todo_mas_la_herramienta()
    {
        Assert.Equal(2 * (4000 + 400 + 100) + 200, Bot.LoDeLasCompras(4000, 400, 100, 200) - Bot.LoDeLasCompras(0, 0, 0, 0));
    }

    /// <summary>LOS BANQUEROS DE RINKEL ESTÁN ADENTRO DEL BANCO: desde el herrero (19,16) no hay camino, y abrir una de las puertas
    /// del mapa lo da. En la ronda 42 (06/10) Elmo salía "sin camino" y no depositaba nunca en el pueblo donde compran casi todos.</summary>
    [Fact]
    public void A_los_banqueros_de_Rinkel_se_llega_abriendo_una_puerta()
    {
        var datos = GameData.Load();
        if (datos.MapExits.Count == 0 || !datos.MapObjects.TryGetValue(20, out var objetos)) return;
        var mapas = new Navegante(datos);
        bool EsBanquero(int ficha) => datos.Npc((short)ficha) is { NpcType: 4 };
        var puertas = Bot.PuertasQueElMapaTiene(objetos, (19, 16), obj => Bot.PuertaQueSeAbre(datos.Obj((short)obj)));
        Assert.NotEmpty(puertas);
        Assert.Contains(puertas, p => mapas.AbrirlaDaCamino(20, (19, 16), p.Donde, EsBanquero));
    }
}
