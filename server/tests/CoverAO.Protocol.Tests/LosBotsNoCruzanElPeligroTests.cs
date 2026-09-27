using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS DOS DEFECTOS DE `tienda4` (25/09), sobre el mundo de verdad y con las mismas piezas que usa el bot.
/// <list type="bullet">
/// <item>Una Pescadora de nivel 14 murió en el 17 (cinco Arañas gigantes, nivel 24, pegan hasta 115): pescaba en el 101 y
/// eligió cazar en el 551, al que sólo se llega por el 17. La búsqueda de adónde ir cruzaba con <c>Pisable</c> y el viaje,
/// sin camino sin peligro, iba por el de siempre. Ahora se busca sólo por donde no lo matan (<c>CruzaSinPeligro</c>).</item>
/// <item>Un Guerrero ciudadano arrancó la sesión en la Cordillera del Norte (806), con cuatro guardias republicanos, y se pasó
/// 19 de 20 minutos huyendo adentro del mapa: el guardia persigue en su mapa y tira hechizos de lejos. Ahora se sale al
/// mapa más cercano sin guardias que vayan por él (<c>SalirDelMapaAsync</c>).</item>
/// </list>
/// </summary>
public class LosBotsNoCruzanElPeligroTests
{
    private static (GameData Datos, Navegante Mapas)? Mundo()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0 || datos.MapExits.Count == 0) return null;
        return (datos, new Navegante(datos));
    }

    [Fact]
    public void Desde_el_101_el_551_se_alcanza_solo_cruzando_el_17_y_sin_peligro_no()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 14, vida = 120;   // Dalia: 114 de vida en el nivel 13 del arranque de `tienda4`
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));
        var cruzaSinPeligro = Bot.SeCruzaBuscando(m.Datos, new ServerIni(), Player.StatusCiudadano, nivel, vida, conPociones: true,
            curaVeneno: false);

        Assert.True(m.Datos.IsPkMap(17), "premisa: el 17 es inseguro, así que lo que nace ahí cuenta");
        Assert.False(cruzaSinPeligro(17), "en el 17 nacen Arañas gigantes, que a nivel 14 lo matan");
        Assert.NotNull(m.Mapas.MapaMasCercano(101, (10, 26), mapa => mapa == 551, pisable));   // por el camino de siempre, sí
        Assert.Null(m.Mapas.MapaMasCercano(101, (10, 26), mapa => mapa == 551, cruzaSinPeligro));
    }

    [Fact]
    public void De_la_Cordillera_del_Norte_hay_a_un_paso_un_mapa_sin_guardias_que_vayan_por_un_ciudadano()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 18;               // Ciro, en el arranque de `tienda4`
        var ini = new ServerIni();
        Assert.True(Bot.HayGuardiaQueVaPor(806, Player.StatusCiudadano, nivel, m.Datos, ini),
            "premisa: en el 806 nacen guardias republicanos que van por un ciudadano");
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));

        // La misma búsqueda que `SalirDelMapaAsync`, desde donde arrancó.
        var afuera = m.Mapas.MapaMasCercano(806, (54, 52),
            mapa => mapa != 806 && pisable(mapa) && !Bot.HayGuardiaQueVaPor(mapa, Player.StatusCiudadano, nivel, m.Datos, ini), pisable);

        Assert.NotNull(afuera);
        Assert.Equal(1, afuera!.Value.Saltos);
        Assert.False(Bot.HayGuardiaQueVaPor(afuera.Value.Mapa, Player.StatusCiudadano, nivel, m.Datos, ini));
    }

    /// <summary>ADÓNDE SE SALE (`tienda7`): de la Cordillera del Norte, el vecino con menos de lo que lo mata. El Río Daglor (798)
    /// tiene cuatro Sanguijuelas gigantes; el Bosque mortecino (805), un solo Turbador.</summary>
    [Fact]
    public void De_la_Cordillera_del_Norte_se_sale_por_el_vecino_menos_peligroso()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 18, vida = 199;   // Ciro
        var ini = new ServerIni();
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(m.Datos.Npc((short)f), vida, nivel, conPociones: true,
            curaVeneno: false, Player.StatusCiudadano, m.Datos, ini);
        Func<int, int> peligros = mapa => m.Datos.MapSpawns.TryGetValue(mapa, out var nacen) ? nacen.Count(sp => esPeligroso(sp.NpcIndex)) : 0;
        Func<int, bool> sirve = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa))
            && !Bot.HayGuardiaQueVaPor(mapa, Player.StatusCiudadano, nivel, m.Datos, ini);

        Assert.True(peligros(798) > peligros(805), "premisa: el 798 es más peligroso que el 805 para él");
        var orden = Bot.VecinosDelMenosPeligroso(m.Datos, 806, sirve, peligros).ToList();

        Assert.Equal(805, orden[0]);
        Assert.DoesNotContain(807, orden);   // con guardias que van por él
        Assert.DoesNotContain(814, orden);
    }

    /// <summary>
    /// DEL MAPA DE SUS GUARDIAS NO SE CAZA AHÍ (`pelea_b2`): un Guerrero ciudadano cortó en (28,40) la salida de la Cordillera del
    /// Norte y la búsqueda de coto, sin ningún vecino que se cruce sin peligro, caía a la de repuesto y elegía el mismo 806, hacia
    /// donde nacen los guardias. Con los guardias en el "nunca", ese mapa no sale ni como repuesto.
    /// </summary>
    [Fact]
    public void Desde_el_mapa_de_sus_guardias_el_coto_nunca_es_ese_mapa()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 18, vida = 199;   // Ciro
        var ini = new ServerIni();
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(m.Datos.Npc((short)f), vida, nivel, conPociones: true,
            curaVeneno: false, Player.StatusCiudadano, m.Datos, ini);
        Func<int, bool> esPresa = f => Bot.EsPresaPara(m.Datos.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, IEnumerable<int>> nace = mapa => m.Datos.MapSpawns.TryGetValue(mapa, out var n) ? n.Select(sp => sp.NpcIndex) : Enumerable.Empty<int>();
        var cruza = Bot.SeCruzaBuscando(m.Datos, ini, Player.StatusCiudadano, nivel, vida, conPociones: true, curaVeneno: false);
        Func<int, bool> guardia = mapa => Bot.HayGuardiaQueVaPor(mapa, Player.StatusCiudadano, nivel, m.Datos, ini);
        Func<int, bool> seCaza = mapa => Bot.SeCazaAhi(m.Datos.IsPkMap(mapa), nace(mapa), esPeligroso) && !guardia(mapa);
        Func<int, int> rinde = f => esPresa(f) ? (int)(m.Datos.Npc((short)f)?.GiveExp ?? 0) : 0;
        (int Mapa, byte X, byte Y, int Saltos)? Coto(Func<int, bool>? nunca) => Bot.CotoSinVolverAlLleno(m.Mapas, 806, (28, 40),
            esPresa, f => m.Datos.Npc((short)f)?.GiveGld > 0, rinde, cruza, seCaza, _ => false,
            mapa => Bot.SirveParaIrseDelLleno(false, nace(mapa), esPeligroso), mapa => Bot.CuantoLeGusta(Bot.SemillaDelNombre("Ciro"), mapa),
            out _, out _, nunca);

        Assert.Equal(806, Coto(nunca: null)?.Mapa);   // premisa: el repuesto elegía el mismo 806
        Assert.NotEqual(806, Coto(guardia)?.Mapa);
    }

    /// <summary>
    /// DE VUELTA A LA CUNA (`oficios15`): un Druida y un Clérigo de nivel 3 a 5 dejaron la cuna llena por el 58, que tiene cuatro
    /// presas, y se quedaron ahí cuarenta minutos sin pasar del nivel 5. La búsqueda de coto cruzaba sólo por mapas donde no nace
    /// nada peligroso, y en la cuna nacen Lobos. La cuna es segura y ahí caza, así que también se la cruza, y se vuelve a ella.
    /// </summary>
    [Fact]
    public void Desde_el_58_un_bot_de_poca_vida_vuelve_a_cazar_a_la_cuna()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 4, vida = 36;   // Hilda, Druida republicana, en `oficios15`
        const byte bando = Player.StatusRepublicano;
        var ini = new ServerIni();
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(m.Datos.Npc((short)f), vida, nivel, conPociones: true,
            curaVeneno: false, bando, m.Datos, ini);
        Func<int, bool> esPresa = f => Bot.EsPresaPara(m.Datos.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, IEnumerable<int>> nace = mapa => m.Datos.MapSpawns.TryGetValue(mapa, out var n) ? n.Select(sp => sp.NpcIndex) : Enumerable.Empty<int>();
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));
        Func<int, bool> guardia = mapa => Bot.HayGuardiaQueVaPor(mapa, bando, nivel, m.Datos, ini);
        Func<int, bool> seCaza = mapa => Bot.SeCazaAhi(m.Datos.IsPkMap(mapa), nace(mapa), esPeligroso) && !guardia(mapa);
        Func<int, int> rinde = f => esPresa(f) ? (int)(m.Datos.Npc((short)f)?.GiveExp ?? 0) : 0;
        int? Coto(Func<int, bool> cruza) => Bot.CotoSinVolverAlLleno(m.Mapas, 58, (27, 79), esPresa, f => m.Datos.Npc((short)f)?.GiveGld > 0,
            rinde, cruza, seCaza, _ => false, mapa => Bot.SirveParaIrseDelLleno(false, nace(mapa), esPeligroso),
            mapa => Bot.CuantoLeGusta(Bot.SemillaDelNombre("Hilda"), mapa), out _, out _, guardia)?.Mapa;

        // Premisa: con el filtro del viaje (en ningún mapa nace algo peligroso) la cuna no pasa, y se quedaba en el 58.
        Func<int, bool> sinNadaPeligroso = mapa => Bot.PasaPorAhi(mapa, -1, pisable(mapa), nace(mapa).Any(esPeligroso));
        Assert.False(sinNadaPeligroso(37), "premisa: en la cuna nacen Lobos, peligrosos con 36 de vida");
        Assert.Equal(58, Coto(sinNadaPeligroso));

        Assert.Equal(37, Coto(Bot.SeCruzaBuscando(m.Datos, ini, bando, nivel, vida, conPociones: true, curaVeneno: false)));
    }

    /// <summary>
    /// NI AL 208 CON EL SEÑOR DE LAS SOMBRAS (`oficios18`, `oficios20`): dos casters de nivel 7 salían del pueblo del 20 a cazar
    /// Escorpiones al 208 y el Señor de las sombras (pega de 60 a 75) los mató en nivel 8. El 208 es seguro, así que el filtro
    /// de "por donde cazaría" lo dejaba; lo que nace ahí lo mata de un golpe, así que ya no.
    /// </summary>
    [Fact]
    public void Del_pueblo_del_20_un_caster_de_nivel_7_no_va_al_208_del_Senor_de_las_sombras()
    {
        if (Mundo() is not { } m) return;
        const int nivel = 7, vida = 73;   // Nadia en `oficios18`: nivel 7 y 73 de vida en el 20, antes de ir al 208
        const byte bando = Player.StatusCiudadano;
        var ini = new ServerIni();
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(m.Datos.Npc((short)f), vida, nivel, conPociones: true,
            curaVeneno: false, bando, m.Datos, ini);
        Func<int, bool> esPresa = f => Bot.EsPresaPara(m.Datos.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, IEnumerable<int>> nace = mapa => m.Datos.MapSpawns.TryGetValue(mapa, out var n) ? n.Select(sp => sp.NpcIndex) : Enumerable.Empty<int>();
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));
        Func<int, bool> guardia = mapa => Bot.HayGuardiaQueVaPor(mapa, bando, nivel, m.Datos, ini);
        Func<int, bool> seCaza = mapa => Bot.SeCazaAhi(m.Datos.IsPkMap(mapa), nace(mapa), esPeligroso) && !guardia(mapa);
        Func<int, int> rinde = f => esPresa(f) ? (int)(m.Datos.Npc((short)f)?.GiveExp ?? 0) : 0;
        // Desde la calle del 20: la baldosa de la corrida, (76,14), es adentro de una tienda, y con la puerta cerrada de la data
        // quieta el navegante no sale de ahí.
        int? Coto(Func<int, bool> cruza) => Bot.CotoSinVolverAlLleno(m.Mapas, 20, (55, 40), esPresa, f => m.Datos.Npc((short)f)?.GiveGld > 0,
            rinde, cruza, seCaza, _ => false, mapa => Bot.SirveParaIrseDelLleno(false, nace(mapa), esPeligroso),
            mapa => Bot.CuantoLeGusta(Bot.SemillaDelNombre("Nadia"), mapa), out _, out _, guardia)?.Mapa;

        // Premisas: el Señor de las sombras nace en el 208 y lo mata de un golpe; y sin mirar eso, el coto era el 208.
        Assert.False(m.Datos.IsPkMap(208), "premisa: el 208 es seguro");
        Assert.Contains(m.Datos.MapSpawns[208], sp => Bot.UnGolpeLoMata(m.Datos.Npc((short)sp.NpcIndex), vida));
        Func<int, bool> sinElGolpe = mapa => pisable(mapa) && !guardia(mapa) && Bot.SeCazaAhi(m.Datos.IsPkMap(mapa), nace(mapa), esPeligroso);
        Assert.Equal(208, Coto(sinElGolpe));

        var cruza = Bot.SeCruzaBuscando(m.Datos, ini, bando, nivel, vida, conPociones: true, curaVeneno: false);
        Assert.False(cruza(208));
        Assert.NotEqual(208, Coto(cruza));
        Assert.True(cruza(37), "la cuna sigue pasando: el Lobo pega 18");
        Assert.True(cruza(1), "Ullathorpe sigue pasando: sus Guerreros y Asesinos pegan fuerte, pero no son hostiles");
    }

    /// <summary>
    /// EL CAMINO DE UN VIAJE, POR ESCALONES: por donde no lo matan, por donde cazaría y por el de siempre; el viaje largo, sólo por
    /// los dos primeros. De muerto, por el de siempre: escrito como <c>deMuerto ? null : sinPeligro ?? (...)</c>, el cadáver no
    /// encontraba ninguna puerta y no llegaba nunca al sacerdote de otro mapa.
    /// </summary>
    [Fact]
    public void El_camino_del_viaje_va_por_escalones_y_de_muerto_por_el_de_siempre()
    {
        int? Camino(bool deMuerto, bool viajeLargo, int? sinPeligro, int? porDondeCazaria, int? deSiempre) =>
            Bot.CaminoDelViaje(deMuerto, viajeLargo, () => sinPeligro, () => porDondeCazaria, () => deSiempre);

        Assert.Equal(1, Camino(false, false, 1, 2, 3));
        Assert.Equal(2, Camino(false, false, null, 2, 3));
        Assert.Equal(3, Camino(false, false, null, null, 3));
        Assert.Equal(2, Camino(false, true, null, 2, 3));
        Assert.Null(Camino(false, true, null, null, 3));

        Assert.Equal(3, Camino(true, false, null, null, 3));
        Assert.Equal(3, Camino(true, true, null, null, 3));
        Assert.Equal(3, Camino(true, false, 1, 2, 3));
    }
}
