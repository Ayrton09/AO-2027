using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT ABRE LAS PUERTAS. Era el hueco que le cerraba medio juego: 494 puertas en el mundo, 381 sin llave, y ni
/// una línea para abrirlas — así que todo lo que vive adentro de un edificio le era inalcanzable. Medido con el
/// navegante real: los ocho vendedores de pergaminos del mapa 20 dan "no hay camino" desde la entrada, y las once
/// puertas de ese mapa están cerradas y ninguna tiene llave.
/// </summary>
public class PuertasDelBotTests
{
    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    /// <summary>
    /// QUÉ PUERTA SE PUEDE ABRIR, contra la data de verdad: cerrada, sin llave y con versión abierta. Y la abierta
    /// NO se vuelve a abrir, que es lo que evita que el bot se quede haciendo doble clic para siempre.
    /// </summary>
    [Fact]
    public void La_puerta_cerrada_sin_llave_se_abre_y_la_abierta_no()
    {
        var datos = Datos();
        var puertas = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).Where(o => o is { IsDoor: true }).ToList();
        Assert.True(puertas.Count >= 10, $"la data trae {puertas.Count} puertas: el test no prueba nada");

        var cerradaSinLlave = puertas.FirstOrDefault(o => o!.Closed == 1 && o.Llave == 0 && o.IndexAbierta > 0);
        var abierta = puertas.FirstOrDefault(o => o!.Closed == 0);
        var conLlave = puertas.FirstOrDefault(o => o!.Llave != 0);

        Assert.True(cerradaSinLlave is not null, "no hay ninguna puerta cerrada sin llave: el test no prueba nada");
        Assert.True(Bot.PuertaQueSeAbre(cerradaSinLlave));
        if (abierta is not null) Assert.False(Bot.PuertaQueSeAbre(abierta), $"'{abierta.Name}' ya está abierta");
        if (conLlave is not null) Assert.False(Bot.PuertaQueSeAbre(conLlave), $"'{conLlave.Name}' pide llave");
        Assert.False(Bot.PuertaQueSeAbre(null));
        var noEsPuerta = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).First(o => o is { IsWeapon: true });
        Assert.False(Bot.PuertaQueSeAbre(noEsPuerta));
    }

    /// <summary>
    /// LA DISTANCIA ES LA EUCLÍDEA DEL ORIGINAL, no el Chebyshev de los gates de NPC: la diagonal de dos —2,83— NO
    /// alcanza, y el servidor contesta "Estás muy lejos". Si el bot usa la medida equivocada manda el doble clic
    /// desde donde no lo van a escuchar.
    /// </summary>
    [Theory]
    // dx | dy | ¿alcanza?
    [InlineData(0, 0, true)]
    [InlineData(2, 0, true)]      // justo, en línea recta
    [InlineData(0, -2, true)]
    [InlineData(1, 1, true)]      // la diagonal de uno: 1,41
    [InlineData(2, 2, false)]     // la diagonal de dos: 2,83 — NO alcanza
    [InlineData(3, 0, false)]
    [InlineData(2, 1, false)]     // 2,24
    public void Se_abre_a_distancia_euclidea_de_dos(int dx, int dy, bool alcanza)
    {
        Assert.Equal(alcanza, Bot.AlAlcanceDeLaPuerta(dx, dy));
    }

    /// <summary>
    /// Y EL CASO QUE LO MOTIVA, contra los mapas de verdad: el mapa 20 (Rinkel) tiene puertas cerradas sin llave, y
    /// detrás de ellas están los vendedores de pergaminos a los que el bot no llegaba.
    /// </summary>
    [Fact]
    public void En_rinkel_hay_puertas_cerradas_sin_llave_y_vendedores_detras()
    {
        var datos = Datos();
        if (!datos.MapObjects.TryGetValue(20, out var objetos)) return;   // sin la data de mapas no hay nada que mirar

        int puertasQueSeAbren = objetos.Count(o => Bot.PuertaQueSeAbre(datos.Obj((short)o.ObjIndex)));
        Assert.True(puertasQueSeAbren >= 5,
            $"el mapa 20 tiene {puertasQueSeAbren} puertas que el bot puede abrir; se esperaban varias");

        if (!datos.MapSpawns.TryGetValue(20, out var spawns)) return;
        int vendenPergaminos = spawns.Count(s => datos.Npc(s.NpcIndex) is { StockItems: { Count: > 0 } st }
                                                 && st.Any(o => datos.Obj((short)o.ObjIndex) is { IsPergamino: true }));
        Assert.True(vendenPergaminos > 0, "el mapa 20 dejó de tener vendedores de pergaminos: el caso ya no aplica");
    }

    /// <summary>
    /// SE VIAJA AL PUEBLO AUNQUE NO SE LLEGUE AL MERCADER. Buscar la puerta donde el bot está parado sólo sirve si
    /// ya llegó, y en el bosque no hay ninguna: medido en `puertas1`, once veces "NINGUNA a la vista" con los bots
    /// en m202 y m807. El mercader de ciudad está adentro de un edificio, así que "no se llega a ninguno" es lo
    /// normal desde afuera — pero el MAPA sí se encuentra, y una vez adentro la puerta está a la vista.
    /// </summary>
    [Theory]
    // ¿hay mercader al que se llegue? | ¿el mapa que lo tiene es otro? | ¿se viaja igual?
    [InlineData(false, true, true)]     // el caso: nadie alcanzable, pero el pueblo está a unos saltos
    [InlineData(false, false, false)]   // ya está en ese mapa: lo que falta es la puerta, no el viaje
    [InlineData(true, true, false)]     // hay uno alcanzable: se va por el camino de siempre
    [InlineData(true, false, false)]
    public void Se_viaja_al_pueblo_cuando_no_hay_mercader_alcanzable(bool hayDondeLlegar, bool otroMapa, bool viaja)
    {
        Assert.Equal(viaja, Bot.ValeViajarAlPueblo(hayDondeLlegar, otroMapa));
    }
}
