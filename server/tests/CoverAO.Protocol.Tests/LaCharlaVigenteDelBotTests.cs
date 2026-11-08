using System.Collections;
using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Como = CoverAO.Server.Net.SentidosDelBot.ComoLoDijo;

namespace CoverAO.Protocol.Tests;

public sealed class LaCharlaVigenteDelBotTests
{
    private static readonly MethodInfo Charlar = Metodo("Charlar");
    private static readonly FieldInfo Acompana = Campo("_acompana");
    private static readonly FieldInfo PorContestar = Campo("_porContestar");
    private static readonly FieldInfo Oido = typeof(SentidosDelBot).GetField("_oido", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Lazy<GameData> Datos = new(GameData.Load);

    [Fact]
    public void UnChauViejoNoCancelaElAcompanamientoActual()
    {
        var (bot, sentidos, transporte) = Armar();
        long ahora = Environment.TickCount64;
        Acompana.SetValue(bot, ("Ana", ahora + 600_000, ahora, ahora, 0));
        Oir(sentidos, "Ana", "chau", ahora - Bot.OidoViejoMs - 1);

        Charlar.Invoke(bot, new object[] { transporte, sentidos });

        Assert.Equal("Ana", bot.AcompanaA);
        Assert.Empty(Pendientes(bot));
    }

    [Fact]
    public async Task UnChauRecienteCancelaLaConfirmacionQueTodaviaNoSalio()
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        Hablar(bot, sentidos, transporte, "Ana", "chau");
        Assert.Null(bot.AcompanaA);

        VencerLasRespuestas(bot);
        Charlar.Invoke(bot, new object[] { transporte, sentidos });

        Assert.Empty(await RecibirTodo(transporte));
    }

    [Fact]
    public async Task DejarDeAcompanarPorSuCuentaTampocoMandaUnSiObsoleto()
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        Metodo("DejarDeAcompanar").Invoke(bot, new object[] { transporte, "Ana", true });
        VencerLasRespuestas(bot);
        Charlar.Invoke(bot, new object[] { transporte, sentidos });

