namespace CoverAO.Server.Game;

/// <summary>
/// EL MODELO DE MAGIA DE AOWEB, portado entero POR DECISIÓN DEL AUTOR.
///
/// <para>No es el del VB6 del que sale el resto del port. El VB6 resta la <c>ResistenciaMagica</c> del equipo
/// como un número PLANO y su skill de «Resistencia mágica» no se usa para nada —sólo se nombra y se entrena—.
/// <c>aoweb</c> (github.com/dcatanzaro/aoweb) la rehizo: la resistencia es un PORCENTAJE, entra la diferencia
/// de nivel entre el que lanza y el que recibe, cada clase tiene lo suyo, y el atacante puede PERFORARLA con
/// su equipo. Es el modelo que se adopta.</para>
///
/// <para><b>Las tablas de clase vienen remapeadas POR NOMBRE, no por número.</b> Las dos numeraciones no
/// coinciden y copiarlas por índice hubiera sido un desastre silencioso: en aoweb el 1 es el Mago y el 8 el
/// Paladín, y acá el 1 es el Clérigo y el 8 el Gladiador. O sea que por índice el Gladiador —que no castea—
/// heredaba los números del Paladín y el Mago los del Clérigo.</para>
///
/// <para><b>Lo que NO tiene data todavía:</b> <c>magicDamageBonus</c> y <c>magicPenetration</c> en objetos y
/// la defensa mágica de los NPCs no existen en nuestro <c>obj.dat</c> ni en <c>NPCs.dat</c>. Se portan igual y
/// valen 0, que es exactamente lo que hace aoweb cuando le falta el campo (<c>?? 0</c>): el día que la data
/// los traiga, funcionan sin tocar una línea.</para>
/// </summary>
public static class MagiaWeb
{
    /// <summary>
    /// LA «HABILIDAD MÁGICA» DE UN PERSONAJE. En aoweb no sale de una skill guardada sino del nivel
    /// (<c>getSimulatedSkill</c>: <c>min(100, level * 3)</c>), y por eso editar la skill en el personaje no
    /// cambia nada — ni allá ni acá.
    /// </summary>
    public static int HabilidadMagica(int nivel) => System.Math.Min(100, System.Math.Max(0, nivel) * 3);

    /// <summary>
    /// EL DAÑO QUE SALE DEL LANZADOR, antes de que el blanco se defienda (aoweb <c>applyMagicBonuses</c>):
    ///
    /// <list type="number">
    /// <item>la tirada del hechizo, <b>+3 % por nivel</b> del que lanza;</item>
    /// <item><b>+<paramref name="bonoBaculo"/></b>, que es el <c>CuantoAumento</c> del báculo de DañoMágico.
    /// Este NO es de aoweb: es del VB6 y se conserva porque tiene data —hay báculos así en el obj.dat— y
    /// sacarlo sería perder una pieza del juego;</item>
    /// <item>por cada pieza del lanzador (arma y anillo), <b>+su porcentaje</b> de daño mágico, y se le suma
    /// la <b>penetración</b> que traiga;</item>
    /// <item>y al final, el <b>modificador de daño mágico de su clase</b>.</item>
    /// </list>
    ///
    /// <para>El porcentaje de cada pieza se aplica sobre lo ya acumulado, no sobre la tirada: es el orden de
    /// aoweb, donde <c>damage</c> se reasigna dentro del bucle.</para>
    /// </summary>
    public static (int Dano, int Penetracion) DanoDelLanzador(
        int tirada, int nivelLanzador, int bonoBaculo,
        System.Collections.Generic.IReadOnlyList<(int Bono, int Penetracion)> piezas, double modDeSuClase)
    {
        int dano = tirada + (int)System.Math.Round(tirada * (3.0 * nivelLanzador) / 100.0,
                                                   System.MidpointRounding.AwayFromZero);
        dano += bonoBaculo;
        int penetracion = 0;
        foreach (var (bono, pen) in piezas)
        {
            dano += (int)System.Math.Round(dano * bono / 100.0, System.MidpointRounding.AwayFromZero);
            penetracion += pen;
        }
        return ((int)System.Math.Floor(dano * modDeSuClase), penetracion);
    }

    /// <summary>
    /// CUÁNTO PORCENTAJE DEL DAÑO FRENA EL BLANCO (aoweb <c>applyMagicResistanceToUser</c>):
    /// la resistencia de su equipo, más la de su clase, más <b>el doble de la diferencia de habilidad</b>
    /// cuando él está por encima del que lanza, menos la penetración del atacante. Nunca menos de cero.
    ///
    /// <para>La diferencia cuenta SÓLO a favor del que se defiende: si el que lanza tiene más nivel, el
    /// término es 0, no negativo. O sea que el nivel del mago no perfora resistencia por sí solo — para eso
    /// está la penetración del equipo.</para>
    /// </summary>
    public static int ResistenciaEnPorciento(int delEquipo, int deSuClase, int nivelDelBlanco,
                                             int nivelDelLanzador, int penetracion)
    {
        int diferencia = HabilidadMagica(nivelDelBlanco) - HabilidadMagica(nivelDelLanzador);
        int porciento = delEquipo + deSuClase + (diferencia > 0 ? diferencia * 2 : 0) - penetracion;
        return porciento < 0 ? 0 : porciento;
    }

