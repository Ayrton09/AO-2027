using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class NombresDeStaffReservadosTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_staff_reservado_" + Guid.NewGuid().ToString("N"));
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly ServerIni _ini;
    private readonly PacketDispatcher _dispatcher;
    private readonly List<ClientSession> _sesiones = new();
    private const string Clave = "clave_larga_de_prueba";

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "tcp:10.0.0.3:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }

        public IEnumerable<string> Avisos() => Recibidos
            .Where(p => p.Length > 0 && p[0] is (byte)ServerPacketId.ShowMessageBox or (byte)ServerPacketId.SendMsgBox)
            .Select(p => { var r = new ByteQueueReader(p); r.ReadByte(); return r.ReadAsciiString(); });
    }

    public NombresDeStaffReservadosTests()
    {
        Directory.CreateDirectory(_root);
        string ruta = Path.Combine(_root, "Server.ini");
        File.WriteAllText(ruta, "[INIT]\nMaxUsers=0\n");
        _ini = ServerIni.Cargar(ruta);
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling, serverIni: _ini);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        foreach (var sesion in _sesiones) sesion.Close("fin del test");
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _db.Dispose();
        _cts.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Transporte Mandar(ByteQueueWriter paquete)
    {
        var t = new Transporte();
        var s = new ClientSession(t, _dispatcher);
        _sesiones.Add(s);
        _dispatcher.HandleOne(s, new ByteQueueReader(paquete.ToArray()));
        return t;
    }

    private Transporte Crear(string cuenta, string nombre) => Mandar(new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.LoginNewChar).WriteAsciiString(cuenta).WriteAsciiString(Clave)
        .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString(nombre)
        .WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1).WriteAsciiString("mac").WriteLong(1));

    private Transporte Borrar(string cuenta, string nombre) => Mandar(new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.BorrarPersonaje).WriteAsciiString(cuenta).WriteAsciiString(Clave).WriteAsciiString(nombre));

    private async Task<T> EnElLoop<T>(Func<T> hacer)
    {
        var listo = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => listo.SetResult(hacer()));
        return await listo.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Esperar(Func<bool> listo)
    {
        long fin = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < fin)
        {
            if (listo()) return;
            await Task.Delay(10);
        }
        Assert.Fail("No llegó el resultado esperado.");
    }

    private async Task<Player> EsperarIngreso(string nombre)
    {
        long fin = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < fin)
        {
            if (await EnElLoop(() => _world.PlayerByName(nombre)) is { } p) return p;
            await Task.Delay(10);
        }
        throw new TimeoutException("El personaje no entró al mundo.");
    }

    private void PersonajeDelHost()
    {
        var cuenta = _db.CreateAccount("host", Clave);
        _db.CreateCharacter(cuenta, "Merlin", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();
    }

    [Theory]
    [InlineData((byte)16)]
    [InlineData((byte)8)]
    [InlineData((byte)4)]
    [InlineData((byte)2)]
    [InlineData((byte)32)]
    public async Task No_se_toma_un_nombre_libre_configurado_como_staff(byte rango)
    {
        _db.CreateAccount("ajena", Clave);
        Assert.True(_ini.GuardarStaff("Merlin", rango));
        var t = Crear("ajena", "mErLiN");

        await Esperar(() => t.Avisos().Any(a => a.Contains("reservado para el staff")));
        Assert.False(_db.CharacterExists("Merlin"));
        Assert.Empty(_db.LoadAccount("ajena")!.Characters);
        Assert.Null(await EnElLoop(() => _world.PlayerByName("Merlin")));
    }

    [Fact]
    public async Task El_personaje_existente_del_host_conserva_su_ingreso_y_rango()
    {
        PersonajeDelHost();
        Assert.True(_ini.GuardarStaff("Merlin", 8));
        Mandar(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LoginExistingChar)
            .WriteAsciiString("host").WriteAsciiString(Clave).WriteByte(1).WriteByte(4).WriteByte(5)
            .WriteAsciiString("Merlin").WriteAsciiString("mac").WriteLong(1));

        var p = await EsperarIngreso("Merlin");
        Assert.Equal("host", p.Account);
        Assert.Equal((byte)8, p.Privileges);
    }

    [Fact]
    public async Task No_se_borra_un_staff_ni_se_libera_su_nombre_con_el_rango_puesto()
    {
        PersonajeDelHost();
        Assert.True(_ini.GuardarStaff("Merlin", 8));
        var t = Borrar("host", "mErLiN");

        await Esperar(() => t.Avisos().Any(a => a.Contains("quitarle el rango")));
        Assert.True(_db.CharacterExists("Merlin"));
        Assert.Contains("Merlin", _db.LoadAccount("host")!.Characters);
    }

    [Fact]
    public async Task Quitar_el_rango_permite_borrar_y_recrear_sin_heredar_privilegios()
    {
        PersonajeDelHost();
        Assert.True(_ini.GuardarStaff("Merlin", 8));
        Assert.True(_ini.GuardarStaff("Merlin", 1));
        var t = Borrar("host", "Merlin");
        await Esperar(() => t.Avisos().Any(a => a.EndsWith("fue borrado.")));
        Assert.False(_db.CharacterExists("Merlin"));

        _db.CreateAccount("ajena", Clave);
        Crear("ajena", "Merlin");
        var p = await EsperarIngreso("Merlin");
        Assert.Equal("ajena", p.Account);
        Assert.Equal((byte)1, p.Privileges);
    }
}
