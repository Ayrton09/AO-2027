using System;
using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS BOTS EN LAS ARMADAS (pedido del autor, 10/10): a quién le pegan, qué bajas les cuentan, cuándo van al enlistador y la ficha de
/// estadísticas de la que leen las bajas. Ver <c>Bots.Armadas.cs</c>.
/// </summary>
public class ArmadasDeLosBotsTests
{
    private const byte Ren = Player.StatusCriminal, Ciu = Player.StatusCiudadano, Rep = Player.StatusRepublicano,
                       Cao = Player.StatusCaos, Arm = Player.StatusArmada, Mil = Player.StatusMilicia;

    [Fact]
    public void El_que_no_es_agresivo_sólo_le_pega_a_quien_no_lo_vuelve_criminal()
    {
        Assert.True(Bot.EsBlanco(Ciu, Ren, agresivo: false, atacarAliadoVuelveCriminal: true));
        Assert.True(Bot.EsBlanco(Ciu, Cao, false, true));
        Assert.False(Bot.EsBlanco(Ciu, Rep, false, true));    // lo volvería criminal
        Assert.False(Bot.EsBlanco(Ciu, Ciu, false, true));    // el servidor no los deja
        Assert.False(Bot.EsBlanco(Ciu, Arm, false, true));
        Assert.True(Bot.EsBlanco(Arm, Mil, false, true));     // ejércitos enemigos
        Assert.False(Bot.EsBlanco(Arm, Rep, false, true));
        Assert.True(Bot.EsBlanco(Mil, Arm, false, true));
        Assert.False(Bot.EsBlanco(Rep, Mil, false, true));
        Assert.True(Bot.EsBlanco(Rep, Ren, false, true));
        // Con la regla de la casa apagada, el civil del otro bando también.
        Assert.True(Bot.EsBlanco(Ciu, Rep, false, atacarAliadoVuelveCriminal: false));
        // A un GM (7 a 9) nunca, ni a quien no se le ve el bando.
        Assert.False(Bot.EsBlanco(Ciu, 7, false, true));
        Assert.False(Bot.EsBlanco(Ren, 0, true, true));
    }

    [Fact]
    public void El_agresivo_le_pega_al_civil_del_otro_bando_y_ya_renegado_a_cualquiera_menos_al_Caos_si_es_del_Caos()
    {
        Assert.True(Bot.EsBlanco(Ciu, Rep, agresivo: true, atacarAliadoVuelveCriminal: true));
        Assert.True(Bot.EsBlanco(Rep, Ciu, true, true));
        Assert.False(Bot.EsBlanco(Ciu, Arm, true, true));     // el servidor no lo deja
        foreach (byte el in new[] { Ren, Ciu, Rep, Cao, Arm, Mil }) Assert.True(Bot.EsBlanco(Ren, el, true, true));
        Assert.False(Bot.EsBlanco(Cao, Cao, true, true));
        Assert.True(Bot.EsBlanco(Cao, Ciu, true, true));
    }

    /// <summary>Lo que el bot cuenta es lo mismo que el enlistador le cuenta al jugador (<c>Player.KillsEnemigos*</c>).</summary>
    [Fact]
    public void Las_bajas_que_cuenta_el_bot_son_las_del_enlistador()
    {
        var p = new Player { CharIndex = 1, Name = "Prueba", Account = "prueba", RenegadosMatados = 1, CiudadanosMatados = 2, RepublicanosMatados = 4, CaosMatados = 8, ArmadaMatados = 16,
                             MilicianosMatados = 32 };
        var b = new SentidosDelBot.Bajas(Ciudadanos: 2, Renegados: 1, Republicanos: 4, Caos: 8, Armada: 16, Milicianos: 32);
        Assert.Equal(p.KillsEnemigosArmada, Bot.BajasParaSuBando(Ciu, b));
        Assert.Equal(p.KillsEnemigosArmada, Bot.BajasParaSuBando(Arm, b));
        Assert.Equal(p.KillsEnemigosMilicia, Bot.BajasParaSuBando(Rep, b));
        Assert.Equal(p.KillsEnemigosMilicia, Bot.BajasParaSuBando(Mil, b));
        Assert.Equal(p.KillsEnemigosCaos, Bot.BajasParaSuBando(Ren, b));
        Assert.Equal(p.KillsEnemigosCaos, Bot.BajasParaSuBando(Cao, b));
    }

