using System.Diagnostics;
using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class LasAzulesEnCombateTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task Una_azul_real_le_permite_volver_a_lanzar_respetando_la_espera_de_pociones()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var reloj = Stopwatch.StartNew();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
            f.Sentidos.Recibir(ServerMessages.UpdateMana((short)Potions.ManaRestore(2_000, 24)));
            f.Sentidos.Recibir(ServerMessages.ChangeInventorySlotUser(1, 3, 9));
            Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
            Assert.True(reloj.Elapsed >= TimeSpan.FromMilliseconds(700), $"Actuó después de sólo {reloj.Elapsed.TotalMilliseconds:0} ms.");
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Sin_confirmacion_de_mana_no_se_queda_encadenando_azules_sin_pelear()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData("sin azules")]
    [InlineData("mana suficiente")]
    [InlineData("sin daño")]
    [InlineData("sin stamina")]
    [InlineData("coste imposible")]
    [InlineData("vida baja sin roja")]
    public async Task No_gasta_una_azul_si_no_hace_falta_o_no_habilitaria_un_hechizo_util(string caso)
    {
        var f = caso switch
        {
            "sin azules" => Armar(azules: false),
            "mana suficiente" => Armar(mana: 20),
            "sin daño" => Armar(hechizo: 5),
            "sin stamina" => Armar(hechizo: 25, stamina: 100),
            "coste imposible" => Armar(hechizo: 25, manaMaximo: 500),
            "vida baja sin roja" => Armar(hp: 100),
            _ => throw new ArgumentOutOfRangeException(nameof(caso)),
        };
        if (caso is "sin stamina" or "coste imposible")
        {
            Assert.True(f.Datos.Spell(25) is { SubeHp: 2, StaRequerido: > 100, ManaCost: > 500 });
        }
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(caso == "mana suficiente" ? ClientPacketId.CastSpell : ClientPacketId.ChangeHeading,
                (ClientPacketId)(await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task La_roja_es_prioritaria_y_no_encadena_una_azul_en_la_misma_decision()
    {
        var f = Armar(hp: 100, rojas: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 2 }, await Proximo(f));
            f.Sentidos.Recibir(ServerMessages.UpdateHP(200));
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task La_azul_no_reemplaza_la_retirada_si_la_roja_no_lo_saca_del_estado_critico()
    {
        var f = Armar(hp: 40, rojas: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 2 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Si_la_vida_cae_durante_la_azul_revisa_la_roja_y_la_huida_antes_de_castear()
    {
        var f = Armar(rojas: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
            f.Sentidos.Recibir(ServerMessages.UpdateMana((short)Potions.ManaRestore(2_000, 24)));
            f.Sentidos.Recibir(ServerMessages.UpdateHP(40));
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 2 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData("muerte", true)]
    [InlineData("cierre", false)]
    [InlineData("warp", true)]
    [InlineData("aliado", true)]
    [InlineData("desaparece", true)]
    public async Task La_espera_de_la_azul_no_conserva_una_decision_que_ya_es_invalida(string cambio, bool sigue)
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            switch (cambio)
            {
                case "muerte": f.Sentidos.Recibir(ServerMessages.UpdateHP(0)); break;
                case "cierre": f.Cerrada.SetResult(); break;
                case "warp": f.Sentidos.Recibir(ServerMessages.ChangeMap(37)); break;
                case "aliado": f.Sentidos.Recibir(ServerMessages.CharStatus(501, Player.StatusCiudadano)); break;
                case "desaparece": f.Sentidos.Recibir(ServerMessages.CharacterRemove(501, false)); break;
            }
            Assert.Equal(sigue, await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task La_cancelacion_durante_la_azul_no_deja_otro_uso_o_ataque_pendiente()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
        await Cancelar(pelea, corte);
        f.Transporte.Terminar();
        Assert.Empty(await Proximo(f));
    }

    [Fact]
    public async Task El_clic_del_hechizo_usa_la_posicion_del_rival_posterior_a_la_azul()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.UpdateMana((short)Potions.ManaRestore(2_000, 24)));
            f.Sentidos.Recibir(ServerMessages.CharacterMove(501, (byte)(f.X + 1), f.Y));
            Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
            var clic = await Proximo(f);
            Assert.Equal((byte)ClientPacketId.WorkLeftClick, clic[0]);
            Assert.Equal((byte)(f.X + 1), clic[1]);
            Assert.Equal(Bot.ClicEnLaCabeza(f.Y), clic[2]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(GameData Datos, Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte,
        TaskCompletionSource Cerrada, byte X, byte Y);

    private static Fixture Armar(int hp = 200, int mana = 0, int manaMaximo = 2_000, int stamina = 300,
        short hechizo = 6, bool azules = true, bool rojas = false)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-1, 3)
                            from dx in Enumerable.Range(-1, 3)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy)).All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 24,
            FactionStatus = Player.StatusCiudadano, MaxHp = 200, Hp = hp, MaxMana = manaMaximo, Mana = mana,
            MaxStamina = 300, Stamina = stamina,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeSpellSlot(1, hechizo));
        if (azules) s.Recibir(ServerMessages.ChangeInventorySlot(1, 37, 10, false, 0, 1));
        if (rojas) s.Recibir(ServerMessages.ChangeInventorySlot(2, 38, 10, false, 0, 1));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - 1)),
        }));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        return new Fixture(datos, bot, s, new TransporteDeBot("Rufo"),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), sitio.X, sitio.Y);
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
