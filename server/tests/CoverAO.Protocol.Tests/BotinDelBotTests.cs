using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// QUÉ SE LEVANTA DEL PISO, con la regla del servidor y la data de verdad. El decorado fijo viaja como objeto del piso, y el bot
/// caminaba hacia él a levantarlo: en `normal2` una Pescadora pasó veinticuatro minutos al lado del Teleport de la cuna.
/// </summary>
public class BotinDelBotTests
{
    /// <summary>LOS TELEPORTS Y LAS PUERTAS NO SE LEVANTAN; el Pollo y las monedas, sí.</summary>
    [Fact]
    public void Los_teleports_y_las_puertas_no_son_botin()
    {
        var d = GameData.Load();
        if (d.Obj(378) is null) return;
        Assert.Equal("Teleport", d.Obj(378)!.Name);
        Assert.False(Bot.SeLevantaDelPiso(d.Obj(378)));
        Assert.False(Bot.SeLevantaDelPiso(d.Obj(7)));
        Assert.True(Bot.SeLevantaDelPiso(d.Obj(26)));
        Assert.True(Bot.SeLevantaDelPiso(d.Obj(12)));
        Assert.False(Bot.SeLevantaDelPiso(null));
    }
}
