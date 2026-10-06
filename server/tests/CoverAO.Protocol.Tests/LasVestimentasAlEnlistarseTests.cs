using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS VESTIMENTAS AL ENLISTARSE (ModFacciones.bas, el final de EnlistarArmadaReal, EnlistarMilicia y EnlistarCaos): el original
/// las da al entrar y acá no se daban (el autor, 06/10). Se cruza cada número con su nombre en la data.
/// </summary>
public class LasVestimentasAlEnlistarseTests
{
    private const byte Humano = 1, Gnomo = 4, Enano = 5;
    private const byte Clerigo = 1, Mago = 2, Ladron = 5, Nigromante = 18;

    private static short Vestimenta(byte estado, byte raza, byte clase) =>
        PacketDispatcher.ArmaduraAlEnlistarse(new Player { CharIndex = 1, Name = "Aldo", Account = "aldo", FactionStatus = estado, Race = raza, Class = clase });

    [Theory]
    [InlineData(Player.StatusArmada, Humano, Mago, 1546, "Túnica de Mago Imperial de 1ª Jerarquía")]
    [InlineData(Player.StatusArmada, Gnomo, Mago, 1547, "Túnica de Mago Imperial de 1ª Jerarquía (Bajos)")]
    [InlineData(Player.StatusCaos, Humano, Clerigo, 1500, "Túnica de Clérigo del Caos de 1ª Jerarquía")]
    [InlineData(Player.StatusCaos, Enano, Nigromante, 1521, "Armadura de Nigromante del Caos de 1ª Jerarquía (Bajos)")]
    [InlineData(Player.StatusArmada, Humano, Nigromante, 1564, "Armadura de Nigromante Imperial de 1ª Jerarquía")]
    [InlineData(Player.StatusMilicia, Humano, Mago, 1588, "Vestimentas de Miliciano de 1ª Jerarquía")]
    [InlineData(Player.StatusMilicia, Gnomo, Mago, 1589, "Vestimentas de Miliciano de 1ª Jerarquía (Bajos)")]
    public void Cada_bando_da_sus_vestimentas(byte estado, byte raza, byte clase, short obj, string nombre)
    {
        Assert.Equal(obj, Vestimenta(estado, raza, clase));
        var datos = GameData.Load();
        if (datos.Obj(obj) is { } o) Assert.Equal(nombre, o.Name);
    }

    /// <summary>Las clases sin vestimenta del bando (los oficios y el Ladrón) no reciben nada, como en el original.</summary>
    [Fact]
    public void El_Ladron_de_la_Armada_no_recibe_nada()
    {
        Assert.Equal(0, Vestimenta(Player.StatusArmada, Humano, Ladron));
    }

    /// <summary>LA 2ª JERARQUÍA DEL NIGROMANTE (el autor, 06/10): el original no se la daba por un Case repetido; ahora sí.</summary>
    [Theory]
    [InlineData(Player.StatusArmada, Humano, 1586, "Armadura de Nigromante Imperial de 2ª Jerarquía")]
    [InlineData(Player.StatusArmada, Gnomo, 1587, "Armadura de Nigromante Imperial de 2ª Jerarquía (Bajos)")]
    [InlineData(Player.StatusCaos, Humano, 1542, "Armadura de Nigromante del Caos de 2ª Jerarquía")]
    public void El_Nigromante_recibe_su_armadura_de_segunda_jerarquia(byte estado, byte raza, short obj, string nombre)
    {
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo", FactionStatus = estado, Race = raza, Class = Nigromante };
        Assert.Equal(obj, PacketDispatcher.ArmaduraDeSegundaJerarquia(p));
        if (GameData.Load().Obj(obj) is { } o) Assert.Equal(nombre, o.Name);
    }
}
