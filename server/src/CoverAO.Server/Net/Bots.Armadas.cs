using CoverAO.Protocol;
using CoverAO.Server.Data;
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

    /// <summary>A la persona con la que ya peleó, quede en pie o no, no la vuelve a buscar por este rato; si ella le pega, se defiende
    /// igual (<see cref="AQuienSeDefiende"/>). Eran 5 minutos y sólo para la que quedaba en pie: en la ronda 62 Elmo, renegado de nivel
    /// 40, mató en el 13 a Quim y a Dalia, de nivel 21 a 24, dos veces a cada uno (el muerto revive y vuelve a donde cazaba).</summary>
    public const int GenteDejadaMs = 15 * 60_000;

    /// <summary>Hasta cuándo vale el nivel que se vio al mirar a alguien.</summary>
    private const int NivelVistoMs = 5 * 60_000;

    /// <summary>Al que ya miró y no contestó (el que apagó su nombre, o se fue del rango del clic), no lo vuelve a mirar por este rato:
    /// sale a pegarle sin saber su nivel, como antes.</summary>
    private const int MirarDeNuevoMs = 30_000;

    private readonly Dictionary<short, long> _miradoEn = new();

    /// <summary>Si le bajó la vida hace menos que esto y no tiene una criatura encima, le está pegando una persona.</summary>
    private const int LePeganHaceMs = 2_500;

    /// <summary>Hasta dónde busca al que le pega: un hechizo llega desde lejos.</summary>
    private const int RadioDeDefensa = 8;

    /// <summary>Entre un viaje al enlistador y el siguiente.</summary>
    public const int EntreViajesAlEjercitoMs = 10 * 60_000;

    /// <summary>Si el viaje lo cortó lo que lo mata, a tiro, vuelve a salir a este rato (la vuelta del ciclo huye o pelea primero), hasta
    /// <see cref="CortesSeguidosAlEjercito"/> veces seguidas.</summary>
    public const int ReintentoDelEjercitoMs = 30_000, CortesSeguidosAlEjercito = 6;

    /// <summary>Cada cuánto pide la ficha de estadísticas, además de después de cada pelea con gente.</summary>
    private const int EntreFichasMs = 5 * 60_000;

    private readonly Dictionary<short, long> _genteDejada = new();
    private long _proximoViajeAlEjercito, _proximaFicha;
    private int _bajasEnLaUltimaRecompensa = -1, _cortesAlEjercito;
    private bool _runaAlEjercito;

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

    /// <summary>Hasta cuántos mapas se busca el camino al enlistador: los de la Armada y la Milicia quedan en Banderbill y en Suramei,
    /// y pasado el novato no se entra por la zona de novatos, así que el camino a pie es largo (de los Campos Abiertos a Banderbill,
    /// trece mapas por la costa este).</summary>
    public const int SaltosHastaElEnlistador = 20;

    /// <summary>Cuántas puertas seguidas abre para llegar al enlistador (a Jerek, dos).</summary>
    public const int PuertasHastaElEnlistador = 3;

    /// <summary>
    /// POR DÓNDE SE ENTRA AL ENLISTADOR: la salida de otro mapa que deja, en el mapa del enlistador, en un lugar desde donde se lo
    /// alcanza a pie o abriendo una puerta; de ésas, la más cercana a la que se llega. Devuelve la baldosa de la salida (pisarla es
    /// entrar) y a cuántos mapas queda; null si no hay ninguna.
    ///
    /// <para>Hace falta porque los enlistadores viven adentro: a Jerek (Armada, Distrito Real 60) no se llega desde ninguna entrada de
    /// Banderbill, sólo bajando del castillo (255), que se sube desde el mismo 60; y el navegante no arma un camino que sale de un mapa
    /// para volver a entrar a otra parte del mismo.</para>
    ///
    /// <para>Y A LA SALIDA SE TIENE QUE LLEGAR desde donde se cae al entrar a su mapa: el 185, por el que se entra a Suramei, está
    /// partido, y a su salida (9,94) no se llega ni desde el 186 ni desde el 261 (ronda 62: Lira entraba y quedaba "sin camino").</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? EntradaAlEnlistador(GameData datos, Navegante mapas, Func<int, bool> sirve,
                                                                             int mapa, (byte X, byte Y) desde, Func<int, bool>? pisable)
    {
        var dondeEsta = datos.MapSpawns.Where(kv => kv.Value.Any(s => sirve(s.NpcIndex))).Select(kv => kv.Key).ToHashSet();
        // Por dónde se entra a cada mapa al que se llega, y a cuántos saltos.
        var entradas = mapas.MapasAlcanzables(mapa, desde, SaltosHastaElEnlistador, pisable).ToList();
        (int Mapa, byte X, byte Y, int Saltos)? mejor = null;
        foreach (var porMapa in datos.MapExits.Where(e => dondeEsta.Contains(e.Value.DestMap) && e.Key.Map != e.Value.DestMap)
                                              .GroupBy(e => e.Key.Map))
        {
            // De cada mapa, la primera salida (de unas pocas de muestra) que deja donde se alcanza: las de un mismo borde dejan en la
            // misma zona, y probarlas todas es una búsqueda por baldosa.
            var salidas = porMapa.OrderBy(e => (e.Key.X, e.Key.Y)).ToList();
            foreach (var e in new[] { salidas[0], salidas[salidas.Count / 2], salidas[^1] }.Distinct())
            {
                if (!mapas.SeLlegaAbriendoPuertas(e.Value.DestMap, ((byte)e.Value.DestX, (byte)e.Value.DestY), sirve, PuertasHastaElEnlistador))
                    continue;
                (byte X, byte Y) salida = ((byte)e.Key.X, (byte)e.Key.Y);
                int? saltos = porMapa.Key == mapa ? (mapas.HayComoLlegar(mapa, desde, salida, 1) is not null ? 0 : null)
                            : entradas.Where(en => en.Mapa == porMapa.Key && mapas.HayComoLlegar(en.Mapa, en.Entrada, salida, 1) is not null)
                                      .Select(en => (int?)en.Saltos).Min();
                if (saltos is { } n && (mejor is null || n < mejor.Value.Saltos)) mejor = (porMapa.Key, salida.X, salida.Y, n);
            }
        }
        return mejor;
    }

    /// <summary>
    /// ¿CUÁNDO VUELVE A SALIR AL ENLISTADOR? Si el viaje lo cortó lo que lo mata, a tiro (<paramref name="cortoPorPeligro"/>), en
    /// <see cref="ReintentoDelEjercitoMs"/>, mientras no lleve más de <see cref="CortesSeguidosAlEjercito"/> cortes seguidos
    /// (<paramref name="cortesSeguidos"/>, contando éste); si no, en <see cref="EntreViajesAlEjercitoMs"/>. Visto en la ronda 62: Lira,
    /// a un mapa de Barinke, cortó el viaje en el 186 y no volvía a salir en diez minutos.
    /// </summary>
    public static long ProximoViajeAlEjercito(long ahora, bool cortoPorPeligro, int cortesSeguidos) =>
        ahora + (cortoPorPeligro && cortesSeguidos <= CortesSeguidosAlEjercito ? ReintentoDelEjercitoMs : EntreViajesAlEjercitoMs);

    /// <summary>
    /// SIN CAMINO AL ENLISTADOR, A CASA CON LA RUNA. Del lado de Orac (la Fortaleza Kerthon, el 815, el 823 y el 824, unos ochenta
    /// mapas) no se sale a pie, ni pasado el novato ni antes: se entra por el teleporte de Rinkel al 457, que es de una sola mano, y sus
    /// pasajes cruzan el mar (piden Navegación). Ahí cazan republicanos como Hilda y Bruma, con hogar en Illiandor, desde donde la
    /// Milicia queda a mano. Camina hasta el mapa seguro más cercano, donde la runa anda, la usa, y el viaje se vuelve a probar en
    /// <see cref="ReintentoDelEjercitoMs"/>. Una vez por cada vez que se queda sin camino: si desde el hogar tampoco hay, espera lo de
    /// siempre. Devuelve si hizo algo.
    /// </summary>
    private async Task<bool> ALaCasaParaElEjercitoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (_runaAlEjercito || sentidos.Ranura(obj => _entorno.Datos.Obj(obj) is { ObjType: ObjTypeRuna }) == 0) return false;
        if (_entorno.Datos.IsPkMap(sentidos.Mapa))
        {
            if (!await IrAlMapaSeguroAsync(transporte, sentidos, cerrada, ct)) return false;
            // Si no llegó, se vuelve a probar enseguida, como el viaje que cortó el peligro.
            if (_entorno.Datos.IsPkMap(sentidos.Mapa)) { ViajeAlEjercitoCortado(); return true; }
        }
        _runaAlEjercito = true;
        int antes = sentidos.Mapa;
        var dondeEstaba = sentidos.Posicion;
        if (!Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna)) return false;
        for (int i = 0; i < 12 && sentidos.Mapa == antes && sentidos.Posicion == dondeEstaba; i++) await Task.Delay(250, ct);
        _proximoViajeAlEjercito = Environment.TickCount64 + ReintentoDelEjercitoMs;
        return true;
    }

    /// <summary>Lo que hace el viaje que no llegó con <see cref="ProximoViajeAlEjercito"/>.</summary>
    private void ViajeAlEjercitoCortado()
    {
        bool porPeligro = _cortoPorElHueco || _agotadoPorPeligro;
        _cortoPorElHueco = false;
        _cortesAlEjercito = porPeligro ? _cortesAlEjercito + 1 : 0;
        _proximoViajeAlEjercito = ProximoViajeAlEjercito(Environment.TickCount64, porPeligro, _cortesAlEjercito);
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
    /// También al que dejó hace poco (<see cref="GenteDejadaMs"/>): eso es para no ir a buscarlo, no para dejarse pegar.
    /// </summary>
    private short? AQuienSeDefiende(SentidosDelBot sentidos)
    {
        if (!_entorno.Datos.IsPkMap(sentidos.Mapa) || sentidos.HaceCuantoLeBajoLaVida > LePeganHaceMs) return null;
        if (!TieneFuelleParaPelear(sentidos) || sentidos.Salud < SeRetira) return null;
        if (sentidos.MasCercano(f => LoEstaAtacando(_entorno.Datos.Npc(f), 1)) is { Pasos: <= 1 }) return null;
        return sentidos.PersonaMasCercana(EsBlancoParaMi()) is { Pasos: <= RadioDeDefensa } quien
            ? quien.Quien.CharIndex : null;
    }

    /// <summary>
    /// ¿LO SALE A BUSCAR, POR SU NIVEL? <paramref name="nivelVisto"/> es lo que dijo su ficha al mirarlo
    /// (<see cref="SentidosDelBot.NivelDe"/>). Al que está más de 5 niveles por encima (<see cref="SentidosDelBot.NivelEscondido"/>)
    /// no; al que no miró todavía, sólo si está adonde llega el clic (<see cref="NpcSystem.RangoVisionX"/> por
    /// <see cref="NpcSystem.RangoVisionY"/>), para mirarlo primero. Visto en la ronda 64: Dalia, ciudadana de nivel 21, salió a pegarle a
    /// Elmo, renegado de nivel 40, y murió.
    /// </summary>
    public static bool SeLoBusca(short? nivelVisto, int dx, int dy) =>
        nivelVisto != SentidosDelBot.NivelEscondido
        && (nivelVisto is not null || (Math.Abs(dx) <= NpcSystem.RangoVisionX && Math.Abs(dy) <= NpcSystem.RangoVisionY));

    /// <summary>¿A QUIÉN SALE A PEGARLE? Desde <see cref="NivelParaPelearConGente"/>, en un mapa inseguro, con la vida arriba de
    /// <see cref="SaludParaPelearConGente"/>, energía y las pociones de pelear, al que se busca por su nivel (<see cref="SeLoBusca"/>).
    /// <c>Mirarlo</c>: antes de ir, mirarlo (<see cref="MirarA"/>), si no se sabe su nivel y no lo miró hace poco.</summary>
    private (short Quien, bool Mirarlo)? AQuienSaleAPegarle(SentidosDelBot sentidos)
    {
        if (sentidos.Nivel < NivelParaPelearConGente || !_entorno.Datos.IsPkMap(sentidos.Mapa)) return null;
        if (sentidos.Salud < SaludParaPelearConGente || !TieneFuelleParaPelear(sentidos)) return null;
        if (sentidos.CuantoTiene(EsPocionDeVida) < PocionesParaPelear) return null;
        var esBlanco = EsBlancoParaMi();
        var (x, y) = sentidos.Posicion;
        if (sentidos.PersonaMasCercana(v => esBlanco(v) && SeLoBusca(sentidos.NivelDe(v.CharIndex, NivelVistoMs), v.X - x, v.Y - y),
                                       GenteDejada()) is not { Pasos: <= RadioDeCaza } quien) return null;
        short ci = quien.Quien.CharIndex;
        bool mirarlo = sentidos.NivelDe(ci, NivelVistoMs) is null
                       && (!_miradoEn.TryGetValue(ci, out long cuando) || Environment.TickCount64 - cuando > MirarDeNuevoMs);
        return (ci, mirarlo);
    }

    /// <summary>LO MIRA: el clic del cliente sobre esa persona, que contesta con su ficha (<see cref="SentidosDelBot.NivelDe"/>). El clic
    /// mira primero la baldosa de abajo (<c>LookatTile</c>), así que se clickea la de arriba de sus pies si cae en el rango del clic.</summary>
    private void MirarA(TransporteDeBot transporte, SentidosDelBot sentidos, short quien)
    {
        _miradoEn[quien] = Environment.TickCount64;
        if (sentidos.Donde(quien) is not { } v) return;
        byte y = v.Y > GameLimits.MinYBorder && Math.Abs(v.Y - 1 - sentidos.Posicion.Y) <= NpcSystem.RangoVisionY ? (byte)(v.Y - 1) : v.Y;
        transporte.Mandar(Paquete(ClientPacketId.LeftClick, w => w.WriteByte(v.X).WriteByte(y)));
    }

    /// <summary>Pelea con esa persona (<see cref="CazarAsync"/>), y después pide la ficha para ver si sumó la baja. Quede en pie o no,
    /// queda dejada un rato (<see cref="GenteDejadaMs"/>). Devuelve false sólo si se cayó la conexión.</summary>
    private async Task<bool> PelearConGenteAsync(TransporteDeBot transporte, SentidosDelBot sentidos, short quien, Task cerrada,
                                                 CancellationToken ct)
    {
        bool sigue = await CazarAsync(transporte, sentidos, quien, cerrada, ct, aGente: true);
        _genteDejada[quien] = Environment.TickCount64 + GenteDejadaMs;
        PedirLaFicha(transporte, ya: true);
        return sigue;
    }

    /// <summary>
    /// AL ENLISTADOR: a enlistarse (<see cref="EjercitoQueBusca"/>) o a cobrar la recompensa (<see cref="TocaRecompensa"/>). Va
    /// caminando hasta el enlistador de su ejército más cercano, por mapas sin guardias que vayan por él (<see cref="PisableDelRescate"/>),
    /// y le hace doble clic, que es lo que hace el cliente: al que no es de su bando lo enlista y al que sí le da la recompensa. Devuelve
    /// si salió a hacerlo.
    ///
    /// <para>EL VIAJE VA COMO EL DEL RESCATE (<c>porCualquierCamino</c>): por donde no hay peligro si hay por dónde, y si no, por el camino
    /// de siempre. Un viaje largo común sólo va por los dos primeros, y en la ronda 62 Gonzo, a doce mapas de Barinke desde el puerto de
    /// Illiandor, no salía ("desde acá no se llega").</para>
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
        // Y SIN LOS MAPAS DE LOS QUE RECIÉN SE FUE HUYENDO, que el viaje no cruza (ver `SinHuidas`). Visto en la ronda 64: Hilda huyó del
        // 277, la entrada elegida volvía a pasar por ahí, y el viaje decía "desde acá no se llega" y esperaba diez minutos.
        var pisable = SinHuidas(PisableDelRescate(_entorno.Datos, _entorno.Ini, _miBando, sentidos.Nivel), sentidos.Mapa);

        bool Sirve(int ficha) => _entorno.Datos.Npc(ficha) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == ejercito;
        // Si desde donde está no lo alcanza, hasta la salida que lo deja donde sí, y la pisa: ver `EntradaAlEnlistador`.
        if (!_entorno.Mapas.SeLlegaAbriendoPuertas(sentidos.Mapa, sentidos.Posicion, Sirve, PuertasHastaElEnlistador))
        {
            if (EntradaAlEnlistador(_entorno.Datos, _entorno.Mapas, Sirve, sentidos.Mapa, sentidos.Posicion, pisable)
                is not { } entrada) return await ALaCasaParaElEjercitoAsync(transporte, sentidos, cerrada, ct);
            _runaAlEjercito = false;
            _cortoPorElHueco = false;
            if (sentidos.Mapa != entrada.Mapa
                || Math.Abs(sentidos.Posicion.X - entrada.X) + Math.Abs(sentidos.Posicion.Y - entrada.Y) > 1)
                await IrHastaAsync(transporte, sentidos, entrada.Mapa, entrada.X, entrada.Y, cerrada, ct, 1,
                                   topeDeMapas: Math.Max(Navegante.TopeDeMapas, entrada.Saltos), porCualquierCamino: true, soloALaMeta: true);
            int dx = entrada.X - sentidos.Posicion.X, dy = entrada.Y - sentidos.Posicion.Y;
            if (sentidos.Mapa != entrada.Mapa || Math.Abs(dx) + Math.Abs(dy) != 1) { ViajeAlEjercitoCortado(); return true; }
            if (await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct, puedeSalir: true) == Paso.SinConexion) return true;
            await Task.Delay(_rng.Next(400, 900), ct);
            if (!_entorno.Mapas.SeLlegaAbriendoPuertas(sentidos.Mapa, sentidos.Posicion, Sirve, PuertasHastaElEnlistador)) return true;
        }
        if (!_entorno.Datos.MapSpawns.TryGetValue(sentidos.Mapa, out var aca) || !aca.Any(s => Sirve(s.NpcIndex))) return true;
        var donde = aca.First(s => Sirve(s.NpcIndex));
        // Y LAS PUERTAS DEL CAMINO, una por una: ver `Navegante.PuertasDelCamino`.
        for (int abiertas = 0; ; abiertas++)
        {
            _cortoPorElHueco = false;
            if (await IrHastaAsync(transporte, sentidos, sentidos.Mapa, (byte)donde.X, (byte)donde.Y, cerrada, ct, RadioDelMostrador)) break;
            if (_cortoPorElHueco || _agotadoPorPeligro) { ViajeAlEjercitoCortado(); return true; }
            if (abiertas == PuertasHastaElEnlistador
                || _entorno.Mapas.PuertasDelCamino(sentidos.Mapa, sentidos.Posicion, new[] { ((byte)donde.X, (byte)donde.Y) }, RadioDelMostrador)
                    is not { Count: > 0 } puertas
                || !await AbrirLaPuertaQueAsync(transporte, sentidos, d => d == puertas[0], cerrada, ct)) return true;
        }
        if (sentidos.MasCercano(f => Sirve(f)) is not { Pasos: <= RadioDelMostrador } enlistador) return true;
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(enlistador.Quien.X).WriteByte(enlistador.Quien.Y)));
        _cortesAlEjercito = 0;
        if (recompensa) _bajasEnLaUltimaRecompensa = cuantas;
        await Task.Delay(_rng.Next(800, 1_600), ct);
        PedirLaFicha(transporte, ya: true);
        return true;
    }
}
