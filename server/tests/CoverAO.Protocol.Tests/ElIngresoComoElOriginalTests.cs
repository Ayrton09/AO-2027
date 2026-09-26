using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL INGRESO, COMO EL ORIGINAL. Los carteles del login y del alta son los de la tabla de errores del cliente
/// original (<c>locale_error_es.ind</c>). El original los manda por número: los <c>ShowMessageBox</c> como texto
/// ("47") y los <c>SendMsgBox</c> con modo 1. Acá los primeros van ya traducidos y los segundos por número, que el
/// cliente web traduce con su tabla. La cuenta que no existe contesta como la contraseña equivocada (decisión del
/// autor, 25/09). Cada caso mira también si la conexión se corta: los carteles del nombre de un personaje nuevo, la
/// cuenta que ya existe y el personaje bloqueado NO cortan, como en el original.
/// </summary>
public class ElIngresoComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_ingreso_" + Guid.NewGuid().ToString("N"));
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private const string Clave = "clave_larga_de_prueba";

    private sealed class Transporte(string origen) : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public volatile bool Cerrado;
        public string RemoteEndpoint => origen;
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) => Cerrado = true;
    }

    public ElIngresoComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling);
        // El login verifica la contraseña fuera del loop y vuelve por él: el loop tiene que girar de verdad.
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private static List<string> Carteles(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    /// <summary>Los <c>SendMsgBox</c> con modo 1: el número de <c>locale_error_es.ind</c>.</summary>
    private static List<string> Avisos(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 4 && x[0] == (byte)ServerPacketId.SendMsgBox && x[^1] == 1)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    private static async Task<bool> Esperar(Func<bool> condicion, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (condicion()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private Transporte Mandar(ByteQueueWriter paquete, string origen = "tcp:10.0.0.2:2222")
    {
        var t = new Transporte(origen);
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(paquete.ToArray()));
        return t;
    }

    private static ByteQueueWriter Entrar(string cuenta, string clave, string personaje) => new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.LoginExistingChar).WriteAsciiString(cuenta).WriteAsciiString(clave)
        .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString(personaje).WriteAsciiString("mac").WriteLong(1);

    private static ByteQueueWriter Crear(string cuenta, string personaje, byte raza = 1, short cabeza = 1) => new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.LoginNewChar).WriteAsciiString(cuenta).WriteAsciiString(Clave)
        .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString(personaje)
        .WriteByte(raza).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(cabeza).WriteAsciiString("mac").WriteLong(1);

    private static ByteQueueWriter AbrirCuenta(string cuenta) => new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.ConnectAccount).WriteAsciiString(cuenta).WriteAsciiString(Clave)
        .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString("mac").WriteLong(1);

    private static ByteQueueWriter CrearCuenta(string cuenta, string codigo) => new ByteQueueWriter()
        .WriteByte((byte)ClientPacketId.CreateNewAccount).WriteAsciiString(cuenta).WriteAsciiString(Clave)
        .WriteAsciiString(codigo).WriteByte(1).WriteByte(4).WriteByte(5);

    private Account CuentaCon(string nombre, params string[] personajes)
    {
        var cuenta = _db.CreateAccount(nombre, Clave);
        short i = 1;
        foreach (var p in personajes) _db.CreateCharacter(cuenta, p, i++, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        return cuenta;
    }

    // ── Entrar ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Con_otro_personaje_de_la_cuenta_adentro_sale_el_cartel_47()
    {
        CuentaCon("cuenta", "Aldo", "Bruno");
        // Aldo ya está jugando, desde otra dirección (así no lo frena el tope por IP).
        var aldo = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "cuenta",
            Pos = new WorldPos(1, 50, 50), Hp = 100, MaxHp = 100, Level = 1,
        };
        aldo.Connection = new ClientSession(new Transporte("tcp:10.0.0.1:1111"), _dispatcher) { Player = aldo };
        _world.Add(aldo);

        var t = Mandar(Entrar("cuenta", Clave, "Bruno"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0), "no llegó ningún cartel");
        Assert.Equal(["Ya hay un usuario conectado con esta cuenta, solo puedes logear un personaje por cuenta."], Carteles(t));
        Assert.Null(_world.PlayerByName("Bruno"));
    }

    [Fact]
    public async Task La_cuenta_que_no_existe_contesta_como_la_contrasena_equivocada()
    {
        CuentaCon("cuenta", "Aldo");

        var mala = Mandar(Entrar("cuenta", "otra_clave_cualquiera", "Aldo"));
        var inexistente = Mandar(Entrar("nadie", Clave, "Aldo"), "tcp:10.0.0.3:3333");

        Assert.True(await Esperar(() => Carteles(mala).Count > 0 && Carteles(inexistente).Count > 0));
        Assert.Equal(["Contraseña incorrecta."], Carteles(mala));
        Assert.Equal(["Contraseña incorrecta."], Carteles(inexistente));
        Assert.True(mala.Cerrado && inexistente.Cerrado);
    }

    [Fact]
    public async Task La_cuenta_baneada_sale_con_el_cartel_19()
    {
        var cuenta = CuentaCon("cuenta", "Aldo");
        cuenta.Banned = true;
        _db.SaveAccount(cuenta);

        var t = Mandar(Entrar("cuenta", Clave, "Aldo"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.StartsWith("Su cuenta está baneada.", Carteles(t)[0]);
        Assert.True(t.Cerrado);
    }

    [Fact]
    public async Task El_personaje_ajeno_es_invalido_y_el_que_no_existe_no_existe()
    {
        CuentaCon("cuenta", "Aldo");
        CuentaCon("otra", "Bruno");

        var ajeno = Mandar(Entrar("cuenta", Clave, "Bruno"));
        var inexistente = Mandar(Entrar("cuenta", Clave, "Nadie"), "tcp:10.0.0.3:3333");

        Assert.True(await Esperar(() => Carteles(ajeno).Count > 0 && Carteles(inexistente).Count > 0));
        Assert.Equal(["Personaje invalido."], Carteles(ajeno));
        Assert.Equal(["El personaje no existe."], Carteles(inexistente));
    }

    [Fact]
    public async Task El_personaje_bloqueado_sale_con_el_22_y_no_corta()
    {
        CuentaCon("cuenta", "Aldo");
        var aldo = _db.LoadCharacter("Aldo", 0, "cuenta")!;
        aldo.Banned = true;
        _db.SaveCharacter(aldo);

        var t = Mandar(Entrar("cuenta", Clave, "Aldo"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.Equal(["El personaje ha sido bloqueado y no puede ser accedido."], Carteles(t));
        await Task.Delay(200);
        Assert.False(t.Cerrado);
    }

    // ── Abrir y crear la cuenta ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "62")]
    [InlineData("una_cuenta_de_mas_de_veinte", "63")]
    [InlineData("con/barra", "64")]
    public async Task El_nombre_de_cuenta_que_no_vale_va_por_numero(string cuenta, string aviso)
    {
        var abrir = Mandar(AbrirCuenta(cuenta));
        var crear = Mandar(CrearCuenta(cuenta, "1234"), "tcp:10.0.0.3:3333");

        Assert.True(await Esperar(() => Avisos(abrir).Count > 0 && Avisos(crear).Count > 0));
        Assert.Equal([aviso], Avisos(abrir));
        Assert.Equal([aviso], Avisos(crear));
        Assert.True(abrir.Cerrado && crear.Cerrado);
    }

    [Theory]
    [InlineData("", "67")]
    [InlineData("12a4", "53")]
    [InlineData("12345", "68")]
    public async Task El_codigo_de_la_cuenta_nueva_como_el_original(string codigo, string aviso)
    {
        var t = Mandar(CrearCuenta("nueva", codigo));

        Assert.True(await Esperar(() => Avisos(t).Count > 0));
        Assert.Equal([aviso], Avisos(t));
        Assert.False(_db.AccountExists("nueva"));
    }

    [Fact]
    public async Task La_cuenta_que_ya_existe_sale_con_el_41_y_no_corta()
    {
        CuentaCon("cuenta");

        var t = Mandar(CrearCuenta("cuenta", "1234"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.Equal(["Ya existe la cuenta."], Carteles(t));
        Assert.False(t.Cerrado);
    }

    // ── Crear el personaje ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Pepe1", "Nombre inválido.")]
    [InlineData("A", "El nombre es muy corto.")]
    [InlineData("Unnombrequetienemuchasmasdetreintaletras", "El nombre es muy largo.")]
    [InlineData("Unnombredeveinteletras", "Nombre inválido.")]
    public async Task El_nombre_del_personaje_nuevo_tiene_su_cartel_y_no_corta(string nombre, string cartel)
    {
        CuentaCon("cuenta");

        var t = Mandar(Crear("cuenta", nombre));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.Equal([cartel], Carteles(t));
        await Task.Delay(200);
        Assert.False(t.Cerrado);
        Assert.False(_db.CharacterExists(nombre));
    }

    [Fact]
    public async Task El_personaje_que_ya_existe_sale_con_el_7_y_no_corta()
    {
        CuentaCon("otra", "Bruno");
        CuentaCon("cuenta");

        var t = Mandar(Crear("cuenta", "Bruno"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.Equal(["Ya existe el personaje."], Carteles(t));
        await Task.Delay(200);
        Assert.False(t.Cerrado);
    }

    [Fact]
    public async Task Con_diez_personajes_no_se_crea_otro_y_corta()
    {
        CuentaCon("cuenta", "Uno", "Dos", "Tres", "Cuatro", "Cinco", "Seis", "Siete", "Ocho", "Nueve", "Diez");

        var t = Mandar(Crear("cuenta", "Once"));

        Assert.True(await Esperar(() => Carteles(t).Count > 0));
        Assert.Equal(["No puedes crear mas personajes, has llegado a tu límite de diez personajes."], Carteles(t));
        Assert.True(t.Cerrado);
    }

    [Fact]
    public async Task La_raza_y_la_cabeza_que_no_valen_tienen_su_cartel_y_cortan()
    {
        CuentaCon("cuenta");

        var raza = Mandar(Crear("cuenta", "Aldo", raza: 99));
        var cabeza = Mandar(Crear("cuenta", "Bruno", cabeza: 999), "tcp:10.0.0.3:3333");

        Assert.True(await Esperar(() => Carteles(raza).Count > 0 && Carteles(cabeza).Count > 0));
        Assert.Equal(["Ingresa una raza válida."], Carteles(raza));
        Assert.Equal(["Ingresa una cabeza válida."], Carteles(cabeza));
        Assert.True(raza.Cerrado && cabeza.Cerrado);
    }
}
