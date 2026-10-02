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

    private static Func<int, bool> Seguro(Mundo w, int nivel) => m => Bot.MapaSeguroPara(m, Player.StatusCiudadano, nivel, w.Datos, w.Ini);

    /// <summary>Adónde va el rescate de un ciudadano de ese nivel y esa vida, con los filtros del bot.</summary>
    private static (int Mapa, byte X, byte Y, int Saltos)? Destino(Mundo w, int mapa, (byte X, byte Y) desde, int nivel, int vida)
    {
        var pisable = Bot.PisableDelRescate(w.Datos, w.Ini, Player.StatusCiudadano, nivel);
        var sinHuidas = new Dictionary<int, long>();
        return Bot.DestinoDelRescate(w.Mapas, mapa, desde, LeVenden(w, nivel), VendePociones(w.Datos), Seguro(w, nivel),
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

    /// <summary>
    /// Y EL VIAJE DE ALDO AL 544, CON EL TOPE DEL DESTINO, tampoco pisa ningún mapa de guardias: mapa por mapa, la puerta del camino de
    /// siempre y si no, la de por donde se pueda (los escalones tercero y cuarto de <c>Bot.IrHastaAsync</c> en el rescate; los dos
    /// primeros ya no cruzan esos mapas).
    /// </summary>
    [Fact]
    public void El_viaje_del_rescate_de_Aldo_no_pisa_mapas_de_guardias()
    {
        if (ElMundo() is not { } w) return;
        var pisable = Bot.PisableDelRescate(w.Datos, w.Ini, Player.StatusCiudadano, 19);
        var deSiempre = Bot.PorElCaminoDeSiempre(w.Datos, pisable, _ => true, 544, 199, false);
        int mapa = 195, tope = 13;
        (byte X, byte Y) desde = (24, 88);
        var pisados = new List<int>();
        for (int i = 0; i < tope && mapa != 544; i++)
        {
            var salida = w.Mapas.PuertaHacia(mapa, 544, desde, (40, 8), deSiempre, tope)
                         ?? w.Mapas.PuertaHacia(mapa, 544, desde, (40, 8), pisable, tope);
            Assert.True(salida is not null, $"desde el {mapa} no se llega");
            var llegada = w.Datos.MapExits[(mapa, salida!.Value.Puerta.X, salida.Value.Puerta.Y)];
            mapa = llegada.DestMap;
            desde = ((byte)llegada.DestX, (byte)llegada.DestY);
            pisados.Add(mapa);
        }
        Assert.Equal(544, mapa);
        Assert.DoesNotContain(pisados, m => Bot.HayGuardiaQueVaPor(m, Player.StatusCiudadano, 19, w.Datos, w.Ini));
    }
}
