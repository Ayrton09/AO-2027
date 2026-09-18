using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS OFICIOS DE FABRICAR, contra la data de verdad. El pedido del autor: *"que sean como una persona que puedan aprender
/// oficios y todo"*. Hasta acá el bot sólo sabía juntar (pescar, talar, minar), y el Carpintero, el Druida y el Sastre
/// jugaban como un guerrero cualquiera.
/// </summary>
public class FabricarDelBotTests
{
    private static GameData? Datos()
    {
        var d = GameData.Load();
        return d.CarpenterItems.Count == 0 || d.AlchemyItems.Count == 0 || d.TailorItems.Count == 0 ? null : d;
    }

    private static short Llamado(GameData d, IReadOnlySet<int> recetas, string nombre) =>
        (short)recetas.Single(i => d.Obj(i)?.Name == nombre);

    private static Func<int, int> Tiene(params (int Obj, int Cuanto)[] mochila) =>
        obj => mochila.Where(m => m.Obj == obj).Sum(m => m.Cuanto);

    /// <summary>
    /// LO MÁS DIFÍCIL QUE YA SABE HACER. Con skill 5 y dos leñas, la Cuchara (pide 5 y 2 de leña); con 20 y leña de sobra, la
    /// Flecha (pide 20 y 1) y no la Envenenante (pide 20 y 15): entre dos que piden lo mismo, la que gasta menos.
    /// </summary>
    [Fact]
    public void El_carpintero_hace_lo_mas_dificil_que_ya_sabe_hacer()
    {
        if (Datos() is not { } d) return;
        var recetas = d.CarpenterItems;
        Assert.Equal(Llamado(d, recetas, "Cuchara de Madera"),
            Bot.QueFabricar(d, recetas, Oficios.Carpinteria, 5, Tiene((GameLimits.WoodObj, 2))));
        Assert.Equal(Llamado(d, recetas, "Flecha"),
            Bot.QueFabricar(d, recetas, Oficios.Carpinteria, 20, Tiene((GameLimits.WoodObj, 50))));
        // Con skill 20 y UNA leña, la flecha igual: pide una.
        Assert.Equal(Llamado(d, recetas, "Flecha"),
            Bot.QueFabricar(d, recetas, Oficios.Carpinteria, 20, Tiene((GameLimits.WoodObj, 1))));
    }

    /// <summary>SIN LA SKILL MÍNIMA O SIN MATERIALES NO HAY RECETA: el servidor la rechazaría en silencio.</summary>
    [Fact]
    public void Sin_la_skill_minima_o_sin_materiales_no_fabrica()
    {
        if (Datos() is not { } d) return;
        Assert.Null(Bot.QueFabricar(d, d.CarpenterItems, Oficios.Carpinteria, 4, Tiene((GameLimits.WoodObj, 500))));
        Assert.Null(Bot.QueFabricar(d, d.CarpenterItems, Oficios.Carpinteria, 20, Tiene()));
        Assert.Null(Bot.QueFabricar(d, d.CarpenterItems, Oficios.Carpinteria, 5, Tiene((GameLimits.WoodObj, 1))));
    }

    /// <summary>
    /// EL DRUIDA HACE SU ANTÍDOTO: la Poción Violeta de alquimia pide 10 y tres raíces, y cura el veneno igual que la que se
    /// compra (SubTipo 5 que lanza el mismo hechizo), así que cuenta como antídoto y deja de comprarlo.
    /// </summary>
    [Fact]
    public void El_druida_hace_la_violeta_y_le_sirve_de_antidoto()
    {
        if (Datos() is not { } d) return;
        var violeta = Bot.QueFabricar(d, d.AlchemyItems, Oficios.Alquimia, 10, Tiene((GameLimits.RootObj, 3)));
        Assert.NotNull(violeta);
        var o = d.Obj(violeta!.Value);
        Assert.StartsWith("Poción Violeta", o!.Name);
        Assert.True(Bot.CuraElVeneno(o, d.Spell((short)o.LanzaHechizo)), "la violeta de alquimia no cura el veneno");
        Assert.Null(Bot.QueFabricar(d, d.AlchemyItems, Oficios.Alquimia, 9, Tiene((GameLimits.RootObj, 30))));
    }

