using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>El Minero va a la mina acompañado (pedido del tester del 20/11, decisión del autor del 22/11).</summary>
public sealed class EscoltaDelMineroTests
{
    private const byte Minero = 14;

    [Theory]
    [InlineData(Minero, true, 1_000, 0, true)]
    [InlineData(Minero, false, 1_000, 0, false)]          // una mina en un mapa seguro, no
    [InlineData(Minero, true, 1_000, 5_000, false)]       // la pidió hace poco
    [InlineData(GameLimits.ClaseGuerrero, true, 1_000, 0, false)]
    public void Cuando_pide_escolta(byte clase, bool insegura, long ahora, long proximo, bool pide) =>
        Assert.Equal(pide, Bot.PideEscolta(clase, insegura, ahora, proximo));

    [Theory]
    [InlineData(GameLimits.ClaseGuerrero, (short)25, true)]
    [InlineData(GameLimits.ClaseMago, (short)20, true)]
    [InlineData(13, (short)24, false)]                       // un Leñador no
    [InlineData(GameLimits.ClaseGuerrero, SentidosDelBot.NivelEscondido, false)]
    public void Quien_sirve_de_escolta(byte clase, short nivel, bool sirve) =>
        Assert.Equal(sirve, Bot.SirveDeEscolta((clase, 100), nivel));

    [Fact]
    public void Sin_ficha_no_sirve() => Assert.False(Bot.SirveDeEscolta(null, 25));

    [Theory]
    [InlineData(14, 15, true)]
    [InlineData(14, 16, false)]   // ya no llega al vendedor: lo compra sólo si hay mina
    [InlineData(13, 10, false)]   // un Leñador no
    public void De_novato_compra_el_piquete(byte clase, int nivel, bool compra) =>
        Assert.Equal(compra, Bot.ElMineroLoCompraDeNovato(clase, nivel));

    /// <summary>La premisa: el vendedor que le vende a un ciudadano se alcanza sólo por la cuna (el 37).</summary>
    [Fact]
    public void El_piquete_se_vende_pasando_por_la_cuna()
    {
        var datos = GameData.Load();
        var mapas = new Navegante(datos);
        bool Vende(int ficha) => datos.Npc(ficha)?.StockItems?.Any(s => s.ObjIndex == 187) == true;
        bool Atiende(int m) => Bot.ElMercaderLeVende(null, datos.MapBattleMode(m), Player.StatusCiudadano);
        Assert.NotNull(mapas.DondeHay(13, (58, 32), Vende, Atiende, m => Bot.PuedeEntrar(GameLimits.NewbieMaxLevel, m, datos, new ServerIni()), 30));
        Assert.Null(mapas.DondeHay(13, (58, 32), Vende, Atiende, m => Bot.PuedeEntrar(GameLimits.NewbieMaxLevel + 1, m, datos, new ServerIni()), 30));
    }

    /// <summary>El que lo escucha la toma por una invitación, como la de una persona.</summary>
    [Fact]
    public void La_frase_es_una_invitacion() => Assert.True(Bot.EsInvitacion(Bot.FraseDeLaEscolta, "Ciro"));

    /// <summary>
    /// Medido el 22/11: sin escolta un Minero no encuentra ninguna mina desde el 20 (todas están en mapas inseguros donde nace algo
    /// que le queda grande), así que no compraba el piquete y no minaba nunca. Acompañado, sí hay a dónde ir.
    /// </summary>
    [Theory]
    [InlineData(20, 10, 9)]
    [InlineData(13, 58, 32)]
    [InlineData(13, 34, 70)]
    [InlineData(13, 79, 35)]
    [InlineData(24, 75, 29)]
    public void Sin_escolta_no_hay_mina_y_acompanado_si(short mapa, byte x, byte y)
    {
        var datos = GameData.Load();
        var s = new SentidosDelBot { MiNombre = "Quim" };
        var yo = new Player { CharIndex = 500, Name = "Quim", Account = "quim_test", Pos = new WorldPos(mapa, x, y), Level = 18,
                              MaxHp = 200, Hp = 200, MaxStamina = 300, Stamina = 300, Class = Minero };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 38, 200, false, 0, 1));
        var bot = new Bot("Quim", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Minero);
        bool Llamar(string metodo) => (bool)typeof(Bot).GetMethod(metodo, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bot, new object[] { s })!;
        Assert.False(Llamar("HayDondeTrabajarSeguro"));
        Assert.True(Llamar("HayMinaConEscolta"));
    }
}
