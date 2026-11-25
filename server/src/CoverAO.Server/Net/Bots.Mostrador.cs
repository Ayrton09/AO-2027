using CoverAO.Server.Data;

namespace CoverAO.Server.Net;

/// <summary>
/// DEL LADO DEL MOSTRADOR. El servidor atiende hasta tres baldosas (VB6 <c>HandleCommerceStart</c>) sin mirar paredes, y el bot que
/// llegaba a tres baldosas de un mercader de adentro de una casa le compraba parado afuera, contra la pared (ver
/// <see cref="Navegante.HayComoLlegar"/>, que busca así a los mercaderes). Reportado por el tester (25/11): Fabia se paró al lado de una
/// casa y le apareció un Hacha de Dos Filos. Un jugador no puede: desde afuera el techo le tapa al mercader. Adentro es lo que el juego
/// toma por interior para no dejar montar (trigger 20 o más, ver <see cref="GameData.NoSeMontaAca"/>): cada casa lleva el suyo en
/// todas sus baldosas (el banco de Nix, 25 y 32), y la calle 0.
/// </summary>
public sealed partial class Bot
{
    /// <summary>¿La baldosa es interior de una casa? (trigger 20 o más.)</summary>
    public static bool EsInterior(GameData datos, int mapa, (byte X, byte Y) p) => datos.TileTrigger(mapa, p.X, p.Y) >= 20;

    /// <summary>¿Desde <paramref name="desde"/> se le compra al de <paramref name="vendedor"/>? Al de afuera, desde cualquier lado; al de
    /// adentro de una casa, sólo desde adentro.</summary>
    public static bool DelLadoDelMostrador(GameData datos, int mapa, (byte X, byte Y) desde, (byte X, byte Y) vendedor) =>
        !EsInterior(datos, mapa, vendedor) || EsInterior(datos, mapa, desde);

    /// <summary>Adónde pararse para comprarle al de adentro (ver <see cref="Navegante.BaldosaDeAdentro"/>), con
    /// <paramref name="abriendo"/> abierta si se da.</summary>
    public static (byte X, byte Y)? MostradorDeAdentro(GameData datos, Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                     (byte X, byte Y) vendedor, (byte X, byte Y)? abriendo = null) =>
        mapas.BaldosaDeAdentro(mapa, desde, vendedor, (x, y) => EsInterior(datos, mapa, ((byte)x, (byte)y)), abriendo);

    /// <summary>
    /// Si el mercader está adentro de una casa y el bot afuera, entra: caminando, o abriendo la puerta que da adentro si está cerrada
    /// (los bancos de Nix). Devuelve si quedó del lado del mostrador.
    /// </summary>
    private async Task<bool> EntrarAlMostradorAsync(TransporteDeBot transporte, SentidosDelBot sentidos, (byte X, byte Y) vendedor,
                                                    Task cerrada, CancellationToken ct, Func<bool>? cortaAntesDePaso = null)
    {
        if (DelLadoDelMostrador(_entorno.Datos, sentidos.Mapa, sentidos.Posicion, vendedor)) return true;
        int mapa = sentidos.Mapa;
        var adentro = MostradorDeAdentro(_entorno.Datos, _entorno.Mapas, mapa, sentidos.Posicion, vendedor);
        if (adentro is null
            && await AbrirLaPuertaQueAsync(transporte, sentidos,
                   donde => MostradorDeAdentro(_entorno.Datos, _entorno.Mapas, mapa, sentidos.Posicion, vendedor, donde) is not null,
                   cerrada, ct, cortaAntesDePaso)
            && sentidos.Mapa == mapa)
            adentro = MostradorDeAdentro(_entorno.Datos, _entorno.Mapas, mapa, sentidos.Posicion, vendedor);
        if (adentro is not { } baldosa)
        {
            Console.WriteLine($"[bot {Nombre}] no encuentra cómo entrar al mostrador de ({vendedor.X},{vendedor.Y}) en m{mapa}");
            return false;
        }
        await IrHastaAsync(transporte, sentidos, mapa, baldosa.X, baldosa.Y, cerrada, ct, tolerancia: 0,
                           cortaAntesDePaso: cortaAntesDePaso);
        return sentidos.Mapa == mapa && DelLadoDelMostrador(_entorno.Datos, mapa, sentidos.Posicion, vendedor);
    }
}
