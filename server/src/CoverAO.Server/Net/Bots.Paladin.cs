using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL PALADÍN (pedido del tester, 20/11): "suelen usar la magia para inmovilizar al rival y supportearse ellos mismos, una vez que
/// inmovilizaron al rival lo atacan cuerpo a cuerpo con la espada". Pelea como el Clérigo: la quieta, se le arrima y al lado pega y
/// lanza en la misma vuelta (<see cref="SeArrimaConElCombo"/>).
///
/// <para>CON QUIÉN PELEA: Milo, Paladina 23, es la que más muere de todos los bots: 34 veces en 60 corridas (rondas 121 a 129),
/// 18 contra Ciro (Guerrero 25), 4 contra Quim (Leñador 24), 4 contra Elmo (Clérigo 40) y 3 contra Fabia (Gladiadora 27). A un
/// Guerrero o un Gladiador sólo si es de menos nivel; a los oficios hasta su nivel; al resto hasta <see cref="NivelesQueBuscaElMago"/>
/// por encima; al que está para rematar, siempre. Con menos de la mitad de la vida se va del mapa, como el Mago.</para>
///
/// <para>Muere tanto también por la ropa: es mujer y Gnoma, y las armaduras de hombre no se las puede poner (decisión del autor del
/// 27/09). Pelea con las Vestimentas de Baja, 2 de defensa; lo que hay para ella se vende lejos (el Vestido de Novia (Bajas), de 15
/// a 25, en Tiama, Lindos y Nix).</para>
/// </summary>
public sealed partial class Bot
{
    public static bool PaladinLeConvienePelear(int miNivel, short? nivelRival, (byte Clase, int Vida)? ficha)
    {
        if (nivelRival is not { } nivel || ficha is not { } f) return true;
        if (nivel == SentidosDelBot.NivelEscondido) return false;
        if (f.Vida <= VidaParaRematar) return true;
        if (f.Clase is >= 11 and <= 16) return nivel <= miNivel;
        if (f.Clase is GameLimits.ClaseGuerrero or GameLimits.ClaseGladiador) return nivel < miNivel;
        return nivel <= miNivel + NivelesQueBuscaElMago;
    }
}
