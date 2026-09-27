using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los dos tipos que el port rechazaba EN SILENCIO al equipar: <c>otNudillos</c> (46) y <c>otItemsMagicos</c>
/// (21). <c>Equipment.Category</c> sólo conocía arma, armadura, casco, escudo, munición y herramienta, y
/// <c>HandleEquipItem</c> corta con <c>if (cat == 0) return;</c> sin mandar nada — así que hacer clic en unos
/// nudillos o en un anillo no producía ni un mensaje de error. Son 36 objetos del catálogo.
///
/// El otro lado del test es igual de importante: el original equipa SIETE tipos y ni uno más
/// (<c>EquiparInvItem</c>, InvUsuario.bas:934-1145), así que los trofeos <c>otAnillos</c> y el Manual de
/// Liderazgo <b>tienen</b> que seguir dando 0. Sin este contrapeso, "arreglar" el equipado sería agregarle al
/// juego un slot que no existe.
/// </summary>
public class NudillosEItemsMagicosTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    // Los cuatro nudillos de la data (Declares.bas otNudillos = 46).
    public static TheoryData<int, string> Nudillos => new()
    {
        { 1331, "Nudillos de Hierro" }, { 1332, "Nudillos de Plata" },
        { 1333, "Nudillos de Oro" }, { 1354, "Nudillos de Hierro (Newbies)" },
    };

    [Theory]
    [MemberData(nameof(Nudillos))]
    public void Los_nudillos_se_pueden_equipar_y_tienen_slot_propio(int objIndex, string nombre)
    {
        var data = LoadOrSkip();
        var o = data.Obj(objIndex);
        Assert.True(o is not null, $"{nombre} ({objIndex}) tiene que existir en obj.dat");
        Assert.True(o!.IsKnuckles);
        Assert.Equal(7, Equipment.Category(o));   // 7 = nudillos, NO la 1 del arma: son slots distintos
    }

    [Fact]
    public void Los_32_items_magicos_se_pueden_equipar()
    {
        var data = LoadOrSkip();
        int magicos = 0;
        foreach (var o in data.Objects.Values)
            if (o.IsMagicItem) { Assert.Equal(8, Equipment.Category(o)); magicos++; }
        Assert.Equal(32, magicos);
    }

    [Fact]
    public void Los_tipos_que_el_original_NO_equipa_siguen_dando_cero()
    {
        // otAnillos (47) son trofeos de criatura y otAnilloEspec (49) es el Manual de Liderazgo: el
        // `Select Case` de EquiparInvItem no tiene rama para ninguno de los dos, así que rechazarlos es
        // fiel. Este assert es el que impide que la categoría se siga estirando "por las dudas".
        var data = LoadOrSkip();
        Assert.Equal(0, Equipment.Category(data.Obj(542)!));   // Pluma de Águila (otAnillos)
        Assert.Equal(0, Equipment.Category(data.Obj(578)!));   // Colmillo de Lobo (otAnillos)
        Assert.Equal(0, Equipment.Category(data.Obj(208)!));   // Manual de Liderazgo (otAnilloEspec)
    }

    [Fact]
    public void El_arma_y_los_nudillos_se_echan_mutuamente()
    {
        // InvUsuario.bas:991-997 y :1051-1058: las dos ramas desequipan los DOS slots. Sin esto se podría
        // pelear con espada y nudillos puestos a la vez, y como en combate ganan los nudillos, el arma
        // quedaría equipada sin hacer absolutamente nada.
        Assert.True(Equipment.LiberaCategoria(1, 7));   // ponerse un arma saca los nudillos
        Assert.True(Equipment.LiberaCategoria(7, 1));   // y al revés
        Assert.True(Equipment.LiberaCategoria(7, 7));
        Assert.True(Equipment.LiberaCategoria(1, 1));

        // Y no arrastra nada más: ponerse un arma no te descalza el casco.
        Assert.False(Equipment.LiberaCategoria(1, 3));
        Assert.False(Equipment.LiberaCategoria(7, 2));
        Assert.False(Equipment.LiberaCategoria(8, 1));   // el ítem mágico tiene su propio slot y no toca al arma
    }

    [Fact]
    public void El_item_magico_no_ocupa_el_slot_de_la_herramienta()
    {
        // El nombre confunde: el "anillo" del original (otAnillo = 18) es la HERRAMIENTA de oficio, y los
        // anillos de verdad son otItemsMagicos. Si compartieran categoría, ponerse el Anillo del Guerrero
        // te sacaría el Costurero de la mano y cortaría la faena.
        var data = LoadOrSkip();
        Assert.Equal(6, Equipment.Category(data.Obj(886)!));    // Costurero — herramienta
        Assert.Equal(8, Equipment.Category(data.Obj(734)!));    // Anillo del Guerrero (+20) — ítem mágico
        Assert.False(Equipment.LiberaCategoria(6, 8));
        Assert.False(Equipment.LiberaCategoria(8, 6));
    }

    [Fact]
    public void El_bonus_del_anillo_sale_de_EfectoMagico_y_no_del_nombre()
    {
        // Los cuatro que importan, con los campos que el bono lee. Si el transcode dejara de traer alguno,
        // el ítem se equiparía igual y no haría nada — el modo de fallar más silencioso que hay.
        var data = LoadOrSkip();
        var guerrero = data.Obj(734)!;                          // Anillo del Guerrero (+20)
        Assert.Equal(3, guerrero.EfectoMagico);                  // eMagicType.ModificaSkill
        Assert.Equal(5, guerrero.QueSkill);
        Assert.Equal(20, guerrero.CuantoAumento);

        var agilidad = data.Obj(654)!;                           // Anillo de Agilidad (+5)
        Assert.Equal(2, agilidad.EfectoMagico);                  // eMagicType.ModificaAtributo
        Assert.Equal(2, agilidad.QueAtributo);
        Assert.Equal(5, agilidad.CuantoAumento);

        var ogro = data.Obj(707)!;                               // Brazalete del Ogro (+30)
        Assert.Equal(6, ogro.EfectoMagico);                      // eMagicType.AumentaGolpe (+daño contra NPC)
        Assert.Equal(30, ogro.CuantoAumento);
    }

    [Fact]
    public void Los_nudillos_no_traen_grafico_de_arma_en_esta_data()
    {
        // Ninguno de los cuatro tiene la clave `Anim`, que es de donde el original saca el WeaponAnim
        // (FileIO.bas:865). O sea que se pelea con las manos vacías a la vista, y el CharacterChangeSlot del
        // slot 8 viaja con un 0. Queda escrito para que nadie lo lea como un campo perdido por el transcode.
        var data = LoadOrSkip();
        foreach (int ix in new[] { 1331, 1332, 1333, 1354 })
            Assert.Equal(0, data.Obj(ix)!.Anim);
    }

    [Fact]
    public void Pegar_con_nudillos_cuesta_la_energia_del_ITEM()
    {
        // SistemaCombate.bas:994: la energía del golpe sale primero de los nudillos y recién después del arma.
        // Los tres nudillos normales cuestan 10, 20 y 30 — los de oro cansan el triple que los de hierro.
        var data = LoadOrSkip();
        Assert.Equal(10, data.Obj(1331)!.StaRequerido);
        Assert.Equal(20, data.Obj(1332)!.StaRequerido);
        Assert.Equal(30, data.Obj(1333)!.StaRequerido);
    }

    [Fact]
    public void Con_nudillos_puestos_el_arma_no_es_la_que_pelea()
    {
        // El helper que usa el combate: `EquippedOfCategory(p, data, 7)` tiene que encontrar los nudillos, y
        // la categoría 1 (arma) tiene que quedar vacía. De ahí sale que se pelee por Wrestling.
        var data = LoadOrSkip();
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta_test" };
        p.Inventory[1] = new InventorySlot { ObjIndex = 1333, Amount = 1, Equipped = true };   // Nudillos de Oro

        var nudillos = Equipment.EquippedOfCategory(p, data, 7);
        Assert.NotNull(nudillos);
        Assert.Equal(6, nudillos!.MinHit);
        Assert.Equal(12, nudillos.MaxHit);
        Assert.Null(Equipment.EquippedOfCategory(p, data, 1));
    }

    [Fact]
    public void La_montura_equipada_se_encuentra_aunque_no_tenga_categoria()
    {
        // La montura no ocupa slot de equipo (Category la deja en 0), pero su [MinHIT,MaxHIT] se suma al daño
        // de cualquier golpe dado a caballo (SistemaCombate.bas:421-427). Por eso hay un helper aparte.
        var data = LoadOrSkip();
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta_test" };
        Assert.Null(Equipment.MonturaEquipada(p, data));

        p.Inventory[1] = new InventorySlot { ObjIndex = 1351, Amount = 1, Equipped = false };   // Unicornio
        Assert.Null(Equipment.MonturaEquipada(p, data));                                        // en la mochila no cuenta

        p.Inventory[1] = new InventorySlot { ObjIndex = 1351, Amount = 1, Equipped = true };
        var montura = Equipment.MonturaEquipada(p, data);
        Assert.NotNull(montura);
        Assert.True(montura!.IsMount);
        Assert.Equal(0, Equipment.Category(montura));   // sigue sin ser una categoría de equipo
    }

    /// <summary>El AMULETO DEL SILENCIO (obj 755). Su único efecto en todo el juego es que las palabras mágicas
    /// del portador NO salgan (<c>DecirPalabrasMagicas</c>, modHechizos.bas:283-287), así que el gate lo busca
    /// por <c>EfectoMagico == Silencio (16)</c> en el slot de ítem mágico — el mismo slot que este archivo
    /// habilitó. Se fija la data porque de ella depende el gate: si el catálogo tuviera un segundo objeto con
    /// EfectoMagico 16 el efecto sería de los dos, y si el amuleto dejara de ser <c>otItemsMagicos</c> no habría
    /// slot donde ponerlo y el gate nunca lo vería.</summary>
    [Fact]
    public void El_amuleto_del_silencio_es_el_unico_que_calla_las_palabras()
    {
        const int Silencio = 16;
        var data = LoadOrSkip();
        var amuleto = data.Obj(755);
        Assert.True(amuleto is not null, "el Amuleto del Silencio (755) tiene que existir en obj.dat");
        Assert.Equal(Silencio, amuleto!.EfectoMagico);
        Assert.True(amuleto.IsMagicItem);
        Assert.Equal(8, Equipment.Category(amuleto));

        var conSilencio = data.Objects.Values.Where(o => o.EfectoMagico == Silencio).ToList();
        Assert.Single(conSilencio);
        Assert.Equal(755, conSilencio[0].Index);

        // Y que el gate lo ENCUENTRE puesto: es la consulta exacta que hace DecirPalabrasMagicas.
        var p = new Player { CharIndex = 1, Name = "Test", Account = "cuenta_test" };
        p.Inventory[1] = new InventorySlot { ObjIndex = 755, Amount = 1, Equipped = false };
        Assert.Null(Equipment.EquippedOfCategory(p, data, 8));      // en la mochila no calla nada
        p.Inventory[1] = new InventorySlot { ObjIndex = 755, Amount = 1, Equipped = true };
        Assert.Equal(Silencio, Equipment.EquippedOfCategory(p, data, 8)?.EfectoMagico);
    }
}
