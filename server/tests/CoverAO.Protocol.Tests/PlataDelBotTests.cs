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

    /// <summary>
    /// NO SE VIAJA A MIRAR ALGO QUE NO PUEDE PAGAR. Es el caso de Ciro en `seguir1`: 2445 monedas y el Herrero Rinkel
    /// con la Armadura de Cuero a 3000 — doce minutos de ida y vuelta sin cazar. El precio de lista es el techo de lo
    /// que le van a cobrar, así que con eso alcanza para decidir el viaje.
    /// </summary>
    [Theory]
    // precio de lista | plata que le sobra | ¿vale el viaje?
    [InlineData(3000, 2441, false)]    // la Armadura de Cuero contra lo que tenía Ciro
    [InlineData(3000, 3000, true)]     // justo
    [InlineData(300, 2441, true)]      // el Escudo de Tortuga, que sí podía
    [InlineData(0, 0, true)]           // lo que no cuesta nada se puede pagar siempre
    public void No_viaja_a_mirar_lo_que_no_puede_pagar(int precio, double plata, bool leAlcanza)
    {
        Assert.Equal(leAlcanza, Bot.LoPuedePagar(precio, plata));
    }

    /// <summary>
    /// EL EQUIPO Y LOS PERGAMINOS TAMBIÉN SON ALGO QUE COMPRAR. Es lo que habilita la runa a casa cuando no hay
    /// tienda al alcance; sin ellos, el bot que ya tiene lo esencial se queda en el bosque para siempre con la plata
    /// sin gastar. Medido en `puertas3` y `seguir7`.
    /// </summary>
    [Theory]
    // herramienta | comida | bebida | pociones | equipo | hechizo | ¿vuelve a casa a comprar?
    [InlineData(false, false, false, false, false, false, false)]   // no le falta nada: se queda cazando
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, true)]
    [InlineData(false, false, true, false, false, false, true)]
    [InlineData(false, false, false, true, false, false, true)]
    [InlineData(false, false, false, false, true, false, true)]     // sólo equipo: antes NO volvía
    [InlineData(false, false, false, false, false, true, true)]     // sólo pergamino: antes NO volvía
    public void El_equipo_y_los_pergaminos_cuentan_como_algo_que_comprar(bool herr, bool com, bool beb, bool poc,
                                                                         bool eq, bool hech, bool vuelve)
    {
        Assert.Equal(vuelve, Bot.HayAlgoQueComprar(herr, com, beb, poc, eq, hech, antidoto: false));
    }

    /// <summary>
    /// SALIR A COMPRAR ANTÍDOTO: sólo si le falta Y le alcanza. Lo segundo no es un detalle — es el mismo
    /// espiral que el archivo ya documentó con la herramienta: si faltar alcanzara para salir, un bot sin plata
    /// volvería con las manos vacías y saldría de nuevo, sin cazar nunca.
    ///
    /// <para>Y un precio en cero significa que NADIE lo vende: entonces tampoco se sale.</para>
    /// </summary>
    [Theory]
    // ¿le falta? | oro | lo que cuesta | ¿sale?
    [InlineData(true, 100, 10, true)]     // le falta y le sobra
    [InlineData(true, 10, 10, true)]      // justo le alcanza
    [InlineData(true, 9, 10, false)]      // le falta una moneda: no sale a dar la vuelta al vicio
    [InlineData(true, 0, 10, false)]      // sin plata, el espiral cerrado
    [InlineData(false, 100, 10, false)]   // ya tiene: no sale
    [InlineData(true, 100, 0, false)]     // nadie lo vende
    public void Vale_salir_por_el_antidoto(bool falta, int oro, int cuesta, bool sale)
    {
        Assert.Equal(sale, Bot.ValeSalirPorElAntidoto(falta, oro, cuesta));
    }

    /// <summary>El antídoto cuenta para salir de compras, como cualquier otra necesidad.</summary>
    [Fact]
    public void El_antidoto_es_motivo_para_salir_de_compras()
    {
        Assert.True(Bot.HayAlgoQueComprar(false, false, false, false, false, false, antidoto: true));
        Assert.False(Bot.HayAlgoQueComprar(false, false, false, false, false, false, antidoto: false));
    }

    /// <summary>
    /// Y EL PRECIO SALE DE LA DATA: contra el obj.dat y los NPC de verdad, el antídoto que alguien vende
    /// existe y cuesta poco. Si nadie lo vendiera, `ValeSalirPorElAntidoto` apagaría la necesidad sola.
    /// </summary>
    [Fact]
    public void El_precio_del_antidoto_sale_de_la_data()
    {
        var datos = GameData.Load();
        if (datos.Obj(1) is null) return;   // sin la carpeta del juego no hay nada que decir
        int cuesta = Bot.PrecioDelAntidotoEn(datos);
        Assert.True(cuesta > 0, "ningún mercader vende antídoto: el bot no tendría cómo conseguirlo");
        Assert.True(cuesta <= 100, $"el antídoto pasó a costar {cuesta}: revisar si sigue siendo barato");
    }

    /// <summary>
    /// VENDER NO ALCANZA PARA ELEGIR AL MERCADER cuando falta comida o bebida y hay con qué pagarlas.
    ///
    /// <para>Botín para vender hay casi siempre, así que la última condición de <c>Sirve</c> aceptaba al mercader
    /// más cercano sea cual sea lo que venda, se compraba lo que ése tuviera y la necesidad seguía abierta para el
    /// viaje siguiente, que terminaba en el mismo mostrador. Medido con los bots pasando el nivel 15 —cuando el kit
    /// de novato deja de contarles—: cuatro visitas seguidas con la comida en cero y veinte mil monedas encima,
    /// con el vendedor de comida a los mismos saltos que el tabernero al que sí llegó.</para>
    ///
    /// <para>Y sin plata para pagarlo, vender sigue alcanzando: si no, el que se quedó sin nada viajaría a un
    /// mostrador que no le compra el botín y no podría comprar nunca.</para>
    /// </summary>
    [Theory]
    // ¿falta comida o bebida? | oro | lo que cuesta cubrirlo | ¿alcanza con que le compre?
    [InlineData(false, 0, 500, true)]      // no le falta nada: el viaje a vender vale igual
    [InlineData(false, 5000, 500, true)]
    [InlineData(true, 5000, 500, false)]   // el caso: le falta y puede pagarlo, así que va a quien le venda
    [InlineData(true, 500, 500, false)]    // justo le alcanza
    [InlineData(true, 499, 500, true)]     // no le alcanza: primero vender, o queda sin salida
    [InlineData(true, 0, 500, true)]
    public void Vender_no_alcanza_para_elegir_al_mercader(bool falta, int oro, int cuesta, bool alcanza)
    {
        Assert.Equal(alcanza, Bot.VenderAlcanzaParaElegirlo(falta, oro, cuesta));
    }

    /// <summary>Sin nada que cueste —la data podría dar cero— vender tampoco alcanza: no hay excusa de plata.</summary>
    [Fact]
    public void Con_lo_que_falta_gratis_el_viaje_es_a_quien_lo_venda()
    {
        Assert.False(Bot.VenderAlcanzaParaElegirlo(true, 0, 0));
    }
}
