namespace CoverAO.Server.Game;

/// <summary>
/// Level progression, ported faithfully from CheckUserLevel (Modulo_UsUaRiOs.bas:357) — the
/// DETERMINISTIC part: consuming Exp, advancing ELV, and setting ELU from the Niveles.dat curve
/// (levelELU). The HP/mana/stamina gains in the original are RNG-based and are not modeled here.
///
/// FIDELITY NOTE: the source does `.Stats.ELU = levelELU(.Stats.ELV)` BEFORE `.Stats.ELV = .Stats.ELV + 1`,
/// so ELU lags the curve by one step (both L1->L2 and L2->L3 cost Nivel1). This port reproduces that
/// exact ordering; whether it is intended or an original off-by-one must be reconciled against a live
/// parity capture. Locked by golden tests (LevelingTests) so the behavior can't drift silently.
/// </summary>
public sealed class Leveling
{
    public const int MaxLevel = 50; // STAT_MAXELV (Declares.bas:665)
    private const int MaxHpCap = 999;          // STAT_MAXHP (Declares.bas:666)
    private const int MaxStaCap = 999;         // STAT_MAXSTA (Declares.bas:667)
    private const int MaxManaCap = 9999;       // STAT_MAXMAN (Declares.bas:668)
    private const int MaxHitUnder36 = 99;      // STAT_MAXHIT_UNDER36
    private const int MaxHitOver36 = 999;      // STAT_MAXHIT_OVER36
    private const int StaDef = 15;             // AumentoSTDef (Declares.bas:509) y sus variantes por clase
    private readonly IReadOnlyDictionary<int, long> _curve;
    private readonly IReadOnlyDictionary<int, double> _lifeMod;   // MODVIDA por clase (Balance.dat)
    private readonly Random _rng;

    public Leveling(IReadOnlyDictionary<int, long> levelExp,
        IReadOnlyDictionary<int, double>? lifeMod = null, Random? rng = null)
    {
        _curve = levelExp;
        _lifeMod = lifeMod ?? new Dictionary<int, double>();
        _rng = rng ?? new Random();
    }

    // Ids de clase = eClass (Declares.bas:117-136), el MISMO espacio que indexa MODVIDA en Balance.dat y las
    // secciones de NACIMIENTO.dat: Clerigo=1, Mago=2, Guerrero=3, Asesino=4, ladron=5, Bardo=6, Druida=7,
    // Gladiador=8, Paladin=9, Cazador=10, PescadoR=11, Herrero=12, Leñador=13, Minero=14, Carpintero=15,
    // Sastre=16, Mercenario=17, Nigromante=18.
    private const int Clerigo = 1, Mago = 2, Guerrero = 3, Asesino = 4, Ladron = 5, Bardo = 6, Druida = 7,
        Gladiador = 8, Paladin = 9, Cazador = 10, Pescador = 11, Herrero = 12, Lenador = 13, Minero = 14,
        Carpintero = 15, Sastre = 16, Mercenario = 17, Nigromante = 18;

    /// <summary>
    /// Stamina que gana por nivel (el <c>AumentoSTA</c> del Select Case de CheckUserLevel,
    /// Modulo_UsUaRiOs.bas:445-521, con las constantes de Declares.bas:509-516).
    /// OJO: <c>AumentoStBandido</c> y <c>AumentoSTTrabajador</c> están declaradas pero NO se usan en ninguna
    /// rama — el Gladiador y los trabajadores no-mineros/leñadores/pescadores caen en AumentoSTDef.
    /// </summary>
    private static int StaminaGain(int klass) => klass switch
    {
        Mago => StaDef - 1,      // AumentoSTMago
        Ladron => StaDef + 3,    // AumentoSTLadron
        Pescador => StaDef + 20, // AumentoSTPescador
        Lenador => StaDef + 23,  // AumentoSTLeñador
        Minero => StaDef + 25,   // AumentoSTMinero
        _ => StaDef,
    };

