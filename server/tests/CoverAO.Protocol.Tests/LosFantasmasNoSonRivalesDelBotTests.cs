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

    [Theory]
    [InlineData(18, Player.StatusRepublicano, 1, 51, true)]
    [InlineData(18, Player.StatusMilicia, 1, 51, true)]
    [InlineData(18, Player.StatusArmada, 1, 51, false)]
    [InlineData(18, Player.StatusRepublicano, PacketDispatcher.CorpseBody, 51, false)]
    [InlineData(18, Player.StatusRepublicano, 1, 59, false)]
    [InlineData(20, Player.StatusRepublicano, 1, 51, false)]
    public void Solo_un_rival_vivo_al_alcance_en_zona_pvp_interrumpe_la_caza(short mapa, byte bando, short cuerpo, byte x, bool interrumpe)
    {
        var e = Crear(cuerpo, mapa, bando);
        e.Sentidos.Recibir(ServerMessages.CharacterMove(501, x, 50));
        Assert.Equal(interrumpe, e.Bot.RivalQueInterrumpeCaza(e.Sentidos) is not null);
    }

    [Theory]
    [InlineData(24, 0, 200, 50, ClientPacketId.LeftClick)]
    [InlineData(24, 24, 200, 50, ClientPacketId.ChangeHeading)]
    [InlineData(24, 31, 200, 50, ClientPacketId.Walk)]
    [InlineData(24, 24, 130, 50, ClientPacketId.Walk)]
    [InlineData(24, 24, 200, 0, ClientPacketId.Walk)]
    [InlineData(19, 19, 200, 50, ClientPacketId.Walk)]
    public async Task Ante_un_rival_mira_pelea_o_se_aleja_segun_su_preparacion(
        byte nivel, byte nivelRival, int vida, short pociones, ClientPacketId primerPaquete)
    {
        var e = Crear(1, nivel: nivel, vida: vida, pociones: pociones);
        if (nivelRival != 0)
        {
            e.Rival.Level = nivelRival;
            e.Sentidos.Recibir(ServerMessages.CharMsgStatus(e.Rival, e.Propio, 0));
        }
        var rival = e.Bot.RivalQueInterrumpeCaza(e.Sentidos);
        Assert.NotNull(rival);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var decision = e.Bot.AtenderRivalAsync(e.Transporte, e.Sentidos, rival.Value, new TaskCompletionSource().Task, fin.Token);
        try
        {
            Assert.Equal((byte)primerPaquete, (await Siguiente(e, fin.Token))[0]);
            if (primerPaquete == ClientPacketId.ChangeHeading)
                Assert.Equal((byte)ClientPacketId.attack, (await Siguiente(e, fin.Token))[0]);
            if (primerPaquete == ClientPacketId.Walk)
                Assert.True(e.Sentidos.Posicion.X < 50 || e.Sentidos.Posicion.Y != 50);
        }
        finally
        {
            fin.Cancel();
            try { await decision; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Deja_de_golpear_a_un_npc_al_ver_un_rival_sin_esperar_a_que_le_pegue()
    {
        var e = Crear(1);
        e.Sentidos.Recibir(ServerMessages.CharacterRemove(501, false));
        var ficha = Mundo.Value.Datos.Npc(504)!;
        e.Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = 502, Template = ficha, Hp = ficha.MaxHp, Pos = new WorldPos(18, 49, 50),
        }));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var pelea = Cazar(e, fin.Token, 502, aGente: false);
        try
        {
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Siguiente(e, fin.Token))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Siguiente(e, fin.Token))[0]);
            e.Sentidos.Recibir(ServerMessages.CharacterCreate(e.Rival));
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            e.Transporte.Terminar();
            Assert.Empty(await Siguiente(e, fin.Token));
        }
        finally
        {
            fin.Cancel();
            try { await pelea; } catch (OperationCanceledException) { }
        }
    }

    private sealed record Escena(Bot Bot, SentidosDelBot Sentidos, Player Propio, Player Rival, TransporteDeBot Transporte);

    private static Escena Crear(short cuerpo, short mapa = 18, byte bandoRival = Player.StatusCriminal,
                                byte nivel = 24, int vida = 200, short pociones = 50)
    {
        var (datos, mapas) = Mundo.Value;
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_qa", FactionStatus = Player.StatusCiudadano, Level = nivel,
            Hp = vida, MaxHp = 200, Stamina = 300, MaxStamina = 300, Pos = new WorldPos(mapa, 50, 50),
        };
        var rival = new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_qa", FactionStatus = bandoRival, Body = cuerpo,
            Pos = new WorldPos(mapa, 51, 50),
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        if (pociones > 0) s.Recibir(ServerMessages.ChangeInventorySlot(1, 38, pociones, false, 0, 1));
        s.Recibir(ServerMessages.CharacterCreate(rival));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
        return new Escena(bot, s, yo, rival, new TransporteDeBot("Rufo"));
    }

    private static short? Elegir(Escena e) =>
        ((ValueTuple<short, bool>?)typeof(Bot).GetMethod("AQuienSaleAPegarle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object?[] { e.Sentidos, null }))?.Item1;

    private static short? Defender(Escena e) =>
        (short?)typeof(Bot).GetMethod("AQuienSeDefiende", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object[] { e.Sentidos });

    private static Task<bool> Cazar(Escena e, CancellationToken ct, short objetivo = 501, bool aGente = true) =>
        (Task<bool>)typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(e.Bot, new object[] { e.Transporte, e.Sentidos, objetivo, new TaskCompletionSource().Task, ct, aGente })!;

    private static async Task<byte[]> Siguiente(Escena e, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int n = await e.Transporte.ReceiveAsync(buffer, ct);
        return buffer.Take(n).Select(b => (byte)(b ^ e.Transporte.Clave)).ToArray();
    }
}
