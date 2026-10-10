using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LAS ARMADAS (pedido del autor, 10/10). Los bots pelean con gente del bando enemigo, bots y jugadores, para juntar las bajas que
/// pide el enlistador, se enlistan y vuelven a cobrar la recompensa de cada rango. Los requisitos son los del original
/// (<c>PacketDispatcher.Enlistar</c>): Armada Real y Milicia Republicana, nivel 25 y 15 bajas enemigas; Legión del Caos, nivel 40, 30
/// bajas y ser renegado.
///
/// <para><b>A quién le pega</b> (<see cref="EsBlanco"/>): sólo a quien el servidor deja pelear (<see cref="Player.BandosQueNoSePelean"/>),
/// en un mapa inseguro, y que no lo vuelve criminal (<see cref="Player.AtacarloEsAtacarAUnAliado"/>): un ciudadano o un republicano
/// le pega a los renegados y a los del Caos; un soldado, además, al ejército enemigo. A un GM (estado 7 a 9) no.</para>
///
/// <para><b>El agresivo</b> (<see cref="EsAgresivo"/>): uno de cada nueve bots, todos cazadores, es el único que va al Caos (decisión
/// del autor). No se enlista en la Armada ni en la Milicia, y le pega también a los civiles del otro bando: con la regla de la casa
/// <c>AtacarAliadoVuelveCriminal</c> prendida, el primer golpe lo vuelve renegado, y de ahí en más pelea con cualquiera. Con la
/// regla apagada no hay cómo volverse renegado peleando, y el agresivo no llega al Caos.</para>
///
/// <para>Las bajas las lee de la ficha de estadísticas (MiniStats), la misma que pide el cliente.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde este nivel sale a buscar pelea con gente. Antes sólo se defiende.</summary>
    public const int NivelParaPelearConGente = 20;

    /// <summary>Los requisitos del enlistador, los de <c>PacketDispatcher.Enlistar</c> (VB6 ModFacciones.bas).</summary>
    public const int NivelParaElEjercito = 25, BajasParaElEjercito = 15, NivelParaElCaos = 40, BajasParaElCaos = 30;

    /// <summary>Con menos vida que esto no empieza una pelea con gente (defenderse sí, con lo que tenga).</summary>
    private const double SaludParaPelearConGente = 0.75;

    /// <summary>Hasta cuánto pelea con una persona: la que se va corriendo o a la que el servidor no deja pegarle no se persigue para
    /// siempre.</summary>
    private const int PeleaConGenteMaxMs = 60_000;

    /// <summary>Golpes seguidos sin respuesta del servidor (un fantasma, alguien que no se puede atacar) antes de dejar la pelea.</summary>
    private const int GolpesSinRespuestaConGente = 5;

    /// <summary>A la persona con la que ya peleó y sigue en pie no la vuelve a elegir por este rato.</summary>
    private const int GenteDejadaMs = 5 * 60_000;

    /// <summary>Si le bajó la vida hace menos que esto y no tiene una criatura encima, le está pegando una persona.</summary>
    private const int LePeganHaceMs = 2_500;

    /// <summary>Hasta dónde busca al que le pega: un hechizo llega desde lejos.</summary>
    private const int RadioDeDefensa = 8;

    /// <summary>Entre un viaje al enlistador y el siguiente.</summary>
    private const int EntreViajesAlEjercitoMs = 10 * 60_000;

    /// <summary>Cada cuánto pide la ficha de estadísticas, además de después de cada pelea con gente.</summary>
    private const int EntreFichasMs = 5 * 60_000;

    private readonly Dictionary<short, long> _genteDejada = new();
    private long _proximoViajeAlEjercito, _proximaFicha;
    private int _bajasEnLaUltimaRecompensa = -1;

    /// <summary>
    /// ¿ES DE TEMPERAMENTO AGRESIVO? Sale del nombre, como el temperamento: uno de cada nueve, todos cazadores. Es el único que va al
    /// Caos (decisión del autor, 10/10).
    /// </summary>
    public static bool EsAgresivo(string nombre) => Math.Abs(SemillaDelNombre(nombre)) % 9 == 0;

    private bool Agresivo => EsAgresivo(Nombre);

    /// <summary>
    /// ¿LE PEGA? <paramref name="yo"/> y <paramref name="el"/> son los bandos (<c>Player.Status*</c>). Ver la nota de la clase.
    /// </summary>
    public static bool EsBlanco(byte yo, byte el, bool agresivo, bool atacarAliadoVuelveCriminal)
    {
        if (yo is < Player.StatusCriminal or > Player.StatusMilicia || el is < Player.StatusCriminal or > Player.StatusMilicia) return false;
        if (Player.BandosQueNoSePelean(yo, el)) return false;
        // Los de la Legión no se pelean entre ellos.
        if (yo == Player.StatusCaos && el == Player.StatusCaos) return false;
        if (!atacarAliadoVuelveCriminal || !Player.AtacarloEsAtacarAUnAliado(yo, el)) return true;
        // Lo vuelve criminal: sólo el agresivo que todavía es civil, que va al Caos.
        return agresivo && yo is Player.StatusCiudadano or Player.StatusRepublicano;
    }

    /// <summary>Las bajas que le cuentan a su bando, como <c>Player.KillsEnemigosArmada</c>, <c>KillsEnemigosMilicia</c> y
    /// <c>KillsEnemigosCaos</c>: el civil cuenta como el ejército al que puede entrar, y el renegado como el Caos.</summary>
    public static int BajasParaSuBando(byte bando, SentidosDelBot.Bajas b) => bando switch
    {
        Player.StatusCiudadano or Player.StatusArmada => b.Renegados + b.Caos + b.Republicanos + b.Milicianos,
        Player.StatusRepublicano or Player.StatusMilicia => b.Renegados + b.Caos + b.Armada + b.Ciudadanos,
        Player.StatusCriminal or Player.StatusCaos => b.Renegados + b.Armada + b.Ciudadanos + b.Milicianos + b.Republicanos,
        _ => 0,
    };

    /// <summary>El ejército en el que le toca enlistarse ahora, o 0: el ciudadano a la Armada y el republicano a la Milicia (no el
    /// agresivo), y el renegado agresivo al Caos, cada uno con el nivel y las bajas que pide el enlistador.</summary>
    public static byte EjercitoQueBusca(byte bando, bool agresivo, int nivel, int bajas) => bando switch
    {
        Player.StatusCiudadano when !agresivo && nivel >= NivelParaElEjercito && bajas >= BajasParaElEjercito => Player.StatusArmada,
        Player.StatusRepublicano when !agresivo && nivel >= NivelParaElEjercito && bajas >= BajasParaElEjercito => Player.StatusMilicia,
        Player.StatusCriminal when agresivo && nivel >= NivelParaElCaos && bajas >= BajasParaElCaos => Player.StatusCaos,
        _ => 0,
    };

    /// <summary>
    /// ¿VA A COBRAR LA RECOMPENSA? Cada rango pide 20 bajas más 5 por rango en la Armada y la Milicia, y 10 en el Caos
    /// (<c>PacketDispatcher.MatadosParaRango</c>): va cada vez que cruza uno de esos escalones desde la última visita. Sin saber el
    /// rango, alguna visita sobra (el enlistador contesta "mata N más"), y ninguna falta.
    /// </summary>
    public static bool TocaRecompensa(byte bando, int bajas, int bajasEnLaUltima)
    {
        if (bando is not (Player.StatusArmada or Player.StatusMilicia or Player.StatusCaos)) return false;
        int paso = bando == Player.StatusCaos ? 10 : 5;
        int Escalon(int k) => k < 20 ? -1 : (k - 20) / paso;
        return Escalon(bajas) >= 0 && Escalon(bajas) > Escalon(bajasEnLaUltima);
    }

    private void PedirLaFicha(TransporteDeBot transporte, bool ya = false)
    {
        long ahora = Environment.TickCount64;
        if (!ya && ahora < _proximaFicha) return;
        _proximaFicha = ahora + EntreFichasMs;
        transporte.Mandar(Paquete(ClientPacketId.RequestMiniStats, _ => { }));
    }

    private IReadOnlySet<short> GenteDejada()
    {
        long ahora = Environment.TickCount64;
        foreach (var vencida in _genteDejada.Where(p => p.Value <= ahora).Select(p => p.Key).ToList()) _genteDejada.Remove(vencida);
        return _genteDejada.Keys.ToHashSet();
    }

    private Func<SentidosDelBot.Visible, bool> EsBlancoParaMi()
    {
        byte yo = _miBando;
        bool agresivo = Agresivo, regla = _entorno.Ini.AtacarAliadoVuelveCriminal;
        return v => EsBlanco(yo, v.Estado, agresivo, regla);
    }

    /// <summary>
    /// ¿A QUIÉN LE DEVUELVE EL GOLPE? Si le bajó la vida recién, no tiene una criatura que lo esté atacando encima y hay a la vista
    /// alguien a quien le puede pegar: el que le pega a un legal se vuelve renegado, así que el agresor casi siempre es un blanco.
    /// </summary>
    private short? AQuienSeDefiende(SentidosDelBot sentidos)
    {
        if (!_entorno.Datos.IsPkMap(sentidos.Mapa) || sentidos.HaceCuantoLeBajoLaVida > LePeganHaceMs) return null;
        if (!TieneFuelleParaPelear(sentidos) || sentidos.Salud < SeRetira) return null;
        if (sentidos.MasCercano(f => LoEstaAtacando(_entorno.Datos.Npc(f), 1)) is { Pasos: <= 1 }) return null;
        return sentidos.PersonaMasCercana(EsBlancoParaMi(), GenteDejada()) is { Pasos: <= RadioDeDefensa } quien
            ? quien.Quien.CharIndex : null;
    }

    /// <summary>¿A QUIÉN SALE A PEGARLE? Desde <see cref="NivelParaPelearConGente"/>, en un mapa inseguro, con la vida arriba de
    /// <see cref="SaludParaPelearConGente"/>, energía y las pociones de pelear.</summary>
    private short? AQuienSaleAPegarle(SentidosDelBot sentidos)
    {
        if (sentidos.Nivel < NivelParaPelearConGente || !_entorno.Datos.IsPkMap(sentidos.Mapa)) return null;
        if (sentidos.Salud < SaludParaPelearConGente || !TieneFuelleParaPelear(sentidos)) return null;
        if (sentidos.CuantoTiene(EsPocionDeVida) < PocionesParaPelear) return null;
        return sentidos.PersonaMasCercana(EsBlancoParaMi(), GenteDejada()) is { Pasos: <= RadioDeCaza } quien
            ? quien.Quien.CharIndex : null;
    }

    /// <summary>Pelea con esa persona (<see cref="CazarAsync"/>), y después pide la ficha para ver si sumó la baja. La que sigue en pie
    /// queda dejada un rato. Devuelve false sólo si se cayó la conexión.</summary>
    private async Task<bool> PelearConGenteAsync(TransporteDeBot transporte, SentidosDelBot sentidos, short quien, Task cerrada,
                                                 CancellationToken ct)
    {
        bool sigue = await CazarAsync(transporte, sentidos, quien, cerrada, ct, aGente: true);
        if (sentidos.Donde(quien) is not null) _genteDejada[quien] = Environment.TickCount64 + GenteDejadaMs;
        PedirLaFicha(transporte, ya: true);
        return sigue;
    }

    /// <summary>
    /// AL ENLISTADOR: a enlistarse (<see cref="EjercitoQueBusca"/>) o a cobrar la recompensa (<see cref="TocaRecompensa"/>). Va
    /// caminando hasta el enlistador de su ejército más cercano, por mapas sin guardias que vayan por él, y le hace doble clic, que es
    /// lo que hace el cliente: al que no es de su bando lo enlista y al que sí le da la recompensa. Devuelve si salió a hacerlo.
    /// </summary>
    private async Task<bool> IrAlEjercitoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        long ahora = Environment.TickCount64;
        if (ahora < _proximoViajeAlEjercito) return false;
        PedirLaFicha(transporte);
        if (sentidos.MisBajas is not { } bajas) return false;
        byte bando = _miBando;
        int cuantas = BajasParaSuBando(bando, bajas);
        byte ejercito = EjercitoQueBusca(bando, Agresivo, sentidos.Nivel, cuantas);
        bool recompensa = ejercito == 0 && TocaRecompensa(bando, cuantas, _bajasEnLaUltimaRecompensa);
        if (recompensa) ejercito = bando;
        if (ejercito == 0) return false;
        _proximoViajeAlEjercito = ahora + EntreViajesAlEjercitoMs;

        bool Sirve(int ficha) => _entorno.Datos.Npc(ficha) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == ejercito;
        if (!_entorno.Datos.MapSpawns.TryGetValue(sentidos.Mapa, out var aca) || !aca.Any(s => Sirve(s.NpcIndex)))
        {
            if (_entorno.Mapas.PuebloMasCercano(sentidos.Mapa, sentidos.Posicion, m => !HayGuardiaQueLoAtaca(m), Sirve, Pisable(sentidos),
                                                SaltosHastaElPuebloLejano) is not { } lugar) return false;
            await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, ToleranciaDelCoto,
                               topeDeMapas: Math.Max(Navegante.TopeDeMapas, lugar.Saltos));
            if (sentidos.Mapa != lugar.Mapa || !_entorno.Datos.MapSpawns.TryGetValue(sentidos.Mapa, out aca)) return true;
        }
        if (!aca.Any(s => Sirve(s.NpcIndex))) return true;
        var donde = aca.First(s => Sirve(s.NpcIndex));
        if (!await IrAbriendoAsync(transporte, sentidos, sentidos.Mapa, (byte)donde.X, (byte)donde.Y, cerrada, ct, RadioDelMostrador, Sirve))
            return true;
        if (sentidos.MasCercano(f => Sirve(f)) is not { Pasos: <= RadioDelMostrador } enlistador) return true;
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(enlistador.Quien.X).WriteByte(enlistador.Quien.Y)));
        if (recompensa) _bajasEnLaUltimaRecompensa = cuantas;
        await Task.Delay(_rng.Next(800, 1_600), ct);
        PedirLaFicha(transporte, ya: true);
        return true;
    }
}
