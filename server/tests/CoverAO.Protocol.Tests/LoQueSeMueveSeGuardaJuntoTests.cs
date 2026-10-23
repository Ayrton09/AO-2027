using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE MUEVE ORO U OBJETOS SE GUARDA EN UNA SOLA TRANSACCIÓN (auditoría externa del 23/10): la transferencia, el correo con
/// adjunto, levantar del piso y lo que se le cae a un muerto. Eran guardados separados, en orden, y una caída entre los dos
/// perdía o duplicaba lo movido. Acá la caída es un almacén que falla en la segunda escritura: tiene que quedar todo como
/// antes, o todo como después, nunca a medias.
/// </summary>
public class LoQueSeMueveSeGuardaJuntoTests : IDisposable
{
    private static readonly GameData Datos = GameData.Load();
    private readonly AlmacenSqlite _real = AlmacenSqlite.EnMemoria();
    private readonly AlmacenQueFalla _falla;
    private readonly Persistence _db;
    private readonly World _world = new() { OroQueSeCaeAlMorir = 100_000, OroPorMonton = 10_000 };
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private short _indice = 1;

    private const short Pocion = 38;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "tcp:10.0.0.7:1111";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public LoQueSeMueveSeGuardaJuntoTests()
    {
        var leveling = new Leveling(Datos.LevelExp);
        (var almacen, _falla) = AlmacenQueFalla.Crear(_real, _ => null);
        _db = new Persistence(almacen, leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, Datos, leveling,
            serverIni: ServerIni.Desde(IniFile.Parse("[INIT]\nPersistirSuelo=1\n"), "test.ini"));
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        _db.Dispose();
        _real.Dispose();
    }

    /// <summary>Un personaje creado en la base y conectado, en el mapa 15 (inseguro) en (50,50).</summary>
    private (Player, ClientSession) Conectado(string nombre, byte x = 50, byte y = 50)
    {
        var cuenta = _db.CreateAccount(nombre.ToLowerInvariant(), "clave_larga_de_prueba");
        var p = _db.CreateCharacter(cuenta, nombre, _indice++, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        p.Pos = new WorldPos(15, x, y);
        p.Hp = p.MaxHp = 100;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++) p.Inventory[i] = default;   // sin el kit: se cuentan pociones
        _db.SaveCharacter(p);
        var sesion = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        _db.EsperarEscriturasPendientes();
        return (p, sesion);
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        await Task.Delay(300);
        _db.EsperarEscriturasPendientes();
    }

    private Player Guardado(string nombre)
    {
        _db.EsperarEscriturasPendientes();
        return _db.LoadCharacter(nombre, 0, nombre.ToLowerInvariant())!;
    }

    /// <summary>La segunda vez que se llama <paramref name="metodo"/>, el almacén falla con un error que no se reintenta.</summary>
    private void FallaLaSegunda(string metodo)
    {
        int n = 0;
        _falla.Falla(m => m == metodo && Interlocked.Increment(ref n) == 2 ? new InvalidOperationException("se cortó a la mitad") : null);
    }

    private static int Cuantas(Player p, short objeto) =>
        p.Inventory.Where(s => s.ObjIndex == objeto).Sum(s => s.Amount);

    // ── La transferencia ───────────────────────────────────────────────────────────────────────────────────────