    /// <summary>
    /// Golpe que gana por nivel (<c>AumentoHIT</c> del mismo Select Case). El VB6 incrementa <c>.Stats.ELV</c>
    /// ANTES del Select, así que los IIf comparan contra el nivel YA subido — por eso Grant llama a
    /// ApplyLevelGains después de incrementar p.Level.
    /// </summary>
    private static int HitGain(int klass, int level) => klass switch
    {
        Guerrero or Cazador => level > 35 ? 2 : 3,
        Asesino or Paladin => level > 35 ? 1 : 3,
        Gladiador => level > 40 ? 2 : 3,
        Nigromante => level > 40 ? 1 : 3,
        Mercenario => level > 30 ? 2 : 3,
        Mago or Ladron or Pescador => 1,
        _ => 2,   // Clérigo, Bardo, Druida, Herrero, Carpintero, Sastre y el Case Else
    };

    /// <summary>Maná que gana por nivel: múltiplo de la Inteligencia según la clase (0 = clase sin maná).</summary>
    private static double ManaFactor(int klass) => klass switch
    {
        Mago => 2.8,
        Nigromante => 2.5,
        Clerigo or Bardo or Druida => 2.0,
        Asesino or Paladin => 1.0,
        _ => 0.0,
    };

    /// <summary>levelELU(level) = Niveles.dat "Nivel&lt;level&gt;" — exp to advance FROM this level.</summary>
    public long EluForLevel(int level) => _curve.TryGetValue(level, out var e) ? e : 0;

    /// <summary>Initializes a fresh character's progression at level 1.</summary>
    public void InitNewCharacter(Player p)
    {
        p.Level = 1;
        p.Exp = 0;
        p.Elu = EluForLevel(1);
    }

    /// <summary>Lo que se ganó en UN nivel. El original avisa cada uno por separado y sólo si es &gt; 0
    /// (locales 197/198/199/200, Modulo_UsUaRiOs.bas:559-573), así que hay que llevarlos discriminados.</summary>
    public readonly record struct Ganancia(int Hp, int Stamina, int Mana, int Golpe);

    /// <summary>Awards experience and applies any level-ups. Returns the number of levels gained.
    /// <paramref name="detalle"/> recibe una entrada por nivel, en orden, para poder avisarlas.</summary>
    /// <summary><c>MAXEXP</c> (Declares.bas:345): el techo de la experiencia ACUMULADA. El original lo aplica
    /// en los tres lugares donde suma —al matar un NPC (MODULO_NPCs.bas:135), al repartir el daño
    /// (SistemaCombate.bas:2037) y en Modulo_UsUaRiOs.bas:1215—, o sea sobre el total después de sumar. Acá va
    /// en el ÚNICO sitio por donde entra toda la experiencia, así que cubre también los caminos que se
    /// agreguen. No es alcanzable jugando —el nivel topa en 50 mucho antes—, pero un `/EXP` de GM repetido sí
    /// llega, y ahí el original corta y este port no lo hacía.</summary>
    public const long MaxExp = 99_999_999;   // Declares.bas:345 MAXEXP

    public int Grant(Player p, long xp, List<Ganancia>? detalle = null)
    {
        p.Exp += xp;
        if (p.Exp > MaxExp) p.Exp = MaxExp;
        int gained = 0;
        while (p.Elu > 0 && p.Exp >= p.Elu && p.Level < MaxLevel)
        {
            p.Exp -= p.Elu;                 // Modulo_UsUaRiOs.bas:391
            p.Elu = EluForLevel(p.Level);   // :394  (ELV read BEFORE the increment below)
            p.FreeSkillPoints += 5;         // :396  (Pts = Pts + 5)
            p.Level = (byte)(p.Level + 1);  // :445
            var g = ApplyLevelGains(p);     // :397-560 — HP/maná/stamina/golpe de este nivel
            detalle?.Add(g);
            gained++;
        }
        if (p.Level >= MaxLevel) { p.Exp = 0; p.Elu = 0; } // :381-384
        return gained;
    }

