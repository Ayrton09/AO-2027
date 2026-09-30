using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL CAMINO DE SIEMPRE NO CRUZA LO QUE LO MATA DE DOS GOLPES, Y LA BÚSQUEDA DE COTO NO CUENTA LO QUE EL VIAJE NO CRUZA (30/09).
/// Medido en `early7`: Fabia se fue del 261 huyendo, eligió cazar en el 262 —detrás del 261— y el viaje, que el 261 lo tenía tapado,
/// la mandó por el camino de siempre a través del 187 y del 186 (Gladiadores que pegan 282, Ents que pegan 90). En el nivel 13 pasó;
/// en el 15, de compras del 263 a la cuna por lo mismo, un Ent la mató con la vida al 95 %. Ver <see cref="Bot.NaceLoQueLoMataDeDos"/>
/// y <see cref="Bot.CruzaSinHuidas"/>.
/// </summary>
public class ElCaminoDeSiempreTests
{
    [Fact]
    public void Lo_hostil_que_de_dos_golpes_llega_a_la_vida_lo_mata()
    {
        var ent = new NpcInfo(Index: 592, Name: "Ent", Body: 1, Head: 1, Heading: 3, MaxHp: 650, MinHit: 40, MaxHit: 90, Def: 0,
            GiveExp: 1200, GiveGld: 200, PoderEvasion: 30, PoderAtaque: 80, Hostile: true, Attackable: true);
        Assert.True(Bot.LoMataDeDosGolpes(ent, 171));
        Assert.True(Bot.LoMataDeDosGolpes(ent, 180));
        Assert.False(Bot.LoMataDeDosGolpes(ent, 181));
        Assert.False(Bot.LoMataDeDosGolpes(ent with { Hostile = false }, 100), "el guardia que no ataca no cuenta");
    }

    [Fact]
    public void En_el_lago_de_Suramei_nace_lo_que_mata_de_dos_golpes_a_un_nivel_15()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.NaceLoQueLoMataDeDos(d, 186, 171), "los Ents del 186");
        Assert.True(Bot.NaceLoQueLoMataDeDos(d, 187, 171), "los Gladiadores del 187");
        Assert.False(Bot.NaceLoQueLoMataDeDos(d, 262, 171), "los Zombies y Esqueletos del 262");
        Assert.False(Bot.NaceLoQueLoMataDeDos(d, 186, 181), "con más vida, el Ent ya no mata de dos");
    }

    /// <summary>
    /// DEL 263 A LA CUNA CON EL 261 TAPADO, el camino de siempre sólo pasa por el 187 y el 186; sin ellos no hay camino, y el viaje no
    /// sale. Cuando el 261 se destapa, el camino vuelve a estar; y el fantasma va por donde sea.
    /// </summary>
    [Fact]
    public void Del_263_a_la_cuna_el_camino_de_siempre_no_cruza_el_lago_de_los_Ents()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var nav = new Navegante(d);
        const int nivel = 15, vida = 171;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(nivel, d.MapZone(m));
        var huyo = new Dictionary<int, long> { [261] = 1_000 };
        Func<int, bool> sinHuidas = m => !Bot.NoVuelveACruzar(huyo, m, 37, 500);
        var desde = ((byte)11, (byte)67);
        var meta = ((byte)52, (byte)65);
        Assert.NotNull(nav.PuertaHacia(263, 37, desde, meta, m => pisable(m) && sinHuidas(m)));
        Assert.Null(nav.PuertaHacia(263, 37, desde, meta, Bot.PorElCaminoDeSiempre(d, pisable, sinHuidas, 37, vida, deMuerto: false)));
        Assert.NotNull(nav.PuertaHacia(263, 37, desde, meta, Bot.PorElCaminoDeSiempre(d, pisable, _ => true, 37, vida, deMuerto: false)));
        Assert.NotNull(nav.PuertaHacia(263, 37, desde, meta, Bot.PorElCaminoDeSiempre(d, pisable, sinHuidas, 37, vida, deMuerto: true)));
    }

    [Fact]
    public void Buscando_coto_no_cuenta_el_mapa_del_que_se_fue_huyendo()
    {
        var huyo = new Dictionary<int, long> { [261] = 1_000 };
        var cruza = Bot.CruzaSinHuidas(_ => true, huyo, 500);
        Assert.False(cruza(261));
        Assert.True(cruza(262));
        Assert.True(Bot.CruzaSinHuidas(_ => true, huyo, 1_500)(261), "pasado el rato, vuelve a contar");
        Assert.False(Bot.CruzaSinHuidas(_ => false, huyo, 1_500)(262), "y lo que no cruza sigue sin cruzar");
    }

    /// <summary>
    /// DESDE EL 185, CON EL 261 RECIÉN DEJADO, EL 262 NO ES COTO: sólo se llega por el 261. Es Fabia en `early7`, nivel 13 con 149 de
    /// vida, que eligió el 262 y fue por el lago.
    /// </summary>
    [Fact]
    public void Desde_Suramei_con_el_261_recien_dejado_no_elige_el_262()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        var nav = new Navegante(d);
        const int nivel = 13, vida = 149;
        byte bando = Player.StatusCiudadano;
        Func<int, bool> esPresa = f => Bot.EsPresaPara(d.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(d.Npc((short)f), vida, nivel, true, false, bando, d, ini);
        Func<int, IEnumerable<int>> nace = m => d.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>();
        Func<int, bool> nunca = m => Bot.HayGuardiaQueVaPor(m, bando, nivel, d, ini) || Bot.NoSeGraduaAhi(nivel, d.MapZone(m));
        Func<int, bool> seCazaAhi = m => Bot.SeCazaAhi(d.IsPkMap(m), nace(m), esPeligroso) && !nunca(m);
        Func<int, int> rinde = f => esPresa(f) ? (int)(d.Npc((short)f)?.GiveExp ?? 0) : 0;
        var cruza = Bot.SeCruzaBuscando(d, ini, bando, nivel, vida, conPociones: true, curaVeneno: false);
        var huyo = new Dictionary<int, long> { [261] = 1_000 };
        int? Coto(Func<int, bool> pisable) => Bot.CotoSinVolverAlLleno(nav, 185, ((byte)91, (byte)8), esPresa,
            f => d.Npc((short)f)?.GiveGld > 0, rinde, pisable, seCazaAhi, m => m == 261,
            m => m != 261 && Bot.SirveParaIrseDelLleno(false, nace(m), esPeligroso), null, out _, out _, nunca)?.Mapa;
        Assert.Equal(262, Coto(cruza));
        Assert.NotEqual(262, Coto(Bot.CruzaSinHuidas(cruza, huyo, 500)));
    }
}
