using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class ElViajePerteneceAlGrupoDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private const BindingFlags Privado = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Invitar = typeof(Bot).GetMethod("ContestarLaInvitacion", Privado)!;
    private static readonly MethodInfo Charlar = typeof(Bot).GetMethod("Charlar", Privado)!;
    private static readonly MethodInfo Acompanar = typeof(Bot).GetMethod("AcompanarAsync", Privado)!;
    private static readonly FieldInfo Estado = typeof(Bot).GetField("_acompana", Privado)!;

    [Theory]
    [InlineData("Beto")]
    [InlineData("Ana")]
    [InlineData(null)]
    public async Task Una_salida_terminada_no_sigue_caminando_hacia_su_destino_viejo(string? nuevaPersona)
    {
        var (bot, s, t) = Crear();
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Invitar.Invoke(bot, new object[] { s, "Ana", "vamos?", Environment.TickCount64 });
        var tarea = Vuelta(bot, s, t, fin.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.Walk, (await Leer(t, fin.Token))[0]);
            var ultimoPasoVigente = s.Posicion;
            // El primer paso ya salió. Su espera atiende la charla; el chau sí debe terminar esta salida.
            s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Ana", "chau, gracias"), PacketDispatcher.FuenteSusurro));
            Charlar.Invoke(bot, new object[] { t, s });
            Assert.Null(bot.AcompanaA);
            if (nuevaPersona is not null)
                Invitar.Invoke(bot, new object[] { s, nuevaPersona, "vamos?", Environment.TickCount64 });
            object? salidaNueva = Estado.GetValue(bot);

            Assert.True(await tarea.WaitAsync(TimeSpan.FromSeconds(6)));
            t.Terminar();
            var restantes = await Restantes(t, fin.Token);
            Assert.DoesNotContain(restantes, p => p[0] == (byte)ClientPacketId.Walk);
            Assert.Equal(ultimoPasoVigente, s.Posicion);
            Assert.Equal(salidaNueva, Estado.GetValue(bot)); // Ni el contador de la salida nueva hereda el viaje anterior.
            Assert.Equal(nuevaPersona, bot.AcompanaA);
        }
        finally
        {
            fin.Cancel(); t.Terminar();
            try { await tarea; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task El_chau_recibido_mientras_espera_un_paso_cancela_el_siguiente_sin_atender_la_charla_desde_el_test()
    {
        var (bot, s, t) = Crear();
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Invitar.Invoke(bot, new object[] { s, "Ana", "vamos?", Environment.TickCount64 });
        var tarea = Vuelta(bot, s, t, fin.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.Walk, (await Leer(t, fin.Token))[0]);
            var ultimoPasoVigente = s.Posicion;
            // Llega durante la espera del primer paso. La propia caminata debe leerlo y detenerse.
            s.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Ana", "chau, gracias"), PacketDispatcher.FuenteSusurro));

            Assert.True(await tarea.WaitAsync(TimeSpan.FromSeconds(6)));
            t.Terminar();
            Assert.Null(bot.AcompanaA);
            Assert.DoesNotContain(await Restantes(t, fin.Token), p => p[0] == (byte)ClientPacketId.Walk);
            Assert.Equal(ultimoPasoVigente, s.Posicion);
        }
        finally
        {
            fin.Cancel(); t.Terminar();
            try { await tarea; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Sin_cambiar_de_grupo_sigue_hasta_la_persona_que_lo_invito()
    {
        var (bot, s, t) = Crear();
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Invitar.Invoke(bot, new object[] { s, "Ana", "vamos?", Environment.TickCount64 });
        var tarea = Vuelta(bot, s, t, fin.Token);
        try
        {
            Assert.True(await tarea.WaitAsync(TimeSpan.FromSeconds(6)));
            t.Terminar();
            var pasos = (await Restantes(t, fin.Token)).Where(p => p[0] == (byte)ClientPacketId.Walk).ToArray();
            Assert.Equal(4, pasos.Length);
            Assert.All(pasos, p => Assert.Equal((byte)3, p[1]));
            Assert.Equal(((byte)75, (byte)59), s.Posicion);
            Assert.Equal("Ana", bot.AcompanaA);
        }
        finally
        {
            fin.Cancel(); t.Terminar();
            try { await tarea; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deja_el_destino_viejo_si_la_persona_se_mueve_o_sale_de_vista(bool desaparece)
    {
        var (bot, s, t) = Crear();
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Invitar.Invoke(bot, new object[] { s, "Ana", "vamos?", Environment.TickCount64 });
        var tarea = Vuelta(bot, s, t, fin.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.Walk, (await Leer(t, fin.Token))[0]);
            var ultimoPaso = s.Posicion;
            // La compañera vuelve hacia el bot, o cruza fuera de vista mientras él espera el paso.
            s.Recibir(desaparece ? ServerMessages.CharacterRemove(501, false)
                                : ServerMessages.CharacterMove(501, 76, 55));
            Assert.True(await tarea.WaitAsync(TimeSpan.FromSeconds(6)));
            t.Terminar();
            Assert.DoesNotContain(await Restantes(t, fin.Token), p => p[0] == (byte)ClientPacketId.Walk);
            Assert.Equal(ultimoPaso, s.Posicion);
            Assert.Equal("Ana", bot.AcompanaA);
            Assert.Equal(0, ((ValueTuple<string, long, long, long, int>)Estado.GetValue(bot)!).Item5);
        }
        finally
        {
            fin.Cancel(); t.Terminar();
            try { await tarea; } catch (OperationCanceledException) { }
        }
    }

    private static Task<bool?> Vuelta(Bot bot, SentidosDelBot s, TransporteDeBot t, CancellationToken ct) =>
        (Task<bool?>)Acompanar.Invoke(bot, new object[] { t, s, (Func<short, bool>)(_ => false), true, new TaskCompletionSource().Task, ct })!;

    private static (Bot Bot, SentidosDelBot S, TransporteDeBot T) Crear()
    {
        var (datos, mapas) = Mundo.Value;
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(20, 75, 55), Level = 20,
            Hp = 200, MaxHp = 200, Mana = 0, MaxMana = 0, Stamina = 300, MaxStamina = 300, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500)); s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(yo)); s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Ana", Account = "ana_test", Pos = new WorldPos(20, 75, 61), FactionStatus = Player.StatusCiudadano,
        }));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 502, Name = "Beto", Account = "beto_test", Pos = new WorldPos(20, 76, 55), FactionStatus = Player.StatusCiudadano,
        }));
        return (new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1), s, new TransporteDeBot("Bruma"));
    }

    private static async Task<byte[]> Leer(TransporteDeBot t, CancellationToken ct)
    {
        var buffer = new byte[4096]; int n = await t.ReceiveAsync(buffer, ct);
        return buffer.Take(n).Select(b => (byte)(b ^ t.Clave)).ToArray();
    }
    private static async Task<List<byte[]>> Restantes(TransporteDeBot t, CancellationToken ct)
    {
        var paquetes = new List<byte[]>();
        for (byte[] p; (p = await Leer(t, ct)).Length > 0;) paquetes.Add(p);
        return paquetes;
    }
}
