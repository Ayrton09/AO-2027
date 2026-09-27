using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Kit inicial por clase (NACIMIENTO.dat). Estaba transcodeado pero sin loader: TODO personaje nuevo nacía
/// con el kit demo maestro (skills de oficio al 100, montura, todas las pociones), en vez del equipo de su
/// clase. Estos tests fijan el loader contra la data REAL y el reparto del VB6 (RellenarInventario,
/// TCP.bas:2195): el slot 3 es el arma equipada y el 4 la armadura RACIAL (el .dat trae 0 ahí a propósito).
/// </summary>
public class NewbieKitTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    [Fact]
    public void Loads_a_kit_for_every_class()
    {
        var data = LoadOrSkip();
        Assert.Equal(18, data.NewbieKits.Count);            // INIT.Clases = 18
        Assert.All(data.NewbieKits.Values, k => Assert.NotEmpty(k));
    }

    [Fact]
    public void Kit_indices_ignore_the_dat_comment()
    {
        var data = LoadOrSkip();
        // Clase 1: "573 'Manzana" x100 -> el comentario del .dat no debe entrar en el índice (VB6 usa val()).
        var kit = data.NewbieKit(1);
        Assert.Equal(573, kit[0].ObjIndex);
        Assert.Equal(100, kit[0].Amount);
        Assert.Equal(572, kit[1].ObjIndex);   // Botella
    }

    [Fact]
    public void Slot4_is_the_racial_armour_not_the_zero_from_the_dat()
    {
        var data = LoadOrSkip();
        var kit = data.NewbieKit(1);
        Assert.True(kit.Count >= 4);
        Assert.Equal(0, kit[3].ObjIndex);   // el .dat trae 0 en el slot 4: lo rellena el server por raza

        var store = new FileStore(Path.Combine(Path.GetTempPath(), "coverao_kit_" + Guid.NewGuid().ToString("N")));
        var db = new Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("kit_test", "pw");

        // Humano -> 463, Elfo -> 464, Drow -> 465, Gnomo/Enano -> 466, Orco -> 1087 (VB6 eRaza).
        var humano = db.CreateCharacter(acc, "KitHumano", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1, newbieKit: kit);
        var elfo = db.CreateCharacter(acc, "KitElfo", 2, race: 2, gender: 1, @class: 1, homeland: 1, head: 1, newbieKit: kit);
        var enano = db.CreateCharacter(acc, "KitEnano", 3, race: 5, gender: 1, @class: 1, homeland: 1, head: 1, newbieKit: kit);
        Assert.Equal(463, humano.Inventory[4].ObjIndex);
        Assert.Equal(464, elfo.Inventory[4].ObjIndex);
        Assert.Equal(466, enano.Inventory[4].ObjIndex);
        Assert.True(humano.Inventory[4].Equipped, "la armadura inicial nace puesta");
    }

    /// <summary>
    /// LA MUJER NACE CON LA VERSIÓN DE MUJER (decisión del autor, 2026-09-27): con la ropa de hombre sólo para hombres, la de
    /// novato de su raza no se la podría volver a poner. La Orca no tiene versión de mujer y nace con las Tribales, que por
    /// eso son de las dos.
    /// </summary>
    [Fact]
    public void La_mujer_nace_con_la_ropa_de_novato_de_mujer_y_se_la_puede_poner()
    {
        var data = LoadOrSkip();
        var kit = data.NewbieKit(1);
        var store = new FileStore(Path.Combine(Path.GetTempPath(), "coverao_kit_" + Guid.NewGuid().ToString("N")));
        var db = new Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("kit_mujer", "pw");
        // Humana, Elfa, Drow, Gnoma, Enana, Orca (VB6 eRaza).
        var esperado = new (byte Raza, short Ropa)[] { (1, 1283), (2, 1284), (3, 1285), (4, 563), (5, 563), (6, 1087) };
        short charIndex = 10;
        foreach (var (raza, ropa) in esperado)
        {
            var mujer = db.CreateCharacter(acc, "KitMujer" + raza, charIndex++, race: raza, gender: 2, @class: 1, homeland: 1,
                                           head: 1, newbieKit: kit);
            Assert.Equal(ropa, mujer.Inventory[4].ObjIndex);
            Assert.True(mujer.Inventory[4].Equipped, "la armadura inicial nace puesta");
            var prenda = data.Obj(ropa)!;
            Assert.True(Equipment.PuedeUsarloSuGenero(Equipment.GeneroDeLaPrenda(prenda), Equipment.Mujer),
                $"la raza {raza} nace con '{prenda.Name}', que una mujer no se puede poner");
        }
    }

    [Fact]
    public void Weapon_slot_is_equipped_and_class_specific()
    {
        var data = LoadOrSkip();
        var store = new FileStore(Path.Combine(Path.GetTempPath(), "coverao_kit_" + Guid.NewGuid().ToString("N")));
        var db = new Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("kit_test2", "pw");

        var c1 = db.CreateCharacter(acc, "KitUno", 1, 1, 1, @class: 1, homeland: 1, head: 1, newbieKit: data.NewbieKit(1));
        var c2 = db.CreateCharacter(acc, "KitDos", 2, 1, 1, @class: 2, homeland: 1, head: 1, newbieKit: data.NewbieKit(2));
        Assert.Equal(574, c1.Inventory[3].ObjIndex);    // clase 1: Martillo
        Assert.Equal(1356, c2.Inventory[3].ObjIndex);   // clase 2: Báculo
        Assert.True(c1.Inventory[3].Equipped, "el arma inicial nace puesta");
        Assert.True(c2.Inventory[3].Equipped);
    }

    [Fact]
    public void Demo_kit_is_untouched_when_no_class_kit_is_given()
    {
        var data = LoadOrSkip();
        var store = new FileStore(Path.Combine(Path.GetTempPath(), "coverao_kit_" + Guid.NewGuid().ToString("N")));
        var db = new Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("kit_demo", "pw");
        // Sin kit -> kit demo maestro, el layout del que dependen los flows del fixture.
        var demo = db.CreateCharacter(acc, "KitDemo", 1, 1, 1, 1, 1, 1);
        Assert.Equal(2, demo.Inventory[2].ObjIndex);      // espada larga
        Assert.Equal(1480, demo.Inventory[15].ObjIndex);  // armadura
        Assert.Equal(573, demo.Inventory[18].ObjIndex);   // comida
        Assert.Equal(100, demo.Skills[GameLimits.SkillHerreria]);
    }
}
