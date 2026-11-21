using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL MINERO VA A LA MINA ACOMPAÑADO (pedido del tester, 20/11: "hacer team para ir a minar ya que minar es peligroso"; decisión del
/// autor del 22/11, la única excepción a la del 21/10 de no agrupar bots entre ellos). Todas las minas están en mapas inseguros. Antes
/// de viajar a una, el Minero le susurra a un aliado de una clase de pelea que tenga a la vista que lo acompañe
/// (<see cref="FraseDeLaEscolta"/>), como lo invitaría una persona: el otro la acepta y lo sigue con la compañía de siempre
/// (<see cref="ContestarLaInvitacion"/>), que lo ayuda contra lo que lo ataca. Si no tiene la ficha de nadie, mira al más cercano.
/// Como mucho cada <see cref="EntrePedidosDeEscoltaMs"/>.
/// </summary>
public sealed partial class Bot
{
    public const int EntrePedidosDeEscoltaMs = 15 * 60_000;

    public const string FraseDeLaEscolta = "vamos a minar? me acompanas?";

    private long _proximoPedidoDeEscolta;

    /// <summary>¿Pide escolta? Un Minero que va a una mina en un mapa inseguro, si no la pidió hace poco.</summary>
    public static bool PideEscolta(byte clase, bool minaInsegura, long ahora, long proximo) =>
        clase == ClaseMinero && minaInsegura && ahora >= proximo;

    /// <summary>¿Sirve de escolta? De una clase de pelea (no de oficio) y con el nivel a la vista.</summary>
    public static bool SirveDeEscolta((byte Clase, int Vida)? ficha, short? nivel) =>
        ficha is { Clase: < 11 or > 16 } && nivel is { } n && n != SentidosDelBot.NivelEscondido;

    /// <summary>Le pide a un aliado que lo acompañe. Devuelve si se lo pidió (o si miró a alguien para pedírselo).</summary>
    private bool PedirEscolta(TransporteDeBot transporte, SentidosDelBot sentidos, int mapaDeLaMina)
    {
        long ahora = Environment.TickCount64;
        if (!PideEscolta(_clase, _entorno.Datos.IsPkMap(mapaDeLaMina), ahora, _proximoPedidoDeEscolta) || sentidos.PersonaMasCercana() is null)
            return false;
        var aliado = EsBlancoParaMi();
        if (sentidos.PersonaMasCercana(v => !v.Muerto && !aliado(v)
                && SirveDeEscolta(sentidos.FichaDe(v.CharIndex, NivelVistoMs), sentidos.NivelDe(v.CharIndex, NivelVistoMs))) is { } escolta)
        {
            _proximoPedidoDeEscolta = ahora + EntrePedidosDeEscoltaMs;
            return transporte.Mandar(Paquete(ClientPacketId.Whisper,
                w => w.WriteAsciiString(escolta.Quien.Nombre).WriteAsciiString(FraseDeLaEscolta)));
        }
        if (sentidos.PersonaMasCercana(v => !v.Muerto && !aliado(v) && sentidos.FichaDe(v.CharIndex, NivelVistoMs) is null
                && !(_miradoEn.TryGetValue(v.CharIndex, out long mirada) && ahora - mirada <= MirarDeNuevoMs)) is { } sinFicha)
        {
            MirarA(transporte, sentidos, sinFicha.Quien.CharIndex);
            return true;
        }
        return false;
    }
}
