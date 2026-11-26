using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL LEÑADOR Y EL CARPINTERO VENDEN EN LA SUBASTA (pedido del tester, 25/11, punto 1): la leña en lotes de 500, y el Carpintero de a
/// un lote por vez, flechas de a 500 rotando los tipos, cada tanto un arco y rara vez un barco. Los precios salen de la leña.
/// </summary>
public sealed class LenadorYCarpinteroVendenTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private const short Flecha = 480, FlechaMas1 = 551, FlechaMas3 = 553, Explosiva = 1082, Envenenante = 1083, Incendiaria = 1084,
                        Electrica = 1086, Barca = 474, Galera = 475, Galeon = 476, ArcoDeCazador = 478, Cuchara = 163, Lena = 58;

    [Fact]
    public void Vende_flechas_arcos_y_barcos_pero_no_la_cuchara()
    {
        var d = Datos.Value;
        foreach (short o in new[] { Flecha, Electrica, Barca, Galeon, ArcoDeCazador })
            Assert.True(Bot.EsDeCarpinteriaParaVender(d, o), $"obj {o}");
        Assert.False(Bot.EsDeCarpinteriaParaVender(d, Cuchara));
    }

    /// <summary>Por la leña (10 cada una) más la mitad: la Barca lleva 10.000 leñas, la Galera 20.000.</summary>
    [Fact]
    public void El_precio_sale_de_la_lena()
    {
        var d = Datos.Value;
        Assert.Equal(10, d.Obj(Lena)!.Value);
        Assert.Equal(15, Bot.ValorDeMercado(d, Flecha));
        Assert.Equal(150_000, Bot.ValorDeMercado(d, Barca));
        Assert.Equal(300_000, Bot.ValorDeMercado(d, Galera));
        var (_, final) = Bot.PrecioDeSubasta(Bot.ValorDeMercado(d, Galera));
        Assert.Equal(195_000, final);
    }

    [Fact]
    public void La_flecha_de_la_que_menos_tiene_y_entre_iguales_la_mas_barata()
    {
        var d = Datos.Value;
        // Con Carpintería 65 sabe la Flecha, la +1, +2, +3, la Explosiva, la Incendiaria y la Envenenante.
        Assert.Equal(Flecha, Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, tocaBarco: false, tocaArco: false));   // la que menos leña lleva
        var stock = new Dictionary<short, int> { [Explosiva] = 500, [Incendiaria] = 500, [FlechaMas3] = 20 };
        short? elegida = Bot.ElegirLoteDeCarpinteria(d, 65, o => stock.GetValueOrDefault(o), false, false);
        Assert.NotNull(elegida);
        Assert.NotEqual(Explosiva, elegida);           // ya tiene el lote entero
        Assert.NotEqual(Incendiaria, elegida);
        Assert.NotEqual(FlechaMas3, elegida);          // tiene algunas, hay otras de las que no tiene ninguna
        Assert.Equal(Flecha, elegida);
        stock[Flecha] = 500;
        Assert.Equal(FlechaMas1, Bot.ElegirLoteDeCarpinteria(d, 65, o => stock.GetValueOrDefault(o), false, false));
        // La Eléctrica pide 90: con 65 no.
        Assert.NotEqual(Electrica, Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, false, false));
    }

    [Fact]
    public void Rara_vez_un_barco_el_mas_grande_que_sabe()
    {
        var d = Datos.Value;
        Assert.Equal(Galera, Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, tocaBarco: true, tocaArco: false));
        Assert.Equal(Barca, Bot.ElegirLoteDeCarpinteria(d, 65, o => o == Galera ? 1 : 0, tocaBarco: true, tocaArco: false));
        // Sin la leña encima, una flecha: la Barca pide 10.000 y la Galera 20.000.
        Assert.Equal(Barca, Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, tocaBarco: true, tocaArco: false, lena: 15_000));
        Assert.True(d.Obj(Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, tocaBarco: true, tocaArco: false, lena: 1_734)!.Value) is { IsAmmo: true });
        // Sin barco que sepa, o con los dos ya hechos, una flecha.
        Assert.True(d.Obj(Bot.ElegirLoteDeCarpinteria(d, 30, _ => 0, tocaBarco: true, tocaArco: false)!.Value) is { IsAmmo: true });
        // Sin arco que sepa (piden 90 o más), una flecha.
        Assert.True(d.Obj(Bot.ElegirLoteDeCarpinteria(d, 65, _ => 0, tocaBarco: false, tocaArco: true)!.Value) is { IsAmmo: true });
        Assert.True(d.Obj(Bot.ElegirLoteDeCarpinteria(d, 100, _ => 0, tocaBarco: false, tocaArco: true)!.Value) is { IsBow: true });
    }

    [Fact]
    public void Las_flechas_van_de_a_500_y_los_barcos_de_a_uno()
    {
        var d = Datos.Value;
        Assert.Equal(500, Bot.CuantasPorLote(d.Obj(Envenenante)!));
        Assert.Equal(500, Bot.CuantasPorLote(d.Obj(FlechaMas1)!));
        Assert.Equal(1, Bot.CuantasPorLote(d.Obj(Barca)!));
        Assert.Equal(1, Bot.CuantasPorLote(d.Obj(ArcoDeCazador)!));
    }

    /// <summary>El Carpintero le compra al Leñador el lote de leña al precio de la subasta: 500 leñas por 3.250.</summary>
    [Fact]
    public void El_carpintero_le_compra_la_lena_al_lenador()
    {
        var (_, final) = Bot.PrecioDeSubasta(10 * Bot.LenaPorLote);
        Assert.Equal(3_250, final);
        var lote = new ServerMessages.FilaDeSubasta(1, Lena, (short)Bot.LenaPorLote, 0, final, "Quim", 3_600, "");
        Assert.True(Bot.LeConvieneElLote(lote, 10, "Osmar", 10_000, Bot.LenaPorLote));
        Assert.False(Bot.LeConvieneElLote(lote, 10, "Quim", 10_000, Bot.LenaPorLote));   // la suya
    }

    /// <summary>Antes de salir a cazar, la leña y lo que tiene a medio hacer van al banco con el equipo de repuesto (el tester, 26/11).</summary>
    [Fact]
    public void Guarda_la_lena_y_el_lote_a_medio_hacer()
    {
        var d = Datos.Value;
        var mochila = new List<(byte, short, short, bool)> { (1, Lena, 1_200, false), (2, FlechaMas1, 320, false), (3, 38, 200, false) };
        var guarda = Bot.LoQueGuarda(d, mochila, false, false, _ => false, o => o == Lena || Bot.EsDeCarpinteriaParaVender(d, o));
        Assert.Contains(guarda, g => g.Obj == Lena && g.Cantidad == 1_200);
        Assert.Contains(guarda, g => g.Obj == FlechaMas1 && g.Cantidad == 320);
        Assert.DoesNotContain(guarda, g => g.Obj == 38);
        Assert.DoesNotContain(Bot.LoQueGuarda(d, mochila, false, false, _ => false), g => g.Obj == Lena);
    }

    /// <summary>El piquete de repuesto se apila sobre el que usa, que va puesto: al banco va uno y el otro queda en la mano.</summary>
    [Fact]
    public void Del_piquete_puesto_guarda_el_repuesto_y_deja_uno()
    {
        var d = Datos.Value;
        const short Piquete = 187;
        Assert.True(d.Obj(Piquete) is { DropsOnDeath: true });
        var mochila = new List<(byte, short, short, bool)> { (1, Piquete, 2, true) };
        var guarda = Bot.LoQueGuarda(d, mochila, false, false, _ => true, o => o == Piquete);
        Assert.Equal((1, Piquete, 1), Assert.Single(guarda));
        Assert.Empty(Bot.LoQueGuarda(d, new List<(byte, short, short, bool)> { (1, Piquete, 1, true) }, false, false, _ => true, o => o == Piquete));
    }
}
