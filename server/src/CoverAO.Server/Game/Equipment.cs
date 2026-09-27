using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

/// <summary>
/// Shared equipment helpers used by both the packet dispatcher (equipping) and the NPC AI (defence
/// when an NPC hits a player). All defensive gear is <c>ObjType == 3</c>; the piece is distinguished by
/// SubTipo (0 armadura / 1 casco / 2 escudo), per InvUsuario.bas:772. The equipped item of a category
/// is derived from the inventory's <c>Equipped</c> flags — a single source of truth that can never
/// desync from what the client is shown.
/// </summary>
public static class Equipment
{
    /// <summary>
    /// ¿EL GÉNERO DEL JUGADOR PUEDE USAR ESA PRENDA? Sin marca (0) la usa cualquiera; con marca, sólo ese género.
    /// <c>eGenero</c> del original (Declares.bas:173): Hombre = 1, Mujer = 2.
    ///
    /// <para>La regla es la del original —<c>SexoPuedeUsarItem</c>, InvUsuario.bas:851-856, con su <c>Else …
    /// = True</c> para lo que no lleva marca, que es donde caen las 56 túnicas sin género— pero el DATO no. El VB6
    /// alimenta esa función con las claves <c>Mujer</c> y <c>Hombre</c> del obj.dat (FileIO.bas:959-960), y esas dos
    /// claves no existen en ningún obj.dat del repo: allá la regla nunca rechaza nada. Lo que la data sí trae es
    /// <c>Genero=2</c> en 43 prendas —todas de mujer; no hay una sola con <c>Genero=1</c>—, una clave que el VB6 no
    /// lee. Respetarla es una DECISIÓN DEL AUTOR del 2026-09-12, a partir del reporte del tester, y un apartamiento
    /// consciente de la paridad. El género de cada prenda sale de <see cref="GeneroDeLaPrenda"/>.</para>
    /// </summary>
    public static bool PuedeUsarloSuGenero(int generoDeLaPrenda, int generoDelJugador) =>
        generoDeLaPrenda <= 0 || generoDeLaPrenda == generoDelJugador;

    /// <summary><c>eGenero</c> del original (Declares.bas:173).</summary>
    public const int Hombre = 1, Mujer = 2;

    /// <summary>
    /// ¿DE QUÉ GÉNERO ES ESA PRENDA? Sólo la armadura de cuerpo tiene (<see cref="MiraElGenero"/>): la marca de la data si la
    /// trae, y si no, DE HOMBRE, salvo las túnicas y la ropa de novato de la Orca, que son de cualquiera (0).
    ///
    /// <para>DECISIÓN DEL AUTOR del 2026-09-27, sobre otro reporte del tester: "en todas las ropas, las de hombre se las pueden
    /// poner las mujeres". La data marca sólo las 43 de mujer; las de hombre no tienen marca, así que se las distingue por
    /// descarte: todo lo que no es de mujer es de hombre, salvo las túnicas (<see cref="EsTunica"/>), que las usa cualquiera.
    /// Y las Vestimentas Tribales (Newbies), la ropa con la que nace la Orca, porque no tienen versión de mujer (ver
    /// <see cref="RopaDeNovatoDeMujer"/>). A una mujer le quedan las de mujer y las túnicas.</para>
    /// </summary>
    public static int GeneroDeLaPrenda(ObjInfo o) =>
        !MiraElGenero(o) ? 0
        : o.Gender > 0 ? o.Gender
        : EsTunica(o) || o.Index == TribalesDeNovato ? 0
        : Hombre;

    /// <summary>¿Es una túnica? Por el nombre: la data no trae otra cosa que las distinga, y son 57, con y sin acento.</summary>
    public static bool EsTunica(ObjInfo o) =>
        o.Name.StartsWith("Túnica", StringComparison.OrdinalIgnoreCase) || o.Name.StartsWith("Tunica", StringComparison.OrdinalIgnoreCase);

