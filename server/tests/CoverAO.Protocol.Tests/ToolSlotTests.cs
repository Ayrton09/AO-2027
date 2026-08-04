using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El slot de ANILLO (<c>AnilloEqpSlot</c>), que en esta variante es donde va la <b>herramienta de oficio</b>.
/// El port no lo modelaba: <c>Equipment.Category</c> devolvía 0 para <c>otAnillo</c>, así que el Costurero no se
/// podía equipar y la fabricación se conformaba con tenerlo <i>en la mochila</i> — se podía coser con el
/// costurero guardado en el bolsillo. El original exige <c>Invent.AnilloEqpObjIndex = COSTURERO</c>
/// (Trabajo.bas:390).
///
/// Y con el slot aparece lo otro que colgaba de él: la <c>ResistenciaMagica</c>. El VB6 la descuenta de CINCO
/// slots equipados —casco, escudo, armadura, montura y anillo— tanto en el hechizo de área
/// (modHechizos.bas:530) como en el de un solo objetivo (:1474). El port sólo miraba los tres de defensa.
/// </summary>
public class ToolSlotTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    // Las ocho herramientas de la data, con su constante del original (Declares.bas:361-377).
    public static TheoryData<int, string> Herramientas => new()
    {
        { 127, "HACHA_LEÑADOR" }, { 138, "RED_PESCA" }, { 187, "PIQUETE_MINERO" }, { 198, "SERRUCHO_CARPINTERO" },
        { 389, "MARTILLO_HERRERO" }, { 881, "CAÑA_PESCA" }, { 886, "COSTURERO" }, { 887, "OLLA" },
    };

    [Theory]
    [MemberData(nameof(Herramientas))]
    public void Las_ocho_herramientas_ocupan_el_slot_de_anillo(int objIndex, string nombre)
    {
        var data = LoadOrSkip();
        var o = data.Obj(objIndex);
        Assert.True(o is not null, $"{nombre} ({objIndex}) tiene que existir en obj.dat");
        // otAnillo = 18. El nombre del tipo engaña: no son anillos, son las herramientas de los oficios.
        Assert.True(o!.IsRing, $"{nombre} debe ser otAnillo para poder equiparse en el slot de herramienta");
        Assert.Equal(6, Equipment.Category(o));
    }

    [Fact]
    public void La_herramienta_no_era_equipable_antes_de_modelar_el_slot()
    {
        // Este es EL test de regresión de la tarea: si `IsRing` desapareciera de Category, el Costurero volvería
        // a dar 0 (= no equipable), HandleEquipItem lo ignoraría en silencio y el gate de fabricación quedaría
        // imposible de cumplir — o sea, ningún oficio se podría ejercer.
        var data = LoadOrSkip();
        Assert.NotEqual(0, Equipment.Category(data.Obj(886)!));   // Costurero
        Assert.NotEqual(0, Equipment.Category(data.Obj(887)!));   // Olla
    }

    [Fact]
    public void La_montura_Unicornio_es_la_unica_resistencia_magica_fuera_de_la_defensa()
    {
        var data = LoadOrSkip();
        var unicornio = data.Obj(1351);
        Assert.NotNull(unicornio);
        Assert.True(unicornio!.IsMount);                 // otMonturas = 44
        Assert.Equal(10, unicornio.ResistenciaMagica);   // "Unicornio (RM +10)"

        // De los 15 objetos con ResistenciaMagica, 14 son otArmadura (armadura/casco/escudo) y éste es el único
        // que no. Por eso mirar sólo los tres slots de defensa alcanzaba para 14 de 15 y el bug no saltaba: el
        // Unicornio era la pieza que no protegía nada.
        int fueraDeDefensa = 0;
        foreach (var o in data.Objects.Values)
            if (o.ResistenciaMagica > 0 && !(o.IsArmor || o.IsHelmet || o.IsShield)) fueraDeDefensa++;
        Assert.Equal(1, fueraDeDefensa);
    }

    [Fact]
    public void Ninguna_herramienta_tiene_resistencia_magica_en_esta_data()
    {
        // La rama del anillo del original existe pero en ESTE catálogo no tiene data. Se porta igual porque el
        // gate es del código y no del obj.dat —un catálogo editado la activaría—, pero queda anotado para que
        // nadie escriba un test esperando que el Costurero resista magia.
        var data = LoadOrSkip();
        foreach (int ix in new[] { 127, 138, 187, 198, 389, 881, 886, 887 })
            Assert.Equal(0, data.Obj(ix)!.ResistenciaMagica);
    }

    [Fact]
    public void La_resistencia_magica_suma_los_cinco_slots_y_deja_afuera_el_arma()
    {
        var data = LoadOrSkip();
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta_test" };

        // Sin nada puesto no hay nada que resistir.
        Assert.Equal(0, Equipment.MagicResistance(p, data));

        // Un casco con RM 20 (obj 993) — pero SIN equipar: tenerlo en la mochila no protege.
        p.Inventory[1] = new InventorySlot { ObjIndex = 993, Amount = 1, Equipped = false };
        Assert.Equal(0, Equipment.MagicResistance(p, data));

        // Puesto, sí.
        p.Inventory[1] = new InventorySlot { ObjIndex = 993, Amount = 1, Equipped = true };
        Assert.Equal(20, Equipment.MagicResistance(p, data));

        // Y la montura SUMA a la defensa: 20 + 10 del Unicornio. Ésta es la parte que faltaba.
        p.Inventory[2] = new InventorySlot { ObjIndex = 1351, Amount = 1, Equipped = true };
        Assert.Equal(30, Equipment.MagicResistance(p, data));

        // El báculo de DañoMagico (1147) equipado NO resta nada: el arma le suma daño al que lanza, no
        // resistencia al que la lleva. Si el helper contara el arma, este assert subiría solo.
        p.Inventory[3] = new InventorySlot { ObjIndex = 1147, Amount = 1, Equipped = true };
        Assert.Equal(30, Equipment.MagicResistance(p, data));
    }

    [Fact]
    public void Un_casco_con_resistencia_alta_anula_un_hechizo_debil()
    {
        var data = LoadOrSkip();
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta_test" };
        p.Inventory[1] = new InventorySlot { ObjIndex = 993, Amount = 1, Equipped = true };   // RM 20

        // La Bomba mágica hace 5 fijos (MinHP = MaxHP = 5) y a nivel 1 el bono del 2% no suma nada por la
        // división entera. Contra 20 de resistencia el daño queda en 0 — el hechizo no hace NADA, que es
        // justamente el piso 0 del original (el melee, en cambio, nunca baja de 1).
        int dano = Combat.DanoHechizoOfensivo(5, 1, 0, Equipment.MagicResistance(p, data));
        Assert.Equal(0, dano);
    }
}
