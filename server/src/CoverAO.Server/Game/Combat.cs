namespace CoverAO.Server.Game;

/// <summary>
/// Melee damage, ported faithfully from CalcularDano (SistemaCombate.bas:280, final formula line 420):
///
///   CalcularDano = (3*DanoArma + (DanoMaxArma/5)*max(0, Fuerza-15) + DanoUsuario) * ModifClase
///
/// The RNG-resolved inputs (weapon roll, user-hit roll) are parameters so the arithmetic is a pure,
/// golden-testable function. The result is assigned to a VB6 Long, which rounds half-to-even (banker's
/// rounding) — reproduced here with MidpointRounding.ToEven. Special weapons (dragon-slayer, arco-demonio),
/// mount/ship bonuses and magic-ring bonuses from the original are not modeled yet.
/// </summary>
public static class Combat
{
    /// <param name="weaponDamage">RandomNumber(weapon.MinHIT, weapon.MaxHIT), or wrestling roll.</param>
    /// <param name="weaponMaxDamage">weapon.MaxHIT (wrestling: 3).</param>
    /// <param name="userHit">RandomNumber(user MinHIT, MaxHIT) — the character's own hit roll.</param>
    /// <param name="strength">Fuerza attribute.</param>
    /// <param name="classModifier">ModClase(class).DanoArmas or .DanoWrestling from Balance.dat.</param>
    public static long CalcularDano(long weaponDamage, long weaponMaxDamage, long userHit, int strength, double classModifier)
    {
        double mod = (float)classModifier; // VB6's ModifClase is a Single: truncate to float32 precision first
        double raw = (3.0 * weaponDamage
                      + (weaponMaxDamage / 5.0) * Math.Max(0, strength - 15)
                      + userHit)
                     * mod;
        return (long)Math.Round(raw, MidpointRounding.ToEven); // VB6 CLng rounds half-to-even
    }

    private static long Clng(double d) => (long)Math.Round(d, MidpointRounding.ToEven);

    /// <summary>Unarmed attack power (PoderAtaqueWrestling, SistemaCombate.bas:157). Two VB6 Long rounds.</summary>
    public static long PoderAtaqueWrestling(int wrestlingSkill, int agility, int level, double attackMod)
    {
        double raw = wrestlingSkill < 31 ? wrestlingSkill * attackMod
                   : wrestlingSkill < 61 ? (wrestlingSkill + agility) * attackMod
                   : wrestlingSkill < 91 ? (wrestlingSkill + 2 * agility) * attackMod
                   : (wrestlingSkill + 3 * agility) * attackMod;
        long temp = Clng(raw);
        return Clng(temp + 2.5 * Math.Max(level - 12, 0));
    }

    /// <summary>Evasion power (PoderEvasion, SistemaCombate.bas:77). Two VB6 Long rounds.</summary>
    public static long PoderEvasion(int tacticsSkill, int agility, int level, double evasionMod)
    {
        long lTemp = Clng((tacticsSkill + tacticsSkill / 33.0 * agility) * evasionMod);
        return Clng(lTemp + 2.5 * Math.Max(level - 12, 0));
    }

    /// <summary>Hit probability % (UserImpacto*, line 212): clamp(50 + (atk-eva)*0.4, 10, 90).
    ///
    /// <para>Y si la VÍCTIMA está MEDITANDO se le recorta la evasión un 25% (<c>UsuarioImpacto</c>,
    /// SistemaCombate.bas:1123-1128, con el comentario del propio autor: "Se reduce la evasion un 25%"). Es un
    /// segundo tramo con su propio techo de 90 y <b>sin</b> el piso de 10, así que con la chance ya en el piso
    /// meditar la sube de 10 a 32. Faltaba entera: meditar en medio de una pelea no costaba nada.</para>
    ///
    /// <para>Es de PvP y sólo de PvP: <c>NpcImpacto</c> (:250) y <c>NpcImpactoNpc</c> (:782) calculan la misma
    /// línea sin esta rama, así que una criatura no aprovecha que estés meditando.</para>
    ///
    /// <para>El <c>ProbEvadir</c> del original es un <c>Long</c>, así que el <c>* 0.75</c> entra al entero con
    /// el redondeo AL PAR de VB6 y no truncado: con la chance en 10 da 67,5 → <b>68</b>, no 67.</para></summary>
    public static long HitChance(long poderAtaque, long poderEvasion, bool victimaMeditando = false)
    {
        long prob = Math.Clamp(Clng(50 + (poderAtaque - poderEvasion) * 0.4), 10, 90);
        if (!victimaMeditando) return prob;
        long probEvadir = Clng((100 - prob) * 0.75);
        return Math.Min(90, 100 - probEvadir);
    }

