using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Pedido del tester del 20/11: cada bot nuevo nace con una raza que le sirve a su clase, y el Minero lleva el Carro de Minería
/// (objeto 880), que salva el 70 % de los minerales al morir.
/// </summary>
public class RazaYCarroDelBotTests
{
    private static readonly GameData Datos = GameData.Load();
    private const byte Humano = 1, Elfo = 2, Drow = 3, Gnomo = 4, Enano = 5, Orco = 6;
    private const short Piquete = 187, Carro = 880;

    [Theory]
    [InlineData(GameLimits.ClaseMago, Gnomo, Elfo)]
    [InlineData(GameLimits.ClaseClerigo, Drow, Humano)]
    [InlineData(GameLimits.ClaseGuerrero, Orco, Enano)]
    [InlineData(GameLimits.ClaseAsesino, Humano, Drow)]
    [InlineData(GameLimits.ClaseLadron, Orco, Enano)]
    [InlineData(GameLimits.ClaseBardo, Drow, Humano)]
    [InlineData(GameLimits.ClaseDruida, Gnomo, Elfo)]
    [InlineData(GameLimits.ClaseGladiador, Orco, Enano)]
    [InlineData(GameLimits.ClasePaladin, Humano, Drow)]
    [InlineData(GameLimits.ClaseCazador, Elfo, Humano)]
    [InlineData(17, Drow, Enano)]                       // Mercenario
    [InlineData(GameLimits.ClaseNigromante, Humano, Drow)]
    [InlineData(11, Enano, Orco)]                       // Pescador
    [InlineData(12, Enano, Orco)]                       // Herrero
    [InlineData(13, Enano, Orco)]                       // Leñador
    [InlineData(14, Enano, Orco)]                       // Minero
    [InlineData(15, Enano, Orco)]                       // Carpintero
    [InlineData(16, Enano, Orco)]                       // Sastre
    public void Cada_clase_nace_con_una_de_sus_dos_razas(byte clase, byte una, byte otra)
    {
        Assert.Equal(una, Bot.RazaParaLaClase(clase, 0));
        Assert.Equal(otra, Bot.RazaParaLaClase(clase, 1));
    }

    [Fact]
    public void Todas_las_clases_del_reparto_tienen_cabezas_para_su_raza()
    {
        foreach (byte clase in Bot.Reparto.Distinct())
            foreach (int dado in new[] { 0, 1 })
                foreach (byte genero in new byte[] { 1, 2 })
                    Assert.NotNull(Equipment.HeadRange(Bot.RazaParaLaClase(clase, dado), genero));
    }

    private static (Bot Bot, SentidosDelBot Sentidos) Preparar(byte clase, byte nivel)
    {
        var p = new Player { CharIndex = 500, Name = "Quim", Account = "Quim_qa", Class = clase, Race = Enano, Gender = 1,
            Pos = new WorldPos(20, 50, 50), Level = nivel, Hp = 300, MaxHp = 300, Gold = 5000, Hunger = 100, Thirst = 100 };
        var s = new SentidosDelBot { MiNombre = p.Name };
        s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
        s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        var b = new Bot(p.Name, null, new EntornoDeBots(null!, new ServerIni(), 0, 0, Datos, new Navegante(Datos), null!), 42);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(b, clase);
        return (b, s);
    }

    private static short LeFalta(Bot b, SentidosDelBot s) =>
        (short)typeof(Bot).GetMethod("HerramientaQueLeFalta", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(b, [s])!;

    private static bool NoLoVende(Bot b, short obj) =>
        (bool)typeof(Bot).GetMethod("EsHerramientaDeSuOficio", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(b, [obj])!;

    [Fact]
    public void El_carro_lo_vende_el_minero_y_pide_nivel_12()
    {
        var carro = Datos.Obj(Carro)!;
        Assert.Equal(350, carro.Value);
        Assert.Equal(12, carro.MinLevel);
        Assert.Contains(Datos.Npcs.Values, n => n.Name == "Minero" && n.StockItems!.Any(i => i.ObjIndex == Carro)
                                              && n.StockItems!.Any(i => i.ObjIndex == Piquete));
    }

    [Fact]
    public void El_minero_compra_primero_el_piquete_y_desde_el_12_el_carro()
    {
        var (b, s) = Preparar(14, nivel: 12);
        Assert.Equal(Piquete, LeFalta(b, s));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Piquete, 1, false, 0, 1));
        Assert.Equal(Carro, LeFalta(b, s));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, Carro, 1, false, 0, 1));
        Assert.Equal(0, LeFalta(b, s));
        Assert.True(NoLoVende(b, Carro));
    }

    [Fact]
    public void Antes_del_12_no_lo_busca()
    {
        var (b, s) = Preparar(14, nivel: 11);
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Piquete, 1, false, 0, 1));
        Assert.Equal(0, LeFalta(b, s));
    }

    [Theory]
    [InlineData(12)]   // Herrero
    [InlineData(13)]   // Leñador
    [InlineData(GameLimits.ClaseGuerrero)]
    public void Las_otras_clases_no_compran_el_carro(byte clase)
    {
        var (b, s) = Preparar(clase, nivel: 30);
        Assert.NotEqual(Carro, LeFalta(b, s));
        Assert.False(NoLoVende(b, Carro));
    }
}
