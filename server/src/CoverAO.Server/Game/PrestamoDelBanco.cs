namespace CoverAO.Server.Game;

/// <summary>
/// EL PRÉSTAMO DEL BANCO (decisión del autor del 23/11, REGLA DE LA CASA: el original no tiene préstamos). El tester encontró a
/// Elmo y a Tulio, Renegados de nivel 22 y 23 que mueren seguido y pierden todo, en el Puerto de Rinkel con 5 monedas, la bóveda vacía,
/// el hambre y la sed en cero y Tulio sin energía: sin comer no recuperan energía, sin energía no cazan y sin plata no comen. Al que no
/// tiene nada el banquero le presta lo justo para comer, una vez a la vez, y lo que deposite después paga primero la deuda.
///
/// <list type="bullet">
/// <item>Lo presta el banquero al hacerle doble clic, con <c>PrestamoDelBanco</c> del <c>Server.ini</c> (0 lo apaga).</item>
/// <item>Sólo pasado el novato (si no, con un personaje nuevo se sacaban 300 para el principal) y con menos de
/// <see cref="TieneMenosDe"/> entre el oro encima y la bóveda.</item>
/// <item>Mientras deba, no le presta otro; cada depósito paga primero la deuda.</item>
/// </list>
/// </summary>
public static class PrestamoDelBanco
{
    /// <summary>Lo que presta si el <c>Server.ini</c> no dice otra cosa: una comida, una bebida y unas rojas.</summary>
    public const int PorDefecto = 300;

    /// <summary>Con menos que esto entre el oro encima y la bóveda, no tiene nada.</summary>
    public const int TieneMenosDe = 100;

    /// <summary>¿Le presta? Con el préstamo prendido, pasado el novato, sin deuda y sin nada.</summary>
    public static bool LePresta(int monto, int nivel, long oro, long banco, long deuda) =>
        monto > 0 && nivel > GameLimits.NewbieMaxLevel && deuda <= 0 && oro + banco < TieneMenosDe;

    /// <summary>De un depósito, cuánto paga la deuda y cuánto va a la bóveda.</summary>
    public static (long Paga, long Guarda) Repartir(long deposito, long deuda)
    {
        long paga = Math.Clamp(deuda, 0, Math.Max(0, deposito));
        return (paga, deposito - paga);
    }
}
