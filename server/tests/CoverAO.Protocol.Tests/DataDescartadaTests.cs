using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS CAMPOS QUE LA DATA TRAE Y EL CARGADOR TIRABA. Salieron de barrer todas las claves de todos los
/// <c>.dat</c> del install contra lo que nuestro código lee: el transcode los conservaba en el JSON y morían
/// en <c>GameData</c>, así que ni el oráculo ni los flows los veían faltar.
///
/// <para>Los valores van DUROS a propósito. <c>ObjInfo</c> y <c>NpcInfo</c> son records posicionales y los
/// campos nuevos se agregan al final: si alguien mete uno en el medio, el cargador pasa los argumentos
/// corridos y todo sigue compilando. Un número concreto por campo es lo que hace que eso salga en rojo.</para>
/// </summary>
public class DataDescartadaTests
{
    private static GameData Cargar()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "estos tests necesitan la gamedata en disco");
        return d;
    }

    [Theory]
    // Los CUATRO ítems con EfectoMagico=2 (ModificaAtributo) de todo el install. Los cuatro anuncian el bonus
    // en el nombre, que es lo que hacía tan visible que no pasara nada.
    [InlineData(652, 1, 10)]    // Nudillos (+10)          -> Fuerza
    [InlineData(654, 2, 5)]     // Anillo de Agilidad (+5) -> Agilidad
    [InlineData(752, 2, 10)]    // Botas de Celeridad (+10)-> Agilidad
    [InlineData(976, 4, 10)]    // Anillo del Carisma (+10)-> Carisma
    public void El_item_declara_que_atributo_sube_y_cuanto(int objeto, int atributo, int cuanto)
    {
        var o = Cargar().Obj(objeto);
        Assert.NotNull(o);
        Assert.Equal(atributo, o!.QueAtributo);
        Assert.Equal(cuanto, o.CuantoAumento);
        Assert.Equal(2, o.EfectoMagico);   // eMagicType.ModificaAtributo
    }

    [Fact]
    public void Ningun_otro_item_trae_QueAtributo()
    {
        // Si aparece un quinto, el porte lo alcanza igual (va por EfectoMagico), pero conviene enterarse.
        var conAtributo = Cargar().Objects.Values.Where(o => o.QueAtributo > 0).Select(o => o.Index).OrderBy(i => i);
        Assert.Equal(new short[] { 652, 654, 752, 976 }, conAtributo);
    }

    [Theory]
    // Los 12 yacimientos del install y el mineral que declara cada uno. Ocho de los doce daban HIERRO.
    [InlineData(188, 192)] [InlineData(189, 192)] [InlineData(190, 192)] [InlineData(191, 192)]   // Hierro
    [InlineData(228, 194)] [InlineData(229, 194)] [InlineData(230, 194)] [InlineData(231, 194)]   // Oro
    [InlineData(232, 193)] [InlineData(233, 193)] [InlineData(234, 193)] [InlineData(235, 193)]   // Plata
    public void Cada_yacimiento_declara_su_mineral(int yacimiento, int mineral)
    {
        var d = Cargar();
        var y = d.Obj(yacimiento);
        Assert.NotNull(y);
        Assert.True(y!.IsDeposit, $"el objeto {yacimiento} tendría que ser un yacimiento (ObjType 22)");
        Assert.Equal(mineral, y.MineralIndex);
        // Y el mineral que declara tiene que existir y ser fundible: si no, picar daría un objeto fantasma.
        var m = d.Obj(mineral);
        Assert.NotNull(m);
        Assert.True(m!.IsMineral, $"el yacimiento {yacimiento} apunta a '{m.Name}', que no es un mineral");
    }

    [Fact]
    public void Los_yacimientos_no_dan_todos_lo_mismo()
    {
        // El corazón del bug: con el mineral cableado los doce daban hierro. Este test se cae si alguien vuelve
        // a una constante, aunque los campos sigan cargándose bien.
        var minerales = Cargar().Objects.Values.Where(o => o.IsDeposit).Select(o => o.MineralIndex).Distinct().ToList();
        Assert.Equal(3, minerales.Count);   // hierro, plata y oro
    }

    [Fact]
    public void Los_cinco_items_que_no_se_pueden_tirar()
    {
        var noSeTiran = Cargar().Objects.Values.Where(o => o.NoSeTira).Select(o => o.Index).OrderBy(i => i);
        // Los sacos de créditos, el de tiempo de donador y el Anillo Dorado: lo que uno no quiere perder de un click.
        Assert.Equal(new short[] { 1603, 1604, 1605, 1606, 1607 }, noSeTiran);
    }

    [Fact]
    public void Los_diecinueve_hechizos_que_se_lanzan_solos()
    {
        var d = Cargar();
        var auto = Enumerable.Range(1, 200).Select(i => d.Spell(i)).Where(s => s is { EsAutoLanzado: true }).ToList();
        Assert.Equal(19, auto.Count);
        // Todos apuntan a usuarios (Target=1): no hay ninguno de terreno ni de NPC entre los auto-lanzados, que
        // es lo que hace correcto resolver el objetivo como "uno mismo".
        Assert.All(auto, s => Assert.Equal(1, s!.Target));
        // Anclas concretas: la Invisibilidad y las metamorfosis, que son las que se notan.
        Assert.True(d.Spell(14)!.EsAutoLanzado, "Invisibilidad tendría que auto-lanzarse");
        Assert.True(d.Spell(39)!.EsAutoLanzado, "Metamorfosis: Dragón tendría que auto-lanzarse");
        Assert.False(d.Spell(32)!.EsAutoLanzado, "Sanar NO se auto-lanza: se le tira a otro");
    }

    [Fact]
    public void Los_NPCs_traen_el_equipo_que_se_les_ve_puesto()
    {
        var d = Cargar();
        // El Guerrero de las ciudades: espada, escudo y casco. Es el caso que se ve entrando al mapa 1.
        var guerrero = d.Npc(6);
        Assert.NotNull(guerrero);
        Assert.Equal(5, guerrero!.WeaponAnim);
        Assert.Equal(5, guerrero.ShieldAnim);
        Assert.Equal(30, guerrero.HelmetAnim);
        // Y el conjunto: 57 de los 291 llevan algo puesto. Con los tres campos en cero salían todos desnudos.
        int conEquipo = d.Npcs.Values.Count(n => n.WeaponAnim > 0 || n.ShieldAnim > 0 || n.HelmetAnim > 0);
        Assert.Equal(57, conEquipo);
    }

    [Fact]
    public void El_porcentaje_de_recupero_de_mana_sale_del_Balance()
    {
        Assert.Equal(6, Cargar().PorcentajeRecuperoMana);   // Balance.dat §EXTRA
    }

    [Fact]
    public void El_equipo_del_NPC_SALE_POR_EL_CABLE()
    {
        // Que el campo se cargue no alcanza: lo que el jugador ve depende de que viaje en el CharacterCreate.
        // Acá se leen los bytes que salen, en el mismo orden del paquete (VB6 MakeNPCChar,
        // MODULO_NPCs.bas:590): …heading, X, Y, ARMA, ESCUDO, CASCO, FX, FXLoops…
        var d = Cargar();
        var npc = new CoverAO.Server.Game.Npc { CharIndex = 7, Template = d.Npc(6)! };
        npc.Pos = new CoverAO.Server.Game.WorldPos(1, 50, 50);
        var bytes = CoverAO.Server.Net.ServerMessages.CharacterCreate(npc);

        var r = new ByteQueueReader(bytes);
        r.ReadByte();                                   // id del paquete
        r.ReadInteger(); r.ReadInteger(); r.ReadInteger();   // charIndex, body, head
        r.ReadByte(); r.ReadByte(); r.ReadByte();            // heading, X, Y
        Assert.Equal(5, r.ReadInteger());    // arma   — iba 0 cableado
        Assert.Equal(5, r.ReadInteger());    // escudo — iba 0 cableado
        Assert.Equal(30, r.ReadInteger());   // casco  — iba 0 cableado
    }
}

