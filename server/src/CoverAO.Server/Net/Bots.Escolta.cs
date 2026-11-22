using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL MINERO VA A LA MINA ACOMPAÑADO (pedido del tester, 20/11: "hacer team para ir a minar ya que minar es peligroso"; decisión del
/// autor del 22/11, la única excepción a la del 21/10 de no agrupar bots entre ellos). Todas las minas están en mapas inseguros. Antes
/// de viajar a una, el Minero le pide a un bot aliado de una clase de pelea que esté libre, donde esté, que lo acompañe
/// (<see cref="PedirEscolta"/>), y lo espera <see cref="EsperaDeLaEscoltaMs"/>; el otro viaja hasta donde está y, al verlo, lo sigue
/// con la compañía de siempre (<see cref="AtenderPedidoDeEscoltaAsync"/>), que lo ayuda contra lo que lo ataca. A una persona a la
/// vista se lo susurra (<see cref="FraseDeLaEscolta"/>), como lo invitaría cualquiera. Como mucho cada
/// <see cref="EntrePedidosDeEscoltaMs"/>.
///
/// <para>Entre bots el pedido no va por el protocolo: el servidor no pasa los susurros de un bot a otro (la regla del 21/10), y
/// medido en la ronda 139 (23/11), Quim cazaba solo en el 13, con la mina del 33 a mano y nadie a la vista a quien pedírselo.</para>
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

    /// <summary>
    /// ¿VUELVE A CASA CON LA RUNA POR EL PIQUETE, como por lo esencial, aunque esté en un mapa inseguro? El Minero novato que va por él
    /// (<see cref="ElMineroLoCompraDeNovato"/>): la runa no anda en un mapa inseguro y, para lo que no es esencial, el bot no caminaba
    /// hasta uno seguro para usarla. Medido en la ronda 138 (22/11): Quim cazaba en el 801 (el Bosque de la desolación), desde donde no se
    /// llega caminando a ningún vendedor que se lo venda, y pasó el nivel 16 sin piquete; desde el 20, su pueblo, el del 194 queda a dos.
    /// </summary>
    public static bool ElMineroVuelveACasaPorElPiquete(byte clase, int nivel, short quiere, short herramientaDelOficio) =>
        ElMineroLoCompraDeNovato(clase, nivel) && quiere != 0 && quiere == herramientaDelOficio;

    /// <summary>¿Pide escolta? Un Minero que va a una mina en un mapa inseguro, si no la pidió hace poco.</summary>
    public static bool PideEscolta(byte clase, bool minaInsegura, long ahora, long proximo) =>
        clase == ClaseMinero && minaInsegura && ahora >= proximo;

    /// <summary>¿Sirve de escolta? De una clase de pelea (no de oficio) y con el nivel a la vista.</summary>
    public static bool SirveDeEscolta((byte Clase, int Vida)? ficha, short? nivel) =>
        ficha is { Clase: < 11 or > 16 } && nivel is { } n && n != SentidosDelBot.NivelEscondido;

    /// <summary>Cuánto lo espera el Minero, y cuánto vale el pedido para el que va.</summary>
    public const int EsperaDeLaEscoltaMs = 8 * 60_000;

    private long _esperaLaEscoltaHasta;

    /// <summary>¿Está esperando a la escolta? La pidió hace poco y todavía no la tiene al lado.</summary>
    private bool EsperaLaEscolta(SentidosDelBot s) =>
        _escolta is not null && Environment.TickCount64 < _esperaLaEscoltaHasta && !EscoltaCerca(s);

    /// <summary>Lo que los demás bots saben de éste para pedirle escolta: lo que se ve de afuera y si está libre.</summary>
    internal sealed record FichaPublica(byte Clase, byte Bando, int Nivel, bool Vivo, bool Libre, int Mapa, byte X = 0, byte Y = 0);

    private volatile FichaPublica? _fichaPublica;

    private void PublicarFicha(SentidosDelBot s) =>
        _fichaPublica = new(_clase, s.MiEstado, s.Nivel, s.Vivo, _acompana is null && _pedidoDeEscolta is null, s.Mapa, s.Posicion.X, s.Posicion.Y);

    /// <summary>¿Ese bot le sirve de escolta? Vivo, libre, de una clase de pelea, de su nivel o hasta dos menos, y aliado de los dos
    /// lados.</summary>
    internal static bool EsEscoltaPosible(FichaPublica f, byte miBando, int miNivel, bool agresivo, bool atacarAliadoVuelveCriminal) =>
        f.Vivo && f.Libre && f.Clase is < 11 or > 16 && f.Nivel >= miNivel - 2
        && !EsBlanco(miBando, f.Bando, agresivo, atacarAliadoVuelveCriminal) && !EsBlanco(f.Bando, miBando, false, atacarAliadoVuelveCriminal);

    /// <summary>El bot al que le pide escolta: de los posibles, el de su mapa, y si no el de más nivel.</summary>
    private Bot? BotParaLaEscolta(SentidosDelBot s)
    {
        bool agresivo = Agresivo, regla = _entorno.Ini.AtacarAliadoVuelveCriminal;
        return _entorno.EnElMundo.Values
            .Where(b => b != this && b._fichaPublica is { } f && EsEscoltaPosible(f, s.MiEstado, s.Nivel, agresivo, regla))
            .OrderByDescending(b => b._fichaPublica!.Mapa == s.Mapa).ThenByDescending(b => b._fichaPublica!.Nivel)
            .FirstOrDefault();
    }

    /// <summary>
    /// LE PIDE A UN ALIADO QUE LO ACOMPAÑE: a un bot libre, donde esté (<see cref="BotParaLaEscolta"/>), diciéndole dónde lo espera; y si
    /// no hay ninguno, a una persona a la vista, por susurro (si no tiene la ficha de nadie, mira al más cercano). Devuelve si se lo
    /// pidió (o si miró a alguien para pedírselo).
    /// </summary>
    private bool PedirEscolta(TransporteDeBot transporte, SentidosDelBot sentidos, int mapaDeLaMina)
    {
        long ahora = Environment.TickCount64;
        if (!PideEscolta(_clase, _entorno.Datos.IsPkMap(mapaDeLaMina), ahora, _proximoPedidoDeEscolta)) return false;
        if (BotParaLaEscolta(sentidos) is { } otro)
        {
            var (x, y) = sentidos.Posicion;
            _proximoPedidoDeEscolta = ahora + EntrePedidosDeEscoltaMs;
            _escolta = (otro.Nombre, ahora + AcompanaMaxMin * 60_000L);
            _esperaLaEscoltaHasta = ahora + EsperaDeLaEscoltaMs;
            otro._pedidosDeEscolta.Enqueue((Nombre, sentidos.Mapa, x, y, ahora + EsperaDeLaEscoltaMs));
            Console.WriteLine($"[bot {Nombre}] le pide escolta a {otro.Nombre}, para la mina del {mapaDeLaMina}");
            return true;
        }
        if (sentidos.PersonaMasCercana() is null) return false;
        var aliado = EsBlancoParaMi();
        bool EsPersona(SentidosDelBot.Visible v) => !_entorno.EnElMundo.ContainsKey(v.Nombre);
        if (sentidos.PersonaMasCercana(v => !v.Muerto && !aliado(v) && EsPersona(v)
                && SirveDeEscolta(sentidos.FichaDe(v.CharIndex, NivelVistoMs), sentidos.NivelDe(v.CharIndex, NivelVistoMs))) is { } escolta)
        {
            _proximoPedidoDeEscolta = ahora + EntrePedidosDeEscoltaMs;
            _escolta = (escolta.Quien.Nombre, ahora + AcompanaMaxMin * 60_000L);
            _esperaLaEscoltaHasta = ahora + EsperaDeLaEscoltaMs;
            return transporte.Mandar(Paquete(ClientPacketId.Whisper,
                w => w.WriteAsciiString(escolta.Quien.Nombre).WriteAsciiString(FraseDeLaEscolta)));
        }
        if (sentidos.PersonaMasCercana(v => !v.Muerto && !aliado(v) && EsPersona(v) && sentidos.FichaDe(v.CharIndex, NivelVistoMs) is null
                && !(_miradoEn.TryGetValue(v.CharIndex, out long mirada) && ahora - mirada <= MirarDeNuevoMs)) is { } sinFicha)
        {
            MirarA(transporte, sentidos, sinFicha.Quien.CharIndex);
            return true;
        }
        return false;
    }

    /// <summary>Los pedidos de escolta que le llegaron de otros bots: quién, dónde lo espera y hasta cuándo.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Quien, int Mapa, byte X, byte Y, long Hasta)> _pedidosDeEscolta = new();

    private (string Quien, int Mapa, byte X, byte Y, long Hasta)? _pedidoDeEscolta;

    /// <summary>
    /// EL QUE LE PIDIERON ESCOLTA, VA: viaja hasta donde lo espera el Minero y, al verlo, lo acompaña (<see cref="EmpezarAAcompanar"/>).
    /// Lo deja si ya acompaña a alguien, si se venció la espera, si urge otra cosa o si no llega. Devuelve si se ocupó de esto.
    /// </summary>
    private async Task<bool> AtenderPedidoDeEscoltaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        while (_pedidosDeEscolta.TryDequeue(out var nuevo))
        {
            _pedidoDeEscolta = nuevo;
            Console.WriteLine($"[bot {Nombre}] va a escoltar a {nuevo.Quien}, al mapa {nuevo.Mapa}");
        }
        if (_pedidoDeEscolta is not { } p) return false;
        long ahora = Environment.TickCount64;
        string? porQue = _acompana is not null ? "ya acompaña a alguien" : !sentidos.Vivo ? "está muerto"
                       : ahora >= p.Hasta ? "se venció la espera" : Urge(sentidos) ? "le urge otra cosa" : null;
        if (porQue is not null)
        {
            Console.WriteLine($"[bot {Nombre}] deja el pedido de escolta de {p.Quien}: {porQue}");
            _pedidoDeEscolta = null;
            return false;
        }
        if (sentidos.PersonaLlamada(p.Quien) is { Muerto: false } el)
        {
            _pedidoDeEscolta = null;
            if (EsBlancoParaMi()(el)) return false;
            EmpezarAAcompanar(sentidos, p.Quien, ahora);
            transporte.Mandar(Paquete(ClientPacketId.Talk,
                w => w.WriteAsciiString(FrasesAcepta[_rng.Next(FrasesAcepta.Length)]).WriteByte((byte)SentidosDelBot.ComoLoDijo.Charla)));
            return true;
        }
        // VA A DONDE ESTÁ AHORA, no a donde lo pidió: el que espera puede haberse movido (lo que hace antes de la faena).
        var (mapa, x, y) = _entorno.EnElMundo.TryGetValue(p.Quien, out var elOtro) && elOtro._fichaPublica is { Mapa: > 0 } f
            ? (f.Mapa, f.X, f.Y) : (p.Mapa, p.X, p.Y);
        p = p with { Mapa = mapa, X = x, Y = y };
        _pedidoDeEscolta = p;
        bool SigueElPedido() => _pedidoDeEscolta == p && Environment.TickCount64 < p.Hasta && sentidos.PersonaLlamada(p.Quien) is null;
        bool llego = await IrHastaAsync(transporte, sentidos, p.Mapa, p.X, p.Y, cerrada, ct, tolerancia: CercaDeElla,
                                        cortaSi: () => !SigueElPedido(), topeDeMapas: SaltosDelOficio);
        // SI NO LLEGA, O LLEGA Y NO ESTÁ, lo deja: el que espera no se movió de ahí (`EsperaLaEscolta`).
        if (SigueElPedido() && (!llego || sentidos.Mapa == p.Mapa))
        {
            Console.WriteLine($"[bot {Nombre}] deja el pedido de escolta de {p.Quien}: {(llego ? "llegó y no estaba" : $"no llega al mapa {p.Mapa}")}");
            _pedidoDeEscolta = null;
        }
        return true;
    }
}
