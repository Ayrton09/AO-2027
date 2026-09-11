using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DÓNDE CAZAR, CONTRA LOS MAPAS DE VERDAD. Lo que paga va primero sólo si hay suficiente: en `sano3` siete de ocho bots recién
/// creados se mudaron de la cuna al mapa 62, donde cuatro Águilas pagan para todos, y cazaron cuatro criaturas en media hora.
/// </summary>
public class CotoDelBotTests
{
    private static (Navegante Mapas, GameData Datos)? Mundo()
    {
        var datos = GameData.Load();
        return datos.MapExits.Count == 0 || datos.Npc(500) is null ? null : (new Navegante(datos), datos);
    }

    private static Func<int, bool> Presa(GameData d, int vida, int nivel) =>
        f => Bot.EsPresaPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);

    /// <summary>
    /// UN NIVEL 1 NO SE VA A LAS CUATRO ÁGUILAS DEL 62, el coto que paga más cercano, y caza en la cuna, que tiene 38 presas y ninguna
    /// paga: desde la cuna, y desde la llegada al 208 y al mapa 1, que tienen 8 y 1. Desde el 62 no se prueba: a pie es un pozo, la
    /// salida de vuelta a la cuna no se alcanza desde las Águilas.
    /// </summary>
    [Fact]
    public void Un_nivel_uno_caza_en_la_cuna_y_no_en_las_cuatro_aguilas()
    {
        if (Mundo() is not { } m) return;
        var presa = Presa(m.Datos, 20, 1);
        Assert.True(m.Mapas.CuantasSeAlcanzan(62, (50, 30), f => presa(f) && m.Datos.Npc(f)?.GiveGld > 0) is > 0 and <= 4,
            "el 62 dejó de tener pocas Águilas que pagan: el test no prueba nada");
        foreach (var (mapa, desde) in new[] { (37, ((byte)57, (byte)74)), (208, ((byte)56, (byte)72)), (1, ((byte)58, (byte)45)) })
        {
            var coto = Bot.ElegirCoto(m.Mapas, mapa, desde, presa, f => m.Datos.Npc(f)?.GiveGld > 0, null, out bool porElOro);
            Assert.True(coto is not null, $"desde el mapa {mapa} no eligió ningún coto");
            Assert.NotEqual(62, coto!.Value.Mapa);
            Assert.False(porElOro);
            Assert.True(m.Mapas.CuantasSeAlcanzan(coto.Value.Mapa, (coto.Value.X, coto.Value.Y), presa) >= 38,
                $"desde el mapa {mapa} eligió el {coto.Value.Mapa}, con menos presas que la cuna");
        }
    }

    /// <summary>
    /// EN LA CUNA NO SE REPARTE A LA VISTA DE DONDE NACEN LOS LOBOS: para un nivel 1 con pociones, 6 de sus 38 spawns de presa tienen un
    /// Lobo naciendo a la vista, y quedan afuera.
    /// </summary>
    [Fact]
    public void En_la_cuna_no_se_caza_a_la_vista_de_donde_nacen_los_lobos()
    {
        if (Mundo() is not { } m) return;
        var spawns = m.Datos.MapSpawns[37];
        var presas = spawns.Where(sp => Bot.EsPresaPara(m.Datos.Npc(sp.NpcIndex), 20, 1, conPociones: true, curaVeneno: false))
                           .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
        var peligros = spawns.Where(sp => Bot.EsPeligrosoPara(m.Datos.Npc(sp.NpcIndex), 20, 1, conPociones: true, curaVeneno: false))
                             .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
        Assert.Equal(38, presas.Count);
        var lejos = Bot.LejosDelPeligro(presas, peligros);
        Assert.Equal(32, lejos.Count);
        Assert.DoesNotContain(lejos, t => peligros.Any(o => Bot.AlAlcanceDeSuVista(t, o)));
    }

    /// <summary>LO QUE PAGA VALE SI ALCANZA: una que paga por cada cuatro presas del coto más poblado.</summary>
    [Fact]
    public void Lo_que_paga_vale_si_hay_una_por_cada_cuatro_presas()
    {
        Assert.False(Bot.ValeMudarsePorElOro(4, 38));
        Assert.True(Bot.ValeMudarsePorElOro(9, 36));
        Assert.True(Bot.ValeMudarsePorElOro(1, 1));
    }
}
