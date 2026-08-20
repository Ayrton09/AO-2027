namespace CoverAO.Server.Game;

/// <summary>
/// EL FAMILIAR: la criatura propia de un Mago, Druida o Cazador — se elige una vez, se llama y se retira con
/// un hechizo, pelea al lado de su dueño, sube de nivel y se guarda con el personaje.
///
/// <para><b>ESTO ES UN AGREGADO NUESTRO, NO UN PORT.</b> El original tenía familiares y los apagó: quedan las
/// 9 fichas rotuladas <c>'FAMILIAR</c> en <c>NPCs.dat</c> (con 0 apariciones en el mundo), los hechizos 59 y
/// 79, el pergamino 1168, la Veterinaria y seis mensajes de locale — y en el cliente VB6 los controles del
/// panel, todos en <c>Visible = 0</c>, más una imagen que dice "NO DISPONIBLE PARA ESTA CLASE" que ya nadie
/// carga. O sea que la data está y el código no, a propósito. Todo lo de acá se decidió mirando esa data;
/// el reparto por clase, la curva y las habilidades salen de un <c>modFamiliar.bas</c> de otro fork que se usó
/// como REFERENCIA DE DISEÑO y no como fuente: ese módulo no compila ni contra su propio proyecto.</para>
///
/// <para>Consecuencia que hay que tener presente: el oráculo VB6 no puede validar nada de este subsistema,
/// porque allá no existe. Lo cubren flows propios. Ver <c>fase5/familiares-plan.md</c>.</para>
/// </summary>
public sealed class Familiar
{
    /// <summary>Qué familiar es (1..9), o 0 si el personaje todavía no eligió — o si su clase no lleva.</summary>
    public byte Tipo { get; set; }

    public string Nombre { get; set; } = "";
    public byte Nivel { get; set; }
    public long Exp { get; set; }
    /// <summary>Experiencia que le falta para el próximo nivel (VB6 <c>ELU</c>).</summary>
    public long Elu { get; set; }

    public long Vida { get; set; }
    public long VidaMaxima { get; set; }
    public long GolpeMinimo { get; set; }
    public long GolpeMaximo { get; set; }

    /// <summary>Las cuatro ranuras de habilidad, en el orden en que se aprenden. 0 = vacía.</summary>
    public int[] Habilidades { get; } = new int[4];

    /// <summary>Muerto: no se puede llamar hasta revivirlo.</summary>
    public bool Muerto { get; set; }

    /// <summary>El <c>CharIndex</c> de la criatura mientras está puesta en el mundo; 0 si no está llamada.</summary>
    public short CharIndexEnElMundo { get; set; }

    public bool Existe => Tipo != 0;
    public bool Invocado => CharIndexEnElMundo != 0;

    // ---- LAS TABLAS -------------------------------------------------------------------------------------

    /// <summary>Los tipos, con el número que va al `.chr` y al paquete.</summary>
    public const byte Fuego = 1, Agua = 2, Tierra = 3, FuegoFatuo = 4, Ely = 5, Ent = 6, Tigre = 7, Lobo = 8, Oso = 9;

    public const byte TipoMaximo = 9;
    /// <summary>Techo de nivel del familiar (VB6 <c>STAT_MAXELV</c>).</summary>
    public const byte NivelMaximo = 50;
    /// <summary>Techo de vida (VB6 <c>STAT_MAXHP</c>).</summary>
    public const long VidaTope = 999;
    /// <summary>Lo que le falta para el nivel 2 al nacer.</summary>
    public const long EluInicial = 300;

    /// <summary>La ficha de <c>NPCs.dat</c> de cada tipo. Son las 9 anotadas <c>'FAMILIAR</c> y no las tocó
    /// nadie más: ninguna tiene apariciones en el mundo.</summary>
    public static short FichaDe(byte tipo) => tipo switch
    {
        Fuego => 128,        // Elemental de fuego
        Agua => 127,         // Elemental de agua
        Tierra => 129,       // Elemental de tierra
        FuegoFatuo => 126,   // Fuego fatuo
        Ely => 132,          // Ely
        Ent => 145,          // Ent
        Tigre => 130,        // Tigre
        Lobo => 133,         // Lobo
        Oso => 131,          // Oso pardo
        _ => 0,
    };

