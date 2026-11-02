using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class AutonomiaDelBancoTests
{
    private static readonly GameData Datos = GameData.Load();
    private static readonly MethodInfo AbrirBanco = typeof(Bot).GetMethod("AbrirElBancoOAnotarloAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo TieneBanco = typeof(Bot).GetMethod("TieneBanquero", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private const int Rinkel = 20;
    private const long EsperaMs = 90_000;

    private sealed class Escena
    {
        public long Ahora = 10_000;
        public Bot Bot { get; }
        public SentidosDelBot Sentidos { get; } = new();
        public TransporteDeBot Transporte { get; } = new("Osmar");
        public TaskCompletionSource Cerrada { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte X { get; }
        public byte Y { get; }

        public Escena()
        {
            var banco = Datos.MapSpawns[Rinkel].First(sp => Datos.Npc((short)sp.NpcIndex)?.NpcType == 4);
            X = (byte)banco.X; Y = (byte)banco.Y;
            var jugador = new Player
            {
                CharIndex = 500, Name = "Osmar", Account = "osmar_test", Pos = new WorldPos(Rinkel, X, (byte)(Y + 1)),
                Level = 23, Hp = 200, MaxHp = 200, Stamina = 300, MaxStamina = 300,
            };
            Sentidos.Recibir(ServerMessages.UserIndexInServer(500));
            Sentidos.Recibir(ServerMessages.UserCharIndexInServer(500));
            Sentidos.Recibir(ServerMessages.ChangeMap(Rinkel));
            Sentidos.Recibir(ServerMessages.CharacterCreate(jugador));
            Sentidos.Recibir(ServerMessages.UpdateUserStats(jugador));
            Sentidos.Recibir(ServerMessages.UpdateGold(0));
            Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = 600, Template = Datos.Npc((short)banco.NpcIndex)!, Pos = new WorldPos(Rinkel, X, Y),
            }));
            Bot = new Bot("Osmar", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, Datos, new Navegante(Datos), null!), 1);
            // En el código anterior no hay reloj y el veto nunca expira: la prueba compila y muestra ese fallo de conducta.
            typeof(Bot).GetProperty("RelojDelBanco", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(Bot, (Func<long>)(() => Ahora));
        }

        public bool PuedeIrAlBanco => (bool)TieneBanco.Invoke(Bot, new object[] { Rinkel })!;
        public bool PuedeIrAlBancoDe(int mapa) => (bool)TieneBanco.Invoke(Bot, new object[] { mapa })!;
        public Task<bool> Abrir(CancellationToken ct = default) => (Task<bool>)AbrirBanco.Invoke(Bot,
            new object[] { Transporte, Sentidos, Cerrada.Task, ct })!;

        public async Task RecibirPedido()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var b = new byte[4096];
            int n = await Transporte.ReceiveAsync(b, cts.Token);
            Assert.Equal(3, n);
            for (int i = 0; i < n; i++) b[i] ^= Transporte.Clave;
            Assert.Equal((byte)ClientPacketId.DoubleClick, b[0]);
            Assert.Equal(X, b[1]);
            Assert.Equal(Y, b[2]);
        }

        public async Task FallarLaApertura()
        {
            var apertura = Abrir();
            await RecibirPedido();
            Assert.False(await apertura.WaitAsync(TimeSpan.FromSeconds(5)));   // el mostrador no contestó
            Assert.Equal(-1, Sentidos.Banco);
            Assert.False(PuedeIrAlBanco);
        }

        public async Task AbrirConRespuesta()
        {
            var apertura = Abrir();
            await RecibirPedido();
            Sentidos.Recibir(ServerMessages.BankInit(goliath: true, bankGold: 5_000));
            Assert.True(await apertura.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(5_000, Sentidos.Banco);
        }
    }

    [Fact]
    public async Task Un_fallo_temporal_expira_y_el_mismo_bot_vuelve_a_abrir_el_banco_por_protocolo()
    {
        var e = new Escena();
        await e.FallarLaApertura();
        long fallo = e.Ahora;
        foreach (long transcurrido in new[] { 0L, 30_000L, EsperaMs - 1 })
        {
            e.Ahora = fallo + transcurrido;
            Assert.False(e.PuedeIrAlBanco);   // consultar no renueva el plazo
        }
        e.Ahora = fallo + EsperaMs;
        Assert.True(e.PuedeIrAlBanco);
        await e.AbrirConRespuesta();
        Assert.True(e.PuedeIrAlBanco);
    }

    [Fact]
    public async Task Una_apertura_confirmada_limpia_el_recuerdo_del_fallo_aunque_no_haya_vencido()
    {
        var e = new Escena();
        await e.FallarLaApertura();
        await e.AbrirConRespuesta();
        Assert.True(e.PuedeIrAlBanco);
    }

    [Fact]
    public async Task Una_conexion_que_ya_termino_no_veta_el_banco_para_la_siguiente_vida()
    {
        var e = new Escena();
        e.Cerrada.SetResult();
        Assert.False(await e.Abrir().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(e.PuedeIrAlBanco);
    }

    [Fact]
    public async Task Desconectarse_mientras_espera_el_saldo_no_se_confunde_con_un_banco_inaccesible()
    {
        var e = new Escena();
        var apertura = e.Abrir();
        await e.RecibirPedido();
        e.Cerrada.SetResult();
        Assert.False(await apertura.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(e.PuedeIrAlBanco);
    }

    [Fact]
    public async Task Un_cambio_de_mapa_no_veta_el_banco_del_destino_ni_el_del_intento()
    {
        var e = new Escena();
        Assert.True(e.PuedeIrAlBancoDe(1));
        var apertura = e.Abrir();
        await e.RecibirPedido();
        e.Sentidos.Recibir(ServerMessages.ChangeMap(1));
        Assert.False(await apertura.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(e.PuedeIrAlBanco);
        Assert.True(e.PuedeIrAlBancoDe(1));
    }

    [Fact]
    public async Task Una_cancelacion_no_veta_el_banco()
    {
        var e = new Escena();
        using var cts = new CancellationTokenSource();
        var apertura = e.Abrir(cts.Token);
        await e.RecibirPedido();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apertura);
        Assert.True(e.PuedeIrAlBanco);
    }
}
