using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// POR QUÉ UN BOT QUE CASTEA TIENE QUE PONER SUS PRIMEROS PUNTOS EN MAGIA.
///
/// <para>Una clase que castea nace con UN hechizo —el original le da <c>UserHechizos(1) = 2</c> en el alta
/// (TCP.bas:541)— y con CERO en todas las habilidades, porque los diez puntos iniciales se reparten después.
/// Si esos puntos no van a Magia, el único hechizo que tiene no se puede tirar: el servidor lo rechaza contra
/// el <c>MinSkill</c> del hechizo. Y un mago pegando con el báculo no mata nada.</para>
///
/// <para>Medido en el campo: el Mago, el Bardo y la Clériga con el maná LLENO y la experiencia congelada
/// mientras los guerreros llegaban a nivel cinco. Lo que se fija acá es la premisa que hace necesario el
/// reparto dirigido — y son todos hechos de la DATA, o sea que la próxima edición del mundo los puede
/// cambiar sin tocar una línea de código.</para>
/// </summary>
public class MagiaDelBotTests
{
    /// <summary>VB6 <c>eClass</c>: las clases que el alta arranca con maná y un hechizo.</summary>
    private static readonly byte[] Casters = { 1 /*Clerigo*/, 2 /*Mago*/, 6 /*Bardo*/, 7 /*Druida*/ };

    /// <summary>Los puntos de habilidad con los que arranca un personaje (VB6 <c>SkillPtsLibres</c>).</summary>
    private const int PuntosIniciales = 10;

    private sealed record Mundo(GameData Datos, Leveling Niveles, Persistence Db);

