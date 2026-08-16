namespace CoverAO.Server.Game;

/// <summary>
/// Desgaste de hambre y sed, fiel al original (General.bas:1183, Server.ini INTERVALOS). La sed (MinAGU) baja
/// 10 cada <c>IntervaloSed</c> y el hambre (MinHam) cada <c>IntervaloHambre</c>; las dos tocan piso en 0.
///
/// OJO LA UNIDAD, que acá estuvo mal: esos números del Server.ini **no son milisegundos**, son VUELTAS del
/// game-loop. `HambreYSed` se llama en cada vuelta con `DeltaTick = (GetTickCount - LastGameTick) / 40`, o sea
/// ~1 (mMainLoop.bas:136, y el Timer de frmMain también está en 40). Como el <c>TickMs</c> del port es
/// igualmente 40, el número se copia **tal cual**. La prueba interna es
/// <c>IntervaloParalizadoReducido = 37</c>, que el comentario de la fuente describe como "Aprox. 1500 ms".
///
/// Leerlos como milisegundos —lo que hacía este archivo: <c>100 = 4000ms / 40ms</c>— deshidrataba al jugador
/// **40 veces más rápido** que el original: la sed se vaciaba en 40 segundos en vez de ~27 minutos. Ver
/// fase5/unidades-del-loop.md.
/// </summary>
public static class Survival
{
    public const int ThirstIntervalTicks = 4000;  // IntervaloSed = 4000 vueltas x 40 ms = 160 s
    public const int HungerIntervalTicks = 4500;  // IntervaloHambre = 4500 vueltas x 40 ms = 180 s
    public const byte DecayStep = 10;

    /// <summary>One decay step, floored at 0 (VB6: MinX = MinX - 10; if MinX &lt;= 0 then MinX = 0).</summary>
    public static byte Decay(byte value) => (byte)System.Math.Max(0, value - DecayStep);

    // --- Regeneración y veneno, en la MISMA unidad (vueltas del loop de 40 ms) ---
    //
    // OJO DE DÓNDE SALEN: estos números NO se leen del Server.ini, se leen del CALL SITE (mMainLoop.bas:186-212),
    // que es lo único que dice qué intervalo recibe cada función. El .ini engaña: define cuatro constantes y el
    // original sólo usa dos, y una de ellas para lo que no es.
    //
    //   de pie      -> Sanar(SanaIntervaloSinDescansar = 1600)    RecStamina(50)   <- 50 HARDCODEADO
    //   descansando -> Sanar(StaminaIntervaloSinDescansar = 5)    RecStamina(50)   <- ídem
    //
    // Sí: descansando, la VIDA se cura con la constante de la ENERGÍA (5 vueltas = 0,2 s). Es un error de la
    // fuente —el `Else` hasta tiene el comentario al revés, "No esta descansando"— pero es el que corre, así que
    // se porta igual: descansar cura vertiginosamente rápido y de pie tardás 64 segundos por curación. Y
    // `SanaIntervaloDescansar` / `StaminaIntervaloDescansar` son dos constantes MUERTAS: se cargan del .ini y no
    // las usa nadie. Ver fase5/unidades-del-loop.md.
    public const int HealIntervalTicks = 1600;          // SanaIntervaloSinDescansar     -> 64 s de pie
    public const int HealRestingIntervalTicks = 5;      // StaminaIntervaloSinDescansar  -> 0,2 s descansando (sic)
    public const int StaminaIntervalTicks = 50;         // literal en las DOS ramas      -> 2 s
    public const int PoisonIntervalTicks = 500;         // IntervaloVeneno               -> 20 s
    public const int ColdIntervalTicks = 50;            // IntervaloFrio (Server.ini:92) -> 2 s
    /// <summary>Vueltas del loop que hacen UN SEGUNDO (40 ms cada una). El original tiene efectos colgados de
    /// un timer de un segundo aparte del loop —el reloj del portal de Lum es el caso vivo—, y acá se cuentan
    /// en vueltas como todo lo demás.</summary>
    public const int TicksPorSegundo = 25;

