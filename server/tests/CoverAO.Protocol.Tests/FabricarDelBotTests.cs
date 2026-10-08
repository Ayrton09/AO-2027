using System.Reflection;
using CoverAO.Protocol;
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
    /// LOS PUNTOS A LA SKILL DE FABRICAR, SÓLO HASTA LA PRIMERA RECETA. Pasada la mínima, la sube fabricando; con todos los
    /// puntos ahí la skill llegaba sola al tope del nivel y fabricar ya no le enseñaba nada. Medido en `oficios1`: un
    /// Carpintero de nivel 12 con Carpintería 30 —el tope— y leña en la mochila, sin fabricar una sola cosa.
    /// </summary>
    [Fact]
    public void Los_puntos_van_a_fabricar_solo_hasta_la_primera_receta()
    {
        if (Datos() is not { } d) return;
        Assert.Equal(5, Bot.MinimaDeLasRecetas(d, d.CarpenterItems, Oficios.Carpinteria));   // la cuchara
        Assert.Equal(10, Bot.MinimaDeLasRecetas(d, d.AlchemyItems, Oficios.Alquimia));      // la Violeta
        Assert.Equal(10, Bot.MinimaDeLasRecetas(d, d.TailorItems, Oficios.Sastreria));      // las Vestimentas
        Assert.True(Bot.LeFaltaParaFabricar(-1, 5));    // todavía no sabe sus skills: el recién creado
        Assert.True(Bot.LeFaltaParaFabricar(4, 5));
        Assert.False(Bot.LeFaltaParaFabricar(5, 5));
        Assert.False(Bot.LeFaltaParaFabricar(30, 5));
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
    /// LO QUE LE FALTA PARA FABRICAR: los materiales de lo que ya sabe hacer, cuando no le alcanza para nada. Al Sastre con una
    /// piel le falta la piel de lobo; con seis, nada; y con la skill por debajo de la primera receta, tampoco: todavía no sabe
    /// hacer nada, así que no hay qué salir a buscar.
    /// </summary>
    [Fact]
    public void Le_falta_lo_que_piden_las_recetas_que_ya_sabe()
    {
        if (Datos() is not { } d) return;
        Assert.Equal(new[] { GameLimits.SkinWolfObj },
            Bot.MaterialesQueLeFaltan(d, d.TailorItems, Oficios.Sastreria, 13, Tiene((GameLimits.SkinWolfObj, 1))).Order());
        Assert.Empty(Bot.MaterialesQueLeFaltan(d, d.TailorItems, Oficios.Sastreria, 13, Tiene((GameLimits.SkinWolfObj, 6))));
        Assert.Empty(Bot.MaterialesQueLeFaltan(d, d.TailorItems, Oficios.Sastreria, 9, Tiene()));
        Assert.Equal(new[] { GameLimits.WoodObj },
            Bot.MaterialesQueLeFaltan(d, d.CarpenterItems, Oficios.Carpinteria, 5, Tiene((GameLimits.WoodObj, 1))).Order());
        Assert.Equal(new[] { GameLimits.RootObj },
            Bot.MaterialesQueLeFaltan(d, d.AlchemyItems, Oficios.Alquimia, 10, Tiene()).Order());
    }

    /// <summary>
    /// DE LO QUE PIDEN LAS PRIMERAS RECETAS, SÓLO LA PIEL SE CAZA: la suelta el Lobo. La leña y la raíz no las suelta ninguna
    /// criatura, se juntan con la herramienta; por eso salir a cazar para el oficio mueve al Sastre y a nadie más. Si la data
    /// cambia, este test tiene que decirlo.
    /// </summary>
    [Fact]
    public void De_lo_que_piden_las_primeras_recetas_solo_la_piel_se_caza()
    {
        if (Datos() is not { } d) return;
        Assert.True(Bot.SueltaAlgoDe(d.Npc(501), new HashSet<int> { GameLimits.SkinWolfObj }), "el Lobo no suelta la piel");
        var seJuntan = new HashSet<int> { GameLimits.WoodObj, GameLimits.RootObj };
        Assert.DoesNotContain(d.Npcs.Values, n => Bot.SueltaAlgoDe(n, seJuntan));
        // Y sin nada que buscar no sirve ninguna: el Lobo suelta cosas, pero no es eso.
        Assert.False(Bot.SueltaAlgoDe(d.Npc(501), new HashSet<int>()));
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

    private static readonly MethodInfo FabricarAsync = typeof(Bot).GetMethod("FabricarAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// EL VIAJE A FABRICAR CUENTA EL SALTO QUE HAY QUE DAR. El Sastre está en el 823, que es pk (MapInfo.json), con 36 pieles de lobo
    /// y el Costurero en la mochila, a un paso de la salida al 815, que no es pk (MapExits.json: la salida está en (10,7)). Con la
    /// skill en 10 y el tope del nivel 5 en 13 (<c>SkillProgression.CapForLevel</c>) le faltan 3 puntos para aprender: menos que
    /// <c>Bot.FabricacionesPorTanda</c>, pero a un solo mapa se va igual (<c>Bot.ValeViajarAFabricar</c> con <c>saltos</c> 1). El
    /// primer paso del viaje es hacia la salida, al norte.
    /// </summary>
    [Fact]
    public async Task El_sastre_con_menos_de_seis_puntos_para_aprender_viaja_al_seguro_de_al_lado()
    {
        var d = GameData.Load();
        Assert.True(d.IsPkMap(823), "premisa: el 823 es pk (MapInfo.json)");
        Assert.False(d.IsPkMap(815), "premisa: el 815 no es pk (MapInfo.json)");
        Assert.True(d.MapExits.TryGetValue((823, 10, 7), out var salida) && salida.DestMap == 815,
                    "premisa: la salida del 823 al 815 está en (10,7) (MapExits.json)");
        Assert.Equal(13, SkillProgression.CapForLevel(5));   // el tope de la tabla del original a nivel 5
        byte clase = (byte)(Array.IndexOf(GameLimits.NombresDeClase, "Sastre") + 1);
        string nombre = PobladoDeBots.Nombres.First(n => Bot.ClaseQueLeToca(PobladoDeBots.LugarEnElPoblado(n)) == clase);

        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = 500, Name = nombre, Account = nombre + "_test", Pos = new WorldPos(823, 10, 8), Level = 5,
            MaxHp = 100, Hp = 100, MaxMana = 0, Mana = 0, MaxStamina = 80, Stamina = 80,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(823));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        var skills = new byte[GameLimits.NumSkills + 1];
        skills[GameLimits.SkillSastreria] = 10;
        s.Recibir(ServerMessages.SendSkills(skills));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, (short)GameLimits.SkinWolfObj, 36, false, 0f, 0));   // seis Vestimentas
        s.Recibir(ServerMessages.ChangeInventorySlot(2, 886, 1, false, 0f, 0));                               // el Costurero

        var bot = new Bot(nombre, null, new EntornoDeBots(null!, null!, 0, 0, d, new Navegante(d), null!), 1);
        var t = new TransporteDeBot(nombre);
        var cerrada = new TaskCompletionSource();
        var viaje = (Task<bool>)FabricarAsync.Invoke(bot, new object[] { t, s, cerrada.Task, CancellationToken.None })!;
        var primero = await Proximo(t, 2_000);
        Assert.NotNull(primero);
        Assert.Equal((byte)ClientPacketId.Walk, primero![0]);
        Assert.Equal(SentidosDelBot.RumboHacia(0, -1), primero[1]);
        cerrada.SetResult();
        Assert.True(await viaje.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>El próximo paquete que mandó el bot, o null si no mandó ninguno en <paramref name="ms"/> milisegundos.</summary>
    private static async Task<byte[]?> Proximo(TransporteDeBot t, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        var buffer = new byte[4096];
        try
        {
            int n = await t.ReceiveAsync(buffer, cts.Token);
            if (n == 0) return null;
            var paquete = new byte[n];
            for (int i = 0; i < n; i++) paquete[i] = (byte)(buffer[i] ^ t.Clave);
            return paquete;
        }
        catch (OperationCanceledException) { return null; }
    }
}
