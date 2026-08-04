namespace CoverAO.Server.Game;

/// <summary>The world weather clock — alternates Despejado ↔ Lluvia on the game tick, independent of the
/// day/night cycle. Pure/deterministic (a function of the tick), so it's unit-testable and resume-safe.</summary>
public enum WeatherState { Despejado, Lluvia }

public static class Weather
{
    /// <summary>Duración del CICLO completo de clima, en vueltas del loop. A 40 ms, 27000 son 18 minutos.</summary>
    public static int CicloTicks { get; set; } = 27000;

    /// <summary>Qué fracción del ciclo llueve, en centésimos. 18 = llueve el 18% del tiempo, o sea unos 3
    /// minutos de cada 18.
    ///
    /// Antes esto era `(tick / 250) % 2`: llovía la MITAD del tiempo en ciclos de 20 segundos. Sumado al
    /// día/noche de 20 s, el chat quedaba así —y es literalmente lo que se veía al entrar—:
    ///     El sol comienza a ponerse. / Comienza a llover. / Ha caído la noche. / Amanece en el mundo.
    ///     La lluvia ha cesado. / Es de día. / El sol comienza a ponerse. / Comienza a llover.
    /// todo en menos de un minuto. La lluvia tiene que ser un evento, no un parpadeo.
    ///
    /// Igual que el día/noche: <b>el original no tiene reloj de clima</b>. `Lloviendo` (Admin.bas:90) es una
    /// global que sólo conmuta el comando GM <c>/LLUVIA</c> (Protocol.bas:9282). El reloj es un agregado del
    /// port, así que estos números son decisión de producto y el host los ajusta desde el Server.ini.</summary>
    public static int LluviaPorciento { get; set; } = 18;

    /// <summary>El clima del RELOJ, función pura del tick. Es lo que decide el clima mientras nadie lo fuerce;
    /// para el clima EFECTIVO (que respeta un /LLUVIA) hay que usar <see cref="Efectivo"/>.</summary>
    public static WeatherState At(long tick)
    {
        int ciclo = Math.Max(1, CicloTicks);
        long fase = ((tick % ciclo) + ciclo) % ciclo;             // % en C# puede dar negativo
        return fase < (long)ciclo * Math.Clamp(LluviaPorciento, 0, 100) / 100
            ? WeatherState.Lluvia : WeatherState.Despejado;
    }

    /// <summary>Override manual de un GM (VB6 `/LLUVIA` → HandleRainToggle, Protocol.bas:9270): el original NO
    /// tiene reloj de clima, tiene DOS GLOBALES —`Lloviendo` (Admin.bas:90) y `Queclima` (Declares.bas:48)— que
    /// el comando conmuta y que el login lee (Modulo_UsUaRiOs.bas:1607) para mandarle el estado al que entra.
    ///
    /// Acá el clima es un reloj determinístico, así que el comando no puede "setear la global": lo que hace es
    /// FIJAR el clima hasta el próximo /LLUVIA. Es el mínimo que respeta las dos cosas — el toggle del GM manda,
    /// y el reloj sigue existiendo para el mundo sin GMs. `null` = nadie lo forzó, gobierna el reloj.
    ///
    /// El `Queclima` del original NO es un booleano: 0 = despejado, 1 = lluvia común, 2 = lluvia eléctrica,
    /// 3 = nieve. Se guarda el byte tal cual porque es lo que viaja en el RainToggle y lo que el cliente pinta.
    /// (Los tres tipos de precipitación entran por ramas distintas del handler pero todas hacen
    /// `Lloviendo = Not Lloviendo`, o sea que comparten el mismo flag: eso es de la fuente.)
    private static byte? _forzado;

    /// <summary>Fija el clima a mano y devuelve el byte que hay que broadcastear. Toggle: si ya estaba forzado
    /// al MISMO clima, vuelve a despejado — igual que el `Lloviendo = Not Lloviendo` del original.</summary>
    public static byte Forzar(byte queclima)
    {
        _forzado = (_forzado == queclima && queclima != 0) ? (byte)0 : queclima;
        return _forzado.Value;
    }

    /// <summary>El clima que rige de verdad: el forzado por un GM si hay, y si no el del reloj.</summary>
    public static byte Efectivo(long tick) => _forzado ?? Clima(At(tick));

    /// <summary>¿Hay un override de GM en curso? Lo mira el tick del reloj para no pisarlo.</summary>
    public static bool Forzado => _forzado is not null;

    /// <summary>The RainToggle "clima" byte the client reads: 1 = raining, 0 = clear.</summary>
    public static byte Clima(WeatherState s) => (byte)(s == WeatherState.Lluvia ? 1 : 0);

    public static string Announce(WeatherState s) =>
        s == WeatherState.Lluvia ? "Comienza a llover." : "La lluvia ha cesado.";
}