    /// <summary>
    /// Ganancias de un nivel (VB6 CheckUserLevel, Modulo_UsUaRiOs.bas:397-560). Antes subir de nivel era
    /// puramente cosmético: un personaje de nivel 50 seguía con los 100 HP del nivel 1.
    ///
    /// HP: <c>Promedio = ModVida(clase) - (21 - Constitución) * 0.5</c> y después una tirada de
    /// <c>RandomNumber(0,100)</c> reparte el resultado alrededor de ese promedio. Si el promedio cae en un
    /// medio exacto (.5) el original usa 4 tramos (±1.5 / ±0.5) y si es entero usa 5 (±2 / ±1 / 0); en
    /// Balance.dat los dos repartos son uniformes (S = 25×4, E = 20×5). Maná = múltiplo de Inteligencia
    /// según la clase, stamina y golpe por tabla. Todo con los topes del original.
    /// </summary>
    private Ganancia ApplyLevelGains(Player p)
    {
        int ganadoHp = 0;
        double modVida = _lifeMod.TryGetValue(p.Class, out var mv) ? mv : 0;
        if (modVida > 0)
        {
            double promedio = modVida - (21 - p.Attributes[GameLimits.AttrConstitucion]) * 0.5;
            int aux = _rng.Next(0, 101);
            double aumentoHp;
            if (Math.Abs(promedio - Math.Floor(promedio) - 0.5) < 1e-9)   // promedio semientero → 4 tramos de 25
                aumentoHp = aux <= 25 ? promedio + 1.5 : aux <= 50 ? promedio + 0.5 : aux <= 75 ? promedio - 0.5 : promedio - 1.5;
            else                                                          // promedio entero → 5 tramos de 20
                aumentoHp = aux <= 20 ? promedio + 2 : aux <= 40 ? promedio + 1 : aux <= 60 ? promedio : aux <= 80 ? promedio - 1 : promedio - 2;
            if (aumentoHp > 0) { ganadoHp = (int)aumentoHp; p.MaxHp = Math.Min(MaxHpCap, p.MaxHp + ganadoHp); }
        }

        int ganadoSta = StaminaGain(p.Class);
        p.MaxStamina = Math.Min(MaxStaCap, p.MaxStamina + ganadoSta);

        int ganadoMana = 0;
        double manaFactor = ManaFactor(p.Class);
        if (manaFactor > 0)
        {
            // `Dim AumentoMANA As Integer` con `2.8 * Inteligencia` (Modulo_UsUaRiOs.bas:361 y :468): el
            // original REDONDEA al guardar, el cast de C# trunca. Un mago con inteligencia 16 ganaba 44 en vez
            // de 45, todos los niveles.
            ganadoMana = (int)Matematicas.AEntero(manaFactor * p.Attributes[GameLimits.AttrInteligencia]);
            p.MaxMana = Math.Min(MaxManaCap, p.MaxMana + ganadoMana);
        }

        int hit = HitGain(p.Class, p.Level);
        int hitCap = p.Level < 36 ? MaxHitUnder36 : MaxHitOver36;
        p.MaxHit = Math.Min(hitCap, p.MaxHit + hit);
        p.MinHit = Math.Min(hitCap, p.MinHit + hit);

        // EL VB6 LLENA SÓLO LA VIDA al subir de nivel (`.Stats.MinHP = .Stats.MaxHP`, Modulo_UsUaRiOs.bas:575): el maná y la
        // energía suben de tope y quedan donde estaban, y el cliente original lo sabe (`ActualizarEst(HP, HP, ...)` en
        // HandleUpdateUserStatsForLevel iguala sólo la vida). Acá se llenaban las tres, con un comentario que decía que el original
        // lo hacía. Llenar todo es la regla de la casa `CuraAlSubirNivel`.
        p.Hp = p.MaxHp;
        return new Ganancia(ganadoHp, ganadoSta, ganadoMana, hit);
    }
}