    private static Mundo? Cargar(string raiz)
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0 || datos.LevelExp.Count == 0) return null;
        Directory.CreateDirectory(raiz);
        var niveles = new Leveling(datos.LevelExp, datos.LifeMod);
        return new Mundo(datos, niveles, new Persistence(new FileStore(raiz), niveles));
    }

    /// <summary>
    /// EL QUE CASTEA NACE CON UN HECHIZO Y SIN HABILIDAD PARA TIRARLO. Las dos mitades importan: si naciera
    /// sin hechizo no habría nada que habilitar, y si naciera con la habilidad puesta el reparto dirigido
    /// sobraría.
    /// </summary>
    [Fact]
    public void Nace_con_un_hechizo_que_todavia_no_puede_tirar()
    {
        string raiz = Path.Combine(Path.GetTempPath(), "coverao_magia_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Cargar(raiz) is not { } m) return;

            foreach (byte clase in Casters)
            {
                var acc = m.Db.CreateAccount("cuenta_magia_" + clase, "clave_de_prueba");
                var p = m.Db.CreateCharacter(acc, "Caster" + clase, 1, 1, 1, clase, 1, 1,
                                             m.Datos.NewbieKit(clase), m.Datos.ModRaza);

                short hechizo = p.Spells[1];
                Assert.True(hechizo > 0, $"la clase {clase} nace sin hechizo: no hay nada que habilitar");

                var h = m.Datos.Spell(hechizo);
                Assert.True(h is not null, $"el hechizo {hechizo} no está en la data");
                Assert.True(p.Skills[GameLimits.SkillMagia] < h!.MinSkill,
                    $"la clase {clase} ya nace con Magia suficiente ({p.Skills[GameLimits.SkillMagia]} contra " +
                    $"MinSkill {h.MinSkill}): el reparto dirigido a Magia dejaría de hacer falta");
            }
        }
        finally { try { Directory.Delete(raiz, recursive: true); } catch { /* el temp queda */ } }
    }

    /// <summary>
    /// Y CON LOS PUNTOS INICIALES ALCANZA. Si el hechizo de arranque pidiera más de lo que se reparte al nacer,
    /// ningún reparto lo habilitaría y el arreglo sería otro — mudar el hechizo, o esperar a subir de nivel.
    /// </summary>
    [Fact]
    public void Los_puntos_iniciales_alcanzan_para_habilitarlo()
    {
        string raiz = Path.Combine(Path.GetTempPath(), "coverao_magia2_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Cargar(raiz) is not { } m) return;

            var acc = m.Db.CreateAccount("cuenta_magia_tope", "clave_de_prueba");
            var p = m.Db.CreateCharacter(acc, "CasterTope", 1, 1, 1, Casters[0], 1, 1,
                                         m.Datos.NewbieKit(Casters[0]), m.Datos.ModRaza);
            var h = m.Datos.Spell(p.Spells[1]);
            if (h is null) return;

            // El bot le dedica la MITAD de lo que tiene libre, que es lo que hace un jugador que no quiere
            // quedarse manco en todo lo demás.
            Assert.True(h.MinSkill <= PuntosIniciales / 2,
                $"el hechizo de arranque pide {h.MinSkill} de Magia y al nacer se reparten {PuntosIniciales / 2}: " +
                "con el reparto de a mitad no se habilita nunca");
        }
        finally { try { Directory.Delete(raiz, recursive: true); } catch { /* el temp queda */ } }
    }

    /// <summary>
    /// QUÉ PERGAMINO VALE LA PENA: el de un hechizo que hace daño O QUE CURA y que todavía no sabe. Un caster nace con
    /// uno solo, así que sin esto se queda con ése para siempre — y sin la cura se le van las pociones.
    /// </summary>
    [Fact]
    public void Vale_la_pena_el_pergamino_de_un_hechizo_de_dano_que_no_sabe()
    {
        var sabe = new List<(byte Ranura, short Hechizo)> { ((byte)1, (short)2) };
        var dano = new SpellInfo(7, "Descarga", SubeHp: 2, MinHp: 20, MaxHp: 40, ManaCost: 30, Target: 1);
        var yaLoSabe = new SpellInfo(2, "Proyectil Mágico", SubeHp: 2, MinHp: 5, MaxHp: 10, ManaCost: 5, Target: 1);
        var cura = new SpellInfo(9, "Curar Heridas", SubeHp: 1, MinHp: 10, MaxHp: 20, ManaCost: 10, Target: 1);

        Assert.True(Bot.ValeLaPenaElPergamino(dano, sabe));
        Assert.False(Bot.ValeLaPenaElPergamino(yaLoSabe, sabe));
        Assert.True(Bot.ValeLaPenaElPergamino(cura, sabe));       // la cura TAMBIÉN: ver `CurarseConMagia`
        Assert.False(Bot.ValeLaPenaElPergamino(null, sabe));
    }

    /// <summary>
    /// EL QUE SABE CURARSE SE CURA CON MAGIA, y con la más fuerte que le dé el maná: entre Curar Heridas Leves y
    /// Graves, la Grave mientras pueda pagarla.
    /// </summary>
    [Fact]
    public void Se_cura_con_el_hechizo_mas_fuerte_que_puede_pagar()
    {
        var curas = new (byte Ranura, int Cura, int Mana)[]
        {
            (1, 15, 6),     // Curar Heridas Leves
            (2, 45, 25),    // Curar Heridas Graves
        };

        Assert.Equal((byte)2, Bot.MejorCuraQuePuedePagar(curas, mana: 30)!.Value.Ranura);
        Assert.Equal((byte)1, Bot.MejorCuraQuePuedePagar(curas, mana: 10)!.Value.Ranura);   // no le alcanza la grave
        Assert.Null(Bot.MejorCuraQuePuedePagar(curas, mana: 3));                            // no le alcanza ninguna
        Assert.Null(Bot.MejorCuraQuePuedePagar(System.Array.Empty<(byte, int, int)>(), mana: 500));   // no sabe curarse
    }

    /// <summary>
    /// Y EL PERGAMINO DE CURA TAMBIÉN SE COMPRA. Antes sólo contaban los de daño, así que el bot mago nunca aprendía
    /// a curarse — que es lo primero que compra cualquiera que juega un caster.
    /// </summary>
    [Theory]
    [InlineData(2, true)]     // hace daño
    [InlineData(1, true)]     // cura
    [InlineData(0, false)]    // ni una cosa ni la otra: un hechizo de estado
    public void El_pergamino_que_vale_la_pena_hace_dano_o_cura(int subeHp, bool vale)
    {
        var hechizo = new SpellInfo(77, "el que sea", SubeHp: subeHp, MinHp: 10, MaxHp: 20, ManaCost: 10, Target: 1);

        Assert.Equal(vale, Bot.ValeLaPenaElPergamino(hechizo, System.Array.Empty<(byte Ranura, short Hechizo)>()));
        Assert.False(Bot.ValeLaPenaElPergamino(hechizo, new (byte Ranura, short Hechizo)[] { (1, 77) }),
            "compró un pergamino de un hechizo que ya sabía");
    }
}
