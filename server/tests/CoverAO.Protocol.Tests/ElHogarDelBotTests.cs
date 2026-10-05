using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HOGAR DEL BOT, DONDE LE VENDEN (29/09). En `tardeM1` doce de los dieciocho bots de nivel 16 a 28 no encontraban dónde comprar
/// pociones, con decenas de miles de monedas: cazaban del lado de Nix y de Ullathorpe, donde nadie le vende a un ciudadano, y la
/// runa —que es la vuelta a casa— sólo se usaba en un mapa seguro, y para cuatro de ellos la casa era Nix. El bot pone el hogar en
/// una ciudad donde le venden, no se gradúa adentro del dungeon (que lo manda a Nix) y, para ir a comprar, camina hasta un mapa
/// seguro y usa la runa.
/// </summary>
public class ElHogarDelBotTests
{
    [Fact]
    public void Rinkel_puede_ser_el_hogar_de_cualquiera_y_las_ciudades_de_un_bando_solo_de_los_suyos()
    {
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusCiudadano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusCiudadano), "Nix, del imperio");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusRepublicano), "Illiandor, de la república");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusCiudadano));
        Assert.False(PacketDispatcher.PuedeSerHogarDe(37, Player.StatusCiudadano), "el dungeon de novatos no es una ciudad");
    }

    /// <summary>Una vez, y otra al dejar de ser novato: graduarse adentro del dungeon le cambia el hogar a Nix.</summary>
    [Theory]
    // nivel con el que lo puso (0: nunca) | nivel ahora | ¿lo vuelve a pedir?
    [InlineData(0, 5, true)]
    [InlineData(5, 10, false)]
    [InlineData(13, 15, false)]
    [InlineData(13, 16, true)]
    [InlineData(16, 20, false)]
    public void El_hogar_se_pide_una_vez_y_otra_al_dejar_de_ser_novato(int nivelDelHogar, int nivel, bool loPide)
    {
        Assert.Equal(loPide, Bot.VuelveAPonerElHogar(nivelDelHogar, nivel));
    }

    [Fact]
    public void En_el_ultimo_nivel_de_novato_no_se_caza_en_el_dungeon()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(37)), "la cuna, en el último nivel de novato");
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(208)), "el otro mapa de novatos");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel - 1, d.MapZone(37)), "antes, sí");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(20)), "afuera del dungeon, sí");
    }

    /// <summary>
    /// NI ENTRE LOS GUARDIAS QUE VAN A IR POR ÉL AL DEJAR DE SER NOVATO: en el 814 nacen un Paladín, dos Magos y un Bardo
    /// republicanos; al ciudadano de nivel 15 todavía no lo atacan, en el 16 sí, y al republicano nunca. Es Elmo en `alto1`. Del
    /// lado imperial, al revés.
    /// </summary>
    [Fact]
    public void En_el_ultimo_nivel_de_novato_no_se_caza_entre_los_guardias_que_van_a_ir_por_el()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        const int ultimo = GameLimits.NewbieMaxLevel;
        byte ciudadano = Player.StatusCiudadano, republicano = Player.StatusRepublicano;
        Assert.False(Bot.HayGuardiaQueVaPor(814, ciudadano, ultimo, d, ini), "premisa: al novato no lo atacan");
        Assert.True(Bot.HayGuardiaQueVaPor(814, ciudadano, ultimo + 1, d, ini), "premisa: graduado, sí");
        Assert.True(Bot.NoSeGraduaEntreGuardias(814, ciudadano, ultimo, d, ini));
        Assert.False(Bot.NoSeGraduaEntreGuardias(814, ciudadano, ultimo - 1, d, ini), "antes del último nivel, sí");
        Assert.False(Bot.NoSeGraduaEntreGuardias(814, republicano, ultimo, d, ini), "son los suyos");
        Assert.False(Bot.NoSeGraduaEntreGuardias(746, ciudadano, ultimo, d, ini), "el 746, adonde iba, no tiene guardias");
        Assert.True(Bot.NoSeGraduaEntreGuardias(761, republicano, ultimo, d, ini));
        Assert.False(Bot.NoSeGraduaEntreGuardias(761, ciudadano, ultimo, d, ini));
    }

    /// <summary>
    /// NI DONDE, GRADUADO, NO TIENE CÓMO VOLVER A COMPRAR (<see cref="Bot.NivelParaLaVuelta"/>): para un republicano con runa, el 29
    /// —los Bosques de Nix— tiene vuelta mientras es novato (los guardias de Nix todavía no lo atacan) y no la tiene en el 16. Salió de
    /// Gonzo en `alto5`, que se graduó en el 19 y no volvió; el 19 ya tiene vuelta, por el Río Otoren a Rinkel.
    /// </summary>
    [Fact]
    public void En_el_ultimo_nivel_de_novato_la_vuelta_a_comprar_es_la_del_graduado()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        const int ultimo = GameLimits.NewbieMaxLevel, vida = 163;
        var nav = new Navegante(d);
        bool TieneVuelta(int nivel)
        {
            var (pasa, seguro, tienda) = Bot.FiltrosDeLaVuelta(d, ini, Player.StatusRepublicano, nivel, vida);
            return Bot.TieneComoVolver(nav, 29, Bot.DesdeDondeSeCaza(d, 29), pasa, seguro, tienda, conRuna: true);
        }
        Assert.True(TieneVuelta(ultimo), "premisa: de novato, desde el 29 tiene vuelta");
        Assert.False(TieneVuelta(ultimo + 1), "premisa: graduado, no");
        Assert.Equal(ultimo + 1, Bot.NivelParaLaVuelta(ultimo));
        Assert.False(TieneVuelta(Bot.NivelParaLaVuelta(ultimo)));
        Assert.Equal(ultimo - 1, Bot.NivelParaLaVuelta(ultimo - 1));
        Assert.Equal(ultimo + 1, Bot.NivelParaLaVuelta(ultimo + 1));
    }

    /// <summary>
    /// Y EL COTO DEL ÚLTIMO NIVEL DE NOVATO SE ELIGE CON ESA VUELTA, con el mismo filtro `nunca` que arma el bot: un ciudadano de 15
    /// desde la cuna se iba al 262, del lado republicano —donde, graduado, no tiene dónde comprar: así quedaron Ciro y Aldo en el
    /// 195— y ahora va al 18. Un republicano de 15 en los Bosques de Nix —graduado, los guardias de Nix van por él— va al 18, que
    /// tiene vuelta por el Río Otoren a Rinkel; desde que se muda por una vez y media lo que suma (<see cref="Bot.LoMinimoParaMudarsePorLaSuma"/>)
    /// se va con cualquiera de las dos vueltas.
    /// </summary>
    [Fact]
    public void El_coto_del_ultimo_nivel_de_novato_tiene_la_vuelta_del_graduado()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        var nav = new Navegante(d);
        const int nivel = GameLimits.NewbieMaxLevel, vida = 163;
        int? Coto(byte bando, int mapa, (byte X, byte Y) desde, int nivelDeLaVuelta)
        {
            Func<int, bool> esPresa = f => Bot.EsPresaPara(d.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
            Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(d.Npc((short)f), vida, nivel, true, false, bando, d, ini);
            Func<int, IEnumerable<int>> nace = m => d.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(q => q.NpcIndex) : Enumerable.Empty<int>();
            Func<int, int> rinde = f => esPresa(f) ? (int)(d.Npc((short)f)?.GiveExp ?? 0) : 0;
            var (pasa, seguro, tienda) = Bot.FiltrosDeLaVuelta(d, ini, bando, nivelDeLaVuelta, vida);
            Func<int, bool> nunca = m => Bot.HayGuardiaQueVaPor(m, bando, nivel, d, ini) || Bot.NoSeGraduaAhi(nivel, d.MapZone(m))
                || Bot.NoSeGraduaEntreGuardias(m, bando, nivel, d, ini)
                || !(Bot.DesdeDondeSeCaza(d, m) is var dd && (dd.Count == 0 || Bot.TieneComoVolver(nav, m, dd, pasa, seguro, tienda, conRuna: true)));
            Func<int, bool> seCazaAhi = m => Bot.SeCazaAhi(d.IsPkMap(m), nace(m), esPeligroso) && !nunca(m);
            return Bot.ElegirCoto(nav, mapa, desde, esPresa, f => d.Npc((short)f)?.GiveGld > 0, rinde,
                                  Bot.SeCruzaBuscando(d, ini, bando, nivel, vida, conPociones: true, curaVeneno: false),
                                  out _, out _, seCazaAhi, null, nunca, Bot.EligePorLoQueSuma(nivel))?.Mapa;
        }
        byte republicano = Player.StatusRepublicano, ciudadano = Player.StatusCiudadano;
        Assert.Equal(18, Coto(republicano, 29, (90, 11), Bot.NivelParaLaVuelta(nivel)));
        Assert.Equal(262, Coto(ciudadano, 37, (60, 66), nivel));
        Assert.Equal(18, Coto(ciudadano, 37, (60, 66), Bot.NivelParaLaVuelta(nivel)));
    }

    /// <summary>
    /// A COMPRAR, PASADO EL NOVATO Y CON LA RUNA, NO SE CRUZA EL DESIERTO: desde el 13 Rinkel quedaba a cinco mapas por el 21 y el
    /// 16, donde nacen Arañas Gigantes que pegan hasta 115. Medido en `paso16b`: tres bots de nivel 20 y 21 muertos ahí yendo de
    /// compras. Queda a tres por los Campos Abiertos del 19 y el Río Otoren, entrando al pueblo y no a la franja de afuera de la
    /// muralla: el tope de zonas tapaba la entrada al 98 (ver <c>Navegante.BaldosasDeUnaPantalla</c>) y, por donde no lo matan, se
    /// lo daba por imposible.
    /// </summary>
    [Fact]
    public void A_comprar_no_se_cruza_el_desierto()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var nav = new Navegante(d);
        const int nivel = 20, vida = 190;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(nivel, d.MapZone(m));
        Func<int, bool> peligroso = m => d.MapSpawns.TryGetValue(m, out var sp)
            && sp.Any(q => Bot.EsPeligrosoPara(d.Npc((short)q.NpcIndex), vida, nivel, conPociones: true, curaVeneno: false));
        Func<int, bool> sinPeligro = m => pisable(m) && !peligroso(m);
        Func<int, bool> atiende = m => Bot.ElMercaderLeVende(null, d.MapBattleMode(m), Player.StatusCiudadano);
        Func<int, bool> vendePociones = f => d.Npc((short)f)?.StockItems is { Count: > 0 } st
            && st.Any(s => d.Obj((short)s.ObjIndex) is { IsPotion: true, SubType: 3 });
        var desde = ((byte)10, (byte)30);
        Assert.True(peligroso(16), "premisa: en el 16 nace algo que lo mata");
        foreach (var filtro in new[] { pisable, sinPeligro })
        {
            var rinkel = Bot.PuebloConMercader(nav, 13, desde, atiende, vendePociones, filtro, sinPeligro);
            Assert.Equal(20, rinkel?.Mapa);
            Assert.Equal(3, rinkel!.Value.Saltos);
            var ruta = CotoDelBotTests.PorDondePasa(nav, d, 13, desde, 20, (rinkel.Value.X, rinkel.Value.Y), filtro);
            Assert.Equal(20, ruta.LastOrDefault());
            Assert.DoesNotContain(ruta, x => peligroso(x));
            Assert.True(nav.SeLlegaAbriendoUnaPuerta(20, (rinkel.Value.X, rinkel.Value.Y), vendePociones), "entra al pueblo, no a la franja");
        }
    }

    /// <summary>
    /// EL REPUBLICANO DEL LADO DE NIX TIENE SU MAPA SEGURO EN RINKEL, A DOS MAPAS: con los guardias del ini que se distribuye,
    /// Ullathorpe, Nix y Banderbill tienen guardias imperiales que van por él, y desde el 19 Rinkel queda pasando el Río Otoren (el
    /// 98). Se lo daba a seis mapas, más que los cinco de siempre, porque el tope de zonas tapaba la entrada al 98 (ver
    /// <c>Navegante.BaldosasDeUnaPantalla</c>). Al ciudadano también le sirve, antes que Ullathorpe. Medido en `tardeL1`: Milo pasó la
    /// hora sin comida.
    /// </summary>
    [Fact]
    public void El_republicano_del_lado_de_Nix_tiene_su_mapa_seguro_en_Rinkel_a_dos_mapas()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        Assert.True(ini.GuardiasAtacan, "premisa: el ini que se distribuye trae los guardias que atacan");
        var nav = new Navegante(d);
        Func<int, bool> pisable = m => Bot.PuedeEntrar(23, d.MapZone(m));
        Func<int, bool> seguroRepublicano = m => Bot.MapaSeguroPara(m, Player.StatusRepublicano, 23, d, ini);
        Func<int, bool> seguroCiudadano = m => Bot.MapaSeguroPara(m, Player.StatusCiudadano, 23, d, ini);
        Assert.False(seguroRepublicano(1));
        Assert.True(seguroRepublicano(20), "en Rinkel los guardias no pelean");
        var desde = ((byte)57, (byte)69);
        var rinkel = nav.MapaMasCercano(19, desde, seguroRepublicano, pisable);
        Assert.Equal(20, rinkel?.Mapa);
        Assert.Equal(2, rinkel!.Value.Saltos);
        Assert.Equal(new List<int> { 98, 20 }, CotoDelBotTests.PorDondePasa(nav, d, 19, desde, 20, (rinkel.Value.X, rinkel.Value.Y), pisable));
        Assert.Equal(20, nav.MapaMasCercano(19, desde, seguroCiudadano, pisable)?.Mapa);
    }

    /// <summary>Y en Rinkel le venden a un ciudadano, y al sacerdote se llega desde la entrada de la cuna —por donde el bot entra a
    /// comprar— abriendo una puerta del templo: sin abrirla, a cinco baldosas no se llega, y el /HOGAR pide cinco o menos.</summary>
    [Fact]
    public void En_Rinkel_le_venden_y_al_sacerdote_se_llega_abriendo_el_templo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.ElMercaderLeVende(null, d.MapBattleMode(20), Player.StatusCiudadano));
        Assert.False(Bot.ElMercaderLeVende(null, d.MapBattleMode(34), Player.StatusCiudadano), "premisa: en Nix no le venden");
        var nav = new Navegante(d);
        var templo = d.MapSpawns[20].First(s => d.Npc((short)s.NpcIndex)?.NpcType == 1);
        var llegada = d.MapExits.First(e => e.Key.Map == 37 && e.Value.DestMap == 20).Value;
        var desde = ((byte)llegada.DestX, (byte)llegada.DestY);
        Assert.Null(nav.HayComoLlegar(20, desde, ((byte)templo.X, (byte)templo.Y), 5));
        Assert.True(d.MapObjects[20].Any(o => d.Obj((short)o.ObjIndex) is { IsDoor: true }
                                          && nav.AbrirlaDaCamino(20, desde, ((byte)o.X, (byte)o.Y), f => d.Npc((short)f)?.NpcType == 1)),
            "ninguna puerta de Rinkel da camino al sacerdote");
    }
}
