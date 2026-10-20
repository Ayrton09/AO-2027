namespace CoverAO.Server.Net;

/// <summary>
/// LA CHARLA CON LA GENTE (pedido del autor, 19/10: que los bots hablen con los jugadores). Primero oír: el bot lee lo que le dicen
/// (<see cref="SentidosDelBot.TomarLoOido"/>) y, por ahora, no contesta. Lo que es para él queda en el log del servidor, para ver
/// cuánto les habla la gente antes de decidir qué contestan.
/// </summary>
public sealed partial class Bot
{
    /// <summary>
    /// ¿ES PARA ÉL? El susurro siempre: sólo le llega al que se lo mandan. La charla, si lo nombra (la palabra entera, sin importar
    /// mayúsculas): es lo que hace cualquiera para hablarle a alguien que tiene al lado. El grito y el global son para todos.
    /// </summary>
    public static bool LeHablanAEl(SentidosDelBot.LoOido oido, string nombre) => oido.Como switch
    {
        SentidosDelBot.ComoLoDijo.Susurro => true,
        SentidosDelBot.ComoLoDijo.Charla => LoNombra(oido.Texto, nombre),
        _ => false,
    };

    /// <summary>
    /// La línea del log para lo que le dijeron: el texto del jugador SANEADO (<see cref="PacketDispatcher.ParaElLog"/>: sin saltos de
    /// línea ni caracteres de control que inventen líneas en la consola) y cortado a <see cref="LargoEnElLog"/>, que el susurro puede
    /// traer mucho más que los 80 de la charla.
    /// </summary>
    public static string LineaDelLog(string bot, SentidosDelBot.LoOido oido)
    {
        string texto = oido.Texto.Length > LargoEnElLog ? oido.Texto[..LargoEnElLog] + "..." : oido.Texto;
        return $"[bot {bot}] {oido.Como} de {PacketDispatcher.ParaElLog(oido.Quien)}: {PacketDispatcher.ParaElLog(texto)}";
    }

    public const int LargoEnElLog = 80;

    /// <summary>¿El texto tiene el nombre como palabra entera? "hola Bruma" y "bruma, vení" sí; "Brumas" no.</summary>
    public static bool LoNombra(string texto, string nombre)
    {
        if (nombre.Length == 0) return false;
        for (int i = texto.IndexOf(nombre, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = texto.IndexOf(nombre, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            bool antes = i == 0 || !char.IsLetterOrDigit(texto[i - 1]);
            int fin = i + nombre.Length;
            bool despues = fin == texto.Length || !char.IsLetterOrDigit(texto[fin]);
            if (antes && despues) return true;
        }
        return false;
    }
}