    private static ByteQueueWriter Transferir(string a, int monto) =>
        new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD).WriteAsciiString(a).WriteLong(monto);

    [Fact]
    public async Task La_transferencia_entre_conectados_entra_entera()
    {
        var (uno, s) = Conectado("Emisor");
        var (dos, _) = Conectado("Receptor", 52, 50);
        uno.Bank = 5_000;
        _db.SaveCharacter(uno);
        await Mandar(s, Transferir("Receptor", 1_000));
        Assert.Equal(4_000, Guardado("Emisor").Bank);
        Assert.Equal(1_000, Guardado("Receptor").Bank);
    }

    [Fact]
    public async Task La_transferencia_entre_conectados_que_falla_a_la_mitad_no_pierde_el_oro()
    {
        var (uno, s) = Conectado("Emisor");
        Conectado("Receptor", 52, 50);
        uno.Bank = 5_000;
        _db.SaveCharacter(uno);
        _db.EsperarEscriturasPendientes();
        FallaLaSegunda(nameof(IAlmacen.GuardarPersonaje));
        await Mandar(s, Transferir("Receptor", 1_000));
        // Las dos fichas o ninguna: guardadas de a una, el que manda quedaba sin el oro y el otro sin recibirlo.
        long emisor = Guardado("Emisor").Bank, receptor = Guardado("Receptor").Bank;
        Assert.Equal(5_000, emisor + receptor);
        Assert.Equal(5_000, emisor);
    }

    [Fact]
    public async Task La_transferencia_a_un_desconectado_que_falla_a_la_mitad_no_pierde_el_oro()
    {
        var (uno, s) = Conectado("Emisor");
        var (dos, _) = Conectado("Ausente", 52, 50);
        _world.Remove(dos.CharIndex);
        uno.Bank = 5_000;
        _db.SaveCharacter(uno);
        _db.EsperarEscriturasPendientes();
        // La ficha del que manda entra y la suma a la bóveda del otro falla: la transacción se deshace entera.
        FallaLaSegunda(nameof(IAlmacen.AcreditarBoveda));   // la primera mira el saldo de antes
        await Mandar(s, Transferir("Ausente", 1_000));
        _falla.Falla(_ => null);
        Assert.Equal(5_000, Guardado("Emisor").Bank);
        Assert.Equal(0, Guardado("Ausente").Bank);
        Assert.Equal(5_000, uno.Bank);   // y en memoria el oro volvió
    }

    [Fact]
    public async Task La_transferencia_a_un_desconectado_entra_entera()
    {
        var (uno, s) = Conectado("Emisor");
        var (dos, _) = Conectado("Ausente", 52, 50);
        _world.Remove(dos.CharIndex);
        uno.Bank = 5_000;
        _db.SaveCharacter(uno);
        await Mandar(s, Transferir("Ausente", 1_000));
        Assert.Equal(4_000, Guardado("Emisor").Bank);
        Assert.Equal(1_000, Guardado("Ausente").Bank);
    }

    // ── El correo con adjunto ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task El_adjunto_que_falla_a_la_mitad_no_se_pierde()
    {
        var (uno, s) = Conectado("Remitente");
        Conectado("Destino", 52, 50);
        uno.Gold = 10_000;
        uno.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        _db.SaveCharacter(uno);
        _db.EsperarEscriturasPendientes();
        _falla.Falla(m => m == nameof(IAlmacen.GuardarCasillas) ? new InvalidOperationException("se cortó a la mitad") : null);
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.EnviarCorreo).WriteAsciiString("Destino")
            .WriteAsciiString("hola").WriteInteger(1).WriteInteger(5));
        _falla.Falla(_ => null);
        // Guardado de a uno, la ficha salía sin las pociones y el buzón no: se perdían.
        Assert.Equal(5, Cuantas(Guardado("Remitente"), Pocion));
        Assert.Empty(_db.LoadMailboxes().GetValueOrDefault("Destino") ?? new List<MailEntry>());
    }

    [Fact]
    public async Task El_adjunto_entra_entero_al_buzon_y_sale_de_la_mochila()
    {
        var (uno, s) = Conectado("Remitente");
        Conectado("Destino", 52, 50);
        uno.Gold = 10_000;
        uno.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        _db.SaveCharacter(uno);
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.EnviarCorreo).WriteAsciiString("Destino")
            .WriteAsciiString("hola").WriteInteger(1).WriteInteger(5));
        // Sin esperar al volcado diferido del correo: va junto con la ficha.
        Assert.Equal(0, Cuantas(Guardado("Remitente"), Pocion));
        var carta = Assert.Single(_db.LoadMailboxes()["Destino"]);
        Assert.Equal(Pocion, carta.ObjIndex);
        Assert.Equal((short)5, carta.Amount);
    }

    // ── El suelo ───────────────────────────────────────────────────────────────────────────────────────────────

    private ObjetoEnElSuelo? BaldosaGuardada(WorldPos pos)
    {
        _db.EsperarEscriturasPendientes();
        return _db.LoadSuelo().Where(o => o.Pos == pos).Select(o => (ObjetoEnElSuelo?)new ObjetoEnElSuelo(o.Pos, o.ObjIndex, o.Amount))
            .FirstOrDefault();
    }

    [Fact]
    public async Task Levantar_guarda_la_mochila_y_la_baldosa_juntas()
    {
        var (p, s) = Conectado("Juntador");
        _world.SetGround(p.Pos, Pocion, 3);
        _db.SaveSuelo(_world.SueloNoFijo());
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.PickUp));
        Assert.Equal(3, Cuantas(p, Pocion));
        // Sin el volcado diferido del suelo: la ficha guardada ya tiene las pociones y la baldosa guardada ya no.
        Assert.Equal(3, Cuantas(Guardado("Juntador"), Pocion));
        Assert.Null(BaldosaGuardada(p.Pos));
    }

    [Fact]
    public async Task Levantar_que_falla_a_la_mitad_no_duplica()
    {
        var (p, s) = Conectado("Juntador");
        _world.SetGround(p.Pos, Pocion, 3);
        _db.SaveSuelo(_world.SueloNoFijo());
        _db.EsperarEscriturasPendientes();
        _falla.Falla(m => m == nameof(IAlmacen.GuardarBaldosas) ? new InvalidOperationException("se cortó a la mitad") : null);
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.PickUp));
        _falla.Falla(_ => null);
        // La baldosa no se pudo guardar: la ficha tampoco, o las pociones quedaban en el piso y en la mochila.
        Assert.Equal(0, Cuantas(Guardado("Juntador"), Pocion));
        Assert.NotNull(BaldosaGuardada(p.Pos));
    }

    [Fact]
    public async Task Tirar_guarda_la_mochila_y_la_baldosa_juntas()
    {
        var (p, s) = Conectado("Tirador");
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 4 };
        _db.SaveCharacter(p);
        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.Drop).WriteByte(1).WriteLong(4));
        Assert.Equal(0, Cuantas(Guardado("Tirador"), Pocion));
        Assert.Equal((short)4, BaldosaGuardada(p.Pos)?.Cantidad);
    }

    [Fact]
    public void Lo_que_se_le_cae_al_muerto_se_guarda_con_su_ficha()
    {
        if (Datos.ItemCount == 0) return;
        var (p, _) = Conectado("Muerto");
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 2 };
        p.Gold = 3_000;
        _db.SaveCharacter(p);
        _db.EsperarEscriturasPendientes();
        Assert.True(Datos.IsPkMap(15) && !Datos.IsSafeZone(15, 50, 50));   // la premisa: ahí se cae todo

        _loop.Enqueue(() =>
        {
            PacketDispatcher.KillPlayer(_world, Datos, p, "test");
            // La vuelta siguiente, como el loop: sólo lo que guarda junto (el volcado del suelo entero no le toca todavía).
            _falla.Falla(m => m == nameof(IAlmacen.GuardarSuelo) ? new InvalidOperationException("no es esto lo que se mide") : null);
            _dispatcher.VolcarPersistenciaDiferida();
        });
        Thread.Sleep(300);
        var guardado = Guardado("Muerto");
        _falla.Falla(_ => null);
        Assert.Equal(0, Cuantas(guardado, Pocion));
        Assert.Equal(0, guardado.Gold);
        var suelo = _db.LoadSuelo();
        Assert.Equal(2, suelo.Where(o => o.ObjIndex == Pocion).Sum(o => o.Amount));
        Assert.Equal(3_000, suelo.Where(o => o.ObjIndex == GameLimits.ObjOro).Sum(o => o.Amount));
    }
}