    /// <summary>Las Vestimentas Tribales (Newbies), la ropa con la que nace la Orca (VB6 RellenarInventario, TCP.bas:2226).</summary>
    public const short TribalesDeNovato = 1087;

    /// <summary>
    /// LA ROPA DE NOVATO DE UNA MUJER: la versión de mujer de la que le toca por raza (VB6 RellenarInventario, TCP.bas:2212,
    /// que la da sólo por raza). Las de mujer tienen las mismas razas permitidas que sus pares de hombre y vienen en el mismo
    /// orden: 463/464/465 (Humano, Elfo, Drow) y 1283/1284/1285; 466 (Gnomo y Enano) y 563. La de la Orca no tiene versión de
    /// mujer y queda la misma, que por eso es de cualquiera (<see cref="GeneroDeLaPrenda"/>). Decisión del autor, 2026-09-27.
    /// </summary>
    public static short RopaDeNovatoDeMujer(short deHombre) => deHombre switch
    {
        463 => 1283,
        464 => 1284,
        465 => 1285,
        466 => 563,
        _ => deHombre,
    };

    /// <summary>
    /// ¿EN QUÉ PRENDAS SE MIRA EL GÉNERO? Sólo en la armadura de CUERPO: es la única rama del original que llama a
    /// <c>SexoPuedeUsarItem</c> (InvUsuario.bas:1151, <c>Case otArmadura</c> / <c>Case 0</c>); el casco (:1193) y el
    /// escudo (:1230) no la llaman, y el resto del equipo ni siquiera pasa por ahí.
    ///
    /// <para>Hoy la data no lo notaría —ninguno de los 43 objetos con marca es casco ni escudo— pero el gate sigue
    /// la FUENTE y no el catálogo: un obj.dat editado sí lo notaría.</para>
    /// </summary>
    public static bool MiraElGenero(ObjInfo o) => o.IsArmor;

    /// <summary>
    /// ¿QUÉ LE FALTA PARA USARLO? Devuelve el <c>LocaleMsg</c> del rechazo —268 nivel, 265 clase, 266 raza— o 0 si no
    /// le falta nada. Es el mismo orden en que los pide <c>EquiparInvItem</c> (VB6 InvUsuario.bas:925 el nivel, y
    /// después la rama del tipo), y el ítem mágico queda afuera del gate de clase como en el original: el
    /// <c>Case otItemsMagicos</c> (:940) es la única rama que no llama a <c>ClasePuedeUsarItem</c>.
    ///
    /// <para>Se extrajo para que la MONTURA pase por lo mismo que el equipo. El original no lo hace —el huargo es
    /// <c>otMonturas</c> y se va por <c>DoEquita</c> (Trabajo.bas:2338), donde la única compuerta de ficha es la skill
    /// de Equitación, así que allá un no-orco sí puede montar un huargo— pero la data trae la restricción escrita:
    /// los dos huargos llevan <c>RazasProhibidas=1,2,3,4,5</c>, que deja sólo al Orco, y hay 10 monturas con raza
    /// prohibida y 5 con clase. Aplicarla es una DECISIÓN DEL AUTOR del 2026-09-12, sobre el reporte del tester.</para>
    /// </summary>
    public static int QueLeFaltaParaUsarlo(ObjInfo o, int nivel, byte clase, byte raza)
    {
        if (o.MinLevel > 0 && nivel < o.MinLevel) return 268;             // "necesitás nivel N"
        if (!o.IsMagicItem && o.ClassForbidden(clase)) return 265;        // "tu clase no puede usar este objeto"
        if (o.RaceForbidden(raza)) return 266;                            // "tu raza no puede usar este objeto"
        return 0;
    }

