using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL CLÉRIGO PELEA CON MAGIA Y CON EL ARMA (pedido del tester, 20/11): "pelea tirando inmovilizar/paralizar al rival y
/// acercándose para pegarle un golpe y algunos hechizos que dañan al rival". Antes peleaba con la gente como un Mago: la quietaba
/// y le lanzaba desde donde estaba, y el arma la usaba sólo sin maná.
///
/// <para>Lo que hace un jugador: con la persona quieta, se le arrima; al lado, le pega y enseguida le lanza un hechizo. El golpe y
/// el hechizo tienen relojes separados en el servidor (<c>IntervaloUserPuedeAtacar</c> e <c>IntervaloLanzaHechizo</c>, 1 s cada
/// uno, <see cref="CombatTiming"/>), así que en la vuelta en la que antes lanzaba ahora pega y lanza. Es el combo de golpe y
/// hechizo de cualquier clase que pelea con las dos cosas.</para>
///
/// <para>Sólo con un arma que pega puesta (no un arco, y no a puño limpio). Sin la persona quieta y a más de un paso, lanza de
/// lejos como antes: arrimarse a la que se mueve es correrla. Contra criaturas, como antes.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Entre el golpe y el hechizo del combo. El servidor deja lanzar 400 ms después de pegar
    /// (<c>IntervaloGolpeMagia</c>); un poco más, como cualquiera.</summary>
    public const int ComboMinMs = 450, ComboMaxMs = 650;

    /// <summary>¿Pelea de cerca con esta persona? Un Clérigo, un Asesino o un Paladín con un arma que pega, y la persona al lado o quieta.</summary>
    public static bool SeArrimaConElCombo(byte clase, bool conArmaQuePega, int pasos, bool quieta) =>
        (clase is GameLimits.ClaseClerigo or GameLimits.ClaseAsesino or GameLimits.ClasePaladin) && conArmaQuePega && (pasos <= 1 || quieta);

    private bool ConArmaQuePega(SentidosDelBot s) => ArmaEnLaMano(s) != 0 && ArmaQueNoPegaPuesta(s, _entorno.Datos) is null;

    /// <summary>
    /// CON QUIÉN PELEA EL CLÉRIGO. Pega de cerca, así que no se mete con el que pega más fuerte que él: a un Guerrero o un
    /// Gladiador, hasta su nivel. Tiene más vida que el Mago (10 por nivel contra 8) y se cura, así que al resto hasta
    /// <see cref="NivelesQueBuscaElMago"/> por encima, y a los oficios y al que está para rematar, como el Mago.
    /// </summary>
    public static bool ClerigoLeConvienePelear(int miNivel, short? nivelRival, (byte Clase, int Vida)? ficha)
    {
        if (nivelRival is not { } nivel || ficha is not { } f) return true;
        if (nivel == SentidosDelBot.NivelEscondido) return false;
        if (f.Vida <= VidaParaRematar) return true;
        if (f.Clase is >= 11 and <= 16) return nivel <= miNivel + NivelesQueBuscaDeUnOficio;
        if (f.Clase is GameLimits.ClaseGuerrero or GameLimits.ClaseGladiador) return nivel <= miNivel;
        return nivel <= miNivel + NivelesQueBuscaElMago;
    }

    /// <summary>El Clérigo se va del mapa y deja la pelea con menos vida que el Mago: se cura con magia mientras pelea.</summary>
    public const double VidaParaSeguirPeleandoElClerigo = SeRetira;
}