    /// <summary>Lo que saca un tick de frío: <c>Porcentaje(máximo, 5)</c> (VB6 <c>EfectoFrio</c>,
    /// General.bas:869/877). En un mapa de NIEVE se lo saca a la VIDA y puede matar; en cualquier otro
    /// terreno, a la ENERGÍA. No es aleatorio, a diferencia de casi todos los demás efectos.</summary>
    /// <remarks>Va por <see cref="Potions.Porcentaje"/> y NO por división entera. `Porcentaje` devuelve
    /// <c>As Long</c> y en VB6 el operador <c>/</c> es de punto flotante, así que la conversión implícita
    /// redondea (half-to-even). Con `maximo * 5 / 100` truncábamos, y difieren en 95 de los primeros 199
    /// valores de MaxHp: un personaje recién nacido tiene MaxHp 16-19 (`15 + rnd(1..Constitucion\3)`), donde el
    /// original muerde 1 y el truncamiento muerde 0 — o sea que el aviso llegaba para siempre sin bajar un solo
    /// punto de vida. No se notó porque el flow usa a Merlin, que tiene MaxHp 100, el único valor donde
    /// truncar y redondear coinciden.</remarks>
    public static int ColdBite(int maximo) => (int)Matematicas.Porcentaje(maximo, 5);

    /// <summary>Cuánta vida devuelve una curación natural (VB6 <c>Sanar</c>, General.bas): <c>RandomNumber(2,
    /// Porcentaje(MaxSta, 5))</c> — sale de la energía MÁXIMA, no de la vida, que es de las rarezas del
    /// original que conviene no "arreglar". El piso en 2 evita el rango invertido cuando el 5% da menos.</summary>
    public static int HealAmount(int maxStamina, System.Func<int, int, int> rnd)
        => rnd(2, System.Math.Max(2, (int)Matematicas.Porcentaje(maxStamina, 5)));

    /// <summary>Cuánta energía devuelve <c>RecStamina</c>: <c>5 * RandomNumber(1, Porcentaje(MaxSta +
    /// Supervivencia, 5))</c>.</summary>
    public static int StaminaAmount(int maxStamina, int supervivencia, System.Func<int, int, int> rnd)
        => 5 * rnd(1, System.Math.Max(1, (int)Matematicas.Porcentaje(maxStamina + supervivencia, 5)));

    /// <summary>La SUERTE de meditar: cada cuántas vueltas, en promedio, acierta un tirón de maná. Sale de la
    /// escalera de <c>DoMeditar</c> (Trabajo.bas:2061-2093), que va de 35 con la skill en 0 a 5 con la skill en
    /// 100 — o sea que un meditador experto acierta SIETE VECES más seguido que uno que recién empieza.
    ///
    /// El original la escribe como once <c>ElseIf</c> encadenados sobre rangos de a diez; acá es la misma tabla
    /// dada vuelta, que se lee de un vistazo y no se puede desalinear.</summary>
    public static int SuerteAlMeditar(int skillMeditar) => skillMeditar switch
    {
        >= 100 => 5,
        >= 91 => 7,
        >= 81 => 10,
        >= 71 => 15,
        >= 61 => 18,
        >= 51 => 20,
        >= 41 => 22,
        >= 31 => 24,
        >= 21 => 28,
        >= 11 => 30,
        _ => 35,
    };

    /// <summary>Cuánto maná devuelve UN acierto de meditación: <c>Porcentaje(MaxMAN, PorcentajeRecuperoMana) /
    /// 10</c>, con piso 1 (Trabajo.bas:2099-2102). Con el 6 del install eso es el 0,6% del maná máximo — así
    /// que el tirón CRECE con el personaje en vez de ser una constante.
    ///
    /// <para>Son DOS redondeos y ninguno es truncar: <c>Porcentaje</c> devuelve un <c>Long</c> y el original
    /// guarda el resultado en <c>Dim cant As Integer</c>, así que la barra vuelve a redondear al par. Con las
    /// dos divisiones enteras de C# el mago recuperaba la MITAD en los pozos chicos: con 250 de maná máximo el
    /// original devuelve 2 puntos por acierto y truncando salía 1.</para></summary>
    public static int ManaAlMeditar(int maxMana, int porcentajeRecupero)
        => System.Math.Max(1, (int)Matematicas.Dividir(Matematicas.Porcentaje(maxMana, porcentajeRecupero), 10));
}