    /// <summary>
    /// ¿QUÉ LE FALTA PARA MONTAR? Lo mismo que para el equipo, pero SÓLO AL SUBIRSE: al que ya está arriba no se le
    /// pregunta nada, o un personaje que dejó de cumplir —cambió de raza con una poción, o cambió la data— se quedaría
    /// trabado sobre el animal para siempre. Es el mismo orden del original, que envuelve con <c>If .flags.Montando
    /// = 0</c> sólo la rama de subirse (Trabajo.bas:2347). El GM pasa, como en el resto del equipo.
    /// </summary>
    public static int QueLeFaltaParaMontar(ObjInfo montura, bool yaEstaMontado, bool esGm, int nivel, byte clase, byte raza) =>
        yaEstaMontado || esGm ? 0 : QueLeFaltaParaUsarlo(montura, nivel, clase, raza);

    /// <summary>1 weapon, 2 armour, 3 helmet, 4 shield, 5 ammo (flechas), 6 anillo/herramienta, 7 nudillos,
    /// 8 ítem mágico; 0 = not equippable. La 6 corresponde al <c>AnilloEqpSlot</c> del original, que es donde se
    /// lleva la HERRAMIENTA de oficio (ver <see cref="ObjInfo.IsRing"/>): sin ella no se puede portar el gate de
    /// fabricación del VB6, que exige <c>AnilloEqpObjIndex = COSTURERO</c> y no "tener el costurero en la mochila".
    ///
    /// <para>Las categorías son EXACTAMENTE los siete tipos que el original equipa. <c>EquiparInvItem</c> y
    /// <c>Desequipar</c> tienen el mismo <c>Select Case</c> con las mismas siete ramas (InvUsuario.bas:692 y
    /// :934): montura, arma, nudillos, ítems mágicos, flechas, anillo y armadura —el escudo y el casco entran
    /// por armadura, que los distingue con el SubTipo—. Lo que NO está en esa lista se rechaza en silencio, y
    /// eso también es fiel: <c>otAnillos</c> (47, que son la Pluma de Águila, el Colmillo de Lobo y los otros
    /// trofeos de criatura), <c>otAnilloEspec</c> (49, el Manual de Liderazgo) y los <c>otESCUDO</c>/
    /// <c>otCASCO</c> del enum viejo (16/17 — en esta data el 16 lo ocupan los dos Mapas y del 17 no hay
    /// ninguno) tampoco se equipan en el original. Devolver 0 para ellos no es un agujero: es la rama que el
    /// VB6 no tiene.</para></summary>
    public static int Category(ObjInfo o) =>
        o.IsWeapon ? 1 : o.IsArmor ? 2 : o.IsHelmet ? 3 : o.IsShield ? 4 : o.IsAmmo ? 5 : o.IsRing ? 6
        : o.IsKnuckles ? 7 : o.IsMagicItem ? 8 : 0;

    /// <summary>Las categorías que se liberan al ponerse una pieza de <paramref name="cat"/>. Normalmente es
    /// ella misma —un casco saca el casco—, pero el arma y los nudillos se echan MUTUAMENTE: el original
    /// desequipa los dos slots en las dos ramas (InvUsuario.bas:991-997 al ponerse nudillos, :1051-1058 al
    /// ponerse un arma). Sin esto se podría pelear con espada y nudillos a la vez, y como en combate los
    /// nudillos ganan, el arma quedaría puesta sin hacer nada.</summary>
    public static bool LiberaCategoria(int cat, int otra) =>
        cat == otra || ((cat == 1 || cat == 7) && (otra == 1 || otra == 7));

