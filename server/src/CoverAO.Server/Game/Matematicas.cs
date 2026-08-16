namespace CoverAO.Server.Game;

/// <summary>
/// El <c>Matematicas.bas</c> del original: las cuentas que usan TODOS los subsistemas.
///
/// <para>Existe como archivo propio por una razón concreta y medida. La barra de VB6 es división de PUNTO
/// FLOTANTE, y guardar el resultado en un entero REDONDEA al par — nunca trunca. La división entera de C# se
/// parece lo suficiente como para que nadie mire dos veces, y da un punto menos cada vez que la cuenta no
/// cierra exacta. Ese punto se reimplementó mal CINCO veces en este port:</para>
///
/// <list type="bullet">
/// <item>el mordisco del frío — un recién nacido (16-19 de vida máxima) perdía 0 en vez de 1, o sea que el
/// aviso salía para siempre sin bajarle un punto;</item>
/// <item>el maná de meditar — con 250 de maná máximo el original devuelve 2 por acierto y truncando salía 1,
/// la mitad;</item>
/// <item>el daño de un hechizo ofensivo y la cura de área — un punto menos a partir de nivel 13;</item>
/// <item>la curación natural y el regreso de energía.</item>
/// </list>
///
/// <para>El primero se arregló solo, con un helper metido adentro de <c>Potions</c> donde el que escribía el
/// siguiente no lo iba a encontrar. Por eso está acá y con el nombre del archivo del original: para que el
/// próximo que porte una fórmula con porcentaje lo tenga delante.</para>
/// </summary>
public static class Matematicas
{
    /// <summary><c>Porcentaje(Total, Porc) = (Total * Porc) / 100</c> devuelto como <c>Long</c>
    /// (<c>Matematicas.bas:32</c>). Redondea al par.</summary>
    public static long Porcentaje(long total, long porc) =>
        (long)System.Math.Round(total * porc / 100.0, System.MidpointRounding.ToEven);

    /// <summary>Una división de VB6 cuyo resultado se guarda en un entero: redondea al par, no trunca.</summary>
    public static long Dividir(long valor, long divisor) =>
        (long)System.Math.Round(valor / (double)divisor, System.MidpointRounding.ToEven);

    /// <summary>Guardar un <c>Double</c> en un <c>Integer</c>/<c>Long</c> de VB6: redondea al par.
    ///
    /// <para>Es la misma trampa que la barra, por el otro lado. El cast <c>(int)</c> de C# TRUNCA, así que
    /// cada <c>AumentoMANA = 2.8 * Inteligencia</c> del original portado con un cast pierde hasta un punto por
    /// nivel — sobre 49 niveles, hasta 49 de maná, y justo en las clases que viven de eso.</para></summary>
    public static long AEntero(double valor) =>
        (long)System.Math.Round(valor, System.MidpointRounding.ToEven);
}
