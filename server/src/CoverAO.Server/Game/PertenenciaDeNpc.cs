namespace CoverAO.Server.Game;

/// <summary>
/// LA PERTENENCIA DE UNA CRIATURA: de quién es un bicho salvaje por haberle pegado primero (VB6
/// <c>ApropioNpc</c> / <c>PerdioNpc</c> / <c>IntervaloPerdioNpc</c>, Modulo_UsUaRiOs.bas:2045-2110 y
/// modNuevoTimer.bas:351).
///
/// <para><b>No confundir con la mascota.</b> El original tiene DOS campos distintos en la criatura:
/// <c>MaestroUser</c> —el amo de una mascota domada o invocada, que es <see cref="Npc.OwnerCharIndex"/>— y
/// <c>Owner</c>, que es esto: una marca temporal de "la estoy peleando yo", que se renueva con cada golpe y
/// se cae sola a los <c>[TIMERS] IntervaloOwnedNpc</c> ms (18.000 en el install).</para>
///
/// <para><b>Qué hace, en concreto.</b> Casi nunca prohíbe pegar — el gate faccionario sólo corta entre dos
/// Armadas o entre dos Caos. Lo que decide es <i>a quién persigue</i> la criatura y quién puede robársela:
/// pasado el intervalo sin golpearla, el siguiente que le pegue se la queda.</para>
///
/// <para>Es una clase aparte y sin dependencias de red a propósito: así se puede ejercer con un
/// <see cref="Player"/> y un <see cref="Npc"/> sueltos, sin levantar un servidor. El reloj entra por
/// parámetro, como en <see cref="DetectorDeSpeedHack"/>.</para>
/// </summary>
public static class PertenenciaDeNpc
{
    /// <summary>El default del install (<c>[TIMERS] IntervaloOwnedNpc</c>).</summary>
    public const int IntervaloPorDefectoMs = 18_000;

    /// <summary>Renueva el reloj (VB6 <c>IntervaloPerdioNpc(idx, True)</c>). Lo llama cada golpe del dueño.</summary>
    public static void Renovar(Player p, long ahoraMs) => p.TimerNpcApropiadoMs = ahoraMs;

    /// <summary>¿Se le venció? (VB6 <c>IntervaloPerdioNpc</c> sin <c>Actualizar</c>.)</summary>
    public static bool SeAgoto(Player p, long ahoraMs, int intervaloMs) =>
        ahoraMs - p.TimerNpcApropiadoMs >= intervaloMs;

    /// <summary>El jugador se apropia de la criatura (VB6 <c>ApropioNpc</c>).
    ///
    /// <para>Tres salidas en el original: <b>los GM no se apropian de nada</b>, <b>no aplica en zona
    /// segura</b>, y no aplica en los mapas con <c>RoboNpcsPermitido</c> — esta última está MUERTA: la clave
    /// se declara y se lee, pero no se le asigna nunca en todo el fuente, así que no se porta.</para>
    ///
    /// <para>Apropiarse de una suelta la anterior: el jugador tiene UNA sola.</para></summary>
    /// <param name="soltarAnterior">Cómo soltar la criatura que tenía antes, si tenía. Lo pone el llamador
    /// porque acá no hay forma de resolver un charIndex a una criatura.</param>
    public static void Apropiar(Player p, Npc npc, long ahoraMs, bool enZonaSegura, Action<short> soltarAnterior)
    {
        if (p.IsGm) return;
        if (enZonaSegura) return;
        if (p.NpcApropiado != 0) soltarAnterior(p.NpcApropiado);
        npc.ApropiadoPor = p.CharIndex;
        p.NpcApropiado = npc.CharIndex;
        Renovar(p, ahoraMs);
    }

    /// <summary>El jugador pierde la criatura (VB6 <c>PerdioNpc</c>): se suelta la marca y sus mascotas dejan
    /// de atacarla y vuelven con él (<c>FollowAmo</c>).</summary>
    /// <param name="soltar">Cómo limpiar la marca en la criatura, por charIndex.</param>
    /// <param name="mascotasVuelven">Cómo devolver las mascotas del jugador a seguirlo.</param>
    public static void Perder(Player p, Action<short> soltar, Action mascotasVuelven)
    {
        if (p.NpcApropiado == 0) return;
        soltar(p.NpcApropiado);
        p.NpcApropiado = 0;
        mascotasVuelven();
    }
}
