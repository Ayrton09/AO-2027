using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// A IGUAL CANTIDAD DE MAPAS, EL CAMINO SIN LO DEL HUECO (DECIDIDO POR EL AUTOR, 07/10; ver <c>Bot.ElDeMenosSustoSiDaIgual</c>). El caso
/// medido: de Illiandor (194) al coto de Cuervos del 275 en nivel 7 a 9, por los Lobos del 195 o por el Puerto (179), los dos a dos saltos.
/// </summary>
public class ElDesempateDelHuecoTests
{
    private sealed record Mundo(GameData Datos, ServerIni Ini, Navegante Mapas);

    private static Mundo? ElMundo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return null;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        return File.Exists(ruta) ? new Mundo(d, ServerIni.Cargar(ruta), new Navegante(d)) : null;
    }

    private const int Nivel = 8, Vida = 70;
    private static readonly (byte X, byte Y) EnIlliandor = (39, 60), Coto = (55, 18);

    private static ((byte X, byte Y) Puerta, int Saltos)? Puerta(Mundo w, int mapa, (byte X, byte Y) desde, Func<int, bool>? sinLoDelHueco) =>
        Bot.PuertaDelViaje(w.Mapas, w.Datos, w.Ini, Player.StatusCiudadano, Nivel, Vida, mapa, desde, 275, Coto,
                           Navegante.TopeDeMapas, false, false, _ => true, _ => true, _ => true, sinLoDelHueco);

    [Fact]
    public void Para_ese_bot_el_Lobo_del_195_es_del_hueco_y_el_Puerto_no_tiene()
    {
        if (ElMundo() is not { } w) return;
        var sin = Bot.SinLoDelHueco(w.Datos, 275, Vida, Nivel, conPociones: true, curaVeneno: false);
        Assert.False(sin(195));
        Assert.True(sin(179));
        Assert.True(sin(275));   // el destino no se esquiva
    }

    [Fact]
    public void De_Illiandor_al_275_va_por_el_Puerto_y_llega_al_coto()
    {
        if (ElMundo() is not { } w) return;
        var sin = Bot.SinLoDelHueco(w.Datos, 275, Vida, Nivel, conPociones: true, curaVeneno: false);

        // Sin el desempate, por el 195; con él, por el 179, a los mismos dos saltos.
        var antes = Puerta(w, 194, EnIlliandor, null);
        Assert.NotNull(antes);
        Assert.Equal(195, w.Datos.MapExits[(194, antes.Value.Puerta.X, antes.Value.Puerta.Y)].DestMap);

        int mapa = 194;
        var desde = EnIlliandor;
        var pisados = new List<int>();
        while (mapa != 275 && pisados.Count < 4)
        {
            var salida = Puerta(w, mapa, desde, sin);
            Assert.NotNull(salida);
            if (mapa == 194) Assert.Equal(antes.Value.Saltos, salida.Value.Saltos);
            var llegada = w.Datos.MapExits[(mapa, salida.Value.Puerta.X, salida.Value.Puerta.Y)];
            mapa = llegada.DestMap;
            desde = ((byte)llegada.DestX, (byte)llegada.DestY);
            pisados.Add(mapa);
        }
        Assert.Equal(new[] { 179, 275 }, pisados);
        // Y del otro lado se llega caminando al coto: el desempate no lo deja en una zona del 275 sin Cuervos.
        Assert.NotNull(w.Mapas.HayComoLlegar(275, desde, Coto));
    }

    [Fact]
    public void El_rescate_del_varado_no_usa_el_desempate()
    {
        if (ElMundo() is not { } w) return;
        var sin = Bot.SinLoDelHueco(w.Datos, 275, Vida, Nivel, conPociones: true, curaVeneno: false);

        ((byte X, byte Y) Puerta, int Saltos)? Rescate(Func<int, bool>? desempate) =>
            Bot.PuertaDelViaje(w.Mapas, w.Datos, w.Ini, Player.StatusCiudadano, Nivel, Vida, 194, EnIlliandor, 275, Coto,
                               Navegante.TopeDeMapas, false, true, _ => true, _ => true, _ => true, desempate);

        var sinDesempate = Rescate(null);
        Assert.NotNull(sinDesempate);
        Assert.Equal(195, w.Datos.MapExits[(194, sinDesempate.Value.Puerta.X, sinDesempate.Value.Puerta.Y)].DestMap);
        Assert.Equal(sinDesempate, Rescate(sin));
    }

    [Fact]
    public void Escapando_o_de_muerto_el_viaje_no_tiene_desempate()
    {
        int armados = 0;
        Func<int, bool> sin = _ => true;
        Func<int, bool> Armar() { armados++; return sin; }

        Assert.Null(Bot.DesempateDelViaje(escapando: true, deMuerto: false, Armar));
        Assert.Null(Bot.DesempateDelViaje(escapando: false, deMuerto: true, Armar));
        Assert.Null(Bot.DesempateDelViaje(escapando: true, deMuerto: true, Armar));
        Assert.Equal(0, armados);
        Assert.Same(sin, Bot.DesempateDelViaje(escapando: false, deMuerto: false, Armar));
        Assert.Equal(1, armados);
    }

    [Fact]
    public void Si_el_camino_sin_lo_del_hueco_es_mas_largo_va_por_el_corto()
    {
        ((byte, byte), int)? Buscar(Func<int, bool> f) => f(5) ? (((byte)1, (byte)1), 2) : (((byte)2, (byte)2), 3);
        var r = Bot.ElDeMenosSustoSiDaIgual(Buscar, _ => true, m => m != 5);
        Assert.Equal(((byte)1, (byte)1), r!.Value.Puerta);
    }

    [Fact]
    public void Si_no_hay_otro_camino_cruza_igual()
    {
        ((byte, byte), int)? Buscar(Func<int, bool> f) => f(5) ? (((byte)1, (byte)1), 2) : null;
        var r = Bot.ElDeMenosSustoSiDaIgual(Buscar, _ => true, m => m != 5);
        Assert.Equal(((byte)1, (byte)1), r!.Value.Puerta);
    }

    [Fact]
    public void A_igual_cantidad_de_mapas_elige_el_tranquilo()
    {
        ((byte, byte), int)? Buscar(Func<int, bool> f) => f(5) ? (((byte)1, (byte)1), 2) : (((byte)2, (byte)2), 2);
        var r = Bot.ElDeMenosSustoSiDaIgual(Buscar, _ => true, m => m != 5);
        Assert.Equal(((byte)2, (byte)2), r!.Value.Puerta);
    }
}
