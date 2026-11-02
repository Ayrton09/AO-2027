using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA AUDITORÍA EXTERNA DEL 23/10, lo que no es persistencia (eso está en <see cref="LoQueSeMueveSeGuardaJuntoTests"/> y
/// <see cref="LaColaNoPierdeNiCreceTests"/>): morir y curarse en la misma vuelta, el TLS que no carga, crear un personaje
/// con la cuenta ya adentro, el listener que se cae, la cadencia con decimales y el paquete que llega de a pedazos.
/// </summary>
public class AuditoriaExterna2310Tests
{
    private static readonly GameData Datos = GameData.Load();

    // ── Morir y curarse en la misma vuelta ────────────────────────────────────────────────────────────────────

    [Fact]
    public void El_que_muere_de_frio_no_se_cura_en_la_misma_vuelta()
    {
        // Una data propia, con el mapa marcado como nevado (el /MODMAPINFO TERRENO del original): la compartida no se toca.
        var datos = GameData.Load();
        const short nieve = 1;
        datos.SetMapSnow(nieve, true);
        var world = new World();
        var p = new Player
        {
            CharIndex = 1, Name = "Helado", Account = "c", Pos = new WorldPos(nieve, 50, 50), Privileges = 1,
            Hp = 1, MaxHp = 100, MaxStamina = 100, Stamina = 100, Hunger = 100, Thirst = 100, Resting = true,
            ColdCounter = Survival.ColdIntervalTicks, HpCounter = Survival.HealRestingIntervalTicks,
        };
        world.Add(p);
        EfectosDeLaVuelta.Aplicar(p, 1, world, datos, new ServerIni(), null, new Random(1));
        // El frío lo mató; la curación, lista para salir en la misma vuelta, le devolvía vida a un cadáver.
        Assert.Equal(0, p.Hp);
        Assert.False(p.IsAlive);
        Assert.Equal(1, p.Deaths);
    }

    [Fact]
    public void El_que_no_muere_se_sigue_curando()
    {
        var world = new World();
        var p = new Player
        {
            CharIndex = 1, Name = "Sano", Account = "c", Pos = new WorldPos(1, 50, 50), Privileges = 1,
            Hp = 50, MaxHp = 100, MaxStamina = 100, Stamina = 100, Hunger = 100, Thirst = 100, Resting = true,
            HpCounter = Survival.HealRestingIntervalTicks,
        };
        world.Add(p);
        EfectosDeLaVuelta.Aplicar(p, 1, world, Datos, new ServerIni(), null, new Random(1));
        Assert.True(p.Hp > 50);
    }

    // ── TLS ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static string CarpetaTemporal()
    {
        string d = Path.Combine(Path.GetTempPath(), "coverao_tls_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Sin_tls_no_hay_certificado_ni_error()
    {
        Assert.Null(CertificadoTls.Cargar(new[] { "7666", "--fixture" }, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Tls_sin_ruta_no_arranca()
    {
        Assert.Null(CertificadoTls.Cargar(new[] { "--tls" }, out var error));
        Assert.NotNull(error);
        Assert.Null(CertificadoTls.Cargar(new[] { "--tls", "--fixture" }, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Tls_con_un_certificado_que_no_existe_no_arranca()
    {
        Assert.Null(CertificadoTls.Cargar(new[] { "--tls", Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid() + ".pfx") }, out var error));
        Assert.Contains("no existe", error);
    }

    [Fact]
    public void Tls_con_un_certificado_roto_no_arranca()
    {
        string d = CarpetaTemporal();
        try
        {
            string pfx = Path.Combine(d, "roto.pfx");
            File.WriteAllBytes(pfx, Encoding.ASCII.GetBytes("esto no es un certificado"));
            Assert.Null(CertificadoTls.Cargar(new[] { "--tls", pfx }, out var error));
            Assert.Contains("no se pudo cargar", error);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Tls_con_un_certificado_valido_carga_y_con_la_clave_equivocada_no()
    {
        string d = CarpetaTemporal();
        try
        {
            using var rsa = RSA.Create(2048);
            var pedido = new CertificateRequest("CN=coverao-prueba", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = pedido.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            string pfx = Path.Combine(d, "bueno.pfx");
            File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "clave"));

            using var cargado = CertificadoTls.Cargar(new[] { "7666", "--tls", pfx, "clave" }, out var error);
            Assert.Null(error);
            Assert.NotNull(cargado);
            Assert.True(cargado!.HasPrivateKey);

            Assert.Null(CertificadoTls.Cargar(new[] { "--tls", pfx, "otra" }, out error));
            Assert.NotNull(error);
        }
        finally { Directory.Delete(d, true); }
    }

    // ── El listener que se cae ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_listener_que_se_cae_apaga_a_los_demas_y_dice_cual()
    {
        using var cts = new CancellationTokenSource();
        var paraSiempre = Task.Delay(Timeout.Infinite, cts.Token);
        var ocupado = Task.Run(() => throw new SocketException((int)SocketError.AddressAlreadyInUse));
        var fallo = await Escuchas.HastaQueTermineOFalleAlguna(new (string, Task)[]
        {
            ("el panel", paraSiempre), ("el puerto 7666", ocupado),
        }, cts).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("el puerto 7666", fallo);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task El_apagado_de_siempre_no_es_un_fallo()
    {
        using var cts = new CancellationTokenSource();
        var a = Task.Delay(Timeout.Infinite, cts.Token);
        // El cliente web apagado no se registra entre las escuchas habilitadas.
        cts.CancelAfter(100);
        Assert.Null(await Escuchas.HastaQueTermineOFalleAlguna(new (string, Task)[] { ("a", a) }, cts)
            .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ── La cadencia con decimales ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(750, 750, true)]     // 1,33 por segundo: a los 750 ms ya hay ficha (con la división entera, 1 por segundo: no)
    [InlineData(1500, 1000, false)]  // 0,67 por segundo: al segundo todavía no (antes sí)
    [InlineData(1500, 1500, true)]
    [InlineData(5000, 1000, false)]  // 0,2 por segundo (antes 1)
    [InlineData(5000, 4999, false)]
    [InlineData(5000, 5000, true)]
    public void La_cadencia_respeta_el_intervalo_del_ini(int ms, long despues, bool permite)
    {
        var c = new CadenciaDeAcciones(ms, 1, 0, 10, ahoraMs: 0);
        Assert.True(c.PermiteRuidosa(0));
        Assert.Equal(permite, c.PermiteRuidosa(despues));
    }

    // ── El paquete que llega de a pedazos ─────────────────────────────────────────────────────────────────────

    /// <summary>Entrega <paramref name="bytes"/> de a uno por lectura, cifrados con la clave inicial.</summary>
    private sealed class DeAUnByte(byte[] bytes) : ITransport
    {
        public int Entregados;
        public volatile string? Motivo;
        public string RemoteEndpoint => "cliente";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
        {
            if (Entregados >= bytes.Length) return new(0);
            buffer[0] = (byte)(bytes[Entregados++] ^ Crypto.DefaultKey);
            return new(1);
        }
        public void Send(byte[] data) { }
        public void Close(string reason) { Motivo ??= reason; }
    }

    private static PacketDispatcher Despachador()
    {
        var leveling = new Leveling(Datos.LevelExp);
        return new PacketDispatcher(new World(), new GameLoop(), new Persistence(AlmacenSqlite.EnMemoria(), leveling), Datos, leveling);
    }

    private static byte[] Hablar(int largo) => new ByteQueueWriter().WriteByte((byte)ClientPacketId.Talk)
        .WriteAsciiString(new string('a', largo)).ToArray();

    [Fact]
    public async Task Un_paquete_partido_en_muchos_pedazos_cuenta_una_vez()
    {
        var paquete = Hablar(60);
        var t = new DeAUnByte(paquete);
        // Cinco por segundo: con cada pedazo cobrando una ficha, el paquete de 63 bytes cortaba la conexión al sexto.
        var sesion = new ClientSession(t, Despachador(), paquetesPorSegundo: 5, rafagaDePaquetes: 5);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sesion.ReceiveLoopAsync(cts.Token);
        Assert.Equal(paquete.Length, t.Entregados);
        Assert.DoesNotContain("flood", t.Motivo ?? "");   // se cierra porque el transporte terminó, no por la cadencia
    }

    [Fact]
    public async Task Un_paquete_que_no_termina_de_llegar_de_a_un_byte_igual_corta()
    {
        var t = new DeAUnByte(Hablar(2000));   // 2003 bytes que nunca terminan antes de que corte
        var sesion = new ClientSession(t, Despachador(), paquetesPorSegundo: 5, rafagaDePaquetes: 5);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sesion.ReceiveLoopAsync(cts.Token);
        Assert.True(t.Entregados < 2003, $"entregó {t.Entregados}");
        EsperaDelEscritor.Hasta(() => t.Motivo is not null);
        Assert.Contains("pedacitos", t.Motivo ?? "");
    }
}

/// <summary>
/// CREAR UN PERSONAJE CON LA CUENTA YA ADENTRO (auditoría externa del 23/10): el ingreso de uno que ya existe mira si la cuenta
/// tiene a otro en el mundo; el alta no lo miraba, y el nuevo entraba igual.
/// </summary>
public class CrearConLaCuentaAdentroTests : IDisposable
{
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;
    private const string Clave = "clave_larga_de_prueba";

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "tcp:10.0.0.3:2222";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public CrearConLaCuentaAdentroTests()
    {
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
    }

    private static List<string> Carteles(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
        .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

    [Fact]
    public async Task No_se_crea_otro_personaje_con_la_cuenta_ya_jugando()
    {
        var cuenta = _db.CreateAccount("cuenta", Clave);
        var primero = _db.CreateCharacter(cuenta, "Primero", _world.AllocateCharIndex(), race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        primero.Account = "cuenta";
        _world.Add(primero);

        var t = new Transporte();
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.LoginNewChar).WriteAsciiString("cuenta").WriteAsciiString(Clave)
            .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString("Segundo")
            .WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1).WriteAsciiString("mac").WriteLong(1).ToArray()));

        long fin = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < fin && Carteles(t).Count == 0) await Task.Delay(25);
        Assert.Contains(Carteles(t), c => c.Contains("solo puedes logear un personaje por cuenta"));
        await Task.Delay(300);
        Assert.Null(_world.PlayerByName("Segundo"));
        Assert.False(_db.CharacterExists("Segundo"));
    }
}
