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
}
