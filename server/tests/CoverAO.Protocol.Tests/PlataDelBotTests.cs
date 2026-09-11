using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PLATA DE LO ESENCIAL. En `sano1` un Gladiador pasó doce minutos sin volver a la tienda con 215 monedas y la comida y
/// la bebida en cero, porque venía de comprar pociones; y una Guerrera se gastó todo en pollos y siguió sin agua.
/// </summary>
public class PlataDelBotTests
{
    /// <summary>EL DESCANSO LARGO SE CORTA SI FALTA LO ESENCIAL y hay con qué comprarlo. Con todo lleno descansa, sin plata no
    /// hay a qué ir, y el descanso corto de un viaje fallido se respeta.</summary>
    [Theory]
    [InlineData(true, 215, 0, 0, true)]
    [InlineData(true, 215, 525, 150, false)]
    [InlineData(true, 2, 0, 0, false)]
    [InlineData(false, 215, 0, 0, false)]
    public void El_descanso_largo_se_corta_si_falta_lo_esencial(bool descansoLargo, int oro, int comida, int bebida, bool salta)
    {
        Assert.Equal(salta, Bot.SaltaElDescanso(descansoLargo, oro, comida, bebida));
    }

    /// <summary>LO QUE FALTA PARA EL MÍNIMO SE GUARDA, y lo que ya está por encima no.</summary>
    [Fact]
    public void Lo_que_falta_para_el_minimo_se_guarda()
    {
        Assert.Equal(10, Bot.ReservaParaLoQueFalta(0.1, 0, 100));
        Assert.Equal(7, Bot.ReservaParaLoQueFalta(0.1, 30, 100));
        Assert.Equal(0, Bot.ReservaParaLoQueFalta(0.1, 150, 100));
    }

    /// <summary>EL PRECIO SALE DE LA DATA: la bebida más barata que venden los mercaderes es el agua (3 monedas, 30 puntos) y la
    /// comida anda por el estilo.</summary>
    [Fact]
    public void El_costo_por_punto_sale_de_lo_que_venden_los_mercaderes()
    {
        var datos = GameData.Load();
        if (datos.Npcs.Count == 0) return;
        Assert.InRange(Bot.CostoPorPuntoEn(datos, esBebida: true), 0.001, 0.1);
        Assert.InRange(Bot.CostoPorPuntoEn(datos, esBebida: false), 0.001, 0.2);
    }
}
