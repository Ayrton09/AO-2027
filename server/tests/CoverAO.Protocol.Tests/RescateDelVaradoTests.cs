using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL RESCATE DEL VARADO (DECIDIDO POR EL AUTOR, 02/10; ver <c>Bot.RescatarseAsync</c>): sin tienda ni mapa seguro al alcance por donde no
/// hay peligro, camina hasta el pueblo más cercano que le venda, o si no hay, hasta el mapa seguro más cercano; primero por el camino de
/// siempre y si no, por donde se pueda, nunca por los mapas de guardias que van por él. Contra la data, desde donde quedaron Rufo y Aldo
/// en `mid34`-`mid37`.
/// </summary>
public class RescateDelVaradoTests
{
    private sealed record Mundo(GameData Datos, ServerIni Ini, Navegante Mapas);

    private static Mundo? ElMundo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return null;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        return File.Exists(ruta) ? new Mundo(d, ServerIni.Cargar(ruta), new Navegante(d)) : null;
    }

    private static Func<int, bool> LeVenden(Mundo w, int nivel) => m =>
        Bot.ElMercaderLeVende(w.Ini, w.Datos.MapBattleMode(m), Player.StatusCiudadano)
        && !Bot.HayGuardiaQueVaPor(m, Player.StatusCiudadano, nivel, w.Datos, w.Ini);

    private static Func<int, bool> VendePociones(GameData d) => f =>
        d.Npc((short)f) is { StockItems: { Count: > 0 } stock } && stock.Any(s => d.Obj((short)s.ObjIndex) is { IsPotion: true, SubType: 3 });

    private static Func<int, bool> VendeComida(GameData d) => f =>
        d.Npc((short)f) is { StockItems: { Count: > 0 } stock } && stock.Any(s => d.Obj((short)s.ObjIndex) is { IsFood: true });

    private static Func<int, bool> Seguro(Mundo w, int nivel) => m => Bot.MapaSeguroPara(m, Player.StatusCiudadano, nivel, w.Datos, w.Ini);

    /// <summary>Adónde va el rescate de un ciudadano de ese nivel y esa vida, con los filtros del bot; por defecto, a quien vende
    /// pociones.</summary>
    private static (int Mapa, byte X, byte Y, int Saltos)? Destino(Mundo w, int mapa, (byte X, byte Y) desde, int nivel, int vida,
                                                                   Func<int, bool>? sirve = null)
    {
        var pisable = Bot.PisableDelRescate(w.Datos, w.Ini, Player.StatusCiudadano, nivel);
        var sinHuidas = new Dictionary<int, long>();
        return Bot.DestinoDelRescate(w.Mapas, mapa, desde, LeVenden(w, nivel), sirve ?? VendePociones(w.Datos), Seguro(w, nivel),
                                     Bot.PorDondeSeRescata(w.Datos, pisable, sinHuidas, 0, vida), Bot.CruzaSinHuidas(pisable, sinHuidas, 0));
    }

    /// <summary>RUFO, ciudadano de nivel 18 (138 de vida) con el hogar en Nix, donde no le venden: por el camino de siempre llega a un
    /// pueblo que sí, Rinkel.</summary>
    [Fact]
    public void Rufo_desde_Nix_va_a_Rinkel()
    {
        if (ElMundo() is not { } w) return;
        Assert.Equal(20, Destino(w, 34, (40, 88), 18, 138)?.Mapa);
    }

    /// <summary>Y SI VINO POR COMIDA, NO A RINKEL: ahí, de lo esencial, sólo el Alquimista vende, y pociones. Con un filtro de comida
    /// (lo que arma <c>LoEsencial</c> de la compra con <see cref="Bot.Esencial.Comida"/>), el Puerto de Rinkel, al lado, que vende
    /// comida y bebida.</summary>
    [Fact]
    public void Rufo_por_comida_va_al_Puerto_de_Rinkel()
    {
        if (ElMundo() is not { } w) return;
        Assert.Equal(99, Destino(w, 34, (40, 88), 18, 138, VendeComida(w.Datos))?.Mapa);
    }

    /// <summary>LO ESENCIAL, CON LAS PRIORIDADES DE LA COMPRA: sin pociones para pelear, sólo pociones, aunque tenga hambre; con la sed
    /// en cero y el hambre aguantando, sólo bebida; al revés, sólo comida; si no, las dos.</summary>
    [Theory]
    [InlineData(true, false, false, true, true, Bot.Esencial.Pociones)]
    [InlineData(false, true, false, true, true, Bot.Esencial.Bebida)]
    [InlineData(false, false, true, true, true, Bot.Esencial.Comida)]
    [InlineData(false, false, false, true, true, Bot.Esencial.Comida | Bot.Esencial.Bebida)]
    [InlineData(false, false, false, true, false, Bot.Esencial.Comida)]
    [InlineData(false, false, false, false, false, Bot.Esencial.Nada)]
    public void Lo_esencial_con_las_prioridades_de_la_compra(bool primeroLasPociones, bool primeroLaBebida, bool primeroLaComida,
                                                            bool vaPorComida, bool vaPorBebida, Bot.Esencial esperado) =>
        Assert.Equal(esperado, Bot.LoEsencialQueBusca(primeroLasPociones, primeroLaBebida, primeroLaComida, vaPorComida, vaPorBebida));

    /// <summary>
    /// ALDO, ciudadano de nivel 19 (199 de vida) en el 195, del lado republicano: los pueblos que le venderían pociones tienen guardias que
    /// van por él, así que pueblo no hay por ningún camino; el mapa seguro más cercano, las Costas de Suramei (544), sólo por donde se pueda,
    /// a trece mapas sin pisar Illiandor (194), y no a once por ahí.
    /// </summary>
    [Fact]
    public void Aldo_desde_el_195_va_al_544_sin_pisar_Illiandor()
    {
        if (ElMundo() is not { } w) return;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(19, w.Datos.MapZone(m));
        var porElDeSiempre = Bot.PorDondeSeRescata(w.Datos, pisable, new Dictionary<int, long>(), 0, 199);
        Assert.Null(w.Mapas.PuebloMasCercano(195, (24, 88), LeVenden(w, 19), VendePociones(w.Datos), pisable, Bot.SaltosDelRescate));
        Assert.Null(w.Mapas.MapaMasCercano(195, (24, 88), Seguro(w, 19), porElDeSiempre, Bot.SaltosDelRescate));
        Assert.True(Bot.HayGuardiaQueVaPor(194, Player.StatusCiudadano, 19, w.Datos, w.Ini), "premisa: en Illiandor nacen guardias que van por él");

        var adonde = Destino(w, 195, (24, 88), 19, 199);
        Assert.Equal(544, adonde?.Mapa);
        Assert.Equal(13, adonde?.Saltos);
    }

    /// <summary>La puerta que toma el viaje de un ciudadano (<c>Bot.PuertaDelViaje</c>) hasta <paramref name="adonde"/>, con el tope del
    /// destino, sin los dos primeros escalones: nada pasa ni se cruza sin peligro, así que va por el camino de siempre o, en el rescate,
    /// por donde se pueda.</summary>
    private static ((byte X, byte Y) Puerta, int Saltos)? Puerta(Mundo w, int mapa, (byte X, byte Y) desde,
                                                                 (int Mapa, byte X, byte Y, int Saltos) adonde, int nivel, int vida,
                                                                 bool rescate) =>
        Bot.PuertaDelViaje(w.Mapas, w.Datos, w.Ini, Player.StatusCiudadano, nivel, vida, mapa, desde, adonde.Mapa, (adonde.X, adonde.Y),
                           Math.Max(Navegante.TopeDeMapas, adonde.Saltos), false, rescate, _ => false, _ => false, _ => true);

    /// <summary>Los mapas que pisa ese viaje, puerta por puerta; null si en alguno no se llega.</summary>
    private static List<int>? Viaje(Mundo w, int mapa, (byte X, byte Y) desde, (int Mapa, byte X, byte Y, int Saltos) adonde, int nivel,
                                    int vida, bool rescate)
    {
        var pisados = new List<int>();
        for (int i = 0; i < Bot.SaltosDelRescate && mapa != adonde.Mapa; i++)
        {
            if (Puerta(w, mapa, desde, adonde, nivel, vida, rescate) is not { } salida) return null;
            var llegada = w.Datos.MapExits[(mapa, salida.Puerta.X, salida.Puerta.Y)];
            mapa = llegada.DestMap;
            desde = ((byte)llegada.DestX, (byte)llegada.DestY);
            pisados.Add(mapa);
        }
        return mapa == adonde.Mapa ? pisados : null;
    }

    /// <summary>
    /// Y EL VIAJE DEL RESCATE DE ALDO AL 544 tampoco pisa ningún mapa de guardias que van por él, ni por el camino de siempre ni por
    /// donde se pueda.
    /// </summary>
    [Fact]
    public void El_viaje_del_rescate_de_Aldo_no_pisa_mapas_de_guardias()
    {
        if (ElMundo() is not { } w) return;
        var adonde = Destino(w, 195, (24, 88), 19, 199);
        Assert.Equal(544, adonde?.Mapa);
        var pisados = Viaje(w, 195, (24, 88), adonde!.Value, 19, 199, rescate: true);
        Assert.NotNull(pisados);
        Assert.DoesNotContain(pisados!, m => Bot.HayGuardiaQueVaPor(m, Player.StatusCiudadano, 19, w.Datos, w.Ini));
    }

    /// <summary>
    /// SIN EL RESCATE, EL MISMO VIAJE NO SALE DEL 195: es largo, así que no va por el camino de siempre, y por donde se pueda va sólo el
    /// rescate. Ése es el "desde acá no se llega" que lo dispara (<c>Bot.IrAlMapaSeguroAsync</c>).
    /// </summary>
    [Fact]
    public void Sin_el_rescate_el_viaje_de_Aldo_no_sale_del_195()
    {
        if (ElMundo() is not { } w) return;
        var adonde = Destino(w, 195, (24, 88), 19, 199)!.Value;
        Assert.Null(Puerta(w, 195, (24, 88), adonde, 19, 199, rescate: false));
        Assert.NotNull(Puerta(w, 195, (24, 88), adonde, 19, 199, rescate: true));
    }

    /// <summary>
    /// EL VIAJE DEL RESCATE DE RUFO A RINKEL va por el camino de siempre: no pisa mapas de guardias que van por él ni, salvo el destino,
    /// mapas donde nace lo que lo mata de dos golpes.
    /// </summary>
    [Fact]
    public void El_viaje_del_rescate_de_Rufo_va_por_el_camino_de_siempre()
    {
        if (ElMundo() is not { } w) return;
        var adonde = Destino(w, 34, (40, 88), 18, 138);
        Assert.Equal(20, adonde?.Mapa);
        var pisados = Viaje(w, 34, (40, 88), adonde!.Value, 18, 138, rescate: true);
        Assert.NotNull(pisados);
        Assert.DoesNotContain(pisados!, m => Bot.HayGuardiaQueVaPor(m, Player.StatusCiudadano, 18, w.Datos, w.Ini));
        Assert.DoesNotContain(pisados!, m => m != 20 && Bot.NaceLoQueLoMataDeDos(w.Datos, m, 138));
    }
}
