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

    /// <summary>
    /// LO QUE SOBRA PARA GUSTOS es lo que queda después de la comida, la bebida, LA HERRAMIENTA y el colchón. La
    /// herramienta del oficio es lo único que le da un ingreso propio: gastarse esas monedas en un arma lo deja sin
    /// con qué trabajar y tarda una hora de caza en juntarlas de nuevo.
    /// </summary>
    [Theory]
    // oro | lo reservado para comer y tomar | lo que cuesta la herramienta que le falta | colchón | sobra
    [InlineData(500, 50, 200, 4, 246)]
    [InlineData(250, 0, 200, 4, 46)]      // el Minero con lo justo: no le alcanza para el arma, y está bien
    [InlineData(250, 0, 0, 4, 246)]       // ya tiene el piquete: puede mirar armas
    [InlineData(100, 120, 0, 4, 0)]       // ni para comer: no sobra nada
    public void Lo_que_sobra_para_gustos_deja_afuera_la_herramienta(int oro, int panza, int herramienta, int colchon, int sobra)
    {
        Assert.Equal(sobra, Bot.PlataQueSobra(oro, panza, herramienta, colchon));
    }

    /// <summary>
    /// EL VIAJE POR GUSTOS ES SÓLO CUANDO NO FALTA NADA ESENCIAL. Si falta cualquiera de las cuatro cosas de vivir
    /// —comer, tomar, con qué curarse, la herramienta del oficio— el mercader se elige por eso; recién cuando no
    /// falta ninguna, el viaje se hace por el equipo o el pergamino.
    /// </summary>
    [Theory]
    // falta comida | falta bebida | faltan pociones | falta la herramienta | sólo le faltan gustos
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    public void El_viaje_por_gustos_es_solo_con_lo_esencial_cubierto(bool comida, bool bebida, bool pociones, bool herramienta, bool gustos)
    {
        Assert.Equal(gustos, Bot.SoloLeFaltanGustos(comida, bebida, pociones, herramienta));
    }

    /// <summary>
    /// Y DE LA VITRINA SE ELIGE POR LO QUE MEJORA A LO QUE YA LLEVA: un arma que pega menos que la suya no cuenta
    /// como oferta, aunque sea lo único del mostrador.
    /// </summary>
    [Fact]
    public void De_la_vitrina_no_sirve_un_arma_peor_que_la_que_lleva()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 5, 20f, 100));    // pega 3: peor que la suya
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 5, 350f, 200));   // pega 8: la mejora
        const int loQueLleva = 5;                                            // el Martillo de Piedra del kit
        int Mejora(short obj) { int pega = obj == 100 ? 3 : obj == 200 ? 8 : 0; return pega > loQueLleva ? pega : 0; }

        var elegida = s.LoMejorDeLaVitrina(Mejora, plata: 1000);

        Assert.NotNull(elegida);
        Assert.Equal((short)200, elegida!.Value.Obj);
        Assert.Null(s.LoMejorDeLaVitrina(Mejora, plata: 100));                // no le alcanza la que mejora
    }
}
