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