    /// <summary>Resistencia mágica TOTAL del equipo puesto — lo que <c>HechizoTerrenoHP</c> descuenta del daño de
    /// un hechizo (modHechizos.bas:530-547). El original suma cinco slots: casco, escudo, armadura, montura y
    /// anillo; acá se recorre el inventario equipado y se filtra por esos mismos tipos.
    ///
    /// En ESTA data la suma sale casi siempre de la defensa: de los 15 objetos con <c>ResistenciaMagica</c>, 14
    /// son <c>otArmadura</c> (armadura/casco/escudo) y el que queda es la montura <b>Unicornio (RM +10)</b>,
    /// obj 1351. Ninguna de las ocho herramientas trae resistencia, así que la rama del anillo no tiene data —
    /// se incluye igual porque el gate es del original y no del catálogo, y un obj.dat editado la activaría.</summary>
    public static int MagicResistance(Player p, GameData data)
    {
        int total = 0;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var slot = p.Inventory[i];
            if (slot.IsEmpty || !slot.Equipped) continue;
            var o = data.Obj(slot.ObjIndex);
            // Los mismos cinco slots del original: defensa (armadura/casco/escudo), montura y anillo. El ARMA
            // queda afuera a propósito — el VB6 no le resta resistencia al arma, sólo le SUMA daño si es un
            // báculo de DañoMagico.
            if (o is not null && (o.IsArmor || o.IsHelmet || o.IsShield || o.IsMount || o.IsRing))
                total += o.ResistenciaMagica;
        }
        return total;
    }

    /// <summary>The item currently equipped in the given category (2 armour, 3 helmet, 4 shield), or null.</summary>
    public static ObjInfo? EquippedOfCategory(Player p, GameData data, int category)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var slot = p.Inventory[i];
            if (slot.IsEmpty || !slot.Equipped) continue;
            var o = data.Obj(slot.ObjIndex);
            if (o is not null && Category(o) == category) return o;
        }
        return null;
    }

    /// <summary>La montura que el personaje lleva puesta, o null. No sale por <see cref="Category"/> porque la
    /// montura no ocupa un slot de equipo: el original la guarda aparte (<c>MonturaObjIndex</c>) y la enciende
    /// con el flag <c>Montando</c>. Acá se deriva del inventario, como el resto.</summary>
    /// <summary>
    /// ¿ESTÁ DESNUDO? Sin armadura de CUERPO puesta. El original lleva un flag (<c>flags.Desnudo</c>) que pone
    /// <c>DarCuerpoDesnudo</c> (General.bas:106) y limpia <c>EquiparInvItem</c> (InvUsuario.bas:1171); acá se
    /// deriva del equipo, que es la misma respuesta y no se puede desincronizar.
    /// </summary>
    public static bool EstaDesnudo(Player p, GameData data) => EquippedOfCategory(p, data, 2) is null;

    public static ObjInfo? MonturaEquipada(Player p, GameData data)
    {
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
        {
            var slot = p.Inventory[i];
            if (slot.IsEmpty || !slot.Equipped) continue;
            var o = data.Obj(slot.ObjIndex);
            if (o is { IsMount: true }) return o;
        }
        return null;
    }

    /// <summary>An item's (MinDef,MaxDef) range for <see cref="Combat.AplicarDefensa"/>, or null if unequipped.</summary>
    public static (int Min, int Max)? DefRange(ObjInfo? o) => o is null ? null : (o.MinDef, o.MaxDef);

    /// <summary>Body-graphic desnudo por raza/género (VB6 DarCuerpoDesnudo, General.bas:37-108). Se usa para
    /// restaurar el cuerpo al desequipar la armadura. eRaza: Humano=1, Elfo=2, Drow=3, gnomo=4, enano=5,
    /// Orco=6; eGenero: Hombre=1, Mujer=2 (Declares.bas:162-178).</summary>
    public static short NakedBody(byte race, byte gender)
    {
        bool mujer = gender == 2;
        return race switch
        {
            3 => (short)(mujer ? 40 : 32),      // Drow
            4 or 5 => (short)(mujer ? 60 : 53), // gnomo / enano
            6 => (short)(mujer ? 249 : 248),    // Orco
            _ => (short)(mujer ? 39 : 21),      // Humano / Elfo (default)
        };
    }

    /// <summary>Rango de CABEZAS válido para una raza/género (VB6 <c>ValidarCabeza</c>, TCP.bas:125-178).
    /// No es 1..N: cada combinación tiene su propio tramo del catálogo de cabezas, y son disjuntos —una mujer
    /// humana va de la 70 a la 80, un elfo varón de la 101 a la 121—. Devuelve null si la combinación no
    /// existe (el VB6 deja el booleano en False y rechaza la creación).</summary>
    public static (short Min, short Max)? HeadRange(byte race, byte gender) => gender switch
    {
        1 => race switch                       // Hombre
        {
            1 => ((short)1, (short)30),        // Humano
            2 => ((short)101, (short)121),     // Elfo
            3 => ((short)202, (short)212),     // Drow
            4 => ((short)401, (short)409),     // gnomo
            5 => ((short)301, (short)315),     // enano
            6 => ((short)501, (short)514),     // Orco
            _ => null,
        },
        2 => race switch                       // Mujer
        {
            1 => ((short)70, (short)80),
            2 => ((short)170, (short)189),
            3 => ((short)270, (short)278),
            4 => ((short)470, (short)481),
            5 => ((short)370, (short)373),
            6 => ((short)570, (short)573),
            _ => null,
        },
        _ => null,
    };

    /// <summary>Rango de cabezas de las POCIONES de cambio de cara y de sexo (VB6 <c>ChangeHead</c>,
    /// InvUsuario.bas:1886, y el mismo bloque repetido dentro de <c>DarCuerpoNuevo</c>:2007).
    ///
    /// <para>Existe aparte de <see cref="HeadRange"/> porque los dos bloques del original NO dicen lo mismo: en
    /// las cuatro razas masculinas que no son humano ni orco discrepan —elfo 101‑121 al crear contra 101‑120 en
    /// la poción, drow 202‑212 contra 201‑213, gnomo 401‑409 contra 401‑410, enano 301‑315 contra 301‑313—. O
    /// sea que la poción puede dar una cabeza que el creador de personajes rechazaría, y al revés. Es una
    /// inconsistencia de la fuente, no un error de transcripción, y por eso NO se unifican: unificarlas sería
    /// elegir una de las dos y cambiarle el juego a alguien.</para></summary>
    public static (short Min, short Max) HeadRangeDePocion(byte race, byte gender) => gender == 2
        ? race switch                          // Mujer — acá los dos bloques sí coinciden
        {
            2 => ((short)170, (short)189),     // Elfo
            3 => ((short)270, (short)278),     // Drow
            4 => ((short)470, (short)481),     // gnomo
            5 => ((short)370, (short)373),     // enano
            6 => ((short)570, (short)573),     // Orco
            _ => ((short)70, (short)80),       // Humano
        }
        : race switch                          // Hombre
        {
            2 => ((short)101, (short)120),     // Elfo
            3 => ((short)201, (short)213),     // Drow
            4 => ((short)401, (short)410),     // gnomo
            5 => ((short)301, (short)313),     // enano
            6 => ((short)501, (short)514),     // Orco
            _ => ((short)1, (short)30),        // Humano
        };

    /// <summary>¿La cabeza es válida para esa raza/género? (VB6 <c>ValidarCabeza</c>). El port sólo exigía
    /// <c>head >= 1</c>, así que se podía crear un humano con cabeza de orco — y el cliente lo dibujaba mal o
    /// no lo dibujaba.</summary>
    public static bool IsValidHead(byte race, byte gender, int head) =>
        HeadRange(race, gender) is var r && r is not null && head >= r.Value.Min && head <= r.Value.Max;

    /// <summary>El body-graphic que debe mostrar el personaje: el Ropaje de la armadura equipada, o el cuerpo
    /// desnudo por raza/género si no lleva armadura. Refleja el `.Char.body` del VB6 al vestir/desvestir.</summary>
    public static short BodyFor(Player p, GameData data)
    {
        var armour = EquippedOfCategory(p, data, 2);
        return armour is { Ropaje: > 0 } ? (short)armour.Ropaje : NakedBody(p.Race, p.Gender);
    }
}
