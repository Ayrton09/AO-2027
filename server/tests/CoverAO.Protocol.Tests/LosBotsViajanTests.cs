using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS BOTS SE PREPARAN PARA VIAJAR (pedido del autor, 2026-11-16): los puntos que no van a su skill propia van primero a Navegación,
/// hasta lo que piden los pasajes que cruzan el agua, y después a Equitación, hasta lo que pide la montura común.
/// </summary>
public class LosBotsViajanTests
{
    private const int Nav = GameLimits.SkillNavegacion, Equ = GameLimits.SkillEquitacion;

    private static List<(int Skill, int Dar)> Puntos(int sobran, int propia = 0, int navegacion = 0, int equitacion = 0,
                                                     int paraCruzar = 80, byte[]? reparto = null) =>
        Bot.PuntosParaViajar(sobran, propia, reparto ?? new byte[GameLimits.NumSkills + 1], paraCruzar, navegacion, equitacion).ToList();

    [Fact]
    public void Primero_navegacion()
    {
        Assert.Equal(new[] { (Nav, 5) }, Puntos(5));
    }

    [Fact]
    public void Con_navegacion_completa_sigue_equitacion_y_no_se_pasa()
    {
        Assert.Equal(new[] { (Nav, 2), (Equ, 3) }, Puntos(5, navegacion: 78));
        Assert.Equal(new[] { (Equ, 1) }, Puntos(5, navegacion: 80, equitacion: Bot.EquitacionParaMontar - 1));
        Assert.Empty(Puntos(5, navegacion: 80, equitacion: Bot.EquitacionParaMontar));   // el resto, al azar
    }

    [Fact]
    public void Con_la_regla_del_cruce_apagada_no_pone_en_navegacion()
    {
        Assert.Equal(new[] { (Equ, 5) }, Puntos(5, paraCruzar: 0));
    }

    [Fact]
    public void Cuenta_lo_que_ya_va_en_el_reparto_y_no_toca_la_propia()
    {
        var reparto = new byte[GameLimits.NumSkills + 1];
        reparto[Nav] = 79;
        Assert.Equal(new[] { (Nav, 1), (Equ, 4) }, Puntos(5, reparto: reparto));
        Assert.Equal(new[] { (Equ, 5) }, Puntos(5, propia: Nav));
        Assert.Empty(Puntos(0));
    }
}

/// <summary>
/// LA MONTURA DEL BOT, contra la data: el único que las vende está en Tiama, a cada raza le toca la suya, y de los puertos de este lado
/// sale un pase a Tiama.
/// </summary>
public class LaMonturaDelBotTests
{
    private static readonly CoverAO.Server.Data.GameData Datos = CoverAO.Server.Data.GameData.Load();
    private const byte Humano = 1, Orco = 6, Guerrero = 1;
    private const int Tiama = 218, Nix = 34, Rinkel = 20;

    [Fact]
    public void Las_vende_uno_solo_y_esta_en_Tiama()
    {
        var v = Bot.VendedorDeMonturas(Datos);
        Assert.NotNull(v);
        Assert.Equal(Tiama, v!.Value.Mapa);
    }

    [Theory]
    [InlineData(Humano, 1342)]   // Montura (Altos)
    [InlineData(Orco, 1344)]     // Huargo
    public void A_cada_raza_la_suya_con_la_equitacion_comun(byte raza, int esperada)
    {
        var v = Bot.VendedorDeMonturas(Datos)!.Value.Ficha;
        var m = Bot.MonturaQueLeSirve(Datos, v, nivel: 30, Guerrero, raza, Bot.EquitacionParaMontar, plata: 300_000);
        Assert.Equal(esperada, (int?)m?.Index);
    }

    [Fact]
    public void Sin_la_equitacion_o_sin_la_plata_ninguna()
    {
        var v = Bot.VendedorDeMonturas(Datos)!.Value.Ficha;
        Assert.Null(Bot.MonturaQueLeSirve(Datos, v, 30, Guerrero, Humano, equitacion: 10, plata: 1_000_000));
        Assert.Null(Bot.MonturaQueLeSirve(Datos, v, 30, Guerrero, Humano, Bot.EquitacionParaMontar, plata: 100_000));
    }

    [Fact]
    public void El_pase_a_Tiama_sale_del_mapa_donde_esta_el_pirata()
    {
        Assert.True(Bot.EsPaseA(Datos, 1058, Nix, Tiama));        // Pase: Nix a Tiama
        Assert.False(Bot.EsPaseA(Datos, 1058, Rinkel, Tiama));    // ese pase no sale de Rinkel
        Assert.False(Bot.EsPaseA(Datos, 597, Nix, Tiama));        // Nix a Rinkel: no cruza a Tiama
        Assert.Contains(Datos.MapSpawns, kv => kv.Value.Any(s => Bot.EsPirataHacia(Datos, s.NpcIndex, kv.Key, Tiama)));
    }
}