    /// <summary>Cuánto daño aguanta la MEDITACIÓN antes de romperse, cuando el golpe lo da una criatura
    /// (<c>NpcDaño</c>, SistemaCombate.bas:594-600):
    /// <c>Fix(MinHP / 100 * Inteligencia * Meditar / 100 * 12 / (RandomNumber(0, 5) + 7))</c>. Se corta si el
    /// daño es <b>mayor</b> que esto — o sea que con la skill Meditar en 0 el umbral es 0 y la parte cualquier
    /// golpe.
    ///
    /// <para>El <c>MinHP</c> es el de DESPUÉS de descontar el golpe: el original resta en la línea anterior. Y
    /// el <c>Fix</c> trunca hacia cero, no redondea — es la excepción a la regla de la casa, porque acá no hay
    /// asignación a un entero de por medio.</para>
    ///
    /// <para>Es la mitad PvE de la regla. En PvP no hay umbral que valga: <c>UsuarioAtacadoPorUsuario</c> la
    /// corta siempre.</para></summary>
    public static long UmbralRompeMeditacion(int hpDespuesDelGolpe, int inteligencia, int meditar, int dado)
        => (long)Math.Truncate(hpDespuesDelGolpe / 100.0 * inteligencia * meditar / 100.0 * 12 / (dado + 7));

    /// <summary>Shield block/evasion bonus (PoderEvasionEscudo, SistemaCombate.bas): added to a defender's
    /// evasion when a shield is equipped. VB6: (Defensa skill * ModClase.Escudo) / 2, assigned to a Long.</summary>
    public static long PoderEvasionEscudo(int defenseSkill, double shieldMod)
        => Clng(defenseSkill * shieldMod / 2.0);

    /// <summary>
    /// Chance (10..90) de RECHAZAR con el escudo un golpe que ya falló: <c>max(10, min(90, 100 * Defensa /
    /// (Defensa + Tácticas)))</c>. Es la segunda tirada del original, en las dos direcciones —PvP
    /// (<c>SistemaCombate.bas:1138</c>) y NPC→jugador (<c>:261</c>)—, y la ÚNICA vía por la que la skill
    /// Defensa sube por uso.
    ///
    /// El <c>/</c> del VB6 es división de PUNTO FLOTANTE y el resultado entra a <c>MinimoInt</c>, así que hay
    /// una conversión a entero con el redondeo half-to-even — el propio comentario de la fuente lo dice:
    /// "Chances are rounded". Con la división entera de C# la chance sale sistemáticamente más BAJA, porque
    /// trunca en vez de redondear. Dos casos que lo separan:
    ///   · Defensa 2, Tácticas 1 → 66,67 → el original 67, el truncado 66.
    ///   · Defensa 3, Tácticas 5 → 37,50 → el original 38 (half-to-even sube porque 37 es impar), truncado 37.
    ///
    /// Devuelve 0 cuando ambas skills son 0: el original se olvidó ese guard en la rama PvP (sí lo tiene en
    /// la de NPC, <c>:259</c>) y ahí divide por cero; el ErrHandler se traga la excepción y no hay rechazo,
    /// que es exactamente lo que devolver 0 produce, sin la excepción.
    /// </summary>
    public static int ProbabilidadRechazoEscudo(int defenseSkill, int tacticsSkill)
    {
        if (defenseSkill + tacticsSkill <= 0) return 0;
        long p = Clng(100.0 * defenseSkill / (defenseSkill + tacticsSkill));
        return (int)Math.Max(10, Math.Min(90, p));
    }

    /// <summary>
    /// Chance de APUÑALAR por la espalda (VB6 <c>DoApuñalar</c>, <c>Trabajo.bas:1766-1780</c>). Cuatro ramas
    /// por clase, tres de ellas polinomios cúbicos sobre la skill Apuñalar y una lineal para el resto.
    ///
    /// Los coeficientes van tal cual, sin "redondear a algo más lindo": son los del original y de ellos sale
    /// la curva entera de la clase. Con skill 0 ya hay chance (entre 4 y 5%), y con skill 100 el asesino llega
    /// a ~34% contra ~8% de un guerrero — esa brecha ES la clase.
    ///
    /// <c>Int()</c> de VB6 trunca hacia abajo, no redondea: acá va <c>Math.Floor</c> y no el <c>Clng</c>
    /// half-to-even que usa el resto del combate. No son intercambiables.
    /// </summary>
    public static int SuerteApunalar(int classId, int stabSkill)
    {
        double s = stabSkill;
        double suerte = classId switch
        {
            4 => ((0.00003 * s - 0.002) * s + 0.098) * s + 4.25,          // Asesino
            1 or 9 or 16 => ((0.000003 * s + 0.0006) * s + 0.0107) * s + 4.93,  // Clérigo, Paladín, Sastre
            6 => ((0.000002 * s + 0.0002) * s + 0.032) * s + 4.81,        // Bardo
            _ => 0.0361 * s + 4.39,
        };
        return (int)Math.Floor(suerte);
    }

