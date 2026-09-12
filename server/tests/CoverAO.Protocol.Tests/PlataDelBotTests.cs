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

    /// <summary>
    /// DE LA VITRINA SE LLEVA LO MEJOR QUE PUEDA PAGAR, no lo primero ni lo más barato. La vitrina se arma con el
    /// mismo paquete que manda el servidor al abrir el comercio, así que esto pasa por la lectura de verdad.
    /// </summary>
    [Fact]
    public void De_la_vitrina_se_lleva_lo_mejor_que_le_alcanza()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 10, 50f, 100));      // barata y flojita
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 10, 300f, 200));     // la mejor, impagable
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(3, 10, 120f, 300));     // la mejor de las que puede pagar
        int Vale(short obj) => obj switch { 100 => 5, 200 => 40, 300 => 12, _ => 0 };

        var elegida = s.LoMejorDeLaVitrina(Vale, plata: 150);

        Assert.NotNull(elegida);
        Assert.Equal((short)300, elegida!.Value.Obj);
    }

    /// <summary>Y si no hay nada que sirva —o nada que le alcance— no se lleva nada.</summary>
    [Theory]
    [InlineData(40, false)]     // no le alcanza ni la más barata
    [InlineData(60, true)]
    public void De_la_vitrina_no_se_lleva_lo_que_no_puede_pagar(int plata, bool seLleva)
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 10, 50f, 100));
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 10, 300f, 200));

        Assert.Equal(seLleva, s.LoMejorDeLaVitrina(obj => obj == 100 ? 5 : obj == 200 ? 40 : 0, plata) is not null);
    }
}
