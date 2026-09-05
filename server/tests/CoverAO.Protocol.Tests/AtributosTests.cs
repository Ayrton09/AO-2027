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

    /// <summary>
    /// LA BASE POR CLASE, las 18 (VB6 <c>AsignarAtributos</c>, TCP.bas:309-341). El tester vio 31 de fuerza en
    /// un gladiador enano y pidió revisar "todos los números contra el original": de nacimiento son 21
    /// (18 de la clase + 3 del enano) y los otros 10 son los Nudillos (+10) equipados. La tabla es la
    /// pertenencia de cada clase a los tres <c>Case</c> del original, escrita clase por clase para que un
    /// cambio en el <c>switch</c> de <c>Persistence</c> se caiga acá. Se crean como Humano (+1,+1,+1,0,+2).
    /// </summary>
    [Theory]
    [InlineData(1,  14, 14, 18, 10, 18)]   // Clérigo     (Case magia)
    [InlineData(2,   6, 18, 18, 10, 18)]   // Mago        (Case Mago)
    [InlineData(3,  18, 18,  6, 10, 18)]   // Guerrero    (Case Else)
    [InlineData(4,  14, 14, 18, 10, 18)]   // Asesino     (Case magia)
    [InlineData(5,  18, 18,  6, 10, 18)]   // Ladrón      (Case Else)
    [InlineData(6,  14, 14, 18, 10, 18)]   // Bardo       (Case magia)
    [InlineData(7,  14, 14, 18, 10, 18)]   // Druida      (Case magia)
    [InlineData(8,  18, 18,  6, 10, 18)]   // Gladiador   (Case Else)
    [InlineData(9,  14, 14, 18, 10, 18)]   // Paladín     (Case magia)
    [InlineData(10, 18, 18,  6, 10, 18)]   // Cazador     (Case Else)
    [InlineData(11, 18, 18,  6, 10, 18)]   // Pescador    (Case Else)
    [InlineData(12, 18, 18,  6, 10, 18)]   // Herrero     (Case Else)
    [InlineData(13, 18, 18,  6, 10, 18)]   // Leñador     (Case Else)
    [InlineData(14, 18, 18,  6, 10, 18)]   // Minero      (Case Else)
    [InlineData(15, 18, 18,  6, 10, 18)]   // Carpintero  (Case Else)
    [InlineData(16, 18, 18,  6, 10, 18)]   // Sastre      (Case Else)
    [InlineData(17, 18, 18,  6, 10, 18)]   // Mercenario  (Case Else)
    [InlineData(18, 14, 14, 18, 10, 18)]   // Nigromante  (Case magia)
    public void Cada_clase_nace_con_la_base_del_original(int clase, int f, int a, int i, int c, int co)
    {
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.Objects.Count == 0) return;   // sin gamedata local no hay nada que verificar
        var store = new CoverAO.Server.Data.FileStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coverao_base_" + System.Guid.NewGuid().ToString("N")));
        var db = new CoverAO.Server.Data.Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("base_test", "pw");
        var pj = db.CreateCharacter(acc, "Base" + clase, 1, race: 1, gender: 1, @class: (byte)clase, homeland: 1, head: 1,
                                    newbieKit: data.NewbieKit((byte)clase), modRaza: data.ModRaza);
        Assert.Equal(new byte[] { (byte)(f + 1), (byte)(a + 1), (byte)(i + 1), (byte)c, (byte)(co + 2) }, pj.Attributes[1..6]);
    }

    [Fact]
    public void El_gladiador_enano_del_tester_nace_con_21_de_fuerza()
    {
        var data = CoverAO.Server.Data.GameData.Load();
        if (data.Objects.Count == 0) return;
        var store = new CoverAO.Server.Data.FileStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coverao_glad_" + System.Guid.NewGuid().ToString("N")));
        var db = new CoverAO.Server.Data.Persistence(store, new Leveling(data.LevelExp));
        var acc = db.CreateAccount("glad_test", "pw");
        // Gladiador (18,18,6,10,18) + Enano (+3,-1,-5,-1,+4): los 31 que vio el tester son estos 21 más los
        // Nudillos (+10) puestos, que el original suma al atributo mientras están equipados.
        var pj = db.CreateCharacter(acc, "GladEnano", 1, race: 5, gender: 1, @class: 8, homeland: 1, head: 1,
                                    newbieKit: data.NewbieKit(8), modRaza: data.ModRaza);
        Assert.Equal([21, 17, 1, 9, 22], pj.Attributes[1..6]);
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