    /// <summary>
    /// Porción de experiencia que corresponde por un golpe (VB6 <c>CalcularDarExp</c>,
    /// <c>SistemaCombate.bas:2003-2012</c>), ANTES de descontarla del pool de la criatura.
    ///
    /// Tres recortes del original, en este orden y todos con consecuencia:
    ///   1. daño negativo cuenta como 0;
    ///   2. el daño se recorta al HP que le QUEDA — el golpe que mata no cobra por el exceso;
    ///   3. <c>Clng(daño * (GiveEXP / MaxHP))</c>, con el redondeo half-to-even de siempre.
    ///
    /// Devuelve 0 si la criatura no da experiencia o si su MaxHP es 0 (el original sale con
    /// <c>Exit Sub</c>), y también cuando la proporción redondea a cero: un rasguño a un dragón no paga.
    /// </summary>
    public static long ExpPorGolpe(long damage, int npcHpActual, int npcMaxHp, long npcGiveExp)
    {
        if (npcMaxHp <= 0) return 0;
        if (damage < 0) damage = 0;
        if (damage > npcHpActual) damage = npcHpActual;   // el remate no cobra de más
        if (damage <= 0) return 0;
        long exp = Clng(damage * ((double)npcGiveExp / npcMaxHp));
        return exp > 0 ? exp : 0;
    }

    /// <summary>
    /// Damage left after the victim's equipment absorbs it — ported from UserDañoUser
    /// (SistemaCombate.bas:1310-1349). The impact zone is rolled first (RandomNumber(bCabeza=1,
    /// bTorso=6)): zone 1 hits the head, where a helmet absorbs; zones 2..6 hit the body, where the
    /// armour absorbs (plus the shield's defence if one is equipped). The absorbed value is
    /// RandomNumber over the piece's [MinDef,MaxDef] (armour+shield ranges summed). Then
    /// <c>daño -= absorbido</c>, and <c>if daño &lt; 0 then daño = 1</c> — note the VB6 uses "&lt; 0",
    /// so a hit absorbed to exactly 0 stays 0 while an over-absorbed hit is clamped up to 1.
    /// The two RandomNumber calls are injected via <paramref name="zona"/> and <paramref name="rollDef"/>
    /// so the arithmetic/selection is a pure, golden-testable function.
    /// </summary>
    /// <param name="dano">Attacker damage from <see cref="CalcularDano"/>.</param>
    /// <param name="zona">RandomNumber(1,6): 1 = head, 2..6 = body.</param>
    /// <param name="casco">Equipped helmet's (MinDef,MaxDef), or null.</param>
    /// <param name="armadura">Equipped armour's (MinDef,MaxDef), or null.</param>
    /// <param name="escudo">Equipped shield's (MinDef,MaxDef), or null.</param>
    /// <param name="rollDef">Resolves RandomNumber(min,max) inclusive.</param>
    /// <param name="defensaExtra">Defensa PLANA que no viene de una pieza de equipo, sumada a lo absorbido —
    /// hoy sólo el <c>ExtraDEF</c> de la metamorfosis (regla de la casa). 0 en el original y en cualquier
    /// camino donde la regla esté apagada, así que la fórmula portada queda idéntica. Va DENTRO de la función
    /// y no restándoselo al resultado afuera para no perder el clamp: el original sube a 1 el golpe
    /// sobre-absorbido, y restar por fuera daría 0 o negativos.</param>
    public static long AplicarDefensa(long dano, int zona,
        (int Min, int Max)? casco, (int Min, int Max)? armadura, (int Min, int Max)? escudo,
        Func<int, int, int> rollDef, int defensaExtra = 0)
    {
        long absorbido = defensaExtra;   // las dos ramas SUMAN sobre esto: asignar acá las pisaría
        if (zona == 1) // PartesCuerpo.bCabeza
        {
            if (casco is { } c) absorbido += rollDef(c.Min, c.Max);
        }
        else           // torso / brazos / piernas → armadura (+ escudo)
        {
            if (armadura is { } a)
                absorbido += escudo is { } s
                    ? rollDef(a.Min + s.Min, a.Max + s.Max)
                    : rollDef(a.Min, a.Max);
        }
        dano -= absorbido;
        if (dano < 0) dano = 1;
        return dano;
    }

