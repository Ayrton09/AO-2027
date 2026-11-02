using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class ElDesarmeDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task Si_lo_desarman_se_repone_el_arma_que_ya_tiene_antes_de_seguir_pegando()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            await PrimerGolpe(f);
            ArmaPuesta(f, false);
            Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 3 }, await Proximo(f));
            ArmaPuesta(f, true);
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Sin_confirmacion_de_equipo_todavia_devuelve_un_golpe_en_vez_de_insistir_solo_con_equipar()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            await PrimerGolpe(f);
            ArmaPuesta(f, false);
            Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Una_confirmacion_ya_observada_no_se_confunde_con_un_rechazo_si_despues_lo_desarman()
    {
        var f = Armar(armaPuesta: false);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
            ArmaPuesta(f, true);
            await PrimerGolpe(f);
            ArmaPuesta(f, false);
            Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 3 }, await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_aceptacion_seguida_de_desarme_no_veta_el_arma_al_volver_al_ciclo_principal(bool confirmacionDelta)
    {
        var f = Armar(armaPuesta: false);
        var pedir = typeof(Bot).GetMethod("PonerseElArmaParaPelear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)pedir.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
        if (confirmacionDelta) f.Sentidos.Recibir(ServerMessages.ChangeInventorySlotUser(3, 1, 0));
        else ArmaPuesta(f, true);
        // Aceptó el pedido; el desarme posterior es otra acción.
        ArmaPuesta(f, false);
        await Task.Delay(1_600); // Venció la espera de equipo de 1,5 s.
        var equiparse = typeof(Bot).GetMethod("Equiparse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)equiparse.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 3 }, await Proximo(f));
    }

    [Fact]
    public async Task La_confirmacion_de_otra_arma_en_la_misma_ranura_no_confirma_el_pedido_anterior()
    {
        var f = Armar(armaPuesta: false);
        var pedir = typeof(Bot).GetMethod("PonerseElArmaParaPelear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)pedir.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(3, OtraArma(f), 1, true, 0, 1));
        ArmaPuesta(f, false); // El arma pedida nunca estuvo puesta.
        await Task.Delay(1_600);
        var equiparse = typeof(Bot).GetMethod("Equiparse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)equiparse.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
    }

    [Fact]
    public async Task Reemplazar_el_objeto_pedido_no_lo_veta_como_si_el_servidor_lo_hubiera_rechazado()
    {
        var f = Armar(armaPuesta: false);
        var pedir = typeof(Bot).GetMethod("PonerseElArmaParaPelear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)pedir.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(3, OtraArma(f), 1, true, 0, 1));
        await Task.Delay(1_600);
        var equiparse = typeof(Bot).GetMethod("Equiparse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)equiparse.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        ArmaPuesta(f, false);
        Assert.True((bool)equiparse.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos })!);
        Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 3 }, await Proximo(f));
    }

    [Fact]
    public async Task No_toca_el_arma_que_sigue_puesta_por_lo_que_no_la_desequipa_accidentalmente()
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            await PrimerGolpe(f);
            ArmaPuesta(f, true);
            await PrimerGolpe(f);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Al_empezar_desarmado_en_estado_critico_la_roja_va_antes_que_el_arma()
    {
        var f = Armar(armaPuesta: false, hp: 40, rojas: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Si_lo_desarman_y_queda_en_estado_critico_cura_y_huye_antes_de_rearmarse()
    {
        var f = Armar(rojas: true);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            await PrimerGolpe(f);
            ArmaPuesta(f, false);
            f.Sentidos.Recibir(ServerMessages.UpdateHP(40));
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData("muerte", true)]
    [InlineData("cierre", false)]
    [InlineData("aliado", true)]
    [InlineData("ausente", true)]
    [InlineData("warp", true)]
    public async Task Reponer_el_arma_no_conserva_una_decision_de_ataque_obsoleta(string cambio, bool sigue)
    {
        var f = Armar();
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            await PrimerGolpe(f);
            ArmaPuesta(f, false);
            Assert.Equal((byte)ClientPacketId.EquipItem, (await Proximo(f))[0]);
            switch (cambio)
            {
                case "muerte": f.Sentidos.Recibir(ServerMessages.UpdateHP(0)); break;
                case "cierre": f.Cerrada.SetResult(); break;
                case "aliado": f.Sentidos.Recibir(ServerMessages.CharStatus(501, Player.StatusCiudadano)); break;
                case "ausente": f.Sentidos.Recibir(ServerMessages.CharacterRemove(501, false)); break;
                case "warp": f.Sentidos.Recibir(ServerMessages.ChangeMap(37)); break;
            }
            Assert.Equal(sigue, await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada, short Arma);

    private static Fixture Armar(bool armaPuesta = true, int hp = 200, bool rojas = false)
    {
        var (datos, mapas) = Mundo.Value;
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        byte clase = Propio(bot, "_clase"), raza = Propio(bot, "_raza"), genero = Propio(bot, "_genero");
        var arma = datos.Objects.Values.First(o => Bot.EsArma(o) && !o.IsNewbie && Bot.PuedeUsarlo(o, clase, raza, genero, 24));
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-1, 3)
                            from dx in Enumerable.Range(-1, 3)
                            select datos.IsBlockedTile(18, x + dx, y + dy)).All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var sentidos = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, sitio.X, sitio.Y), Level = 24,
            Class = clase, Race = raza, Gender = genero, FactionStatus = Player.StatusCiudadano,
            MaxHp = 200, Hp = hp, MaxStamina = 300, Stamina = 300,
        };
        sentidos.Recibir(ServerMessages.UserIndexInServer(500));
        sentidos.Recibir(ServerMessages.ChangeMap(18));
        sentidos.Recibir(ServerMessages.CharacterCreate(yo));
        sentidos.Recibir(ServerMessages.UpdateUserStats(yo));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(3, arma.Index, 1, armaPuesta, 0, 1));
        if (rojas) sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 38, 10, false, 0, 1));
        sentidos.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(18, sitio.X, (byte)(sitio.Y - 1)),
        }));
        return new Fixture(bot, sentidos, new TransporteDeBot("Rufo"), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), arma.Index);
    }

    private static void ArmaPuesta(Fixture f, bool puesta) =>
        f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(3, f.Arma, 1, puesta, 0, 1));

    private static byte Propio(Bot bot, string campo) =>
        (byte)typeof(Bot).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bot)!;

    private static short OtraArma(Fixture f) => Mundo.Value.Datos.Objects.Values.First(o => o.Index != f.Arma && Bot.EsArma(o)
        && !o.IsNewbie && Bot.PuedeUsarlo(o, Propio(f.Bot, "_clase"), Propio(f.Bot, "_raza"), Propio(f.Bot, "_genero"), 24)).Index;

    private static async Task PrimerGolpe(Fixture f)
    {
        Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
        Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
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
