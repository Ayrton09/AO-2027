namespace CoverAO.Server.Game;

/// <summary>
/// Los relojes de combate del original (VB6 <c>modNuevoTimer.bas</c>), que el port no tenía: un mago casteaba
/// a velocidad de red porque no existía ningún gate de casteo — <c>grep</c> de "PuedeCastear" en el server
/// daba CERO.
///
/// Son cuatro intervalos y los cuatro ya venían con su valor en el <c>Server.ini</c> del install, sin que
/// nadie los leyera (sección <c>[INTERVALOS]</c>):
///
/// <code>
///   IntervaloLanzaHechizo = 1000   cadencia base de casteo
///   IntervaloMagiaGolpe   =  400   después de CASTEAR, cuánto esperar para pegar
///   IntervaloGolpeMagia   =  400   después de PEGAR, cuánto esperar para castear
///   IntervaloGolpeUsar    =  400   después de PEGAR, cuánto esperar para usar un objeto
/// </code>
///
/// <para><b>Los dos cruzados no son cadencias, son ATAJOS.</b> Y esto es lo que hay que entender antes de
/// tocar nada, porque leer las funciones sueltas lleva a la conclusión opuesta. En los dos call sites el
/// original los usa con un <b>OR</b>, no con un AND:</para>
///
/// <code>
///   pegar   (SistemaCombate.bas:978)  If Not MagiaGolpe Then If Not Atacar     Then Exit Sub
///   castear (Protocol.bas:2939)       If Not GolpeMagia Then If Not LanzarSpell Then Exit Sub
/// </code>
///
/// <para>O sea: el cruzado te deja actuar ANTES de que venza la cadencia normal; si ya se consumió, se cae
/// al intervalo de siempre y el juego sigue igual. Implementarlos como gates bloqueantes —que es lo que
/// sugiere leer <c>IntervaloPermiteMagiaGolpe</c> por su cuenta, con su <c>Exit Function</c> temprano—
/// rompería TODO el melee del juego.</para>
///
/// <para>El guard temprano (<c>TimerMagiaGolpe &gt; TimerLanzarSpell</c>) es lo que hace que el atajo se use
/// UNA sola vez por casteo: al usarlo, la marca queda por delante del reloj del casteo, y no vuelve a
/// habilitarse hasta que se castea de nuevo.</para>
/// </summary>
public static class CombatTiming
{
    /// <summary>Cadencia base de casteo (<c>IntervaloPermiteLanzarSpell</c>, modNuevoTimer.bas:87).</summary>
    public static bool PermiteLanzarSpell(Player p, long now, long intervalo, bool actualizar = true)
    {
        // Un intervalo en 0 NO desactiva el reloj: significa "sin espera", que es lo que da la aritmética del
        // original (`now - t >= 0` es siempre cierto). La diferencia importa porque el reloj se sigue
        // ESTAMPANDO, y de esos sellos dependen los dos atajos.
        if (intervalo > 0 && now - p.LastCastMs < intervalo) return false;
        if (actualizar) p.LastCastMs = now;
        return true;
    }

    /// <summary>Cadencia base de golpe (<c>IntervaloPermiteAtacar</c>, :113). Al pasar, el original also
    /// resetea <c>TimerGolpeUsar</c>: pegar arranca la espera para poder usar un objeto.</summary>
    public static bool PermiteAtacar(Player p, long now, long intervalo, bool actualizar = true)
    {
        if (intervalo > 0 && now - p.LastAttackMs < intervalo) return false;
        if (actualizar) { p.LastAttackMs = now; p.GolpeUsarMs = now; }
        return true;
    }

    /// <summary>Atajo "pegar después de castear" (<c>IntervaloPermiteMagiaGolpe</c>, :165-184). Al concederlo
    /// el original adelanta TRES relojes —la marca, el de ataque y el de usar— para que el golpe que viene
    /// cuente como un golpe normal.</summary>
    public static bool PermiteMagiaGolpe(Player p, long now, long intervalo, bool actualizar = true)
    {
        if (p.MagiaGolpeMs > p.LastCastMs) return false;    // ya se usó esta ventana
        if (intervalo > 0 && now - p.LastCastMs < intervalo) return false;
        if (actualizar) { p.MagiaGolpeMs = now; p.LastAttackMs = now; p.GolpeUsarMs = now; }
        return true;
    }

    /// <summary>Atajo "castear después de pegar" (<c>IntervaloPermiteGolpeMagia</c>, :200-214).</summary>
    public static bool PermiteGolpeMagia(Player p, long now, long intervalo, bool actualizar = true)
    {
        if (p.GolpeMagiaMs > p.LastAttackMs) return false;  // ya se usó esta ventana
        if (intervalo > 0 && now - p.LastAttackMs < intervalo) return false;
        if (actualizar) { p.GolpeMagiaMs = now; p.LastCastMs = now; }
        return true;
    }

    /// <summary>Usar un objeto después de golpear (<c>IntervaloPermiteGolpeUsar</c>, :140). Es el que impide
    /// tomar una poción en el mismo instante del golpe. El original lo consulta SIN actualizar
    /// (<c>InvUsuario.bas:1559</c> pasa <c>Actualizar:=False</c>), así que acá el default también es no
    /// tocar el reloj: lo mueven el golpe y el atajo, no el trago.</summary>
    public static bool PermiteGolpeUsar(Player p, long now, long intervalo, bool actualizar = false)
    {
        if (intervalo > 0 && now - p.GolpeUsarMs < intervalo) return false;
        if (actualizar) p.GolpeUsarMs = now;
        return true;
    }

    /// <summary>
    /// LA CADENCIA DEL ARCO (<c>IntervaloPermiteUsarArcos</c>, modNuevoTimer.bas:294;
    /// <c>IntervaloFlechasCazadores = 1100</c>).
    ///
    /// <para>No es una cadencia más: es la única <b>transversal</b>. Disparar la SELLA, y mientras esté
    /// caliente el original tampoco deja pegar ni castear. Los cuatro call sites, con su modo:</para>
    ///
    /// <code>
    ///   Protocol.bas:2661   arrojadizas   SELLA   (Actualizar = True)
    ///   Protocol.bas:2760   arco          SELLA   (Actualizar = True)
    ///   Protocol.bas:2936   clic de magia consulta (Actualizar = False)
    ///   SistemaCombate:975  UsuarioAtaca  consulta (Actualizar = False)
    /// </code>
    ///
    /// <para><b>Y en el camino del disparo es la única que sella algo.</b> Los otros dos chequeos de ese call
    /// site —<c>PermiteAtacar</c> y <c>PermiteLanzarSpell</c>— van con <c>Actualizar = False</c>. O sea que
    /// sin esta función el disparo no tenía cadencia NINGUNA: los relojes que consultaba no los movía nadie
    /// desde ahí. Un cliente modificado disparaba a la velocidad del socket.</para>
    ///
    /// <para>Va como GATE y no como atajo, a diferencia de los dos cruzados: sus call sites son
    /// <c>If Not ... Then Exit Sub</c> secos, sin <c>OR</c>.</para>
    /// </summary>
    public static bool PermiteUsarArcos(Player p, long now, long intervalo, bool actualizar = true)
    {
        if (intervalo > 0 && now - p.ArcoMs < intervalo) return false;
        if (actualizar) p.ArcoMs = now;
        return true;
    }
}
