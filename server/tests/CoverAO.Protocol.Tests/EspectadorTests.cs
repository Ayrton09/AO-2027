using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class EspectadorTests : IAsyncDisposable
{
    private readonly World _mundo = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _fin = new();
    private readonly List<ClientSession> _sesiones = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;
    private readonly ServerIni _ini;
    private readonly string _rutaIni = Path.Combine(Path.GetTempPath(), "espectador-" + Guid.NewGuid() + ".ini");
    private const string Clave = "clave-de-prueba-espectador";

    private sealed class Transporte : ITransport
    {
        public ConcurrentQueue<byte[]> Recibidos { get; } = new();
        public string RemoteEndpoint => "prueba de espectador";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public EspectadorTests()
    {
        var data = new GameData();
        var niveles = new Leveling(data.LevelExp);
        File.WriteAllText(_rutaIni, "[INIT]\r\n");
        _ini = ServerIni.Cargar(_rutaIni);
        _db = new Persistence(AlmacenSqlite.EnMemoria(), niveles);
        _dispatcher = new PacketDispatcher(_mundo, _loop, _db, data, niveles, serverIni: _ini);
        _corriendo = _loop.RunAsync(_fin.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _fin.Cancel();
        await _corriendo;
        foreach (var sesion in _sesiones) sesion.Close("fin de prueba");
        _db.Dispose();
        _fin.Dispose();
        File.Delete(_rutaIni);
    }

    private (Player Jugador, ClientSession Sesion, Transporte Transporte) Crear(string nombre, byte rango, WorldPos pos)
    {
        var personaje = new Player
        {
            CharIndex = _mundo.AllocateCharIndex(), Name = nombre, Account = nombre, Privileges = rango,
            Pos = pos, Body = 1, ShowName = true,
        };
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher) { Player = personaje };
        _sesiones.Add(sesion);
        personaje.Connection = sesion;
        _mundo.Add(personaje);
        return (personaje, sesion, transporte);
    }

    private void Comando(ClientSession sesion, byte selector, string? nombre = null)
    {
        var bytes = new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(selector);
        if (nombre is not null) bytes.WriteAsciiString(nombre);
        var r = new ByteQueueReader(bytes.ToArray());
        _dispatcher.HandleOne(sesion, r);
        Assert.Equal(bytes.Length, r.Consumed);
    }

    // El Pong marca que el escritor entregó todo lo encolado antes de él, incluso si la escena no llegó.
    private static async Task<byte[][]> Entregas(ClientSession sesion, Transporte t)
    {
        sesion.Send(ServerMessages.Pong(0));
        var paquetes = new List<byte[]>();
        await Recibir(t, ServerPacketId.Pong, paquetes);
        return paquetes.ToArray();
    }

    private Task HacerEnMundo(Action accion)
    {
        _loop.Enqueue(accion);
        return _loop.SincronizarContinuacionesAsync().WaitAsync(EsperaDelEscritor.Plazo);
    }

    private static ByteQueueReader Escena(byte[] paquete)
    {
        var r = new ByteQueueReader(paquete);
        Assert.Equal((byte)ServerPacketId.EscenaEspectador, r.ReadByte());
        return r;
    }

    private void Flecha(ClientSession sesion, byte direccion)
    {
        var r = new ByteQueueReader(new byte[] { (byte)ClientPacketId.NavegarEspectador, direccion });
        _dispatcher.HandleOne(sesion, r);
        Assert.Equal(2, r.Consumed);
    }

    private (ClientSession Sesion, Transporte Transporte) ConexionNueva()
    {
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        _sesiones.Add(sesion);
        return (sesion, transporte);
    }

    private void GuardarCuenta(string cuenta, string personaje)
    {
        var acc = _db.CreateAccount(cuenta, Clave);
        _db.AltaDe(new Player { CharIndex = 0, Account = cuenta, Name = personaje, Pos = new(1, 50, 50) }, acc);
    }

    private void AbrirCuenta(ClientSession sesion, string cuenta)
    {
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.ConnectAccount).WriteAsciiString(cuenta).WriteAsciiString(Clave);
        w.WriteVersionDelJuego().WriteAsciiString("prueba").WriteLong(1);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(w.ToArray()));
    }

    private static async Task<byte[]> Recibir(Transporte transporte, ServerPacketId id, List<byte[]>? anteriores = null)
    {
        anteriores ??= [];
        long limite = Environment.TickCount64 + (long)EsperaDelEscritor.Plazo.TotalMilliseconds;
        while (Environment.TickCount64 < limite)
        {
            while (transporte.Recibidos.TryDequeue(out var paquete))
            {
                if (paquete[0] == (byte)id) return paquete;
                anteriores.Add(paquete);
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"No llegó {id}. Recibidos: {string.Join(", ", anteriores.Select(p => (ServerPacketId)p[0]))}");
    }

    [Fact]
    public async Task La_cuenta_GM_observa_sin_crear_un_jugador_y_puede_volver_a_observar()
    {
        const string cuenta = "cuentastaff", staff = "Moderador";
        GuardarCuenta(cuenta, staff);
        Assert.True(_ini.GuardarStaff(staff, 16));
        var (observado, _, _) = Crear("Observado", 1, new(2, 50, 50));
        var (sesion, salida) = ConexionNueva();
        AbrirCuenta(sesion, cuenta);
        await Recibir(salida, ServerPacketId.AbrirFormularios);

        Comando(sesion, 241, "Observado");
        var r = Escena(await Recibir(salida, ServerPacketId.EscenaEspectador));
        Assert.Equal(observado.CharIndex, r.ReadInteger());
        Assert.True(sesion.EspectaSinPersonaje);
        Assert.Null(sesion.Player);
        Assert.Same(observado, Assert.Single(_mundo.Players));

        await HacerEnMundo(() => { observado.Pos = new(513, 60, 60); _dispatcher.ActualizarEspectadores(3); });
        r = Escena(await Recibir(salida, ServerPacketId.EscenaEspectador));
        Assert.Equal(observado.CharIndex, r.ReadInteger());
        Assert.Equal(513, r.ReadInteger());

        Comando(sesion, 242);
        Assert.Equal(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());
        Assert.False(sesion.EspectaSinPersonaje);
        Comando(sesion, 241, "Observado");
        Assert.Equal(observado.CharIndex, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());

        Assert.True(_ini.GuardarStaff(staff, 1));
        Flecha(sesion, 1);
        Assert.Equal(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());
        Assert.Null(sesion.Player);
        Assert.Same(observado, Assert.Single(_mundo.Players));
    }

    [Fact]
    public async Task Una_cuenta_normal_o_sin_autenticar_no_recibe_una_escena()
    {
        const string cuenta = "cuentalibre";
        GuardarCuenta(cuenta, "Aprendiz");
        Assert.True(_ini.GuardarStaff("Moderador", 16));
        Crear("Observado", 1, new(2, 50, 50));
        var (sesion, salida) = ConexionNueva();
        Comando(sesion, 241, "Observado");
        await HacerEnMundo(() => { });
        Assert.Empty(await Entregas(sesion, salida));

        AbrirCuenta(sesion, cuenta);
        await Recibir(salida, ServerPacketId.AbrirFormularios);
        Comando(sesion, 241, "Observado");
        await HacerEnMundo(() => { });
        Assert.Empty(await Entregas(sesion, salida));
        Assert.False(sesion.EspectaSinPersonaje);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Vuelve_a_verificar_baneos_al_observar(bool cuentaBaneada)
    {
        const string cuenta = "cuentastaff", staff = "Moderador";
        GuardarCuenta(cuenta, staff);
        Assert.True(_ini.GuardarStaff(staff, 16));
        Crear("Observado", 1, new(2, 50, 50));
        var (sesion, salida) = ConexionNueva();
        AbrirCuenta(sesion, cuenta);
        await Recibir(salida, ServerPacketId.AbrirFormularios);
        Comando(sesion, 241, "Observado");
        Assert.NotEqual(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());

        if (cuentaBaneada) _db.CambiarCuenta(cuenta, acc => acc.Banned = true);
        else _db.SaveCharacter(new Player { CharIndex = 0, Account = cuenta, Name = staff, Banned = true });
        await HacerEnMundo(() =>
        {
            // Adelanta el reloj que comprueba el permiso hasta la ventana de renovación sin esperar cinco segundos.
            long renovacion = _loop.Tick + 4100 / GameLoop.TickMs;
            _dispatcher.ActualizarEspectadores(renovacion - renovacion % 3);
        });
        Assert.Equal(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());
        Assert.False(sesion.EspectaSinPersonaje);
    }

    [Fact]
    public async Task Cambiar_la_clave_cierra_la_vista_y_no_se_puede_reusar_el_ingreso_anterior()
    {
        const string cuenta = "cuentastaff", staff = "Moderador";
        GuardarCuenta(cuenta, staff);
        Assert.True(_ini.GuardarStaff(staff, 16));
        Crear("Observado", 1, new(2, 50, 50));
        var (sesion, salida) = ConexionNueva();
        AbrirCuenta(sesion, cuenta);
        await Recibir(salida, ServerPacketId.AbrirFormularios);
        Comando(sesion, 241, "Observado");
        Assert.NotEqual(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());

        var (otraSesion, otraSalida) = ConexionNueva();
        const string nueva = "nueva-clave-ficticia";
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.ProcesosLogin)
            .WriteAsciiString(cuenta).WriteAsciiString(Clave).WriteAsciiString(nueva)
            .WriteVersionDelJuego().WriteByte(1);
        _dispatcher.HandleOne(otraSesion, new ByteQueueReader(w.ToArray()));
        await Recibir(otraSalida, ServerPacketId.SendMsgBox);
        Assert.True(_db.LoadAccount(cuenta)!.VerifyPassword(nueva));
        await HacerEnMundo(() => _dispatcher.ActualizarEspectadores(3));
        Assert.Equal(0, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());

        Comando(sesion, 241, "Observado");
        await HacerEnMundo(() => { });
        Assert.Equal((byte)ServerPacketId.ShowMessageBox, Assert.Single(await Entregas(sesion, salida))[0]);
    }

    [Fact]
    public async Task Solo_staff_y_sin_espiar_a_un_rango_superior()
    {
        var (_, usuario, salUsuario) = Crear("Usuario", 1, new(1, 50, 50));
        var (_, consejero, salConsejero) = Crear("Consejero", 2, new(1, 52, 50));
        var (dios, _, _) = Crear("Director", 8, new(2, 50, 50));
        Comando(usuario, 241, "Director");
        Comando(consejero, 241, "Director");
        await HacerEnMundo(() => _dispatcher.ActualizarEspectadores(3));

        Assert.Empty(await Entregas(usuario, salUsuario));
        Assert.Equal((byte)ServerPacketId.ConsoleMsg, Assert.Single(await Entregas(consejero, salConsejero))[0]);
        await HacerEnMundo(() => dios.AdminInvisible = true);
        Comando(consejero, 241, "Usuario");
        await HacerEnMundo(() => _dispatcher.ActualizarEspectadores(6));
        Assert.Equal((byte)ServerPacketId.EscenaEspectador, Assert.Single(await Entregas(consejero, salConsejero))[0]);
    }

    [Fact]
    public async Task Sigue_al_personaje_entre_mapas_sin_mover_ni_anunciar_al_gm()
    {
        WorldPos origen = new(1, 41, 50), inicio = new(2, 50, 50), destino = new(513, 60, 60);
        var (gm, sesion, salida) = Crear("Moderador", 4, origen);
        var (objetivo, observada, salidaObservada) = Crear("Lía del Valle", 1, inicio);
        var (oculto, _, _) = Crear("Sombra", 1, new(2, 49, 50));
        oculto.Oculto = true;
        _mundo.SetGround(new(2, 51, 50), 123, 2);
        Comando(sesion, 241, "lía+del+valle");
        await HacerEnMundo(() => _dispatcher.ActualizarEspectadores(3));

        var r = Escena(Assert.Single(await Entregas(sesion, salida)));
        Assert.Equal(objetivo.CharIndex, r.ReadInteger());
        Assert.Equal(inicio.Map, r.ReadInteger());
        Assert.Equal(objetivo.Name, r.ReadAsciiString());
        Assert.Equal(ServerMessages.CharacterCreate(objetivo)[1..], r.ReadBytes(ServerMessages.CharacterCreate(objetivo).Length - 1));
        Assert.Equal(0, r.ReadInteger());
        Assert.Equal(123, r.ReadInteger());
        Assert.Equal(new byte[] { 51, 50 }, r.ReadBytes(2));
        Assert.Equal(2, r.ReadInteger());
        Assert.Equal(0, r.ReadInteger());

        await HacerEnMundo(() => { objetivo.Pos = destino; _dispatcher.ActualizarEspectadores(6); });
        r = Escena(Assert.Single(await Entregas(sesion, salida)));
        Assert.Equal(objetivo.CharIndex, r.ReadInteger());
        Assert.Equal(destino.Map, r.ReadInteger());
        Assert.Equal(origen, gm.Pos);
        Assert.Empty(await Entregas(observada, salidaObservada));

        Comando(sesion, 242);
        await HacerEnMundo(() => { objetivo.Pos = inicio; _dispatcher.ActualizarEspectadores(9); });
        Assert.Equal(0, Escena(Assert.Single(await Entregas(sesion, salida))).ReadInteger());
    }

    [Fact]
    public async Task Las_flechas_vuelven_al_principio_y_saltan_objetivos_no_permitidos()
    {
        var (_, sesion, salida) = Crear("Moderador", 2, new(1, 50, 50));
        var (alba, _, _) = Crear("Alba", 1, new(2, 50, 50));
        var (bella, _, _) = Crear("Bella", 1, new(2, 49, 50));
        bella.AdminInvisible = true;
        Crear("Carlos", 8, new(2, 48, 50));
        var (dante, _, _) = Crear("Dante", 1, new(3, 50, 50));
        var (_, cerrada, _) = Crear("Elena", 1, new(3, 51, 50));
        cerrada.Close("fin de prueba");
        var (faro, _, _) = Crear("Faro", 1, new(4, 50, 50));

        Flecha(sesion, 1);
        await HacerEnMundo(() => { });
        Assert.DoesNotContain(await Entregas(sesion, salida), p => p[0] == (byte)ServerPacketId.EscenaEspectador);
        Comando(sesion, 241, "Alba");
        Assert.Equal(alba.CharIndex, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());

        foreach (var (direccion, esperado) in new (byte, Player)[] { (1, dante), (1, faro), (1, alba), (0, faro), (0, dante) })
        {
            Flecha(sesion, direccion);
            Assert.Equal(esperado.CharIndex, Escena(await Recibir(salida, ServerPacketId.EscenaEspectador)).ReadInteger());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sale_al_desconectarse_el_objetivo_o_perder_el_rango(bool bajaDeRango)
    {
        var (gm, sesion, salida) = Crear("Moderador", 4, new(1, 41, 50));
        var (_, objetivo, _) = Crear("Observado", 1, new(2, 50, 50));
        Comando(sesion, 241, "Observado");
        await HacerEnMundo(() => _dispatcher.ActualizarEspectadores(3));
        Assert.NotEqual(0, Escena(Assert.Single(await Entregas(sesion, salida))).ReadInteger());
        await HacerEnMundo(() =>
        {
            if (bajaDeRango) gm.Privileges = 1;
            else objetivo.Close("se fue");
            _dispatcher.ActualizarEspectadores(6);
        });
        Assert.Equal(0, Escena(Assert.Single(await Entregas(sesion, salida))).ReadInteger());
    }
}
