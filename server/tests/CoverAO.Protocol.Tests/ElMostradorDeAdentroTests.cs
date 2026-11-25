using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// AL MERCADER DE ADENTRO DE UNA CASA, DESDE ADENTRO (<see cref="Bot.MostradorDeAdentro"/>). Reportado por el tester (25/11): Fabia le
/// compró un Hacha de Dos Filos a un mercader de adentro, parada afuera contra la pared.
/// </summary>
public sealed class ElMostradorDeAdentroTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    /// <summary>Los mercaderes de adentro que tienen una baldosa de afuera a tres o menos, con la que el servidor los atiende.</summary>
    private static List<(int Mapa, (byte X, byte Y) Vendedor, (byte X, byte Y) Afuera)> AtendidosDesdeAfuera()
    {
        var (datos, mapas) = Mundo.Value;
        var casos = new List<(int, (byte, byte), (byte, byte))>();
        foreach (var (mapa, spawns) in datos.MapSpawns)
            foreach (var n in spawns)
            {
                if (!datos.Npcs.TryGetValue(n.NpcIndex, out var ficha) || ficha.StockItems is null) continue;
                var vendedor = ((byte)n.X, (byte)n.Y);
                if (!Bot.EsInterior(datos, mapa, vendedor)) continue;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int x = n.X + dx, y = n.Y + dy;
                        if (Math.Abs(dx) + Math.Abs(dy) > 3 || x < 1 || y < 1 || x > 100 || y > 100) continue;
                        var p = ((byte)x, (byte)y);
                        if (Bot.EsInterior(datos, mapa, p) || datos.IsBlockedTile(mapa, x, y)) continue;
                        // Sólo los de afuera desde donde se camina hasta adentro (con las puertas como las deja el mundo).
                        if (Bot.MostradorDeAdentro(datos, mapas, mapa, p, vendedor) is null) continue;
                        casos.Add((mapa, vendedor, p));
                        dy = dx = 4;
                    }
            }
        return casos;
    }

    [Fact]
    public void Hay_mercaderes_de_adentro_que_el_servidor_atiende_desde_afuera()
    {
        var casos = AtendidosDesdeAfuera();
        Assert.NotEmpty(casos);
        var (datos, _) = Mundo.Value;
        Assert.All(casos, c => Assert.False(Bot.DelLadoDelMostrador(datos, c.Mapa, c.Afuera, c.Vendedor)));
    }

    [Fact]
    public void Desde_afuera_el_bot_va_a_una_baldosa_de_adentro_a_tiro_del_mercader()
    {
        var (datos, mapas) = Mundo.Value;
        foreach (var (mapa, vendedor, afuera) in AtendidosDesdeAfuera())
        {
            var adentro = Bot.MostradorDeAdentro(datos, mapas, mapa, afuera, vendedor);
            Assert.NotNull(adentro);
            Assert.True(Bot.EsInterior(datos, mapa, adentro!.Value));
            Assert.True(Math.Abs(adentro.Value.X - vendedor.Item1) + Math.Abs(adentro.Value.Y - vendedor.Item2) <= 3);
            Assert.True(Bot.DelLadoDelMostrador(datos, mapa, adentro.Value, vendedor));
        }
    }

    [Fact]
    public void Al_mercader_de_afuera_se_le_compra_desde_cualquier_lado()
    {
        var (datos, _) = Mundo.Value;
        var (mapa, n) = datos.MapSpawns.SelectMany(kv => kv.Value.Select(s => (kv.Key, s)))
            .First(t => datos.Npcs.TryGetValue(t.s.NpcIndex, out var f) && f.StockItems is not null
                        && !Bot.EsInterior(datos, t.Key, ((byte)t.s.X, (byte)t.s.Y)));
        Assert.True(Bot.DelLadoDelMostrador(datos, mapa, ((byte)(n.X + 1), (byte)n.Y), ((byte)n.X, (byte)n.Y)));
    }
}
