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
    private static readonly FieldInfo UltimaRespuesta = Campo("_ultimaRespuestaA");
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
        ((Dictionary<string, long>)UltimaRespuesta.GetValue(bot)!)[siguiente] = Environment.TickCount64 - Bot.EntreRespuestasAlMismoMs;
        Invitar(bot, sentidos, transporte, siguiente);
        VencerLasRespuestas(bot);
        Charlar.Invoke(bot, new object[] { transporte, sentidos });

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

    private static (Bot, SentidosDelBot, TransporteDeBot) Armar()
    {
        var sentidos = new SentidosDelBot { MiNombre = "Bruma" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(20, 75, 55), Level = 20,
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
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
        return (bot, sentidos, new TransporteDeBot("Bruma"));
    }

    private static MethodInfo Metodo(string nombre) => typeof(Bot).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static FieldInfo Campo(string nombre) => typeof(Bot).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
