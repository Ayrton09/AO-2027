using System.Diagnostics;
using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Clérigo pelea con magia y con el arma (pedido del tester del 20/11): quieta a la persona, se le arrima, y al lado le pega y
/// le lanza en la misma vuelta.
/// </summary>
public sealed class ClerigoPeleaConArmaTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private const short Saeta = 6, Inmovilizar = 24, Spatha = 569;
    private const byte RanuraDelArma = 1;

    [Fact]
    public void La_spatha_es_un_arma_del_clerigo()
    {
        var o = Mundo.Value.Datos.Obj(Spatha);
        Assert.True(o is { IsWeapon: true, Proyectil: 0 });
        Assert.False(o!.ClassForbidden(GameLimits.ClaseClerigo));
    }

    [Theory]
    [InlineData(GameLimits.ClaseClerigo, true, 1, false, true)]    // al lado, pega
    [InlineData(GameLimits.ClaseClerigo, true, 4, true, true)]     // quieta y lejos, se arrima
    [InlineData(GameLimits.ClaseClerigo, true, 4, false, false)]   // la que se mueve, de lejos
    [InlineData(GameLimits.ClaseClerigo, false, 1, true, false)]   // sin arma, lanza
    [InlineData(GameLimits.ClaseAsesino, true, 4, true, true)]     // el Asesino, igual
    [InlineData(GameLimits.ClasePaladin, true, 1, false, true)]    // y el Paladín
    [InlineData(GameLimits.ClaseMago, true, 1, true, false)]       // las otras clases, como siempre
    public void Cuando_se_arrima(byte clase, bool conArma, int pasos, bool quieta, bool seArrima) =>
        Assert.Equal(seArrima, Bot.SeArrimaConElCombo(clase, conArma, pasos, quieta));

    [Theory]
    [InlineData(40, (short)40, GameLimits.ClaseGladiador, 100, true)]    // de su nivel, sí
    [InlineData(30, (short)31, GameLimits.ClaseGladiador, 100, false)]   // uno más, no
    [InlineData(30, (short)31, GameLimits.ClaseGuerrero, 100, false)]
    [InlineData(30, (short)32, GameLimits.ClaseNigromante, 100, true)]   // los demás, hasta 2 por encima
    [InlineData(30, (short)33, GameLimits.ClaseMago, 100, false)]
    [InlineData(30, (short)35, 14, 100, true)]                           // un Minero, hasta 5 por encima
    [InlineData(30, (short)36, GameLimits.ClaseGladiador, 20, true)]     // casi muerto, lo remata
    [InlineData(30, SentidosDelBot.NivelEscondido, 14, 10, false)]        // escondido, nunca
    public void Con_quien_pelea_el_clerigo(int miNivel, short nivel, byte clase, int vida, bool pelea) =>
        Assert.Equal(pelea, Bot.ClerigoLeConvienePelear(miNivel, nivel, (clase, vida)));

    [Fact]
    public void El_clerigo_aguanta_mas_que_el_mago_antes_de_irse()
    {
        Assert.Equal(Bot.VidaParaSeguirPeleando, Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseMago));
        Assert.True(Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseClerigo) < Bot.VidaParaSeguirPeleando);
        Assert.Null(Bot.VidaParaSeguirPeleandoDe(GameLimits.ClaseGuerrero));
    }

    [Fact]
    public async Task Al_lado_pega_y_enseguida_lanza()
    {
        var f = Armar(conArma: true, distancia: 1);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
            var reloj = Stopwatch.StartNew();
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
            Assert.InRange(reloj.ElapsedMilliseconds, Bot.ComboMinMs - 50, Bot.ComboMaxMs + 300);
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
            // Y el golpe que sigue, recién cuando pasó lo de un golpe desde el anterior.
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.True(reloj.ElapsedMilliseconds >= 1_000 - Bot.ComboMaxMs, $"Pegó a los {reloj.ElapsedMilliseconds} ms del hechizo.");
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Sin_arma_lanza_como_siempre()
    {
        var f = Armar(conArma: false, distancia: 1);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task De_lejos_la_inmoviliza_y_se_le_arrima()
    {
        var f = Armar(conArma: true, distancia: 3);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.UpdateMana(2_000 - 250));   // el servidor le cobró: quedó inmovilizada
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task De_lejos_sin_poder_quietarla_lanza()
    {
        var f = Armar(conArma: true, distancia: 3, sabeInmovilizar: false);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada);

    /// <summary>Un Clérigo 30 en un claro del mapa 18 y una persona de otro bando al norte, a <paramref name="distancia"/>, con su
    /// ficha ya vista: una Paladina de su nivel, que le conviene.</summary>
    private static Fixture Armar(bool conArma, int distancia, bool sabeInmovilizar = true)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-4, 9)
                            from dx in Enumerable.Range(-4, 9)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy) || datos.MapExits.ContainsKey((mapa, x + dx, y + dy)))
                           .All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Elmo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Elmo", Account = "elmo_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 30,
            FactionStatus = Player.StatusCiudadano, MaxHp = 300, Hp = 300, MaxMana = 2_000, Mana = 2_000,
            MaxStamina = 400, Stamina = 400, Class = GameLimits.ClaseClerigo,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeSpellSlot(1, Saeta));
        if (sabeInmovilizar) s.Recibir(ServerMessages.ChangeSpellSlot(2, Inmovilizar));
        if (conArma) s.Recibir(ServerMessages.ChangeInventorySlot(RanuraDelArma, Spatha, 1, true, 0, 1));
        var ella = new Player
        {
            CharIndex = 501, Name = "Milo", Account = "milo_test", FactionStatus = Player.StatusCriminal, Level = 30,
            Class = GameLimits.ClasePaladin, MaxHp = 300, Hp = 300, Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - distancia)),
        };
        s.Recibir(ServerMessages.CharacterCreate(ella));
        s.Recibir(ServerMessages.CharMsgStatus(ella, yo, 0));
        var bot = new Bot("Elmo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseClerigo);
        return new Fixture(bot, s, new TransporteDeBot("Elmo"),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private static Task<bool> Empezar(Fixture f, CancellationToken ct) =>
        (Task<bool>)Cazar.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos, (short)501, f.Cerrada.Task, ct, true })!;

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private static async Task Cancelar(Task tarea, CancellationTokenSource corte)
    {
        corte.Cancel();
        try { await tarea.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (corte.IsCancellationRequested) { }
    }
}