    /// <summary>
    /// EL DAÑO FINAL, con el porcentaje descontado. El piso es <b>1</b>, no 0: en aoweb los dos caminos
    /// terminan en <c>if (dmg &lt; 1) dmg = 1</c>, así que un hechizo siempre saca al menos un punto por mucha
    /// resistencia que tenga el blanco. Es al revés que el VB6, donde la resta plana podía anularlo del todo.
    /// </summary>
    public static int ConLaResistencia(int dano, int porcientoDeResistencia)
    {
        int queda = dano - (int)System.Math.Floor(dano * (double)porcientoDeResistencia / 100.0);
        return queda < 1 ? 1 : queda;
    }

    /// <summary>
    /// LO QUE LE FRENA UN NPC (aoweb <c>applyMagicResistanceToNpc</c>): sólo entra si la criatura tiene
    /// resistencia declarada, y el porcentaje sale de su defensa mágica más la diferencia de habilidad contra
    /// el que lanza. Después se le resta además su defensa mágica plana.
    /// </summary>
    public static int DanoContraNpc(int dano, int resistenciaDelNpc, int defensaMagicaDelNpc,
                                    int nivelDelLanzador, int penetracion)
    {
        int queda = dano;
        if (resistenciaDelNpc > 0)
        {
            int diferencia = resistenciaDelNpc - HabilidadMagica(nivelDelLanzador);
            int porciento = defensaMagicaDelNpc + (diferencia > 0 ? diferencia * 2 : 0) - penetracion;
            if (porciento < 0) porciento = 0;
            queda -= (int)System.Math.Floor(queda * (double)porciento / 100.0);
        }
        queda -= defensaMagicaDelNpc;
        return queda < 1 ? 1 : queda;
    }

    // Las dos tablas de aoweb (api/src/jsons/balance.json), con SU id de clase a la izquierda y el nuestro a la
    // derecha. La columna del medio es el nombre, que es lo único que se puede comparar entre los dos juegos.
    //
    //   aoweb  clase       dmg   resist   nuestro (Declares.bas:117)
    //     1    Mago        1.00     0        2
    //     2    Clerigo     0.88     4        1
    //     3    Guerrero    0.00    12        3
    //     4    Asesino     0.76     5        4
    //     6    Bardo       0.93     2        6
    //     7    Druida      0.92     3        7
    //     8    Paladin     0.78     5        9
    //     9    Cazador     0.00     6       10
    //
    // Las clases que aoweb no tiene —Ladrón, Gladiador, Nigromante y los siete oficios— quedan con el valor
    // por defecto de aoweb para lo que no está en la tabla: daño ×1 y resistencia 0.
    private static readonly System.Collections.Generic.Dictionary<int, double> DanoPorClase = new()
    {
        [2] = 1.00,   // Mago
        [1] = 0.88,   // Clérigo
        [3] = 0.00,   // Guerrero
        [4] = 0.76,   // Asesino
        [6] = 0.93,   // Bardo
        [7] = 0.92,   // Druida
        [9] = 0.78,   // Paladín
        [10] = 0.00,  // Cazador
        // EL NIGROMANTE NO ESTABA en la tabla de aoweb y caía al 1.00 del Mago, con más vida, golpe y armadura que él. DECIDIDO POR EL
        // AUTOR (10/10): el del Clérigo, el otro lanzador con armadura.
        [18] = 0.88,  // Nigromante
    };

    private static readonly System.Collections.Generic.Dictionary<int, int> ResistenciaPorClase = new()
    {
        [2] = 0,    // Mago
        [1] = 4,    // Clérigo
        [3] = 12,   // Guerrero
        [4] = 5,    // Asesino
        [6] = 2,    // Bardo
        [7] = 3,    // Druida
        [9] = 5,    // Paladín
        [10] = 6,   // Cazador
        // LOS QUE NO ESTABAN en la tabla de aoweb caían a 0. DECIDIDO POR EL AUTOR (10/10): el Nigromante como el Druida, y los
        // luchadores Mercenario y Gladiador como el Cazador.
        [18] = 3,   // Nigromante
        [17] = 6,   // Mercenario
        [8] = 6,    // Gladiador
    };

    /// <summary>
    /// EL HECHIZO DE UN NPC CONTRA UN JUGADOR (aoweb <c>applyNpcSpellDamageToUser</c>). Es una cuenta MÁS CORTA que la de
    /// jugador contra jugador, y la diferencia es de aoweb: acá no entra ni la diferencia de nivel ni la penetración —una
    /// criatura no tiene nivel ni equipo— así que sólo frena lo que el blanco lleva puesto y lo que le da su clase.
    ///
    /// <para>Sin el corte de la diferencia de nivel, además, no pasa lo que pasaría con la fórmula del PvP: un personaje de
    /// nivel alto quedaría inmune a la magia de cualquier criatura, porque su habilidad la dobla siempre.</para>
    ///
    /// <para>El piso lo pone el que llama: aoweb devuelve el número pelado.</para>
    /// </summary>
    public static int DanoDeNpcAJugador(int dano, int resistenciaDelEquipo, int resistenciaDeSuClase,
                                        int defensaMagicaDelEquipo)
    {
        int resistencia = resistenciaDelEquipo + resistenciaDeSuClase;
        int queda = dano;
        if (resistencia > 0) queda -= (int)System.Math.Floor(queda * (double)resistencia / 100.0);
        return queda - defensaMagicaDelEquipo;
    }

    /// <summary>El modificador de daño mágico de una clase; 1 para la que no está en la tabla.</summary>
    public static double ModDanoDeLaClase(int clase) => DanoPorClase.GetValueOrDefault(clase, 1.0);

    /// <summary>La resistencia mágica que da una clase, en puntos de porcentaje; 0 para la que no está.</summary>
    public static int ModResistenciaDeLaClase(int clase) => ResistenciaPorClase.GetValueOrDefault(clase, 0);
}
