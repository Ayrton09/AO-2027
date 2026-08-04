namespace CoverAO.Server.Game;

/// <summary>The world day/night clock. Cycles Amanecer → Día → Atardecer → Noche driven by the game
/// tick. Pure/deterministic so the period is a function of the tick count (unit-testable, resume-safe).</summary>
public enum WorldPeriod { Amanecer, Dia, Atardecer, Noche }

public static class DayNight
{
    /// <summary>Vueltas que dura CADA período. A 40 ms por vuelta, 9000 son 6 minutos, o sea un día
    /// completo de 24 minutos.
    ///
    /// Estaba en 125 —5 segundos por período, el día entero en 20— con el comentario "in this demo". Con
    /// eso el mundo amanecía y anochecía mientras cruzabas una plaza, y el chat era una lista de avisos de
    /// día y noche pisándose. Se nota apenas entrás.
    ///
    /// OJO: <b>el ciclo día/noche no existe en el original</b>. Buscá `Amanecer`, `Anochecer` o `DeNoche`
    /// en la fuente VB6 y no hay nada: el mundo es siempre de día. Es un agregado del port para que el
    /// cliente web tenga de dónde sacar el tinte de la escena, así que la duración es una decisión de
    /// producto y no un valor a portar — por eso el host puede cambiarla desde el Server.ini.</summary>
    public static int PeriodTicks { get; set; } = 9000;

    public static WorldPeriod PeriodAt(long tick) => (WorldPeriod)((tick / Math.Max(1, PeriodTicks)) % 4);

    /// <summary>A short announcement for entering a period (the "moment of change").</summary>
    public static string Announce(WorldPeriod period) => period switch
    {
        WorldPeriod.Amanecer => "Amanece en el mundo.",
        WorldPeriod.Dia => "Es de día.",
        WorldPeriod.Atardecer => "El sol comienza a ponerse.",
        WorldPeriod.Noche => "Ha caído la noche.",
        _ => "",
    };
}
