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

    /// <summary>
    /// LA BALDOSA A LA QUE NO SE LLEGO TAMPOCO SIRVE. El paso hacia el botin es codicioso: con una pared en el medio el bot
    /// gasta los dieciseis pasos, no llega, y sin anotar la baldosa vuelve a elegir el MISMO botin en la vuelta siguiente.
    /// Medido: `levantar botin` se llevo el 63 % del tiempo de ocho bots, y un Gladiador termino en nivel 1 con cero de oro.
    /// </summary>
    [Fact]
    public void La_baldosa_a_la_que_no_se_llego_queda_anotada()
    {
        // No llego: no sirve, diga lo que diga el piso.
        Assert.True(Bot.BaldosaQueNoSirve(false, 0, 0));
        Assert.True(Bot.BaldosaQueNoSirve(false, 12, 0));
        Assert.True(Bot.BaldosaQueNoSirve(false, 12, 12));

        // Llego y levanto: el piso cambio, la baldosa sigue sirviendo.
        Assert.False(Bot.BaldosaQueNoSirve(true, 12, 0));
        Assert.False(Bot.BaldosaQueNoSirve(true, 12, 26));

        // Llego y no levanto —la mochila llena, o algo que el servidor no deja—: no sirve.
        Assert.True(Bot.BaldosaQueNoSirve(true, 12, 12));
        Assert.True(Bot.BaldosaQueNoSirve(true, 0, 0));
    }
}