    [Fact]
    public void Se_enlista_con_el_nivel_y_las_bajas_del_enlistador_y_al_Caos_sólo_el_agresivo()
    {
        Assert.Equal(Arm, Bot.EjercitoQueBusca(Ciu, agresivo: false, nivel: 25, bajas: 15));
        Assert.Equal(0, Bot.EjercitoQueBusca(Ciu, false, 24, 15));
        Assert.Equal(0, Bot.EjercitoQueBusca(Ciu, false, 25, 14));
        Assert.Equal(0, Bot.EjercitoQueBusca(Ciu, agresivo: true, 30, 30));
        Assert.Equal(Mil, Bot.EjercitoQueBusca(Rep, false, 25, 15));
        Assert.Equal(Cao, Bot.EjercitoQueBusca(Ren, agresivo: true, 40, 30));
        Assert.Equal(0, Bot.EjercitoQueBusca(Ren, true, 39, 30));
        Assert.Equal(0, Bot.EjercitoQueBusca(Ren, agresivo: false, 40, 30));
        Assert.Equal(0, Bot.EjercitoQueBusca(Arm, false, 40, 100));   // ya está adentro
    }

    [Fact]
    public void Va_a_cobrar_la_recompensa_cada_vez_que_cruza_un_escalón()
    {
        Assert.False(Bot.TocaRecompensa(Arm, 19, -1));
        Assert.True(Bot.TocaRecompensa(Arm, 20, -1));
        Assert.False(Bot.TocaRecompensa(Arm, 24, 20));
        Assert.True(Bot.TocaRecompensa(Arm, 25, 20));
        Assert.True(Bot.TocaRecompensa(Mil, 25, 22));
        Assert.False(Bot.TocaRecompensa(Cao, 29, 20));
        Assert.True(Bot.TocaRecompensa(Cao, 30, 20));
        Assert.False(Bot.TocaRecompensa(Ciu, 100, -1));   // sin ejército no hay recompensa
    }

    /// <summary>Uno de cada nueve, todos cazadores: con los 96 nombres, unos cuantos y no la mitad.</summary>
    [Fact]
    public void Los_agresivos_son_pocos_y_todos_cazadores()
    {
        var agresivos = PobladoDeBots.Nombres.Where(Bot.EsAgresivo).ToList();
        Assert.InRange(agresivos.Count, 4, 24);
        Assert.All(agresivos, n => Assert.Equal(0, Math.Abs(Bot.SemillaDelNombre(n)) % 3));   // 0 = Cazador
    }

    /// <summary>Contra la data: hay un enlistador de cada ejército en el mundo (Jerek, Barinke Fauster y el Demonio).</summary>
    [Fact]
    public void En_el_mundo_hay_un_enlistador_de_cada_ejército()
    {
        var datos = GameData.Load();
        Assert.False(datos.SourceDir is null, "el test necesita la gamedata en disco");
        foreach (byte ejercito in new[] { Arm, Mil, Cao })
            Assert.Contains(datos.MapSpawns.Values.SelectMany(l => l),
                s => datos.Npc(s.NpcIndex) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == ejercito);
    }

    /// <summary>Las bajas salen de la ficha de estadísticas tal como la arma el servidor, y el bando que cambia, del CharStatus.</summary>
    [Fact]
    public void El_bot_lee_sus_bajas_de_la_ficha_y_su_bando_del_CharStatus()
    {
        var p = new Player { CharIndex = 1, Name = "Prueba", Account = "prueba", RenegadosMatados = 3, CiudadanosMatados = 1, RepublicanosMatados = 5, CaosMatados = 7, ArmadaMatados = 2,
                             MilicianosMatados = 9 };
        var s = new SentidosDelBot();
        Assert.Null(s.MisBajas);
        s.Recibir(ServerMessages.MiniStats(p));
        Assert.Equal(new SentidosDelBot.Bajas(1, 3, 5, 7, 2, 9), s.MisBajas);

        s.Recibir(ServerMessages.UserCharIndexInServer(42));
        s.Recibir(ServerMessages.CharStatus(42, Player.StatusCriminal));
        Assert.Equal(Player.StatusCriminal, s.MiEstado);
    }

