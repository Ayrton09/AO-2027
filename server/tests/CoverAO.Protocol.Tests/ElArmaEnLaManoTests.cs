using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ARMA EN LA MANO PARA PELEAR (02/10, ver <c>Bot.PonerseElArmaParaPelear</c>). En `alto32` y `alto33` Petra, Sastre de nivel
/// 16 con la Daga +2 en la mochila, peleó dos veces con nada que pegue en la mano: 60 y 73 golpes para sacarle 119 y 145 a un Esqueleto
/// guerrero. Ponerse el equipo va en el ciclo después de pelear con lo que le pega, y la pelea que empieza apenas se queda sin nada
/// que pegue en la mano (al graduarse, o con la herramienta de trabajar puesta) arranca sin el arma.
/// </summary>
public class ElArmaEnLaManoTests
{
    /// <summary>LA PREMISA, con la data: las herramientas con las que se trabaja puestas no son armas ni nudillos.</summary>
    [Theory]
    [InlineData(127)]   // Hacha de Leñador
    [InlineData(187)]   // Piquete de Minero
    [InlineData(885)]   // Tijeras
    [InlineData(886)]   // Costurero
    [InlineData(887)]   // Olla
    public void Las_herramientas_de_trabajar_no_son_armas(short herramienta)
    {
        var d = GameData.Load();
        if (d.Obj(herramienta) is not { } o) return;
        Assert.False(o.IsWeapon || o.IsKnuckles, $"{o.Name} cuenta como arma");
    }

    /// <summary>Y LAS DOS QUE PIDE FABRICAR (<see cref="Oficios"/>) son de ésas: el Sastre fabrica con el Costurero puesto y el Druida
    /// con la Olla.</summary>
    [Fact]
    public void Fabricar_pide_el_costurero_y_la_olla_puestos()
    {
        Assert.Equal(886, Oficios.Sastreria.Herramienta);
        Assert.Equal(887, Oficios.Alquimia.Herramienta);
    }
}
