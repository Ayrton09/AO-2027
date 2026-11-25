using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE NO USA, A LA BÓVEDA (<see cref="Bot.LoQueGuarda"/>). Pedido del tester (25/11): que los bots no pierdan todo al morir.
/// </summary>
public sealed class LoQueGuardaElBotTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);

    private static short Primero(Func<ObjInfo, bool> es) =>
        Datos.Value.Objects.Values.Where(o => o.DropsOnDeath && !o.IsNewbie && es(o)).OrderBy(o => o.Index).First().Index;

    private static readonly Lazy<(short Arma, short Arco, short Armadura, short Casco, short Anillo, short Piel, short Roja, short Azul,
        short Herramienta, short Comida)> Objs = new(() => (
            Primero(o => o.IsWeapon && o.Proyectil == 0), Primero(o => o.IsWeapon && o.Proyectil > 0), Primero(o => o.IsArmor),
            Primero(o => o.IsHelmet), Primero(o => o.IsMagicItem && o.Name.StartsWith("Anillo")), Primero(o => o.ObjType == 30),
            Primero(o => o.IsPotion && o.SubType == 3), Primero(o => o.IsPotion && o.SubType == 4), Primero(o => o.ObjType == 18),
            Primero(o => o.ObjType == 1)));

    private static List<(byte Ranura, short Obj, int Cantidad)> Guarda(
        (byte, short, short, bool)[] mochila, bool sastre = false, bool arquero = false, Func<short, bool>? queda = null) =>
        Bot.LoQueGuarda(Datos.Value, mochila, sastre, arquero, queda ?? (_ => false));

    [Fact]
    public void Guarda_el_equipo_que_no_lleva_puesto_los_anillos_y_las_pieles()
    {
        var o = Objs.Value;
        var guarda = Guarda([(1, o.Arma, 1, true), (2, o.Arma, 1, false), (3, o.Armadura, 1, false), (4, o.Casco, 1, true),
                             (5, o.Anillo, 1, false), (6, o.Piel, 30, false), (7, o.Herramienta, 1, false), (8, o.Comida, 50, false)]);
        Assert.Equal(new byte[] { 2, 3, 5, 6 }, guarda.Select(g => g.Ranura).OrderBy(r => r).ToArray());
        Assert.Equal(30, guarda.Single(g => g.Ranura == 6).Cantidad);
    }

    [Fact]
    public void El_Sastre_se_queda_las_pieles_y_el_arquero_el_arco()
    {
        var o = Objs.Value;
        Assert.Empty(Guarda([(1, o.Piel, 30, false)], sastre: true));
        Assert.Empty(Guarda([(1, o.Arco, 1, false)], arquero: true));
        Assert.Single(Guarda([(1, o.Arco, 1, false)], arquero: false));
    }

    [Fact]
    public void Lo_de_la_subasta_queda_en_la_mochila()
    {
        var o = Objs.Value;
        Assert.Empty(Guarda([(1, o.Arma, 1, false)], queda: obj => obj == o.Arma));
    }

    [Theory]
    [InlineData(1_000, 0)]
    [InlineData(1_001, 500)]
    [InlineData(3_000, 1_500)]
    public void De_las_rojas_guarda_la_mitad_si_pasa_de_mil(int rojas, int guardadas)
    {
        var o = Objs.Value;
        short enUna = (short)Math.Min(rojas, 2_000);
        var mochila = rojas > 2_000
            ? new (byte, short, short, bool)[] { (1, o.Roja, 2_000, false), (2, o.Roja, (short)(rojas - 2_000), false) }
            : new (byte, short, short, bool)[] { (1, o.Roja, enUna, false) };
        Assert.Equal(guardadas, Guarda(mochila).Sum(g => g.Cantidad));
    }

    [Theory]
    [InlineData(2_000, 0)]
    [InlineData(2_400, 1_200)]
    public void De_las_azules_guarda_la_mitad_si_pasa_de_dos_mil(int azules, int guardadas)
    {
        var o = Objs.Value;
        Assert.Equal(guardadas, Guarda([(1, o.Azul, (short)azules, false)]).Sum(g => g.Cantidad));
    }
}