    /// <summary>Daño de un hechizo OFENSIVO sobre un JUGADOR. La misma fórmula corre en los dos caminos del
    /// original —el de área (<c>HechizoTerrenoHP</c> con <c>Accion = False</c>, modHechizos.bas:515) y el de un
    /// solo objetivo (<c>HechizoPropUsuario</c> Case 2, :1441)—, así que vive en un solo lugar. Tres piezas, en
    /// este orden:
    ///
    /// <list type="number">
    /// <item>bono de <b>2%</b> por nivel del lanzador — <c>Porcentaje(daño, 2 * ELV)</c>. Ojo el 2: la CURA de
    /// área y el daño a NPCs usan 3%. La asimetría es de la fuente;</item>
    /// <item><b>+<c>CuantoAumento</c></b> si el arma equipada es un báculo con <c>EfectoMagico = DañoMagico</c>;</item>
    /// <item><b>−<c>ResistenciaMagica</c></b> de cada pieza de defensa de la víctima, sumadas.</item>
    /// </list>
    ///
    /// El piso es <b>0</b>, no 1: <c>If daño &lt; 0 Then daño = 0</c>. Distinto de
    /// <see cref="AplicarDefensa"/>, donde el golpe físico siempre saca al menos 1 punto — un hechizo
    /// contra suficiente resistencia mágica se anula por completo.</summary>
    public static int DanoHechizoOfensivo(int tirada, int nivelLanzador, int bonoBaculo, int resistenciaVictima)
    {
        int dano = tirada + (int)Matematicas.Porcentaje(tirada, 2 * nivelLanzador);
        dano += bonoBaculo;
        dano -= resistenciaVictima;
        return dano < 0 ? 0 : dano;
    }

    /// <summary>Probabilidad (en %) de DESARMAR a la víctima de un golpe (VB6 <c>Desarmar</c>,
    /// Trabajo.bas:2283): <c>Wrestling * 0.2 + ELV * 0.66</c>, comparada contra un <c>RandomNumber(1,100)</c>.
    /// La tirada la hace el llamador para que esto sea una función pura.
    ///
    /// El VB6 asigna el resultado a un <c>Integer</c>, así que la parte decimal se **redondea** (y VB6 redondea
    /// al par en el .5, igual que el resto de las fórmulas de combate portadas acá).</summary>
    public static int ProbabilidadDesarmar(int wrestlingSkill, int nivel)
        => (int)Math.Round(wrestlingSkill * 0.2 + nivel * 0.66, MidpointRounding.ToEven);

    /// <summary>Tope del dado del GOLPE QUE PARALIZA (VB6 <c>GolpeParalizaUsuario</c>/<c>GolpeParalizaNPC</c>,
    /// SistemaCombate.bas:2280 y 2226): <c>RandomNumber(1, 500)</c>, y <c>RandomNumber(1, 300)</c> para
    /// <b>gladiador (8) y bardo (6)</b>, que son los que pelean a mano limpia. La probabilidad contra la que se
    /// compara es la misma de <see cref="ProbabilidadDesarmar"/>, así que paralizar de un golpe es entre 3 y 5
    /// veces más raro que desarmar (ese usa un dado de 100).</summary>
    public static int TopeDadoGolpeParaliza(int clase) => clase is 6 or 8 ? 300 : 500;

    /// <summary>Curación de un hechizo de área, y el daño de área sobre una CRIATURA: los dos usan el bono del
    /// <b>3%</b> por nivel (<c>HechizoTerrenoHP</c> con <c>Accion = True</c>, :494; y <c>HechizoPropNPC</c>,
    /// :1657). Sin resistencia mágica en el medio: a los NPCs no se les descuenta nada.</summary>
    public static int EfectoHechizoArea3Pct(int tirada, int nivelLanzador, int bonoBaculo = 0)
    {
        int v = tirada + (int)Matematicas.Porcentaje(tirada, 3 * nivelLanzador);
        v += bonoBaculo;
        return v < 0 ? 0 : v;
    }
}
