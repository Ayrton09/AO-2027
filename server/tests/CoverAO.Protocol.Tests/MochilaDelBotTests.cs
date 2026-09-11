using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA MOCHILA DEL BOT SIGUE A LA DEL SERVIDOR CUANDO COME Y TOMA.
///
/// <para>Contra el despachador de verdad: el personaje usa la botella con el mismo paquete que manda un cliente, y
/// lo que el servidor contesta lo lee <see cref="SentidosDelBot"/> por el transporte del bot. Si el servidor
/// cambiara la forma de avisar lo consumido, esto se pone rojo antes de que un bot vuelva a contar botellas que ya
/// se tomó.</para>
/// </summary>
public class MochilaDelBotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_mochila_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public MochilaDelBotTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(new Dictionary<int, long>());
        _dispatcher = new PacketDispatcher(new World(), _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        // Usar un objeto corre adentro del loop, así que el loop tiene que girar de verdad.
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    [Theory]
    [InlineData((short)43)]   // Botella de Agua
    [InlineData((short)26)]   // Pollo
    public async Task Lo_que_consume_en_el_servidor_se_descuenta_en_su_mochila(short obj)
    {
        if (_datos.Obj(obj) is not ({ IsDrink: true } or { IsFood: true })) return;   // sin la data del juego no hay qué probar

        var transporte = new TransporteDeBot("Aldo");
        var sentidos = new SentidosDelBot();
        transporte.Recibido += sentidos.Recibir;
        var sesion = new ClientSession(transporte, _dispatcher);
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50), Hunger = 0, Thirst = 0 };
        p.Inventory[2] = new InventorySlot { ObjIndex = obj, Amount = 4 };
        sesion.Player = p;
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, obj, 4, false, 0f, 1));   // lo que sabe desde el login

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.UseItem).WriteByte(2).ToArray()));

        for (int i = 0; i < 250 && sentidos.CuantoTiene(o => o == obj) == 4; i++) await Task.Delay(20);
        Assert.Equal(3, p.Inventory[2].Amount);   // el servidor la consumió
        Assert.Equal(3, sentidos.CuantoTiene(o => o == obj));
        sesion.Close("fin del test");
    }
}