/// <summary>
/// LA MEDITACIÓN, que es aritmética pura y por eso se puede fijar sin servidor. El port daba +20 de maná
/// fijos cada 3 segundos: le regalaba maná al que empieza, se lo escatimaba al mago grande, y dejaba la skill
/// Meditar sin ningún efecto — o sea que entrenarla no servía para nada.
/// </summary>
public class MeditarTests
{
    [Theory]
    // La escalera de once tramos de DoMeditar (Trabajo.bas:2061-2093), con los bordes de cada uno.
    [InlineData(0, 35)] [InlineData(10, 35)]
    [InlineData(11, 30)] [InlineData(20, 30)]
    [InlineData(21, 28)] [InlineData(30, 28)]
    [InlineData(31, 24)] [InlineData(40, 24)]
    [InlineData(41, 22)] [InlineData(50, 22)]
    [InlineData(51, 20)] [InlineData(60, 20)]
    [InlineData(61, 18)] [InlineData(70, 18)]
    [InlineData(71, 15)] [InlineData(80, 15)]
    [InlineData(81, 10)] [InlineData(90, 10)]
    [InlineData(91, 7)] [InlineData(99, 7)]
    [InlineData(100, 5)]
    public void La_suerte_baja_a_medida_que_sube_la_skill(int skill, int suerte)
        => Assert.Equal(suerte, Survival.SuerteAlMeditar(skill));

    [Fact]
    public void Entrenar_Meditar_hace_que_acierte_siete_veces_mas_seguido()
    {
        // Lo que la constante de antes no modelaba: la skill TIENE que cambiar algo.
        Assert.Equal(7, Survival.SuerteAlMeditar(0) / Survival.SuerteAlMeditar(100));
    }

    [Theory]
    // `Porcentaje(MaxMAN, 6) / 10`, con piso 1 (Trabajo.bas:2099-2102): el 0,6% del maná máximo.
    [InlineData(100, 1)]      // 0,6 -> el piso
    [InlineData(500, 3)]
    [InlineData(1000, 6)]
    [InlineData(5000, 30)]
    public void El_tiron_de_mana_crece_con_el_personaje(int maxMana, int esperado)
        => Assert.Equal(esperado, Survival.ManaAlMeditar(maxMana, 6));

    [Fact]
    public void Nunca_devuelve_cero()
    {
        // Sin el piso, un personaje de poco maná meditaría eternamente sin recuperar nada.
        for (int m = 1; m < 200; m++) Assert.True(Survival.ManaAlMeditar(m, 6) >= 1, $"maxMana={m}");
    }
}
