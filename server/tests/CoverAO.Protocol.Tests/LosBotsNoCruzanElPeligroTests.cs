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
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(m.Datos.Npc((short)f), vida, nivel, conPociones: true,
            curaVeneno: false, Player.StatusCiudadano, m.Datos, new ServerIni());
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));
        // La misma cuenta que `CruzaSinPeligro`: PasaPorAhi sin mapa de destino.
        Func<int, bool> cruzaSinPeligro = mapa => Bot.PasaPorAhi(mapa, -1, pisable(mapa),
            m.Datos.MapSpawns.TryGetValue(mapa, out var nacen) && nacen.Any(sp => esPeligroso(sp.NpcIndex)));

        Assert.False(cruzaSinPeligro(17), "premisa: en el 17 nacen Arañas gigantes, que a nivel 14 lo matan");
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
        Func<int, bool> pisable = mapa => Bot.PuedeEntrar(nivel, m.Datos.MapZone(mapa));
        Func<int, bool> cruza = mapa => Bot.PasaPorAhi(mapa, -1, pisable(mapa), nace(mapa).Any(esPeligroso));
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
}
