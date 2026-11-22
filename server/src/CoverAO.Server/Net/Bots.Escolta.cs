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

    /// <summary>A quién le pidió que lo acompañe, y hasta cuándo cuenta (lo que dura una compañía, <see cref="AcompanaMaxMin"/>).</summary>
    private (string Quien, long Hasta)? _escolta;

    /// <summary>A esta distancia o menos, la escolta está con él.</summary>
    public const int DistanciaDeLaEscolta = 8;

    /// <summary>¿Tiene la escolta al lado? La que le pidió, a la vista y a <see cref="DistanciaDeLaEscolta"/> o menos.</summary>
    private bool EscoltaCerca(SentidosDelBot s) =>
        _escolta is { } e && Environment.TickCount64 < e.Hasta && s.PersonaLlamada(e.Quien) is { Muerto: false } v
        && Math.Abs(v.X - s.Posicion.X) + Math.Abs(v.Y - s.Posicion.Y) <= DistanciaDeLaEscolta;

    /// <summary>
    /// LA MINA A LA QUE VA ACOMPAÑADO: cualquiera de un mapa inseguro, aunque nazca algo que le queda grande, y por cualquier mapa
    /// en el que pueda entrar (con la escolta no hace falta el camino sin peligro, que tampoco encontraba ninguna). Sin escolta las
    /// descarta todas (<see cref="MapaParaSuFaena"/>), y medido el 22/11 un Minero de nivel 18 o 30 no encontraba ninguna desde el
    /// 20 ni desde el 1: no compraba el piquete y no minaba nunca.
    /// </summary>
    private (int Mapa, byte X, byte Y, int Saltos)? MinaConEscolta(SentidosDelBot s) =>
        _clase != ClaseMinero ? null
        : _entorno.Mapas.DondeSeTrabaja(s.Mapa, false, RecursoDeSuOficio(s), s.Posicion, m => _entorno.Datos.IsPkMap(m), Pisable(s),
                                        SaltosDelOficio);

    private int _minaConEscoltaMapa = -1;
    private long _minaConEscoltaHasta;
    private bool _hayMinaConEscolta;

    /// <summary>¿Hay una mina a la que ir acompañado? Para comprar el piquete; se recuerda cinco minutos por mapa.</summary>
    private bool HayMinaConEscolta(SentidosDelBot s)
    {
        if (s.Mapa == _minaConEscoltaMapa && Environment.TickCount64 < _minaConEscoltaHasta) return _hayMinaConEscolta;
        _hayMinaConEscolta = MinaConEscolta(s) is not null;
        _minaConEscoltaMapa = s.Mapa;
        _minaConEscoltaHasta = Environment.TickCount64 + 5 * 60_000;
        return _hayMinaConEscolta;
    }

    /// <summary>
    /// ¿EL MINERO COMPRA EL PIQUETE SIENDO NOVATO, aunque no tenga mina a mano? Los únicos que le venden uno a un ciudadano (el NPC 48 en
    /// el 184, el 194 y el 475) quedan pasando por la cuna, y desde el nivel 16 el servidor no lo deja entrar: después no lo consigue
    /// nunca. Medido en la ronda 137 (22/11): Quim llegó al 14 con 13 mil monedas sin mina a la vista desde la cuna, y en la 136, ya
    /// en el 20, con mina y sin camino al vendedor.
    /// </summary>
    public static bool ElMineroLoCompraDeNovato(byte clase, int nivel) => clase == ClaseMinero && nivel <= GameLimits.NewbieMaxLevel;

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
            _escolta = (escolta.Quien.Nombre, ahora + AcompanaMaxMin * 60_000L);
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
