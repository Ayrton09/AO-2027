using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

/// <summary>
/// UN PERSONAJE ARMADO PARA PELEAR, hecho en el acto: clase, nivel, el mejor equipo que esa clase y esa raza
/// pueden usar a ese nivel, y pociones.
///
/// <para><b>Para qué existe.</b> Es una herramienta del ARNÉS, no una regla del juego. Los flows arrancan de
/// fixture limpio, y hasta ahora darle equipo a uno costaba mandarle <c>/CI</c> de a un objeto, o meterlo en
/// el sembrado de <c>Persistence</c> — que pisa una ranura y encima le come el lugar a los <c>/CI</c> de los
/// otros flows. Con esto, un flow de combate dice "un Guerrero de nivel 45" y mide contra algo repetible.</para>
///
/// <para><b>El nivel se sube por el camino de verdad.</b> No se escriben los máximos a mano: se le regala la
/// experiencia y sube con <see cref="Leveling.Grant"/>, que es el mismo código que corre cuando un jugador
/// mata algo. Así el personaje armado tiene EXACTAMENTE los números que tendría uno que jugó hasta ahí, y no
/// una copia de la fórmula que se desincroniza en el primer cambio de balance.</para>
///
/// <para><b>El equipo sale de la data, no de una lista a mano.</b> Se elige el mejor objeto de cada tipo que
/// la clase y la raza pueden usar a ese nivel, respetando <c>ClasesProhibidas</c>, <c>RazasProhibidas</c> y
/// <c>MinLevel</c> igual que se los respeta a un jugador. Una tabla escrita a mano envejece con la data; esto
/// no.</para>
/// </summary>
public static class PlantillaDeCombate
{
    /// <summary>Pociones de cada color que lleva un personaje armado. Suficientes para que una pelea larga no
    /// se decida por quedarse sin nada que tomar, que es lo que arruina una medición.</summary>
    public const short PocionesPorColor = 500;

    private const short PocionRoja = 38, PocionAzul = 37;

    /// <summary>
    /// Arma a <paramref name="p"/> como un personaje de esa clase y ese nivel. Devuelve cuántos objetos le
    /// puso encima.
    ///
    /// <para><b>Toma un personaje YA CREADO</b> y lo sube: no lo inventa de cero. Es a propósito — los
    /// atributos salen del alta (raza y clase deciden fuerza, agilidad, constitución…) y de la constitución
    /// depende cuánta vida da cada nivel. Un muñeco fabricado sin pasar por el alta sube cuarenta niveles y se
    /// queda con la vida del primero, en silencio.
    ///
    /// <para>Le vacía el inventario primero: la gracia es que el resultado sea el MISMO siempre, y un resto
    /// de lo que el personaje traía haría que dos corridas del mismo flow midieran cosas distintas.</para>
    /// </summary>
    public static int Armar(Player p, GameData datos, Leveling niveles, byte clase, int nivel)
    {
        p.Class = clase;
        SubirHasta(p, niveles, nivel);

        for (int slot = 1; slot <= GameLimits.MaxInventorySlots; slot++) p.Inventory[slot] = default;

        byte ranura = 1;
        int puestos = 0;
        foreach (var elegido in EquipoPara(datos, p.Class, p.Race, p.Level))
        {
            p.Inventory[ranura++] = new InventorySlot { ObjIndex = elegido, Amount = 1, Equipped = true };
            puestos++;
        }
        // Las pociones van SIN equipar, como las lleva cualquiera.
        p.Inventory[ranura++] = new InventorySlot { ObjIndex = PocionRoja, Amount = PocionesPorColor };
        p.Inventory[ranura] = new InventorySlot { ObjIndex = PocionAzul, Amount = PocionesPorColor };
        return puestos + 2;
    }

    /// <summary>Lleva al personaje hasta ese nivel regalándole experiencia, que es el camino que corre para
    /// cualquiera. Si ya está más arriba no lo baja: bajar de nivel no es algo que el juego sepa hacer, y
    /// fingirlo daría un personaje con números que nadie podría tener.</summary>
    private static void SubirHasta(Player p, Leveling niveles, int nivel)
    {
        int meta = Math.Clamp(nivel, 1, Leveling.MaxLevel);
        if (p.Level >= meta) return;
        // Un personaje recién construido no tiene la curva puesta (`Elu` en 0), y `Grant` no sube a nadie sin
        // ella: se queda en el nivel uno en silencio. Se inicializa sólo si está sin estrenar, para no pisarle
        // el progreso a uno que ya jugó.
        if (p.Level <= 1 && p.Elu <= 0) niveles.InitNewCharacter(p);

        // De a un nivel: se le da justo lo que le falta y `Grant` hace el resto. El tope de vueltas es el de
        // niveles que existen, así que aunque la curva viniera rota esto termina.
        for (int vuelta = 0; vuelta < Leveling.MaxLevel && p.Level < meta; vuelta++)
        {
            long falta = (long)(p.Elu - p.Exp);
            if (falta <= 0) falta = 1;
            if (niveles.Grant(p, falta) == 0) break;   // la curva no da para más: se queda donde llegó
        }
    }

    /// <summary>El mejor de cada tipo —arma, armadura, casco, escudo— que esa clase y esa raza pueden usar a
    /// ese nivel. Devuelve sólo los que existen: si la data no tiene escudo usable, no se inventa uno.</summary>
    public static IEnumerable<short> EquipoPara(GameData datos, byte clase, byte raza, int nivel)
    {
        var arma = Mejor(datos, clase, raza, nivel, o => o.IsWeapon, o => o.MinHit + o.MaxHit);
        var armadura = Mejor(datos, clase, raza, nivel, o => o.IsArmor, o => o.MinDef + o.MaxDef);
        var casco = Mejor(datos, clase, raza, nivel, o => o.IsHelmet, o => o.MinDef + o.MaxDef);
        var escudo = Mejor(datos, clase, raza, nivel, o => o.IsShield, o => o.MinDef + o.MaxDef);
        foreach (var x in new[] { arma, armadura, casco, escudo })
            if (x is { } idx) yield return idx;
    }

    /// <summary>El objeto de mayor <paramref name="puntaje"/> entre los que cumplen. Las prohibiciones son las
    /// mismas que se le aplican a un jugador cuando se pone algo: si el personaje armado pudiera usar lo que
    /// otro no, el flow mediría una pelea que no puede pasar.</summary>
    private static short? Mejor(GameData datos, byte clase, byte raza, int nivel,
                                Func<ObjInfo, bool> esDelTipo, Func<ObjInfo, int> puntaje)
    {
        short? mejor = null;
        int mejorPuntaje = int.MinValue;
        foreach (var (idx, o) in datos.Objects)
        {
            if (!esDelTipo(o)) continue;
            if (o.MinLevel > nivel) continue;
            if (o.ClassForbidden(clase) || o.RaceForbidden(raza)) continue;
            int v = puntaje(o);
            if (v <= 0 || v <= mejorPuntaje) continue;
            mejor = (short)idx;
            mejorPuntaje = v;
        }
        return mejor;
    }
}
