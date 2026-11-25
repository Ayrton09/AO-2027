using CoverAO.Server.Data;

namespace CoverAO.Server.Net;

/// <summary>
/// DEL LADO DEL MOSTRADOR. El servidor atiende hasta tres baldosas (VB6 <c>HandleCommerceStart</c>) sin mirar paredes, y el bot que
/// llegaba a tres baldosas de un mercader de adentro de una casa le compraba parado afuera, contra la pared. Reportado por el tester
/// (25/11): Fabia se paró al lado de una casa y le apareció un Hacha de Dos Filos. Un jugador no puede: desde afuera el techo le tapa
/// al mercader. Adentro es lo que el juego toma por interior para no dejar montar (trigger 20 o más, ver
/// <see cref="GameData.NoSeMontaAca"/>), que es como este mundo marca las casas.
/// </summary>
public sealed partial class Bot
{
    /// <summary>¿La baldosa es interior de una casa? (trigger 20 o más.)</summary>
    public static bool EsInterior(GameData datos, int mapa, (byte X, byte Y) p) => datos.TileTrigger(mapa, p.X, p.Y) >= 20;

    /// <summary>¿Desde <paramref name="desde"/> se le compra al de <paramref name="vendedor"/>? Al de afuera, desde cualquier lado; al de
    /// adentro de una casa, sólo desde adentro.</summary>
    public static bool DelLadoDelMostrador(GameData datos, int mapa, (byte X, byte Y) desde, (byte X, byte Y) vendedor) =>
        !EsInterior(datos, mapa, vendedor) || EsInterior(datos, mapa, desde);

    /// <summary>
    /// Adónde pararse para comprarle al de adentro: la baldosa interior a <see cref="RadioDelMostrador"/> o menos del mercader a la que se
    /// llega caminando más corto. Null si no hay ninguna.
    /// </summary>
    public static (byte X, byte Y)? MostradorDeAdentro(GameData datos, Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                     (byte X, byte Y) vendedor)
    {
        (byte X, byte Y)? mejor = null;
        int pasos = int.MaxValue;
        for (int dy = -RadioDelMostrador; dy <= RadioDelMostrador; dy++)
            for (int dx = -RadioDelMostrador; dx <= RadioDelMostrador; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dy) > RadioDelMostrador || (dx == 0 && dy == 0)) continue;
                int x = vendedor.X + dx, y = vendedor.Y + dy;
                if (x < 1 || y < 1 || x > 100 || y > 100) continue;
                var p = ((byte)x, (byte)y);
                if (!EsInterior(datos, mapa, p) || datos.IsBlockedTile(mapa, x, y)) continue;
                if (mapas.Camino(mapa, desde, p) is { } camino && camino.Count < pasos) { pasos = camino.Count; mejor = p; }
            }
        return mejor;
    }

    /// <summary>
    /// Si el mercader está adentro de una casa y el bot afuera, entra hasta <see cref="MostradorDeAdentro"/>. Devuelve si quedó del lado
    /// del mostrador.
    /// </summary>
    private async Task<bool> EntrarAlMostradorAsync(TransporteDeBot transporte, SentidosDelBot sentidos, (byte X, byte Y) vendedor,
                                                    Task cerrada, CancellationToken ct, Func<bool>? cortaAntesDePaso = null)
    {
        if (DelLadoDelMostrador(_entorno.Datos, sentidos.Mapa, sentidos.Posicion, vendedor)) return true;
        if (MostradorDeAdentro(_entorno.Datos, _entorno.Mapas, sentidos.Mapa, sentidos.Posicion, vendedor) is not { } adentro)
        {
            Console.WriteLine($"[bot {Nombre}] no encuentra cómo entrar al mostrador de ({vendedor.X},{vendedor.Y}) en m{sentidos.Mapa}");
            return false;
        }
        await IrHastaAsync(transporte, sentidos, sentidos.Mapa, adentro.X, adentro.Y, cerrada, ct, tolerancia: 0,
                           cortaAntesDePaso: cortaAntesDePaso);
        return DelLadoDelMostrador(_entorno.Datos, sentidos.Mapa, sentidos.Posicion, vendedor);
    }
}
