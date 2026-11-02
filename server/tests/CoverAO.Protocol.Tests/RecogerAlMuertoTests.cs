using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class RecogerAlMuertoTests
{
    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "tcp:10.0.0.7:1111";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Muerte_y_recogida_en_el_mismo_drenaje_conservan_lo_movido_despues_de_cada_commit(bool oro)
    {
        const short pocion = 38;
        int cantidad = oro ? 3_000 : 2;
        var datos = GameData.Load();
        using var real = AlmacenSqlite.EnMemoria();
        var (almacen, control) = AlmacenQueFalla.Crear(real, _ => null);
        var niveles = new Leveling(datos.LevelExp);
        using var db = new Persistence(almacen, niveles);
        var mundo = new World { OroQueSeCaeAlMorir = 100_000, OroPorMonton = 10_000 };
        var loop = new GameLoop();
        var dispatcher = new PacketDispatcher(mundo, loop, db, datos, niveles,
            serverIni: ServerIni.Desde(IniFile.Parse("[INIT]\nPersistirSuelo=1\n"), "test.ini"));
        loop.OnTickHandler = _ => dispatcher.VolcarPersistenciaDiferida();

        Player Crear(string nombre, short indice)
        {
            var cuenta = db.CreateAccount(nombre.ToLowerInvariant(), "clave_larga_de_prueba");
            var p = db.CreateCharacter(cuenta, nombre, indice, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
            p.Pos = new WorldPos(15, 50, 50);
            p.Hp = p.MaxHp = 100;
            for (int i = 1; i <= GameLimits.MaxInventorySlots; i++) p.Inventory[i] = default;
            p.Gold = 0;
            mundo.Add(p);
            return p;
        }

        var muerto = Crear("Muerto", 1);
        var recolector = Crear("Recolector", 2);
        if (oro) muerto.Gold = cantidad;
        else muerto.Inventory[1] = new InventorySlot { ObjIndex = pocion, Amount = (short)cantidad };
        db.SaveCharacter(muerto);
        db.SaveCharacter(recolector);
        Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
        Assert.True(datos.IsPkMap(15) && !datos.IsSafeZone(15, 50, 50));

        // Cada commit es un punto en el que se podría cortar el proceso. La suma persistida debe conservarse
        // incluso antes del siguiente commit, no solamente cuando terminó de vaciarse la cola.
        var totales = new ConcurrentQueue<long>();
        control.FallaDespues((metodo, _) =>
        {
            if (metodo != nameof(IAlmacen.EnUnaTransaccion)) return null;
            var fichas = new[] { real.LeerPersonaje("Muerto")!, real.LeerPersonaje("Recolector")! };
            long total = fichas.Sum(f =>
            {
                var p = f.CrearPlayer(0, "");
                return oro ? (long)p.Gold : p.Inventory.Where(s => s.ObjIndex == pocion).Sum(s => (long)s.Amount);
            });
            total += real.LeerSuelo().Where(o => o.Objeto == (oro ? GameLimits.ObjOro : pocion)).Sum(o => (long)o.Cantidad);
            totales.Enqueue(total);
            return null;
        });

        var sesion = new ClientSession(new Transporte(), dispatcher) { Player = recolector };
        recolector.Connection = sesion;
        loop.Enqueue(() => PacketDispatcher.KillPlayer(mundo, datos, muerto, "test"));
        dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.PickUp }));
        var procesado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Enqueue(() => procesado.SetResult());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            await procesado.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(db.Cola.Vaciar(TimeSpan.FromSeconds(10)));
            Assert.True(totales.Count >= 2, "Debe confirmar tanto la muerte como la recogida.");
            Assert.All(totales, total => Assert.Equal((long)cantidad, total));
            var guardado = real.LeerPersonaje("Recolector")!.CrearPlayer(0, "");
            Assert.Equal(cantidad, oro ? guardado.Gold : guardado.Inventory.Where(s => s.ObjIndex == pocion).Sum(s => s.Amount));
        }
        finally
        {
            cts.Cancel();
            await corriendo;
            sesion.Close("fin de prueba");
        }
    }
}
