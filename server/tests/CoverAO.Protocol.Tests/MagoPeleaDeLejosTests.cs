using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El Mago pelea de lejos (pedido del tester del 20/11): con el rival pegado, primero lo inmoviliza o se aleja, y lanza desde
/// lejos. Las otras clases siguen como antes.
/// </summary>
public sealed class MagoPeleaDeLejosTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private const short Saeta = 6, Inmovilizar = 24;

    [Fact]
    public void Los_hechizos_de_la_prueba_son_los_del_juego()
    {
        var datos = Mundo.Value.Datos;
        Assert.True(datos.Spell(Inmovilizar) is { IsImmobilize: true, ManaCost: 250 });
        Assert.True(datos.Spell(Saeta) is { SubeHp: 2 });
    }

    [Theory]
    [InlineData(false, 1, 0, false, true)]    // a una persona pegada, enseguida
    [InlineData(false, 2, 5, false, false)]   // a dos pasos todavía no: se aleja
    [InlineData(true, 1, 1, false, false)]    // a la criatura que lo alcanza la primera vez, no: se aleja
    [InlineData(true, 1, 2, false, true)]     // a la que lo vuelve a alcanzar aunque se alejó, sí
    [InlineData(true, 1, 9, true, false)]     // a una inmune, nunca
    public void Cuando_gasta_el_mana_en_inmovilizar(bool esNpc, int pasos, int alcances, bool inmune, bool inmoviliza) =>
        Assert.Equal(inmoviliza, Bot.MagoInmoviliza(esNpc, pasos, alcances, inmune));

    [Fact]
    public void A_una_criatura_la_da_por_quieta_menos_que_a_una_persona()
    {
        Assert.Equal(1_000 + Bot.QuietaMs, Bot.QuietaHasta(1_000, cobro: true));
        Assert.Equal(1_000 + Bot.QuietaNpcMs, Bot.QuietaHasta(1_000, cobro: true, esNpc: true));
        Assert.Equal(1_000 + Bot.ReintentoDeQuietarMs, Bot.QuietaHasta(1_000, cobro: false, esNpc: true));
        Assert.True(Bot.QuietaNpcMs < 14_000);   // 37 vueltas de la IA de 380 ms (PacketDispatcher.CastStateOnNpc)
    }

    [Fact]
    public async Task El_mago_inmoviliza_a_la_persona_pegada_se_aleja_y_lanza_de_lejos()
    {
        var f = Armar(GameLimits.ClaseMago, sabeInmovilizar: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.UpdateMana(2_000 - 250));   // el servidor le cobró: quedó inmovilizada
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
            var siguiente = await HastaQueNoCamine(f);
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, siguiente);
            Assert.True(Distancia(f) >= Bot.DistanciaDelMago, $"Lanzó a {Distancia(f)} baldosas.");
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task El_mago_que_no_sabe_inmovilizar_se_aleja_antes_de_lanzar()
    {
        var f = Armar(GameLimits.ClaseMago, sabeInmovilizar: false);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await HastaQueNoCamine(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Las_otras_clases_lanzan_donde_estan()
    {
        var f = Armar(GameLimits.ClaseClerigo, sabeInmovilizar: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada, byte X, byte Y);

    /// <summary>El bot en un claro del mapa 18 con lugar para alejarse, y una persona de otro bando pegada al norte.</summary>
    private static Fixture Armar(byte clase, bool sabeInmovilizar)
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
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 30,
            FactionStatus = Player.StatusCiudadano, MaxHp = 300, Hp = 300, MaxMana = 2_000, Mana = 2_000,
            MaxStamina = 400, Stamina = 400, Class = clase,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeSpellSlot(1, Saeta));
        if (sabeInmovilizar) s.Recibir(ServerMessages.ChangeSpellSlot(2, Inmovilizar));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - 1)),
        }));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, clase);
        return new Fixture(bot, s, new TransporteDeBot("Rufo"),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), sitio.X, sitio.Y);
    }

    private static int Distancia(Fixture f)
    {
        var (x, y) = f.Sentidos.Posicion;
        return Math.Abs(x - f.X) + Math.Abs(y - (f.Y - 1));
    }

    private static Task<bool> Empezar(Fixture f, CancellationToken ct) =>
        (Task<bool>)Cazar.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos, (short)501, f.Cerrada.Task, ct, true })!;

    /// <summary>El primer paquete que no es un paso: los pasos de alejarse pueden ser uno o dos.</summary>
    private static async Task<byte[]> HastaQueNoCamine(Fixture f)
    {
        for (int i = 0; i < 4; i++)
        {
            var p = await Proximo(f);
            if (p[0] != (byte)ClientPacketId.Walk) return p;
        }
        throw new InvalidOperationException("Siguió caminando.");
    }

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
