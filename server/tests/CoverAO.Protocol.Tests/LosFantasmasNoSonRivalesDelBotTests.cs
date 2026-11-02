using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class LosFantasmasNoSonRivalesDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    [Theory]
    [InlineData(PacketDispatcher.CorpseBody)]
    [InlineData(PacketDispatcher.GhostShipBody)]
    public void No_elige_un_fantasma_que_ya_aparecio_muerto(short cuerpo)
    {
        var e = Crear(cuerpo);
        Assert.Null(Elegir(e));
        e.Sentidos.Recibir(ServerMessages.UpdateHP(180));
        e.Sentidos.Recibir(ServerMessages.LocaleMsg(34, "Kael%20"));
        Assert.Null(Defender(e));
    }

    [Theory]
    [InlineData(PacketDispatcher.CorpseBody)]
    [InlineData(PacketDispatcher.GhostShipBody)]
    public void Actualiza_la_muerte_y_la_resurreccion_con_el_cambio_de_apariencia(short cuerpo)
    {
        var e = Crear(1);
        Assert.Equal((short)501, Elegir(e));
        e.Rival.Body = cuerpo;
        e.Sentidos.Recibir(ServerMessages.CharacterChange(e.Rival));
        Assert.Null(Elegir(e));
        e.Rival.Body = 1;
        e.Sentidos.Recibir(ServerMessages.CharacterChange(e.Rival));
        Assert.Equal((short)501, Elegir(e));
    }

    [Fact]
    public void Un_slot_de_arma_no_confunde_la_vida_y_el_slot_de_cuerpo_si_actualiza_la_apariencia()
    {
        var e = Crear(PacketDispatcher.CorpseBody);
        e.Sentidos.Recibir(ServerMessages.CharacterChangeSlot(501, 1, 4));
        Assert.Null(Elegir(e));
        e.Sentidos.Recibir(ServerMessages.CharacterChangeSlot(501, 1, 1));
        Assert.Equal((short)501, Elegir(e));
        e.Sentidos.Recibir(ServerMessages.CharacterChangeSlot(501, PacketDispatcher.CorpseBody, 4));
        Assert.Equal((short)501, Elegir(e));
    }

    [Theory]
    [InlineData(PacketDispatcher.CorpseBody)]
    [InlineData(PacketDispatcher.GhostShipBody)]
    public async Task Abandona_la_pelea_si_el_rival_muere_aunque_siga_a_la_vista(short cuerpo)
    {
        var e = Crear(1);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pelea = Cazar(e, fin.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Siguiente(e, fin.Token))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Siguiente(e, fin.Token))[0]);
            e.Rival.Body = cuerpo;
            e.Sentidos.Recibir(ServerMessages.CharacterChange(e.Rival));
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(3)));
            e.Transporte.Terminar();
            Assert.Empty(await Siguiente(e, fin.Token));
        }
        finally
        {
            fin.Cancel();
            try { await pelea; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(PacketDispatcher.CorpseBody)]
    [InlineData(PacketDispatcher.GhostShipBody)]
    public async Task No_empieza_a_pegarle_a_un_fantasma_ya_visible(short cuerpo)
    {
        var e = Crear(cuerpo);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var pelea = Cazar(e, fin.Token);
        try
        {
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(3)));
            e.Transporte.Terminar();
            Assert.Empty(await Siguiente(e, fin.Token));
        }
        finally
        {
            fin.Cancel();
            try { await pelea; } catch (OperationCanceledException) { }
        }
    }

    private sealed record Escena(Bot Bot, SentidosDelBot Sentidos, Player Rival, TransporteDeBot Transporte);

    private static Escena Crear(short cuerpo)
    {
        var (datos, mapas) = Mundo.Value;
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_qa", FactionStatus = Player.StatusCiudadano, Level = 24,
            Hp = 200, MaxHp = 200, Stamina = 300, MaxStamina = 300, Pos = new WorldPos(18, 50, 50),
        };
        var rival = new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_qa", FactionStatus = Player.StatusCriminal, Body = cuerpo,
            Pos = new WorldPos(18, 51, 50),
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(18));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 38, 50, false, 0, 1));
        s.Recibir(ServerMessages.CharacterCreate(rival));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
        return new Escena(bot, s, rival, new TransporteDeBot("Rufo"));
    }

    private static short? Elegir(Escena e) =>
        ((ValueTuple<short, bool>?)typeof(Bot).GetMethod("AQuienSaleAPegarle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object[] { e.Sentidos }))?.Item1;

    private static short? Defender(Escena e) =>
        (short?)typeof(Bot).GetMethod("AQuienSeDefiende", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object[] { e.Sentidos });

    private static Task<bool> Cazar(Escena e, CancellationToken ct) =>
        (Task<bool>)typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object[] { e.Transporte, e.Sentidos, (short)501, new TaskCompletionSource().Task, ct, true })!;

    private static async Task<byte[]> Siguiente(Escena e, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int n = await e.Transporte.ReceiveAsync(buffer, ct);
        return buffer.Take(n).Select(b => (byte)(b ^ e.Transporte.Clave)).ToArray();
    }
}