        var mensajes = await RecibirTodo(transporte);
        var unico = Assert.Single(mensajes);
        Assert.Equal(ClientPacketId.Talk, unico.Id);
        Assert.Contains(unico.Texto, Bot.FrasesSeVa);
    }

    [Theory]
    [InlineData("Ana")]
    [InlineData("Beto")]
    public async Task UnaInvitacionNuevaConservaSoloSuPropiaConfirmacion(string siguiente)
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        Hablar(bot, sentidos, transporte, "Ana", "chau");
        // Simula que ya pasó el intervalo de respuesta sin dormir ni cambiar el cupo por minuto.
        long ahora = Environment.TickCount64 + Bot.EntreRespuestasAlMismoMs;
        Oir(sentidos, siguiente, "vamos a cazar?", ahora);
        bot.AtenderCharla(transporte, sentidos, ahora);
        Assert.Equal(siguiente, bot.AcompanaA);
        VencerLasRespuestas(bot);
        bot.AtenderCharla(transporte, sentidos, ahora + 1);

        var unico = Assert.Single(await RecibirTodo(transporte));
        Assert.Equal(ClientPacketId.Whisper, unico.Id);
        Assert.Equal(siguiente, unico.A);
        Assert.Contains(unico.Texto, Bot.FrasesAcepta);
    }

    [Fact]
    public async Task CancelarLaInvitacionNoDescartaLasRespuestasNormales()
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        Hablar(bot, sentidos, transporte, "Beto", "hola");
        Assert.Equal(2, Pendientes(bot).Count);
        Hablar(bot, sentidos, transporte, "Ana", "chau");
        VencerLasRespuestas(bot);
        Charlar.Invoke(bot, new object[] { transporte, sentidos });

        var unico = Assert.Single(await RecibirTodo(transporte));
        Assert.Equal("Beto", unico.A);
        Assert.Contains(unico.Texto, Bot.Frases[Bot.DeQueHabla.Saludo]);
    }

    [Fact]
    public async Task ContestaUnaConsultaDespuesDelSaludoYSuprimeLaPreguntaRepetida()
    {
        var (bot, sentidos, transporte) = Armar();
        long ahora = Environment.TickCount64;
        Oir(sentidos, "Ana", "hola", ahora);
        bot.AtenderCharla(transporte, sentidos, ahora);
        Oir(sentidos, "Ana", "q nv sos", ahora + 5_000);
        bot.AtenderCharla(transporte, sentidos, ahora + 5_000);
        Oir(sentidos, "Ana", "nivel?", ahora + 10_000);
        bot.AtenderCharla(transporte, sentidos, ahora + 10_000);
        bot.AtenderCharla(transporte, sentidos, ahora + 15_000);

        var mensajes = await RecibirTodo(transporte);
        Assert.Equal(2, mensajes.Count);
        Assert.Contains(mensajes[0].Texto, Bot.Frases[Bot.DeQueHabla.Saludo]);
        Assert.Contains("20", mensajes[1].Texto);
        Assert.All(mensajes, r => Assert.Equal("Ana", r.A));
    }

    [Fact]
    public async Task DiceElMapaActualPorSusurroYNoLlevaLaRespuestaLocalAlMapaSiguiente()
    {
        var (bot, sentidos, transporte) = Armar();
        long ahora = Environment.TickCount64;
        sentidos.Recibir(ServerMessages.ChatOverHead("Bruma, qué nivel tenés?", 501, (byte)Como.Charla));
        Oir(sentidos, "Beto", "dnd tas", ahora);
        bot.AtenderCharla(transporte, sentidos, ahora + 1);
        Assert.Equal(2, Pendientes(bot).Count);
        sentidos.Recibir(ServerMessages.ChangeMap(34));
        // Aun si vuelve a ver a la misma persona, la charla anterior quedó en el otro mapa.
        sentidos.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Ana", Account = "ana_test", Pos = new WorldPos(34, 75, 56),
        }));
        bot.AtenderCharla(transporte, sentidos, ahora + 10_000);

        var respuesta = Assert.Single(await RecibirTodo(transporte));
        Assert.Equal(ClientPacketId.Whisper, respuesta.Id);
        Assert.Equal("Beto", respuesta.A);
        Assert.Contains("mapa 34", respuesta.Texto);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LasRespuestasLocalesDescartadasLiberanElCupoParaElSusurro(bool cambiaDeMapa)
    {
        var (bot, sentidos, transporte) = Armar();
        for (short id = 503; id <= 504; id++)
            sentidos.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = id, Name = $"Jugador{id}", Account = $"jugador{id}", Pos = new WorldPos(20, 74, 55),
            }));
        for (short id = 501; id <= 504; id++)
            sentidos.Recibir(ServerMessages.ChatOverHead("Bruma q lvl", id, (byte)Como.Charla));
        long ahora = Environment.TickCount64;
        bot.AtenderCharla(transporte, sentidos, ahora);
        Assert.Equal(Bot.RespuestasPorMinuto, Pendientes(bot).Count);

        Oir(sentidos, "Luz", "q lvl", ahora + 1);
        bot.AtenderCharla(transporte, sentidos, ahora + 1);
        Assert.Equal(Bot.RespuestasPorMinuto, Pendientes(bot).Count);

        if (cambiaDeMapa)
            sentidos.Recibir(ServerMessages.ChangeMap(34));
        else
            for (short id = 501; id <= 504; id++)
                sentidos.Recibir(ServerMessages.CharacterRemove(id, false));

        Oir(sentidos, "Ana", "q lvl", ahora + 2);
        Oir(sentidos, "Luz", "q lvl", ahora + 2);
        bot.AtenderCharla(transporte, sentidos, ahora + 2);
        Assert.Equal(2, Pendientes(bot).Count);
        bot.AtenderCharla(transporte, sentidos, ahora + 10_000);

        var respuestas = await RecibirTodo(transporte);
        Assert.Equal(new[] { "Ana", "Luz" }, respuestas.Select(r => r.A));
        Assert.All(respuestas, r => Assert.Equal(ClientPacketId.Whisper, r.Id));
    }

    [Fact]
    public async Task UnaDespedidaLiberaCupoParaElSiguienteMensajeDelMismoLote()
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        long ahora = Environment.TickCount64;
        foreach (string quien in new[] { "Beto", "Luz", "Sol" })
            Oir(sentidos, quien, "q lvl", ahora);
        bot.AtenderCharla(transporte, sentidos, ahora);
        Assert.Equal(Bot.RespuestasPorMinuto, Pendientes(bot).Count);

        Oir(sentidos, "Ana", "chau", ahora + 1);
        Oir(sentidos, "Juan", "q lvl", ahora + 1);
        bot.AtenderCharla(transporte, sentidos, ahora + 1);
        bot.AtenderCharla(transporte, sentidos, ahora + 10_000);

        Assert.Null(bot.AcompanaA);
        Assert.Equal(new[] { "Beto", "Luz", "Sol", "Juan" }, (await RecibirTodo(transporte)).Select(r => r.A));
    }

    [Fact]
    public async Task RespondeSobreSuOficioYUnaPreguntaDeGrupoNoIniciaUnaSalida()
    {
        var (bot, sentidos, transporte) = Armar("Quim");
        Campo("_clase").SetValue(bot, (byte)14);
        long ahora = Environment.TickCount64;
        Oir(sentidos, "Ana", "tas en party", ahora);
        Oir(sentidos, "Beto", "q haces", ahora);
        bot.AtenderCharla(transporte, sentidos, ahora);
        bot.AtenderCharla(transporte, sentidos, ahora + 10_000);

        Assert.Null(bot.AcompanaA);
        var mensajes = await RecibirTodo(transporte);
        Assert.Contains(mensajes, r => r.A == "Ana" && r.Texto == "por ahora ando por mi cuenta");
        Assert.Contains(mensajes, r => r.A == "Beto" && r.Texto.Contains("mineral"));
    }

    [Fact]
    public async Task EsperaPorOrdenDelCompaneroYAlSeguirAyudaContraElBlancoObservado()
    {
        var (bot, sentidos, transporte) = Armar();
        Invitar(bot, sentidos, transporte, "Ana");
        var plantilla = new NpcInfo(32_000, "Blanco", 1, 1, 3, 100, 0, 0, 0, 0, 0, 0, 0);
        foreach (var (id, x) in new[] { ((short)510, (byte)74), ((short)511, (byte)76) })
            sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = id, Template = plantilla, Pos = new WorldPos(20, x, 55),
            }));
        // Sólo cuenta lo que vio atacar a Ana, aunque haya otro objetivo igual de cerca.
        sentidos.ObservarAtaqueACriatura(501, 511, Environment.TickCount64);
        sentidos.ObservarAtaqueACriatura(502, 510, Environment.TickCount64);
        var fin = new TaskCompletionSource();
        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        Task<bool?> Acompanar() => (Task<bool?>)Metodo("AcompanarAsync").Invoke(bot,
            [transporte, sentidos, (Func<short, bool>)(_ => true), true, fin.Task, tiempo.Token])!;
        sentidos.Recibir(ServerMessages.ChatOverHead("espera", 501, (byte)Como.Charla));
        bot.AtenderCharla(transporte, sentidos, Environment.TickCount64 + 1);
        Assert.True(await Acompanar());
        using (var pausa = new CancellationTokenSource(100))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await transporte.ReceiveAsync(new byte[4096], pausa.Token));
        sentidos.Recibir(ServerMessages.ChatOverHead("segui", 501, (byte)Como.Charla));
        bot.AtenderCharla(transporte, sentidos, Environment.TickCount64 + 1);
        var pelea = Acompanar();
        try
        {
            var buffer = new byte[4096];
            int n = await transporte.ReceiveAsync(buffer, tiempo.Token);
            Assert.Equal(2, n);
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (byte)(buffer[0] ^ transporte.Clave));
            Assert.Equal(SentidosDelBot.RumboHacia(1, 0), (byte)(buffer[1] ^ transporte.Clave));
            n = await transporte.ReceiveAsync(buffer, tiempo.Token);
            Assert.Equal(1, n);
            Assert.Equal((byte)ClientPacketId.attack, (byte)(buffer[0] ^ transporte.Clave));
            sentidos.Recibir(ServerMessages.ChatOverHead("espera", 501, (byte)Como.Charla));
            Assert.True(await pelea.WaitAsync(tiempo.Token));
            Assert.Equal("Ana", bot.AcompanaA);
        }
        finally
        {
            fin.TrySetResult();
            tiempo.Cancel();
            try { await pelea; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaInvitacionInterrumpeLaCompraOpcionalEnElCaminoOAlAbrirLaBoveda(bool usaElBanco)
    {
        var (bot, sentidos, transporte) = Armar();
        Campo("_miBando").SetValue(bot, Player.StatusCiudadano);
        Campo("_clase").SetValue(bot, GameLimits.ClaseGuerrero);
        Campo("_raza").SetValue(bot, (byte)1);
        Campo("_genero").SetValue(bot, (byte)1);
        int oroEnMano = usaElBanco ? 500 : 100_000;
        sentidos.Recibir(ServerMessages.UpdateGold(oroEnMano));
        // Pan, agua, pociones, antídoto, ropa y un arma básica. Sólo sale para mejorar el equipo.
        foreach (var (ranura, obj, cantidad) in new (byte, short, short)[]
                 { (1, 24, 200), (2, 43, 200), (3, 38, 200), (4, 166, 20), (5, 31, 1), (6, 15, 1) })
            sentidos.Recibir(ServerMessages.ChangeInventorySlot(ranura, obj, cantidad, ranura >= 5, 0, 1));
        sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = 510, Template = Datos.Value.Npc(20)!, Pos = new WorldPos(20, usaElBanco ? (byte)77 : (byte)80, 55),
        }));
        if (usaElBanco)
        {
            var banquero = Datos.Value.MapSpawns[20].First(s => Datos.Value.Npc(s.NpcIndex)?.NpcType == 4);
            sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = 511, Template = Datos.Value.Npc(banquero.NpcIndex)!, Pos = new WorldPos(20, 74, 55),
            }));
            sentidos.Recibir(ServerMessages.BankInit(goliath: true, bankGold: 100_000));
        }

        Assert.True((bool)Metodo("LeVendenAca").Invoke(bot, [sentidos.Mapa])!);
        Assert.True((bool)Metodo("ValeElViajePorEso").Invoke(bot, [sentidos, (short)2, 99_000d])!);
        var antes = sentidos.Posicion;
        if (!usaElBanco) Oir(sentidos, "Ana", "vamos a cazar", Environment.TickCount64);
        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var compra = (Task<bool>)Metodo("IrDeComprasAsync").Invoke(bot,
            [transporte, sentidos, new TaskCompletionSource().Task, tiempo.Token])!;
        if (usaElBanco)
        {
            var buffer = new byte[4096];
            Assert.Equal(3, await transporte.ReceiveAsync(buffer, tiempo.Token));
            Assert.Equal((byte)ClientPacketId.DoubleClick, (byte)(buffer[0] ^ transporte.Clave));
            Assert.Equal(74, buffer[1] ^ transporte.Clave);
            sentidos.Recibir(ServerMessages.ChatOverHead("Bruma vamos a cazar", 501, (byte)Como.Charla));
            sentidos.Recibir(ServerMessages.BankInit(goliath: true, bankGold: 100_000));
        }
        Assert.True(await compra);

        Assert.Equal("Ana", bot.AcompanaA);
        Assert.Equal(antes, sentidos.Posicion);
        Assert.Equal(oroEnMano, sentidos.Oro);
        Assert.Empty(await RecibirTodo(transporte));
    }

    [Fact]
    public async Task AtiendeUnaInvitacionEntreUnaVentaYLaSiguienteOperacion()
    {
        var (bot, sentidos, transporte) = Armar();
        Campo("_miBando").SetValue(bot, Player.StatusCiudadano);
        Campo("_clase").SetValue(bot, GameLimits.ClaseGuerrero);
        Campo("_raza").SetValue(bot, (byte)1);
        Campo("_genero").SetValue(bot, (byte)1);
        sentidos.Recibir(ServerMessages.UpdateGold(100_000));
        // Tiene comida, agua y equipo, pero necesita reponer pociones.
        foreach (var (ranura, obj, cantidad) in new (byte, short, short)[] { (1, 24, 50), (2, 43, 50), (3, 31, 1), (4, 15, 1) })
            sentidos.Recibir(ServerMessages.ChangeInventorySlot(ranura, obj, cantidad, ranura >= 3, 0, 1));
        var botin = Datos.Value.Objects.Values.First(o => o.Value is > 0 and < Bot.ValorParaSubastar
            && !o.IsNewbie && !o.IsWeapon && !o.IsArmor && !o.IsShield && !o.IsHelmet
            && !o.IsFood && !o.IsDrink && !o.IsPotion && !o.IsGold);
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(5, botin.Index, 1, false, 0, 1));
        sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = 510, Template = Datos.Value.Npc(31)!, Pos = new WorldPos(20, 77, 55),
        }));

        using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var comercio = (Task<bool>)Metodo("IrDeComprasAsync").Invoke(bot,
            [transporte, sentidos, new TaskCompletionSource().Task, tiempo.Token])!;
        var buffer = new byte[4096];
        Assert.Equal(3, await transporte.ReceiveAsync(buffer, tiempo.Token));
        Assert.Equal((byte)ClientPacketId.DoubleClick, (byte)(buffer[0] ^ transporte.Clave));
        sentidos.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 100, 20, 38));
        sentidos.Recibir(ServerMessages.CommerceInit());
        Assert.Equal(4, await transporte.ReceiveAsync(buffer, tiempo.Token));
        Assert.Equal((byte)ClientPacketId.CommerceSell, (byte)(buffer[0] ^ transporte.Clave));
        Assert.Equal(5, buffer[1] ^ transporte.Clave);
        sentidos.Recibir(ServerMessages.ChatOverHead("Bruma vamos a cazar", 501, (byte)Como.Charla));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(5, 0, 0, false, 0, 0));

        Assert.True(await comercio);
        Assert.Equal("Ana", bot.AcompanaA);
        Assert.Equal(-1, Campo("_plataQueNoAlcanzo").GetValue(bot));
        Assert.Equal(1, await transporte.ReceiveAsync(buffer, tiempo.Token));
        Assert.Equal((byte)ClientPacketId.CommerceEnd, (byte)(buffer[0] ^ transporte.Clave));
        Assert.Empty(await RecibirTodo(transporte));
    }

    private static void Invitar(Bot bot, SentidosDelBot sentidos, TransporteDeBot transporte, string quien)
    {
        Hablar(bot, sentidos, transporte, quien, "vamos a cazar?");
        Assert.Equal(quien, bot.AcompanaA);
        Assert.NotEmpty(Pendientes(bot));
    }

    private static void Hablar(Bot bot, SentidosDelBot sentidos, TransporteDeBot transporte, string quien, string texto)
    {
        Oir(sentidos, quien, texto, Environment.TickCount64);
        Charlar.Invoke(bot, new object[] { transporte, sentidos });
    }

    private static void Oir(SentidosDelBot sentidos, string quien, string texto, long cuando)
        => ((Queue<SentidosDelBot.LoOido>)Oido.GetValue(sentidos)!).Enqueue(new(quien, texto, Como.Susurro, cuando));

    private static IList Pendientes(Bot bot) => (IList)PorContestar.GetValue(bot)!;

    private static void VencerLasRespuestas(Bot bot)
    {
        // Adelanta solamente la fecha de entrega de las respuestas ya programadas; no depende del reloj ni de Task.Delay.
        var pendientes = Pendientes(bot);
        for (int i = 0; i < pendientes.Count; i++)
        {
            object r = pendientes[i]!;
            r.GetType().GetProperty("Cuando")!.SetValue(r, Environment.TickCount64 - 1);
            pendientes[i] = r;
        }
    }

    private static async Task<List<(ClientPacketId Id, string? A, string Texto)>> RecibirTodo(TransporteDeBot transporte)
    {
        transporte.Terminar();
        var mensajes = new List<(ClientPacketId, string?, string)>();
        var buffer = new byte[4096];
        for (int n; (n = await transporte.ReceiveAsync(buffer, CancellationToken.None)) != 0;)
        {
            var paquete = buffer.Take(n).Select(b => (byte)(b ^ transporte.Clave)).ToArray();
            var r = new ByteQueueReader(paquete);
            var id = (ClientPacketId)r.ReadByte();
            Assert.True(id is ClientPacketId.Talk or ClientPacketId.Whisper);
            string? a = id == ClientPacketId.Whisper ? r.ReadAsciiString() : null;
            mensajes.Add((id, a, r.ReadAsciiString()));
        }
        return mensajes;
    }

    private static (Bot, SentidosDelBot, TransporteDeBot) Armar(string nombreBot = "Bruma")
    {
        var sentidos = new SentidosDelBot { MiNombre = nombreBot };
        var yo = new Player
        {
            CharIndex = 500, Name = nombreBot, Account = "bot_test", Pos = new WorldPos(20, 75, 55), Level = 20,
            Hp = 200, MaxHp = 200, Stamina = 300, MaxStamina = 300, FactionStatus = Player.StatusCiudadano,
        };
        sentidos.Recibir(ServerMessages.UserIndexInServer(500));
        sentidos.Recibir(ServerMessages.ChangeMap(20));
        sentidos.Recibir(ServerMessages.CharacterCreate(yo));
        sentidos.Recibir(ServerMessages.UpdateUserStats(yo));
        foreach (var (id, nombre) in new[] { ((short)501, "Ana"), ((short)502, "Beto") })
            sentidos.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = id, Name = nombre, Account = nombre.ToLowerInvariant() + "_test", Pos = new WorldPos(20, 75, 56),
            }));
        var datos = Datos.Value;
        var bot = new Bot(nombreBot, null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
        return (bot, sentidos, new TransporteDeBot(nombreBot));
    }

    private static MethodInfo Metodo(string nombre) => typeof(Bot).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static FieldInfo Campo(string nombre) => typeof(Bot).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