    /// <summary>EL SASTRE CON LAS PIELES DEL LOBO: las Vestimentas piden 10 y seis pieles; lo siguiente pide 80 y pieles de oso.</summary>
    [Fact]
    public void El_sastre_hace_vestimentas_con_seis_pieles_de_lobo()
    {
        if (Datos() is not { } d) return;
        Assert.Equal(Llamado(d, d.TailorItems, "Vestimentas de Hombre"),
            Bot.QueFabricar(d, d.TailorItems, Oficios.Sastreria, 10, Tiene((GameLimits.SkinWolfObj, 6))));
        Assert.Null(Bot.QueFabricar(d, d.TailorItems, Oficios.Sastreria, 10, Tiene((GameLimits.SkinWolfObj, 5))));
        // Y la piel sale de lo que caza: todo Lobo la suelta.
        Assert.Contains(d.Npc(501)!.Drops ?? Array.Empty<NpcDrop>(), x => x.ObjIndex == GameLimits.SkinWolfObj);
    }

    /// <summary>
    /// FABRICA PARA APRENDER O PARA LO QUE LE FALTA: con la skill en el tope de su nivel (<c>SkillProgression.CapForLevel</c>)
    /// otra cuchara no le enseña nada y le come la energía de pelear; el Druida se hace igual el antídoto que le falta.
    /// </summary>
    [Theory]
    // skill | tope del nivel | ¿le hace falta lo que sale? | ¿fabrica?
    [InlineData(5, 13, false, true)]     // sigue aprendiendo
    [InlineData(13, 13, false, false)]   // en el tope
    [InlineData(13, 13, true, true)]     // el antídoto que le falta
    public void Fabrica_para_aprender_o_para_lo_que_le_falta(int skill, int tope, bool leFalta, bool fabrica)
    {
        Assert.Equal(fabrica, Bot.ValeFabricar(skill, tope, leFalta));
        Assert.Equal(13, SkillProgression.CapForLevel(5));   // el tope de la tabla del original a nivel 5
    }

    /// <summary>
    /// LA SKILL LA DICE EL SERVIDOR: entera con <c>SendSkills</c> y de a una con el aviso 454 (<c>"*skill%valor"</c>), que es
    /// un paquete DELTA. Sin leerlo, el bot creería que su skill de fabricar quedó donde la dejó el reparto.
    /// </summary>
    [Fact]
    public void La_skill_se_lee_entera_y_despues_de_a_una()
    {
        var s = new SentidosDelBot();
        Assert.Equal(-1, s.Skill(GameLimits.SkillCarpinteria));   // todavía no le llegaron
        var skills = new byte[GameLimits.NumSkills + 1];
        skills[GameLimits.SkillCarpinteria] = 5;
        s.Recibir(ServerMessages.SendSkills(skills));
        Assert.Equal(5, s.Skill(GameLimits.SkillCarpinteria));
        s.Recibir(ServerMessages.LocaleMsg(454, $"*{GameLimits.SkillCarpinteria}%6"));
        Assert.Equal(6, s.Skill(GameLimits.SkillCarpinteria));
        // Otro aviso con argumentos no la toca.
        s.Recibir(ServerMessages.LocaleMsg(140, "10"));
        Assert.Equal(6, s.Skill(GameLimits.SkillCarpinteria));
        Assert.Null(SentidosDelBot.SkillQueSubio("10"));
        Assert.Null(SentidosDelBot.SkillQueSubio("*x%3"));
    }

    /// <summary>
    /// EL DRUIDA CASTEA PRIMERO: su hechizo de nacimiento pide Magia 5 y los puntos van ahí hasta llegar; recién después, a la
    /// Alquimia. Con todo a la Alquimia desde el principio no casteaba, que es el defecto viejo de las clases que castean.
    /// </summary>
    [Theory]
    // magia que tiene (-1 = no sabe) | ¿le falta para su primer hechizo?
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void El_que_castea_y_fabrica_pone_primero_en_magia(int magia, bool falta)
    {
        if (Datos() is not { } d) return;
        int minima = d.Spell(2)!.MinSkill;
        Assert.Equal(5, minima);   // si la data cambia, este test tiene que decirlo
        Assert.Equal(falta, Bot.LeFaltaMagiaParaCastear(magia, minima));
    }
}