    /// <summary>Un familiar MÁGICO pelea con hechizos y uno FÍSICO a los golpes. Decide qué habilidades puede
    /// aprender.</summary>
    public static bool EsMagico(byte tipo) => tipo is Fuego or Agua or Tierra or FuegoFatuo or Ely;

    /// <summary>Las clases que llevan familiar, con su lista. El Mago se queda con los elementales, el Ely y el
    /// fuego fatuo; el Druida y el Cazador con las bestias. El resto de las clases no lleva ninguno — y eso es
    /// lo que dice la imagen que el cliente original tenía preparada, "NO DISPONIBLE PARA ESTA CLASE".</summary>
    public static byte[] TiposDeLaClase(byte clase) => clase switch
    {
        GameLimits.ClaseMago => new[] { Ely, Fuego, Tierra, Agua, FuegoFatuo },
        GameLimits.ClaseDruida or GameLimits.ClaseCazador => new[] { Ent, Oso, Tigre, Lobo },
        _ => System.Array.Empty<byte>(),
    };

    public static bool ClasePuedeLlevar(byte clase, byte tipo) =>
        System.Array.IndexOf(TiposDeLaClase(clase), tipo) >= 0;

    /// <summary>Lo que gana por nivel cada tipo. El Oso es el que más aguanta y el fuego fatuo el más frágil.
    /// El elemental de tierra no estaba en la tabla de referencia y cae en el promedio, igual que allá.</summary>
    public static (int Vida, int Golpe) AumentoPorNivel(byte tipo) => tipo switch
    {
        Ent => (18, 5),
        Oso => (35, 6),
        Tigre => (20, 6),
        Lobo => (23, 5),
        Ely => (20, 3),
        Fuego => (25, 4),
        Agua => (25, 4),
        FuegoFatuo => (15, 2),
        _ => (18, 5),
    };

    // ---- HABILIDADES ------------------------------------------------------------------------------------
    // Los números son los del enum de referencia. Sólo dos hacen algo hoy en nuestro motor -CURAR y
    // PARALIZA_AL_GOLPE-; el resto se aprende, se guarda y se muestra, y está anotado en
    // fase5/familiares-plan.md como pendiente. Se prefirió dejarlas visibles y sin efecto antes que inventar
    // un efecto que no se parezca a nada, y antes que esconder que el familiar sube de nivel.

    public const int HabCurar = 5, HabParaliza = 9, HabGolpeParaliza = 10, HabGolpeEntorpece = 11,
                     HabGolpeDesarma = 12, HabGolpeCiega = 13, HabGolpeEnvenena = 14, HabTormenta = 15,
                     HabDesencantar = 22, HabInmoviliza = 24, HabDetectar = 62, HabMisil = 92, HabDescarga = 93;

    /// <summary>Qué habilidad aprende este tipo AL LLEGAR a este nivel, y en qué ranura. Devuelve
    /// <c>(0, 0)</c> si en ese nivel no aprende nada.</summary>
    public static (int Ranura, int Habilidad) AprendeEnNivel(byte tipo, int nivel) => (nivel, tipo) switch
    {
        (10, Ely) => (0, HabCurar),
        (10, FuegoFatuo) => (0, HabMisil),

        (15, Ent) => (1, HabGolpeEnvenena),
        (15, Tigre) or (15, Lobo) => (1, HabGolpeEntorpece),
        (15, Ely) or (15, FuegoFatuo) => (1, HabInmoviliza),

        (20, Ent) or (20, Tigre) or (20, Lobo) => (2, HabGolpeParaliza),
        (20, Ely) or (20, FuegoFatuo) => (2, HabDescarga),
        (20, Fuego) => (2, HabTormenta),
        (20, Tierra) => (2, HabInmoviliza),
        (20, Agua) => (2, HabParaliza),

        (30, Ent) or (30, Oso) => (3, HabGolpeDesarma),
        (30, Tigre) or (30, Lobo) => (3, HabGolpeCiega),
        (30, Ely) => (3, HabDesencantar),
        (30, FuegoFatuo) => (3, HabDetectar),

        _ => (0, 0),
    };

