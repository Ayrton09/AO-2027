using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El techo de los atributos (VB6 <c>MAXATRIBUTOS = 35</c>, Declares.bas:349). Es el que aplican la poción
/// verde y la amarilla (InvUsuario.bas:1575/1592) y los hechizos de fuerza y agilidad (modHechizos.bas:1338/
/// 1382). El port lo tenía en 40, y el cliente del original avisa del tope pintando la cifra de otro color
/// a partir de 35: cinco puntos que el juego decía que no existían.
/// </summary>
public class AtributosTests
{
    [Fact]
    public void El_tope_es_el_MAXATRIBUTOS_del_original()
    {
        Assert.Equal(35, GameLimits.MaxAttribute);
    }

    [Fact]
    public void La_pocion_no_pasa_del_tope_y_al_vencer_vuelve_a_la_base()
    {
        // 31 de base es lo que trae un Enano gladiador con los Nudillos (+10) puestos: el caso del tester.
        var p = new Player { CharIndex = 1, Name = "Rugura", Account = "cuenta_test" };
        p.Attributes[GameLimits.AttrFuerza] = 31;

        p.ApplyAttrBonus(GameLimits.AttrFuerza, 5, nowTick: 100, durationTicks: 50);
        Assert.Equal(35, p.Strength);                       // 36 recortado al tope
        p.ApplyAttrBonus(GameLimits.AttrFuerza, 5, nowTick: 100, durationTicks: 50);
        Assert.Equal(35, p.Strength);                       // otra poción encima no lo pasa

        Assert.False(p.ExpireEffects(149));
        Assert.True(p.ExpireEffects(150));
        Assert.Equal(31, p.Strength);                       // vencida, queda la base con el equipo
    }

    [Fact]
    public void Por_debajo_del_tope_la_pocion_suma_entera()
    {
        // El control del test de arriba: sin este, un tope que recortara TODO a 35 pasaría igual.
        var p = new Player { CharIndex = 1, Name = "Rugura", Account = "cuenta_test" };
        p.Attributes[GameLimits.AttrAgilidad] = 20;
        p.ApplyAttrBonus(GameLimits.AttrAgilidad, 5, nowTick: 1, durationTicks: 10);
        Assert.Equal(25, p.Agility);
    }
}
