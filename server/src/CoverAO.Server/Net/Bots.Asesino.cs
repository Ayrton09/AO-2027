using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL ASESINO (pedido del tester, 20/11): "usa su magia para inmovilizar al rival y moverse sigilosamente para atacar". Pelea como
/// el Clérigo: la quieta, se le arrima y al lado pega y lanza en la misma vuelta (<see cref="SeArrimaConElCombo"/>).
///
/// <para>LA DAGA NO: el servidor apuñala con cualquier arma de <c>Apuñala=1</c> sin mirar la espalda (VB6 <c>DoApuñalar</c>), con
/// un golpe extra de 1,4 veces el daño y una chance que llega al 34 % con la habilidad en 100 (<c>Combat.SuerteApunalar</c>). La
/// mejor daga que se compra, la +3, pega de 4 a 6, y la Spatha que usa Juana de 10 a 21: con la daga pegaría menos. Juana tiene
/// la habilidad en 3, un 4 % de chance.</para>
///
/// <para>CON QUIÉN PELEA: es tan frágil como el Mago (171 de vida en nivel 21). En las rondas 119 a 123 murió 9 veces: 3 contra
/// Ciro (Guerrero 25), 2 contra Quim (Leñador 24), y contra Kael (Nigromante 22), Milo (Paladín 23), Elmo (Clérigo 41) y Karim.
/// A un Guerrero, un Gladiador o un Nigromante sólo si es de menos nivel; al resto hasta su nivel; a los oficios hasta
/// <see cref="NivelesQueBuscaElAsesinoDeUnOficio"/> por encima; al que está para rematar, siempre. Con menos de la mitad de la vida
/// deja la pelea y se va del mapa, como el Mago.</para>
/// </summary>
public sealed partial class Bot
{
    public const int NivelesQueBuscaElAsesinoDeUnOficio = 2;


}