    /// <summary>
    /// A JEREK (Armada, Distrito Real 60) se llega desde la entrada de Banderbill abriendo dos puertas seguidas, (73,76) y (73,42); y
    /// por tierra, sin la zona de novatos, desde los Campos Abiertos (18) por la entrada de Banderbill Central (59). Al Caos, desde el 18
    /// por la Fortaleza Kerthon (838), y a la Milicia desde el puerto de Illiandor (179). Todo sin pisar los mapas donde nacen guardias
    /// que van por él, con los bandos del Server.ini por defecto: el renegado al Caos, que con el renegado en la lista de los guardias
    /// del Caos no tenía por dónde (ronda 62: Elmo murió dos veces en la puerta de la Fortaleza).
    /// </summary>
    [Fact]
    public void Contra_la_data_se_llega_a_los_tres_enlistadores()
    {
        var datos = GameData.Load();
        Assert.False(datos.SourceDir is null, "el test necesita la gamedata en disco");
        var mapas = new Navegante(datos);
        Func<int, bool> Enlista(byte ejercito) => f => datos.Npc(f) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == ejercito;
        var ini = new ServerIni();

        Assert.Equal(new (byte, byte)[] { (73, 76), (73, 42) }, mapas.PuertasDelCamino(60, (25, 92), new (byte, byte)[] { (73, 23) }, 3));
        Assert.False(mapas.SeLlegaAbriendoUnaPuerta(60, (25, 92), Enlista(Arm)));   // premisa: con una sola no alcanza
        Assert.True(mapas.SeLlegaAbriendoPuertas(60, (25, 92), Enlista(Arm), Bot.PuertasHastaElEnlistador));

        Assert.Equal(59, Bot.EntradaAlEnlistador(datos, mapas, Enlista(Arm), 18, (48, 69), Bot.PisableDelRescate(datos, ini, Ciu, 26))?.Mapa);
        Assert.Equal(838, Bot.EntradaAlEnlistador(datos, mapas, Enlista(Cao), 18, (25, 82), Bot.PisableDelRescate(datos, ini, Ren, 40))?.Mapa);
        Assert.True(LlegaALaSalida(mapas, 179, (10, 10), Bot.PisableDelRescate(datos, ini, Rep, 25),
                                   Bot.EntradaAlEnlistador(datos, mapas, Enlista(Mil), 179, (10, 10), Bot.PisableDelRescate(datos, ini, Rep, 25))));
        // Premisa: la Fortaleza tiene guardias del Caos, y con los bandos por defecto no van por el renegado.
        Assert.True(Bot.HayGuardiaQueVaPor(838, Ciu, 40, datos, ini));
        Assert.False(Bot.HayGuardiaQueVaPor(838, Ren, 40, datos, ini));
    }

    /// <summary>
    /// A LA SALIDA SE LLEGA DESDE DONDE SE CAE AL ENTRAR A SU MAPA. El 185, por el que se entra a Suramei, está partido: a su salida
    /// (9,94) no se llega ni desde el 186 ni desde el 261, y la entrada desde el 262 (donde caza Lira en la ronda 62) tiene que ser
    /// otra. Y el navegante, con <c>soloALaMeta</c>, no da la puerta que deja del lado equivocado.
    /// </summary>
    [Fact]
    public void La_entrada_al_enlistador_deja_donde_se_llega_a_la_salida()
    {
        var datos = GameData.Load();
        Assert.False(datos.SourceDir is null, "el test necesita la gamedata en disco");
        var mapas = new Navegante(datos);
        var pisable = Bot.PisableDelRescate(datos, new ServerIni(), Rep, 26);
        Func<int, bool> milicia = f => datos.Npc(f) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == Mil;
        // Premisas: a (9,94) no se llega desde ninguna de las dos entradas, y sin soloALaMeta el navegante igual da una puerta.
        Assert.Null(mapas.HayComoLlegar(185, (10, 8), (9, 94), 1));
        Assert.Null(mapas.HayComoLlegar(185, (91, 8), (9, 94), 1));
        Assert.NotNull(mapas.PuertaHacia(262, 185, (50, 73), (9, 94), pisable, Bot.SaltosHastaElEnlistador));

        Assert.Null(mapas.PuertaHacia(262, 185, (50, 73), (9, 94), pisable, Bot.SaltosHastaElEnlistador, soloALaMeta: true));
        var entrada = Bot.EntradaAlEnlistador(datos, mapas, milicia, 262, (50, 73), pisable);
        Assert.NotEqual((185, (byte)9, (byte)94), (entrada?.Mapa ?? 0, entrada?.X ?? 0, entrada?.Y ?? 0));
        Assert.True(LlegaALaSalida(mapas, 262, (50, 73), pisable, entrada));
    }

