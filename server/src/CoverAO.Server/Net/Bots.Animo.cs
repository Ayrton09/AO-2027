using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>Cuánto se anima un bot a pelear con gente (no es su <c>Temperamento</c>, que es cómo caza). Fijo por su nombre: ver <see cref="Bot.AnimoDe"/>.</summary>
public enum Animo : byte { Prudente, Normal, Agresivo }

/// <summary>
/// BOTS MÁS AGRESIVOS (pedido del tester, 25/11, elegido por el autor): "es un juego pvp, no puede haber un bot que viva corriendo", y
/// "esta bueno que cada bot sea diferente, bots que sean mas pasivos otros mas agresivos". Cada bot tiene un ánimo fijo por su
/// nombre, un tercio de cada uno, que dice hasta cuántos niveles por encima se anima (<see cref="NivelesQueSeAnima"/>: 10, 7 o 4, y 3
/// más con un aliado cerca) y con cuánta vida deja la pelea (<see cref="VidaParaSeguirConAnimo"/>). Reemplaza las reglas por clase
/// del 20 al 22/11, que medidas en las rondas 117 y 118 dejaban a los Magos peleando sólo con quien les sacaba 2 niveles o menos.
/// <para>LA FICHA ESCONDE EL NIVEL del que saca más de 5 (<see cref="SentidosDelBot.NivelEscondido"/>): con el nivel escondido se anima el
/// que llega a 10 (el agresivo, o el normal con un aliado cerca).</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>El carácter de un bot, fijo por su nombre (no por la sesión: el mismo bot es siempre igual).</summary>
    public static Animo AnimoDe(string nombre)
    {
        int suma = 0;
        foreach (char c in nombre.ToLowerInvariant()) suma = unchecked(suma * 31 + c);
        return (Animo)(Math.Abs(suma % 3));
    }

    /// <summary>Hasta cuántos niveles por encima se anima a pelear con una persona.</summary>
    public static int NivelesQueSeAnima(Animo t, bool conAliado) =>
        (t switch { Animo.Agresivo => 10, Animo.Normal => 7, _ => 4 }) + (conAliado ? AnimoConAliado : 0);

    /// <summary>Los niveles de más que se anima con un aliado a la vista.</summary>
    public const int AnimoConAliado = 3;

    /// <summary>Hasta dónde mira para saber si tiene un aliado cerca.</summary>
    public const int RadioDelAliado = 8;

    /// <summary>Con el nivel escondido (más de 5 por encima), se anima el que llega a esto.</summary>
    public const int NivelesParaElEscondido = 10;

    /// <summary>
    /// ¿SE ANIMA CONTRA ESA PERSONA? Hasta <paramref name="niveles"/> por encima, corregido por las clases: contra un oficio, 3 más
    /// (se defiende poco); el que pelea con magia o con el combo contra un Guerrero o un Gladiador, 3 menos (pegan de cerca y aguantan
    /// mucho), y contra un Nigromante, el que pelea con magia, 3 menos (es un mago más fuerte). Al que está para rematar
    /// (<see cref="VidaParaRematar"/>), siempre. Sin el nivel o la ficha, primero la mira (true, como antes).
    /// </summary>
    public static bool SeAnimaContra(byte miClase, int miNivel, short? nivelRival, (byte Clase, int Vida)? ficha, int niveles)
    {
        if (nivelRival is not { } nivel || ficha is not { } f) return true;
        if (f.Vida <= VidaParaRematar) return true;
        bool conMagia = miClase is GameLimits.ClaseMago or GameLimits.ClaseBardo or GameLimits.ClaseDruida or GameLimits.ClaseNigromante;
        bool deCerca = miClase is GameLimits.ClaseClerigo or GameLimits.ClaseAsesino or GameLimits.ClasePaladin or GameLimits.ClaseLadron;
        if (f.Clase is >= 11 and <= 16) niveles += 3;
        else if ((conMagia || deCerca) && f.Clase is GameLimits.ClaseGuerrero or GameLimits.ClaseGladiador) niveles -= 3;
        else if (conMagia && f.Clase == GameLimits.ClaseNigromante) niveles -= 3;
        if (nivel == SentidosDelBot.NivelEscondido) return niveles >= NivelesParaElEscondido;
        return nivel <= miNivel + niveles;
    }

    /// <summary>
    /// CON CUÁNTA VIDA DEJA LA PELEA, según su clase (<see cref="VidaParaSeguirPeleandoDe"/>) y su carácter: el prudente como dice la
    /// clase, el normal se va con el 35 % como mucho y el agresivo con el 25 %. Null, las clases que pelean hasta el final.
    /// </summary>
    public static double? VidaParaSeguirConAnimo(byte clase, Animo t) => VidaParaSeguirPeleandoDe(clase) is not { } deLaClase
        ? null
        : t switch { Animo.Agresivo => Math.Min(deLaClase, 0.25), Animo.Normal => Math.Min(deLaClase, 0.35), _ => deLaClase };

    private Animo? _animo;
    private Animo Animo => _animo ??= AnimoDe(Nombre);

    /// <summary>¿Tiene a la vista, a <see cref="RadioDelAliado"/> pasos o menos, a una persona de su lado (que no es blanco para él)?</summary>
    private bool HayAliadoCerca(SentidosDelBot s, short sinContar)
    {
        var blanco = EsBlancoParaMi();
        return s.PersonaMasCercana(v => !v.Muerto && v.CharIndex != sinContar && !blanco(v)) is { Pasos: <= RadioDelAliado };
    }

    /// <summary>¿Se anima contra esa persona, con su carácter y con o sin un aliado cerca?</summary>
    private bool SeAnimaConEsta(SentidosDelBot s, short quien) =>
        SeAnimaContra(_clase, s.Nivel, s.NivelDe(quien, NivelVistoMs), s.FichaDe(quien, NivelVistoMs),
                      NivelesQueSeAnima(Animo, HayAliadoCerca(s, quien)));

    /// <summary>¿Sale a buscar a uno cuyo nivel dice la ficha escondido? Sólo el que se anima con el nivel escondido.</summary>
    private bool SeAnimaConElEscondido(SentidosDelBot s, short quien) =>
        NivelesQueSeAnima(Animo, HayAliadoCerca(s, quien)) >= NivelesParaElEscondido;
}