    public static string NombreDeHabilidad(int habilidad) => habilidad switch
    {
        HabInmoviliza => "Inmoviliza",
        HabParaliza => "Paraliza",
        HabDescarga => "Lanza descargas",
        HabTormenta => "Tormenta de fuego",
        HabDesencantar => "Desencanta al amo",
        HabCurar => "Cura al amo",
        HabMisil => "Lanza misiles magicos",
        HabDetectar => "Detecta invisibles",
        HabGolpeParaliza => "Paraliza con los golpes",
        HabGolpeEntorpece => "Entorpece con los golpes",
        HabGolpeDesarma => "Desarma con los golpes",
        HabGolpeCiega => "Ciega con los golpes",
        HabGolpeEnvenena => "Envenena con los golpes",
        _ => "Desconocida",
    };

    public bool Sabe(int habilidad) => System.Array.IndexOf(Habilidades, habilidad) >= 0;

    // ---- CICLO DE VIDA ----------------------------------------------------------------------------------

    /// <summary>Lo deja como recién elegido, con las barras de su ficha.</summary>
    public void Nacer(byte tipo, string nombre, long vida, long golpeMin, long golpeMax)
    {
        Tipo = tipo;
        Nombre = nombre;
        Nivel = 1;
        Exp = 0;
        Elu = EluInicial;
        VidaMaxima = Vida = vida;
        GolpeMinimo = golpeMin;
        GolpeMaximo = golpeMax;
        Muerto = false;
        CharIndexEnElMundo = 0;
        System.Array.Clear(Habilidades);
    }

    public void Limpiar()
    {
        Tipo = 0; Nombre = ""; Nivel = 0; Exp = 0; Elu = 0;
        Vida = VidaMaxima = GolpeMinimo = GolpeMaximo = 0;
        Muerto = false; CharIndexEnElMundo = 0;
        System.Array.Clear(Habilidades);
    }

    /// <summary>
    /// Le suma experiencia y lo sube de nivel las veces que haga falta. Devuelve los avisos para el dueño, en
    /// orden, o una lista vacía si no subió.
    ///
    /// <para>La curva es la de la referencia: <c>ELU</c> arranca en 300 y se multiplica por 1,3 hasta el nivel
    /// 10, por 1,4 hasta el 24 y por 1,1 de ahí en más. Al llegar al techo se le corta la cuenta.</para>
    /// </summary>
    public System.Collections.Generic.List<string> GanarExperiencia(long cuanto)
    {
        var avisos = new System.Collections.Generic.List<string>();
        if (!Existe || cuanto <= 0) return avisos;
        Exp += cuanto;

        while (Exp >= Elu && Elu > 0)
        {
            if (Nivel >= NivelMaximo) { Exp = 0; Elu = 0; break; }

            Exp -= Elu;
            Nivel++;
            // El nivel del familiar sale de SU contador. En el módulo de referencia salía del nivel del
            // JUGADOR (`.Familiar.Nivel = .Stats.ELV + 1`), que es uno de los defectos que prueban que ese
            // código nunca se ejecutó: con un dueño de nivel 30, el familiar saltaba a 31 en su primer nivel.
            Elu = Nivel < 11 ? (long)(Elu * 1.3)
                : Nivel < 25 ? (long)(Elu * 1.4)
                : (long)(Elu * 1.1);

            var (masVida, masGolpe) = AumentoPorNivel(Tipo);
            VidaMaxima = System.Math.Min(VidaTope, VidaMaxima + masVida);
            GolpeMaximo += masGolpe;
            GolpeMinimo += masGolpe;
            Vida = VidaMaxima;   // subir de nivel lo cura del todo

            avisos.Add($"{Nombre} subio al nivel {Nivel}.");
            if (masVida > 0) avisos.Add($"{Nombre} gano {masVida} puntos de vida.");
            if (masGolpe > 0) avisos.Add($"El golpe de {Nombre} aumento en {masGolpe} puntos.");

            var (ranura, habilidad) = AprendeEnNivel(Tipo, Nivel);
            if (habilidad != 0 && ranura is >= 0 and < 4)
            {
                Habilidades[ranura] = habilidad;
                avisos.Add($"¡{Nombre} aprendio {NombreDeHabilidad(habilidad)}!");
            }
        }
        return avisos;
    }
}