    /// <summary>
    /// DEL LADO DE ORAC NO SE LLEGA A LA MILICIA A PIE, y desde Illiandor, el hogar de los republicanos que cazan ahí (Hilda y Bruma, en
    /// el 823), sí: es la premisa de la runa del viaje al enlistador (<c>ALaCasaParaElEjercitoAsync</c>).
    /// </summary>
    [Fact]
    public void Del_lado_de_Orac_no_se_llega_a_la_Milicia_y_desde_Illiandor_si()
    {
        var datos = GameData.Load();
        Assert.False(datos.SourceDir is null, "el test necesita la gamedata en disco");
        var mapas = new Navegante(datos);
        var pisable = Bot.PisableDelRescate(datos, new ServerIni(), Rep, 25);
        Func<int, bool> milicia = f => datos.Npc(f) is { } n && NpcSystem.EjercitoDelEnlistador(n.NpcType, n.Status) == Mil;
        Assert.Null(Bot.EntradaAlEnlistador(datos, mapas, milicia, 823, (48, 18), pisable));
        Assert.NotNull(Bot.EntradaAlEnlistador(datos, mapas, milicia, 194, (52, 79), pisable));
        Assert.False(datos.IsPkMap(815), "premisa: el 815, a un mapa del 823, es seguro y ahí la runa anda");
    }

    /// <summary>Si a la salida de <paramref name="entrada"/> se llega a pie, entrando a su mapa por alguna de las entradas a las que se
    /// llega desde <paramref name="desde"/>.</summary>
    private static bool LlegaALaSalida(Navegante mapas, int mapa, (byte X, byte Y) desde, Func<int, bool> pisable,
                                       (int Mapa, byte X, byte Y, int Saltos)? entrada) =>
        entrada is { } e
        && (e.Mapa == mapa ? mapas.HayComoLlegar(mapa, desde, (e.X, e.Y), 1) is not null
            : mapas.MapasAlcanzables(mapa, desde, Bot.SaltosHastaElEnlistador, pisable)
                   .Any(en => en.Mapa == e.Mapa && en.Saltos == e.Saltos && mapas.HayComoLlegar(e.Mapa, en.Entrada, (e.X, e.Y), 1) is not null));

    /// <summary>
    /// EL VIAJE AL ENLISTADOR QUE CORTÓ EL PELIGRO vuelve a salir en medio minuto, hasta seis veces seguidas; el que no llegó por otra
    /// cosa, o el séptimo corte seguido, en diez minutos.
    /// </summary>
    [Fact]
    public void El_viaje_al_enlistador_cortado_por_el_peligro_se_retoma_enseguida()
    {
        Assert.Equal(1_000 + Bot.ReintentoDelEjercitoMs, Bot.ProximoViajeAlEjercito(1_000, true, 1));
        Assert.Equal(1_000 + Bot.ReintentoDelEjercitoMs, Bot.ProximoViajeAlEjercito(1_000, true, Bot.CortesSeguidosAlEjercito));
        Assert.Equal(1_000 + Bot.EntreViajesAlEjercitoMs, Bot.ProximoViajeAlEjercito(1_000, true, Bot.CortesSeguidosAlEjercito + 1));
        Assert.Equal(1_000 + Bot.EntreViajesAlEjercitoMs, Bot.ProximoViajeAlEjercito(1_000, false, 0));
    }
}
