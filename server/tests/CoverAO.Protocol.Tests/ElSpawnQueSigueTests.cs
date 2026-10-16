using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SPAWN QUE SIGUE, BUSCANDO PRESA EN EL MAPA (<c>Bot.ElSpawnQueSigue</c>): sin presa a la vista, el bot paseaba al azar y recién a
/// los treinta segundos elegía al azar otro spawn. Ahora va al más cercano que no ve desde donde está y que no vio vacío hace poco.
/// </summary>
public class ElSpawnQueSigueTests
{
    private static readonly (byte X, byte Y)[] Spawns = { (50, 50), (70, 50), (90, 50), (50, 85) };

    [Fact]
    public void Va_al_mas_cercano_que_no_ve_desde_aca()
    {
        // (50,50) está a la vista: si tuviera algo, lo vería.
        Assert.Equal(((byte)70, (byte)50), Bot.ElSpawnQueSigue((50, 50), Spawns, _ => false));
    }

    [Fact]
    public void Saltea_los_que_vio_vacios()
    {
        Assert.Equal(((byte)50, (byte)85), Bot.ElSpawnQueSigue((50, 50), Spawns, t => t == (70, 50)));
        Assert.Equal(((byte)90, (byte)50), Bot.ElSpawnQueSigue((50, 50), Spawns, t => t == (70, 50) || t == (50, 85)));
    }

    [Fact]
    public void Sin_ninguno_que_quede_no_va_a_ninguno() =>
        Assert.Null(Bot.ElSpawnQueSigue((50, 50), Spawns, t => t != (50, 50)));
}
