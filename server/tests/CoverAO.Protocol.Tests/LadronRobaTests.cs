using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Ladrón se hace Renegado y roba (pedido del tester del 20/11, decisión del autor del 22/11).
/// </summary>
public sealed class LadronRobaTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Robar = typeof(Bot).GetMethod("RobarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Atender = typeof(Bot).GetMethod("AtenderRivalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(GameLimits.ClaseLadron, 26, Player.StatusRepublicano, true)]
    [InlineData(GameLimits.ClaseLadron, 26, Player.StatusMilicia, true)]      // de la Milicia, primero a civil
    [InlineData(GameLimits.ClaseLadron, 26, Player.StatusCriminal, false)]    // ya es Renegado
    [InlineData(GameLimits.ClaseLadron, 14, Player.StatusCiudadano, false)]   // el servidor no deja antes del 15
    [InlineData(GameLimits.ClaseGuerrero, 26, Player.StatusCiudadano, false)]
    public void Cuando_renuncia(byte clase, int nivel, byte bando, bool renuncia) =>
        Assert.Equal(renuncia, Bot.LadronRenuncia(clase, nivel, bando));

    [Theory]
    [InlineData(Player.StatusCriminal, true, 1.0, 200, true)]
    [InlineData(Player.StatusRepublicano, true, 1.0, 200, false)]   // sin ser Renegado, el servidor no deja
    [InlineData(Player.StatusCriminal, false, 1.0, 200, false)]     // en un mapa seguro, tampoco
    [InlineData(Player.StatusCriminal, true, 0.5, 200, false)]      // con poca vida, no
    [InlineData(Player.StatusCriminal, true, 1.0, 40, false)]       // sin energía para robar y pelear
    public void Cuando_sale_a_robar(byte bando, bool inseguro, double salud, int energia, bool sale) =>
        Assert.Equal(sale, Bot.LadronSaleARobar(GameLimits.ClaseLadron, bando, inseguro, salud, energia));

    [Fact]
    public void Renegado_se_va_con_la_mitad_de_la_vida() =>
        Assert.Equal(Bot.VidaParaSeguirPeleando, Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseLadron));

    /// <summary>Ronda 135: para la Renegada el ciudadano es un rival, y atenderlo iba antes que robar.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Se_arrima_y_le_roba(bool comoRival)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        Assert.True(datos.IsPkMap(mapa));
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-5, 11)
                            from dx in Enumerable.Range(-5, 11)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy) || datos.MapExits.ContainsKey((mapa, x + dx, y + dy))
                                   || datos.IsSafeZone(mapa, x + dx, y + dy))
                           .All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Lira" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Lira", Account = "lira_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 26,
            FactionStatus = Player.StatusCriminal, MaxHp = 286, Hp = 286, MaxStamina = 300, Stamina = 300, Class = GameLimits.ClaseLadron,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        var ella = new Player
        {
            CharIndex = 501, Name = "Milo", Account = "milo_test", FactionStatus = Player.StatusRepublicano, Level = 25,
            Class = GameLimits.ClasePaladin, MaxHp = 250, Hp = 250, Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - 4)),
        };
        s.Recibir(ServerMessages.CharacterCreate(ella));
        s.Recibir(ServerMessages.CharMsgStatus(ella, yo, 0));
        var bot = new Bot("Lira", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseLadron);
        var t = new TransporteDeBot("Lira");
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var robo = comoRival
            ? (Task<bool>)Atender.Invoke(bot, new object[] { t, s, s.Donde(501)!, new TaskCompletionSource().Task, corte.Token })!
            : (Task<bool>)Robar.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, corte.Token })!;
        if (comoRival) Assert.Equal((short)501, bot.RivalQueInterrumpeCaza(s)?.CharIndex);
        try
        {
            byte[] p;
            int pasos = 0;
            while ((p = await Proximo(t))[0] == (byte)ClientPacketId.Walk) pasos++;
            Assert.InRange(pasos, 2, 3);
            Assert.Equal(new byte[] { (byte)ClientPacketId.WorkLeftClick, sitio.X, (byte)(sitio.Y - 4), (byte)GameLimits.SkillRobar }, p);
        }
        finally { corte.Cancel(); try { await robo; } catch (OperationCanceledException) { } }
    }

    private static async Task<byte[]> Proximo(TransporteDeBot t)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var buffer = new byte[4_096];
        int n = await t.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ t.Clave)).ToArray();
    }
}
