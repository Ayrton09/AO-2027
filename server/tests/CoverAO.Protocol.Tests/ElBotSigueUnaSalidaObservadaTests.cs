using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class ElBotSigueUnaSalidaObservadaTests
{
    private const BindingFlags Privado = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Lazy<(GameData D, Navegante N)> Mundo = new(() => { var d = GameData.Load(); return (d, new Navegante(d)); });
    private static readonly MethodInfo Invitar = typeof(Bot).GetMethod("ContestarLaInvitacion", Privado)!;
    private static readonly MethodInfo Charlar = typeof(Bot).GetMethod("Charlar", Privado)!;
    private static readonly MethodInfo Acompanar = typeof(Bot).GetMethod("AcompanarAsync", Privado)!;
    private static readonly FieldInfo Estado = typeof(Bot).GetField("_acompana", Privado)!;

    [Fact]
    public async Task Sigue_el_ultimo_move_y_remove_por_la_salida_publica_sin_renovar_vista_en_por_inferencia()
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona();
        long vistaAntes = ((ValueTuple<string, long, long, long, int>)Estado.GetValue(e.Bot)!).Item3;
        await Task.Delay(20); // Separa el tick de la observación del inicio de cualquier inferencia.
        var viaje = e.Vuelta();
        for (int paso = 0; paso < 3; paso++) Assert.Equal((byte)4, (await e.PasoEsperado())[1]);
        e.S.Recibir(ServerMessages.ChangeMap(98)); e.S.Recibir(ServerMessages.PosUpdate(90, 68));
        Assert.True(await viaje.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.Null(e.S.PersonaLlamada("Ana")); // El servidor aún no volvió a mostrarla en el mapa nuevo.
        var estado = ((ValueTuple<string, long, long, long, int>)Estado.GetValue(e.Bot)!);
        Assert.Equal(vistaAntes, estado.Item3);
        Assert.Equal("Ana", e.Bot.AcompanaA);
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Fact]
    public async Task Una_huella_no_reintenta_el_viaje_si_un_peligro_interrumpio_el_primer_acercamiento()
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona();
        var viaje = e.Vuelta();
        await e.PasoEsperado();
        e.MostrarPeligro();
        Assert.True(await viaje.WaitAsync(TimeSpan.FromSeconds(5)));
        e.S.Recibir(ServerMessages.CharacterRemove(700, faded: true));
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal("Ana", e.Bot.AcompanaA);
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ana")]
    [InlineData("Beto")]
    public async Task Despedirse_o_reinvitar_durante_el_acercamiento_cancela_los_pasos_de_la_salida_anterior(string? nuevo)
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona();
        var viaje = e.Vuelta();
        await e.PasoEsperado();
        e.Despedir();
        if (nuevo is not null) e.InvitarDeNuevo(nuevo);
        object? estadoNuevo = Estado.GetValue(e.Bot);
        Assert.True(await viaje.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(estadoNuevo, Estado.GetValue(e.Bot));
        Assert.Equal(nuevo, e.Bot.AcompanaA);
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Theory]
    [InlineData(37, 57, 74)]
    [InlineData(20, 50, 50)]
    public async Task Cualquier_warp_durante_el_acercamiento_termina_el_intento_y_no_vuelve_al_punto_anterior(short mapa, byte x, byte y)
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona();
        var viaje = e.Vuelta();
        await e.PasoEsperado();
        if (mapa != e.S.Mapa) e.S.Recibir(ServerMessages.ChangeMap(mapa));
        e.S.Recibir(ServerMessages.PosUpdate(x, y));
        Assert.True(await viaje.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Theory]
    [InlineData("sin_move")]
    [InlineData("fuera_de_salida")]
    [InlineData("sin_remove")]
    [InlineData("vieja")]
    [InlineData("mapa_anterior")]
    public async Task No_infiere_un_cruce_sin_ultimo_move_reciente_a_una_salida_y_remove_posterior(string caso)
    {
        await using var e = new Escena();
        if (caso == "sin_move") e.MostrarPersona("Ana", e.Salida.X, e.Salida.Y);
        else if (caso == "fuera_de_salida") e.S.Recibir(ServerMessages.CharacterMove(501, (byte)(e.Salida.X + 1), e.Salida.Y));
        else e.MoveALaSalida();
        if (caso == "vieja") await Task.Delay(6_000); // La huella dura 5 s; no se cambia un reloj productivo antes de la roja.
        if (caso != "sin_remove") e.BorrarPersona();
        if (caso == "mapa_anterior") { e.S.Recibir(ServerMessages.ChangeMap(98)); e.S.Recibir(ServerMessages.PosUpdate(90, 68)); }
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ana")]
    [InlineData("Beto")]
    public async Task Una_huella_de_otro_grupo_no_se_reutiliza_al_despedirse_o_aceptar_otra_salida(string? nuevo)
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona(); e.Despedir();
        if (nuevo is not null) e.InvitarDeNuevo(nuevo);
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(nuevo, e.Bot.AcompanaA);
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task No_se_mete_en_el_cruce_muerto_o_con_la_vida_en_rojo(short vida)
    {
        await using var e = new Escena();
        e.MoveALaSalida(); e.BorrarPersona(); e.S.Recibir(ServerMessages.UpdateHP(vida));
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    [Fact]
    public async Task No_sigue_una_salida_publica_que_lleva_a_guardias_que_lo_atacan()
    {
        await using var e = new Escena(conGuardia: true);
        Assert.True(Bot.HayGuardiaQueVaPor(e.Salida.Destino, Player.StatusCiudadano, 23, e.D, e.Ini));
        e.MoveALaSalida(); e.BorrarPersona();
        await e.Vuelta().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(await e.Restantes(), p => p[0] == (byte)ClientPacketId.Walk);
    }

    private sealed record SalidaPublica(short Mapa, byte X, byte Y, short Destino);
    private sealed class Escena : IAsyncDisposable
    {
        public GameData D { get; }
        public ServerIni Ini { get; } = new();
        public Bot Bot { get; }
        public SentidosDelBot S { get; } = new() { MiNombre = "Bruma" };
        public TransporteDeBot T { get; } = new("Bruma");
        public SalidaPublica Salida { get; }
        private readonly CancellationTokenSource _fin = new(TimeSpan.FromSeconds(25));
        private readonly Task _cerrada = new TaskCompletionSource().Task;
        private readonly List<Task> _tareas = new();

        public Escena(bool conGuardia = false)
        {
            var mundo = Mundo.Value; D = mundo.D;
            Salida = conGuardia ? D.MapExits.Where(e => e.Key.X >= 9 && e.Key.X <= 89 && e.Key.Y >= 7 && e.Key.Y <= 94
                && CoverAO.Server.Net.Bot.HayGuardiaQueVaPor(e.Value.DestMap, Player.StatusCiudadano, 23, D, Ini)
                && Enumerable.Range(e.Key.X, 4).All(x => !D.IsBlockedTile(e.Key.Map, x, e.Key.Y) && !D.IsWater(e.Key.Map, x, e.Key.Y))
                && Enumerable.Range(e.Key.X + 1, 3).All(x => !D.MapExits.ContainsKey((e.Key.Map, x, e.Key.Y))))
                .Select(e => new SalidaPublica((short)e.Key.Map, (byte)e.Key.X, (byte)e.Key.Y, (short)e.Value.DestMap)).First()
                : new SalidaPublica(20, 9, 69, 98);
            Assert.Equal(Salida.Destino, D.MapExits[(Salida.Mapa, Salida.X, Salida.Y)].DestMap);
            var yo = new Player
            {
                CharIndex = 500, Name = "Bruma", Account = "bruma_qa", FactionStatus = Player.StatusCiudadano,
                Pos = new WorldPos(Salida.Mapa, (byte)(Salida.X + 3), Salida.Y), Level = conGuardia ? (byte)23 : (byte)12,
                Hp = 200, MaxHp = 200, Stamina = 300, MaxStamina = 300, Mana = 0, MaxMana = 0,
            };
            S.Recibir(ServerMessages.UserIndexInServer(500)); S.Recibir(ServerMessages.ChangeMap(Salida.Mapa));
            S.Recibir(ServerMessages.CharacterCreate(yo)); S.Recibir(ServerMessages.UpdateUserStats(yo));
            Bot = new Bot("Bruma", null, new EntornoDeBots(null!, Ini, 0, 0, D, mundo.N, null!), 1);
            typeof(Bot).GetField("_miBando", Privado)!.SetValue(Bot, Player.StatusCiudadano);
            typeof(Bot).GetField("_miNivel", Privado)!.SetValue(Bot, (int)yo.Level);
            MostrarPersona("Ana", (byte)(Salida.X + 1), Salida.Y);
            Invitar.Invoke(Bot, new object[] { S, "Ana", "vamos?", Environment.TickCount64 });
            Assert.Equal("Ana", Bot.AcompanaA);
        }
        public void MostrarPersona(string nombre, byte x, byte y) => S.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = nombre == "Ana" ? (short)501 : (short)502, Name = nombre, Account = nombre + "_qa", FactionStatus = Player.StatusCiudadano,
            Pos = new WorldPos(Salida.Mapa, x, y), Hp = 200, MaxHp = 200,
        }));
        public void MoveALaSalida() => S.Recibir(ServerMessages.CharacterMove(501, Salida.X, Salida.Y));
        public void BorrarPersona() => S.Recibir(ServerMessages.CharacterRemove(501, faded: true));
        public void Despedir()
        {
            S.Recibir(ServerMessages.ConsoleMsg(ServerMessages.LineaDeChat("Ana", "chau gracias"), PacketDispatcher.FuenteSusurro));
            Charlar.Invoke(Bot, new object[] { T, S }); Assert.Null(Bot.AcompanaA);
        }
        public void InvitarDeNuevo(string nombre)
        {
            MostrarPersona(nombre, (byte)(Salida.X + 1), Salida.Y);
            Invitar.Invoke(Bot, new object[] { S, nombre, "vamos?", Environment.TickCount64 });
            Assert.Equal(nombre, Bot.AcompanaA);
        }
        public void MostrarPeligro()
        {
            var ficha = D.Npcs.Values.First(n => n.Hostile && n.Attackable && n.MaxHit >= 200 && n.GiveExp > 0);
            S.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = 700, Template = ficha, Pos = new WorldPos(Salida.Mapa, (byte)(Salida.X + 2), (byte)(Salida.Y - 1)),
            }));
        }
        public Task<bool?> Vuelta()
        {
            var t = (Task<bool?>)Acompanar.Invoke(Bot, new object[] { T, S, (Func<short, bool>)(_ => false), true, _cerrada, _fin.Token })!;
            _tareas.Add(t); return t;
        }
        public async Task<byte[]> PasoEsperado()
        {
            using var plazo = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token); plazo.CancelAfter(2_500);
            var buffer = new byte[4096];
            try
            {
                int n = await T.ReceiveAsync(buffer, plazo.Token);
                var p = buffer.Take(n).Select(b => (byte)(b ^ T.Clave)).ToArray();
                Assert.NotEmpty(p); Assert.Equal((byte)ClientPacketId.Walk, p[0]); return p;
            }
            catch (OperationCanceledException) when (!_fin.IsCancellationRequested)
            { throw new Xunit.Sdk.XunitException("Esperaba el primer paso hacia la salida observada; el bot sólo esperó al perder a la persona"); }
        }
        public async Task<List<byte[]>> Restantes()
        {
            T.Terminar(); var todos = new List<byte[]>(); var buffer = new byte[4096];
            for (int n; (n = await T.ReceiveAsync(buffer, _fin.Token)) > 0;) todos.Add(buffer.Take(n).Select(b => (byte)(b ^ T.Clave)).ToArray());
            return todos;
        }
        public async ValueTask DisposeAsync()
        {
            _fin.Cancel(); T.Terminar();
            try { await Task.WhenAll(_tareas).WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            finally { _fin.Dispose(); }
        }
    }
}
