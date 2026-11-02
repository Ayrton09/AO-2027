using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class RetiroPendienteDelBotTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private static readonly MethodInfo Retirar = typeof(Bot).GetMethod("RetirarLoDeLaSubastaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Pendientes = typeof(Bot).GetField("_porRetirar", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_rechazo_por_mochila_llena_o_un_timeout_conserva_el_retiro(bool rechazoExplicito)
    {
        await using var e = new Escena();
        var visita = await e.Abrir();
        await e.Proximo(ClientPacketId.BankExtractItem);
        if (rechazoExplicito) e.Sentidos.Recibir(ServerMessages.LocaleMsg(25));
        await e.Terminar(visita);

        Assert.Contains(e.Objeto, e.PorRetirar);
        Assert.Equal(1, e.Sentidos.EnLaBoveda(e.Objeto)!.Value.Cantidad);
        Assert.Equal(0, e.Sentidos.CuantoTiene(o => o == e.Objeto));
    }

    [Fact]
    public async Task Un_retiro_confirmado_desaparece_de_la_lista_pendiente()
    {
        await using var e = new Escena();
        var visita = await e.Abrir();
        await e.Proximo(ClientPacketId.BankExtractItem);
        e.Confirmar(1);
        await e.Terminar(visita);

        Assert.DoesNotContain(e.Objeto, e.PorRetirar);
        Assert.Null(e.Sentidos.EnLaBoveda(e.Objeto));
        Assert.Equal(1, e.Sentidos.CuantoTiene(o => o == e.Objeto));
    }

    [Fact]
    public async Task Un_retiro_parcial_no_olvida_lo_que_sigue_en_la_boveda()
    {
        await using var e = new Escena();
        e.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, e.Objeto, 9_999, false, 0, 1));
        var visita = await e.Abrir(cantidad: 2);
        await e.Proximo(ClientPacketId.BankExtractItem);
        e.Sentidos.Recibir(ServerMessages.ChangeBankSlot(1, e.Objeto, 1, 1));
        e.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, e.Objeto, 10_000, false, 0, 1));
        e.Sentidos.Recibir(ServerMessages.BankOk());
        await e.Terminar(visita);

        Assert.Contains(e.Objeto, e.PorRetirar);
        Assert.Equal(1, e.Sentidos.EnLaBoveda(e.Objeto)!.Value.Cantidad);
    }

    [Fact]
    public async Task Despues_del_rechazo_vuelve_a_pedirlo_y_una_tercera_visita_no_lo_duplica()
    {
        await using var e = new Escena();
        var primera = await e.Abrir();
        await e.Proximo(ClientPacketId.BankExtractItem);
        e.Sentidos.Recibir(ServerMessages.LocaleMsg(25));
        await e.Terminar(primera);
        Assert.Contains(e.Objeto, e.PorRetirar);

        var segunda = await e.Abrir();
        var extraer = await e.Proximo(ClientPacketId.BankExtractItem);
        Assert.Equal(1, extraer[1]);
        Assert.Equal(1, BitConverter.ToInt16(extraer, 2));
        e.Confirmar(1);
        await e.Terminar(segunda);
        await e.Invocar().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(e.PorRetirar);
        Assert.Equal(1, e.Sentidos.CuantoTiene(o => o == e.Objeto));
        Assert.Null(e.Sentidos.EnLaBoveda(e.Objeto));
        Assert.Empty(await e.CerrarYLeerRestantes());
    }

    [Fact]
    public async Task Una_boveda_vacia_confirmada_suelta_el_pendiente_sin_pedir_un_retiro()
    {
        await using var e = new Escena();
        var visita = await e.Abrir(cantidad: 0);
        await e.Terminar(visita);

        Assert.Empty(e.PorRetirar);
        Assert.Empty(await e.CerrarYLeerRestantes());
    }

    [Fact]
    public async Task Un_exito_tardio_se_reconcilia_con_la_boveda_nueva_y_no_se_pide_dos_veces()
    {
        await using var e = new Escena();
        var primera = await e.Abrir();
        await e.Proximo(ClientPacketId.BankExtractItem);
        await e.Terminar(primera); // La respuesta al pedido no llegó dentro de la espera.
        Assert.Contains(e.Objeto, e.PorRetirar);
        e.Confirmar(1);

        var segunda = await e.Abrir(cantidad: 0);
        await e.Terminar(segunda);

        Assert.Empty(e.PorRetirar);
        Assert.Equal(1, e.Sentidos.CuantoTiene(o => o == e.Objeto));
        Assert.Empty(await e.CerrarYLeerRestantes());
    }

    private sealed class Escena : IAsyncDisposable
    {
        public Bot Bot { get; }
        public SentidosDelBot Sentidos { get; } = new() { MiNombre = "Bruma" };
        public TransporteDeBot Transporte { get; } = new("Bruma");
        public HashSet<short> PorRetirar { get; }
        public short Objeto { get; }
        private readonly CancellationTokenSource _fin = new(TimeSpan.FromSeconds(30));
        private readonly Task _cerrada = new TaskCompletionSource().Task;
        private readonly List<Task> _visitas = new();

        public Escena()
        {
            var datos = Datos.Value;
            var banquero = datos.MapSpawns[20].First(n => datos.Npc(n.NpcIndex)?.NpcType == 4);
            Objeto = datos.Objects.Values.First(o => CoverAO.Server.Net.Bot.VaALaSubasta(o) && !o.IsWeapon && !o.IsArmor && !o.IsShield && !o.IsHelmet
                && !o.IsFood && !o.IsDrink && !o.IsPotion && !o.IsGold && o.IsPickable).Index;
            var yo = new Player
            {
                CharIndex = 500, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(20, (byte)banquero.X, (byte)(banquero.Y + 2)),
                Hp = 200, MaxHp = 200, Stamina = 300, MaxStamina = 300, Level = 23,
            };
            Sentidos.Recibir(ServerMessages.UserIndexInServer(500));
            Sentidos.Recibir(ServerMessages.ChangeMap(20));
            Sentidos.Recibir(ServerMessages.CharacterCreate(yo));
            Sentidos.Recibir(ServerMessages.UpdateUserStats(yo));
            Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
            {
                CharIndex = 600, Template = datos.Npc(banquero.NpcIndex)!, Pos = new WorldPos(20, (byte)banquero.X, (byte)banquero.Y),
            }));
            Bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
            PorRetirar = (HashSet<short>)Pendientes.GetValue(Bot)!;
            PorRetirar.Add(Objeto);
        }

        public Task Invocar()
        {
            var tarea = (Task)Retirar.Invoke(Bot, new object[] { Transporte, Sentidos, _cerrada, _fin.Token })!;
            _visitas.Add(tarea);
            return tarea;
        }

        public async Task<Task> Abrir(short cantidad = 1)
        {
            Task visita = Invocar();
            await Proximo(ClientPacketId.DoubleClick);
            Sentidos.Recibir(ServerMessages.BankInit(true, 1_000));
            await Proximo(ClientPacketId.BankStart);
            Sentidos.Recibir(ServerMessages.ChangeBankSlot(1, cantidad > 0 ? Objeto : (short)0, cantidad, 1));
            Sentidos.Recibir(ServerMessages.BankInit(false));
            return visita;
        }

        public void Confirmar(short cantidad)
        {
            Sentidos.Recibir(ServerMessages.ChangeBankSlot(1, 0, 0, 1));
            Sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, Objeto, cantidad, false, 0, 1));
            Sentidos.Recibir(ServerMessages.BankOk());
        }

        public async Task Terminar(Task visita)
        {
            await Proximo(ClientPacketId.BankEnd);
            await visita.WaitAsync(TimeSpan.FromSeconds(8));
        }

        public async Task<byte[]> Proximo(ClientPacketId id)
        {
            var buffer = new byte[4096];
            using var plazo = CancellationTokenSource.CreateLinkedTokenSource(_fin.Token);
            plazo.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                int n = await Transporte.ReceiveAsync(buffer, plazo.Token);
                if (n == 0) throw new InvalidOperationException("Transporte cerrado esperando " + id);
                var p = buffer.Take(n).Select(b => (byte)(b ^ Transporte.Clave)).ToArray();
                Assert.Equal((byte)id, p[0]); // No se saltean retiros inesperados al esperar el cierre.
                return p;
            }
        }

        public async Task<List<byte[]>> CerrarYLeerRestantes()
        {
            Transporte.Terminar();
            var restantes = new List<byte[]>();
            var buffer = new byte[4096];
            for (int n; (n = await Transporte.ReceiveAsync(buffer, _fin.Token)) > 0;)
                restantes.Add(buffer.Take(n).Select(b => (byte)(b ^ Transporte.Clave)).ToArray());
            return restantes;
        }

        public async ValueTask DisposeAsync()
        {
            _fin.Cancel(); Transporte.Terminar();
            try { await Task.WhenAll(_visitas).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            finally { _fin.Dispose(); }
        }
    }
}
