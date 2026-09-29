using System.Collections.Concurrent;
using System.Linq;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// AL DESEQUIPAR TODO, EL SERVIDOR AVISA CADA RANURA, como el original: `Desequipar` termina en
/// `UpdateUserInventario(slot, 2, 0)` (InvUsuario.bas:827), y lo llaman `UserDie` y `DarCuerpoNuevo`. Se mira la muerte, que
/// es lo que vive un bot y cualquier jugador, con la sesión de verdad y los bytes que salen por ella.
/// </summary>
public class DesequiparAvisaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_desequipar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly PacketDispatcher _dispatcher;
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public DesequiparAvisaTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(new World(), _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _loopTask = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
        _cts.Dispose();
    }

    [Fact]
    public async Task Al_morir_el_servidor_avisa_cada_ranura_desequipada()
    {
        var transporte = new TransporteDeBot("Aldo");
        var recibidos = new ConcurrentQueue<byte[]>();
        transporte.Recibido += recibidos.Enqueue;
        var sesion = new ClientSession(transporte, _dispatcher);
        var p = new Player { CharIndex = 1, Name = "Aldo", Account = "aldo_bot", Pos = new WorldPos(1, 50, 50) };
        p.Inventory[3] = new InventorySlot { ObjIndex = 574, Amount = 1, Equipped = true };
        p.Inventory[4] = new InventorySlot { ObjIndex = 466, Amount = 1, Equipped = true };
        p.Connection = sesion;
        sesion.Player = p;

        PacketDispatcher.KillPlayer(new World(), _datos, p, "test");

        bool Avisada(byte ranura) => recibidos.Any(b => b.Length >= 3 && b[0] == (byte)ServerPacketId.ChangeInventorySlotUser
                                                        && b[1] == ranura && b[2] == 2);
        for (int i = 0; i < 250 && !(Avisada(3) && Avisada(4)); i++) await Task.Delay(20);
        Assert.False(p.Inventory[3].Equipped || p.Inventory[4].Equipped, "el servidor no desequipó: el test no mira nada");
        Assert.True(Avisada(3), "la ranura 3 se desequipó sin avisar");
        Assert.True(Avisada(4), "la ranura 4 se desequipó sin avisar");
        sesion.Close("fin del test");
    }
}
