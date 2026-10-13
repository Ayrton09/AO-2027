using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA BASE FUERA DEL LOOP (<see cref="ColaDeLaBase"/>). El ingreso lee al personaje por la cola y vuelve una o dos vueltas
/// después; mientras tanto el personaje está RESERVADO (<see cref="World.EstaEntrando"/>). Antes la lectura era en el acto y
/// esa ventana no existía, así que nada de lo que pasa en ella estaba cubierto.
///
/// <para>Estos tests la abren a propósito —la lectura del personaje queda frenada en la base hasta que el test la suelta— y
/// mandan lo que antes no podía caer ahí: otro ingreso del mismo personaje, un GM que lo edita creyéndolo desconectado, una
/// transferencia a su bóveda. Lo que tiene que pasar es que esperen a que termine de entrar y se apliquen sobre el vivo:
/// si no, el ingreso lee lo de antes del cambio y el primer guardado del personaje lo pisa.</para>
/// </summary>
public class LaBaseFueraDelLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_basefuera_" + Guid.NewGuid().ToString("N"));
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly AlmacenQueFrena _almacen;
    private readonly Persistence _db;
    private readonly PacketDispatcher _dispatcher;

    private const string Clave = "clave_larga_de_prueba";
    private const byte PrivDios = 8;

    /// <summary>Un almacén que frena la lectura de UN personaje hasta que el test la suelta. Frena al escritor entero
    /// —la cola es de a uno—, así que lo que el test quiera tener listo tiene que pasar por la base antes.</summary>
    private sealed class AlmacenQueFrena(IAlmacen real) : IAlmacen
    {
        private readonly ManualResetEventSlim _suelto = new(true);
        private volatile string? _frenado;
        private volatile string? _confirmacionFrenada;
        private volatile bool _llego;

        /// <summary>¿La lectura frenada ya llegó a la base? Desde ese momento el personaje está reservado.</summary>
        public bool Llego => _llego;

        public void Frenar(string nombre) { _llego = false; _suelto.Reset(); _frenado = nombre; }
        /// <summary>Frena la confirmación del alta de <paramref name="nombre"/> (el "¿existe?" que va detrás del alta).</summary>
        public void FrenarLaConfirmacion(string nombre) { _llego = false; _suelto.Reset(); _confirmacionFrenada = nombre; }
        public void Soltar() { _frenado = null; _confirmacionFrenada = null; _suelto.Set(); }

        public FichaDePersonaje? LeerPersonaje(string nombre)
        {
            if (string.Equals(nombre, _frenado, StringComparison.OrdinalIgnoreCase))
            {
                _llego = true;
                _suelto.Wait(TimeSpan.FromSeconds(20));
            }
            return real.LeerPersonaje(nombre);
        }

        /// <summary>Corre en el escritor, recién leída una cuenta y antes de devolverla: lo que haga acá pasa antes de que
        /// el que la pidió siga.</summary>
        public volatile Action<string>? AlLeerCuenta;

        public Account? LeerCuenta(string nombre)
        {
            var cuenta = real.LeerCuenta(nombre);
            AlLeerCuenta?.Invoke(nombre);
            return cuenta;
        }

        public bool ExisteCuenta(string nombre) => real.ExisteCuenta(nombre);
        public void GuardarCuenta(Account cuenta) => real.GuardarCuenta(cuenta);
        public IEnumerable<string> NombresDeCuentas() => real.NombresDeCuentas();
        public bool ExistePersonaje(string nombre)
        {
            if (string.Equals(nombre, _confirmacionFrenada, StringComparison.OrdinalIgnoreCase))
            {
                _llego = true;
                _suelto.Wait(TimeSpan.FromSeconds(20));
            }
            return real.ExistePersonaje(nombre);
        }
        public void GuardarPersonaje(FichaDePersonaje ficha) => real.GuardarPersonaje(ficha);
        public bool CrearPersonaje(Account cuenta, FichaDePersonaje ficha) => real.CrearPersonaje(cuenta, ficha);
        public bool ApartarPersonaje(string nombre) => real.ApartarPersonaje(nombre);
        public IEnumerable<string> NombresDePersonajes() => real.NombresDePersonajes();
        public CharacterSummary? LeerResumen(string nombre) => real.LeerResumen(nombre);
        public long? AcreditarBoveda(string nombre, long monto) => real.AcreditarBoveda(nombre, monto);
        public IReadOnlyList<CharacterFile.FilaDeRanking> FilasDeRanking() => real.FilasDeRanking();
        public IReadOnlyList<ClanGuardado> LeerClanes() => real.LeerClanes();
        public void GuardarClanes(IReadOnlyList<ClanGuardado> clanes) => real.GuardarClanes(clanes);
        public IReadOnlyList<KeyValuePair<string, string>> LeerRechazosDeClan() => real.LeerRechazosDeClan();
        public void GuardarRechazosDeClan(IReadOnlyList<KeyValuePair<string, string>> rechazos) => real.GuardarRechazosDeClan(rechazos);
        public IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> LeerCorreo() => real.LeerCorreo();
        public void GuardarCasillas(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas) => real.GuardarCasillas(casillas);
        public IReadOnlyList<ObjetoEnElSuelo> LeerSuelo() => real.LeerSuelo();
        public void GuardarSuelo(IReadOnlyList<ObjetoEnElSuelo> suelo) => real.GuardarSuelo(suelo);
        public IReadOnlyList<LlaveVendida> LeerLlavesVendidas() => real.LeerLlavesVendidas();
        public void GuardarLlavesVendidas(IReadOnlyList<LlaveVendida> llaves) => real.GuardarLlavesVendidas(llaves);
        public SubastasGuardadas LeerSubastas() => real.LeerSubastas();
        public void GuardarSubastas(SubastasGuardadas subastas) => real.GuardarSubastas(subastas);
        public int LeerRecord() => real.LeerRecord();
        public void GuardarRecord(int record) => real.GuardarRecord(record);
        public IReadOnlyList<string> LeerIpsBaneadas() => real.LeerIpsBaneadas();
        public void GuardarIpsBaneadas(IReadOnlyList<string> ips) => real.GuardarIpsBaneadas(ips);
        public string Backup(string destDir, string stem) => real.Backup(destDir, stem);
    }

    private sealed class Transporte(string origen) : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => origen;
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }

        public List<string> Carteles() => Recibidos
            .Where(x => x.Length >= 3 && x[0] == (byte)ServerPacketId.ShowMessageBox)
            .Select(x => Encoding.Latin1.GetString(x, 3, BitConverter.ToInt16(x, 1))).ToList();

        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();

        /// <summary>Los <c>SendMsgBox</c>: los avisos de la pantalla de la cuenta (el borrado, el cambio de clave).</summary>
        public List<string> Avisos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.SendMsgBox)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public LaBaseFueraDelLoopTests()
    {
        Directory.CreateDirectory(_root);
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        _almacen = new AlmacenQueFrena(new AlmacenDeTexto(new FileStore(_root)));
        _db = new Persistence(_almacen, leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _almacen.Soltar();
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private static async Task<bool> Esperar(Func<bool> condicion, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (condicion()) return true;
            await Task.Delay(10);
        }
        return false;
    }

    /// <summary>Como <see cref="Esperar"/>, con la condición mirada adentro del loop.</summary>
    private async Task<bool> EsperarEnElLoop(Func<bool> condicion, int topeMs = 15_000)
    {
        long fin = Environment.TickCount64 + topeMs;
        while (Environment.TickCount64 < fin)
        {
            if (await EnElLoop(condicion)) return true;
            await Task.Delay(10);
        }
        return false;
    }

    /// <summary>Corre en el loop y espera: el mundo sólo se toca desde ahí.</summary>
    private async Task<T> EnElLoop<T>(Func<T> que)
    {
        var listo = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => listo.SetResult(que()));
        return await listo.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Unas vueltas del loop: lo que se reintenta cada vuelta tuvo tiempo de reintentarse.</summary>
    private async Task Vueltas(int n)
    {
        for (int i = 0; i < n; i++) await EnElLoop(() => 0);
    }

    private async Task<(Player P, ClientSession S, Transporte T)> Conectado(string nombre, byte privilegios = 1, int banco = 0)
    {
        var t = new Transporte("tcp:10.0.9." + nombre.Length + ":2222");
        var s = new ClientSession(t, _dispatcher);
        var p = await EnElLoop(() =>
        {
            var p = new Player
            {
                CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
                Pos = new WorldPos(1, 50, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 30,
                Privileges = privilegios, Bank = banco, Connection = s,
            };
            s.Player = p;
            _world.Add(p);
            return p;
        });
        return (p, s, t);
    }

    private Transporte Entrar(string personaje, string origen)
    {
        var t = new Transporte(origen);
        var paquete = new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.LoginExistingChar).WriteAsciiString("cuenta").WriteAsciiString(Clave)
            .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString(personaje).WriteAsciiString("mac").WriteLong(1);
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(paquete.ToArray()));
        return t;
    }

    private void Mandar(ClientSession s, ByteQueueWriter paquete)
        => _dispatcher.HandleOne(s, new ByteQueueReader(paquete.ToArray()));

    /// <summary>El alta de un personaje de la "cuenta": humano, hombre, la primera clase, de Nix, con la primera cabeza.</summary>
    private Transporte Crear(string personaje, string origen)
    {
        var t = new Transporte(origen);
        var paquete = new ByteQueueWriter()
            .WriteByte((byte)ClientPacketId.LoginNewChar).WriteAsciiString("cuenta").WriteAsciiString(Clave)
            .WriteByte(1).WriteByte(4).WriteByte(5).WriteAsciiString(personaje)
            .WriteByte(1).WriteByte(1).WriteByte(1).WriteByte(1).WriteInteger(1).WriteAsciiString("mac").WriteLong(1);
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(paquete.ToArray()));
        return t;
    }

    private Transporte Borrar(string personaje, string origen)
    {
        var t = new Transporte(origen);
        var paquete = new ByteQueueWriter().WriteByte((byte)ClientPacketId.BorrarPersonaje)
            .WriteAsciiString("cuenta").WriteAsciiString(Clave).WriteAsciiString(personaje);
        _dispatcher.HandleOne(new ClientSession(t, _dispatcher), new ByteQueueReader(paquete.ToArray()));
        return t;
    }

    /// <summary>La cuenta con Aldo, desconectado. El personaje del fixture nace con 5000 de oro y la bóveda vacía.</summary>
    private void CuentaConAldo()
    {
        var cuenta = _db.CreateAccount("cuenta", Clave);
        _db.CreateCharacter(cuenta, "Aldo", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();
    }

    /// <summary>Aldo empieza a entrar y su lectura queda frenada en la base: está reservado.</summary>
    private async Task<Transporte> AldoEntrando()
    {
        _almacen.Frenar("Aldo");
        var t = Entrar("Aldo", "tcp:10.0.0.2:2222");
        Assert.True(await Esperar(() => _almacen.Llego), "el ingreso de Aldo no llegó a leerlo");
        Assert.True(await EnElLoop(() => _world.EstaEntrando("Aldo")));
        return t;
    }

    private async Task<Player> AldoAdentro()
    {
        _almacen.Soltar();
        Assert.True(await EsperarEnElLoop(() => _world.PlayerByName("Aldo") is not null), "Aldo no terminó de entrar");
        return await EnElLoop(() => _world.PlayerByName("Aldo")!);
    }

    [Fact]
    public async Task Un_segundo_ingreso_del_personaje_que_esta_entrando_espera_afuera()
    {
        CuentaConAldo();
        _almacen.Frenar("Aldo");
        // Los dos a la vez, desde dos direcciones (así no los frena la IP repetida). Las dos contraseñas se verifican en
        // paralelo; el primero que vuelve al loop reserva a Aldo, y su lectura queda frenada.
        var a = Entrar("Aldo", "tcp:10.0.0.2:2222");
        var b = Entrar("Aldo", "tcp:10.0.0.3:2222");

        const string Cartel = "El personaje está entrando al juego.";
        Assert.True(await Esperar(() => a.Carteles().Contains(Cartel) || b.Carteles().Contains(Cartel)),
            "ninguno de los dos se enteró de que el otro estaba entrando");
        var aldo = await AldoAdentro();
        await Vueltas(3);

        Assert.Equal(1, await EnElLoop(() => _world.Players.Count(p => p.Name == "Aldo")));
        var entro = a.Carteles().Contains(Cartel) ? b : a;
        Assert.Equal(entro.RemoteEndpoint, aldo.Connection!.RemoteEndpoint);
    }

    /// <summary>
    /// CON LA BASE CAÍDA, EL QUE ENTRA NO SE QUEDA COLGADO (fase5/postgres/DISENO.md, 3.5): si la lectura del personaje no
    /// vuelve en el plazo, recibe un cartel. Cuando la lectura por fin vuelve, el intento ya se abandonó: no entra solo, y
    /// la reserva se suelta —si no, ese personaje no podría entrar nunca más—.
    /// </summary>
    [Fact]
    public async Task Si_la_base_no_trae_al_personaje_a_tiempo_se_avisa_y_no_entra_despues()
    {
        CuentaConAldo();
        _dispatcher.PlazoDeLaLecturaDeEntrada = TimeSpan.FromMilliseconds(300);
        _almacen.Frenar("Aldo");

        var t = Entrar("Aldo", "tcp:10.0.0.2:2222");

        Assert.True(await Esperar(() => t.Carteles().Any(c => c.Contains("en este momento"))), "no se le avisó");
        Assert.True(await EnElLoop(() => _world.EstaEntrando("Aldo")));   // la lectura sigue en vuelo, y la reserva también
        _almacen.Soltar();
        Assert.True(await EsperarEnElLoop(() => !_world.EstaEntrando("Aldo")), "la reserva no se soltó");
        await Vueltas(3);
        Assert.Equal(0, await EnElLoop(() => _world.Players.Count(p => p.Name == "Aldo")));
    }

    [Fact]
    public async Task Un_GM_que_edita_al_que_esta_entrando_lo_edita_vivo()
    {
        CuentaConAldo();
        var (_, gm, tgm) = await Conectado("Dios", privilegios: PrivDios);
        await AldoEntrando();

        // /MOD Aldo ORO 777: todavía no está en el mundo, así que el GM lo trataría como desconectado.
        Mandar(gm, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(20)
            .WriteAsciiString("Aldo").WriteByte(1).WriteAsciiString("777").WriteAsciiString(""));
        await Vueltas(3);   // el comando ya corrió, y espera

        var aldo = await AldoAdentro();
        Assert.True(await Esperar(() => tgm.Textos().Any(x => x.StartsWith("Charfile alterado: Aldo"))), "el /MOD no se aplicó");
        Assert.Equal(777, await EnElLoop(() => aldo.Gold));
    }

    /// <summary>El caso al revés: el cambio del GM ya está en vuelo —leyendo al desconectado— cuando el personaje entra. Si
    /// la entrada no lo esperara, leería lo de antes del cambio, y el guardado del GM quedaría pisado por el suyo.</summary>
    [Fact]
    public async Task El_que_entra_mientras_un_GM_lo_edita_entra_con_el_cambio()
    {
        CuentaConAldo();
        var (_, gm, tgm) = await Conectado("Dios", privilegios: PrivDios);
        // El orden se fuerza: cuando el login de Aldo lee su cuenta —antes del hash de la contraseña—, el GM ya mandó el
        // /MOD, y su lectura de Aldo queda frenada. La entrada de Aldo llega detrás, con el cambio en curso.
        _almacen.AlLeerCuenta = nombre =>
        {
            if (nombre != "cuenta") return;
            _almacen.AlLeerCuenta = null;
            _almacen.Frenar("Aldo");
            Mandar(gm, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(20)
                .WriteAsciiString("Aldo").WriteByte(1).WriteAsciiString("777").WriteAsciiString(""));
        };
        Entrar("Aldo", "tcp:10.0.0.2:2222");
        Assert.True(await Esperar(() => _almacen.Llego), "el /MOD no llegó a leer a Aldo");
        await Task.Delay(500);   // el hash de la contraseña termina y la entrada choca con el cambio en curso
        Assert.False(await EnElLoop(() => _world.PlayerByName("Aldo") is not null), "Aldo entró sin esperar al /MOD");

        var aldo = await AldoAdentro();
        Assert.True(await Esperar(() => tgm.Textos().Any(x => x.StartsWith("Charfile alterado: Aldo"))), "el /MOD no se aplicó");
        Assert.Equal(777, await EnElLoop(() => aldo.Gold));
    }

    [Fact]
    public async Task Una_transferencia_al_que_esta_entrando_le_llega_viva()
    {
        CuentaConAldo();
        var (beto, sb, tb) = await Conectado("Beto", banco: 1000);
        await AldoEntrando();

        Mandar(sb, new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD).WriteAsciiString("Aldo").WriteLong(300));
        await Vueltas(3);

        var aldo = await AldoAdentro();
        Assert.True(await Esperar(() => tb.Textos().Any(x => x.StartsWith("¡Se ha realizado la transferencia"))),
            "la transferencia no terminó");
        Assert.Equal(300, await EnElLoop(() => aldo.Bank));
        Assert.Equal(700, await EnElLoop(() => beto.Bank));
    }

    /// <summary>
    /// EL PERSONAJE NUEVO ESTÁ RESERVADO mientras la base confirma su alta, como el que entra. Una transferencia a su bóveda
    /// en ese rato no lo trata como desconectado —se acreditaría en la base, y el primer guardado del que entra, armado
    /// antes, la pisaría—: espera a que entre, y le llega viva.
    /// </summary>
    [Fact]
    public async Task Una_transferencia_al_personaje_recien_creado_le_llega_viva()
    {
        CuentaConAldo();
        var (_, sb, tb) = await Conectado("Beto", banco: 1000);
        _almacen.FrenarLaConfirmacion("Nuevo");
        Crear("Nuevo", "tcp:10.0.0.4:2222");
        Assert.True(await Esperar(() => _almacen.Llego), "el alta no llegó a confirmarse");
        Assert.True(await EnElLoop(() => _world.EstaEntrando("Nuevo")), "el nombre no quedó reservado");

        Mandar(sb, new ByteQueueWriter().WriteByte((byte)ClientPacketId.TransferGOLD).WriteAsciiString("Nuevo").WriteLong(300));
        await Vueltas(3);

        _almacen.Soltar();
        Assert.True(await EsperarEnElLoop(() => _world.PlayerByName("Nuevo") is not null), "Nuevo no entró");
        var nuevo = await EnElLoop(() => _world.PlayerByName("Nuevo")!);
        Assert.True(await Esperar(() => tb.Textos().Any(x => x.StartsWith("¡Se ha realizado la transferencia"))),
            "la transferencia no terminó");
        Assert.Equal(300, await EnElLoop(() => nuevo.Bank));
    }

    /// <summary>
    /// DOS ALTAS A LA VEZ CON LA CUENTA EN NUEVE: las dos pasan el tope —miran la cuenta leída antes del hash— y la base toma
    /// una sola. La otra no puede quedar jugando un personaje que no existe: al mundo se entra recién con el alta confirmada.
    /// </summary>
    [Fact]
    public async Task Con_la_cuenta_en_nueve_de_dos_altas_a_la_vez_entra_una_sola()
    {
        const string Limite = "No puedes crear mas personajes, has llegado a tu límite de diez personajes.";
        var cuenta = _db.CreateAccount("cuenta", Clave);
        foreach (var n in new[] { "Uno", "Dos", "Tres", "Cuatro", "Cinco", "Seis", "Siete", "Ocho", "Nueve" })
            _db.CreateCharacter(cuenta, n, 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        _db.EsperarEscriturasPendientes();

        var a = Crear("Diez", "tcp:10.0.0.6:2222");
        var b = Crear("Once", "tcp:10.0.0.7:2222");

        Assert.True(await Esperar(() => a.Carteles().Contains(Limite) || b.Carteles().Contains(Limite)), "ninguno se enteró del tope");
        await Vueltas(5);
        Assert.Equal(1, await EnElLoop(() => _world.Players.Count(p => p.Name is "Diez" or "Once")));
        Assert.Equal(10, (await _db.LeerCuentaAsync("cuenta"))!.Characters.Count);
    }

    /// <summary>
    /// NO SE BORRA AL QUE ESTÁ ENTRANDO. El borrado trae la cuenta y la clave y llega por otra conexión; lo que miraba antes
    /// del hash era el mundo, y el que está entrando todavía no está ahí. Apartado ya adentro, jugaría sin que se guarde
    /// nada, con el nombre libre para otro.
    /// </summary>
    [Fact]
    public async Task No_se_borra_al_que_esta_entrando()
    {
        CuentaConAldo();
        await EnElLoop(() => { _world.ReservarEntrada("Aldo"); return 0; });   // un ingreso de Aldo, leyéndolo de la base

        var t = Borrar("Aldo", "tcp:10.0.0.5:2222");

        Assert.True(await Esperar(() => t.Avisos().Any(a => a.StartsWith("Ese personaje está conectado"))), "el borrado no lo frenó");
        await EnElLoop(() => { _world.SoltarEntrada("Aldo"); return 0; });
        Assert.True(_db.CharacterExists("Aldo"));
        Assert.Contains("Aldo", (await _db.LeerCuentaAsync("cuenta"))!.Characters);
    }

    /// <summary>UN BANEADO NO SE BORRA: el baneo vive en su ficha, y borrarlo dejaba el nombre libre para crearlo de nuevo sin
    /// el baneo (auditoría del 13/10).</summary>
    [Fact]
    public async Task Un_personaje_baneado_no_se_borra()
    {
        CuentaConAldo();
        var aldo = _db.LoadCharacter("Aldo", 0, "cuenta")!;
        aldo.Banned = true;
        _db.SaveCharacter(aldo);
        _db.EsperarEscriturasPendientes();

        var t = Borrar("Aldo", "tcp:10.0.0.5:2222");

        Assert.True(await Esperar(() => t.Avisos().Any(a => a.Contains("baneado"))), "el borrado no lo frenó");
        Assert.True(_db.CharacterExists("Aldo"));
        Assert.Contains("Aldo", (await _db.LeerCuentaAsync("cuenta"))!.Characters);
    }

    /// <summary>EL CENTRO DE SUBASTAS RECONOCE AL DUEÑO POR EL NOMBRE: con algo para entregarle, el personaje no se borra, y
    /// uno nuevo con el mismo nombre no lo cobra (auditoría del 13/10).</summary>
    [Fact]
    public async Task Con_algo_en_el_centro_de_subastas_no_se_borra()
    {
        CuentaConAldo();
        await EnElLoop(() => { _world.Subastas.Cargar(Array.Empty<Subasta>(), new[] { new EntregaDeSubasta("Aldo", 0, 0, 500) }); return 0; });

        var t = Borrar("Aldo", "tcp:10.0.0.5:2222");

        Assert.True(await Esperar(() => t.Avisos().Any(a => a.Contains("Centro de Subastas"))), "el borrado no lo frenó");
        Assert.True(_db.CharacterExists("Aldo"));
    }

    /// <summary>EL CORREO SE VA CON EL PERSONAJE, como en el original (vivía en su .chr): uno nuevo con el mismo nombre se
    /// quedaba con las cartas y los adjuntos del borrado (auditoría del 13/10).</summary>
    [Fact]
    public async Task Al_borrar_un_personaje_se_va_su_correo()
    {
        CuentaConAldo();
        await EnElLoop(() => { _world.DeliverMail("Aldo", new MailEntry("Beto", "para vos", 1, 1)); return 0; });

        var t = Borrar("Aldo", "tcp:10.0.0.5:2222");

        Assert.True(await Esperar(() => t.Avisos().Any(a => a.EndsWith("fue borrado."))), "no se borró");
        Assert.Equal(0, await EnElLoop(() => _world.PeekMail("Aldo").Count));
    }

    /// <summary>
    /// UN BANEO DEL PANEL MIENTRAS SE VERIFICA LA CLAVE: la cuenta se leyó antes del hash, sin el baneo. La lectura del
    /// personaje trae en el mismo pedido si la cuenta está baneada —la cola va en orden, así que ve el baneo— y no entra.
    /// </summary>
    [Fact]
    public async Task Un_baneo_mientras_se_verifica_la_clave_no_lo_deja_entrar()
    {
        CuentaConAldo();
        _almacen.AlLeerCuenta = nombre =>
        {
            if (nombre != "cuenta") return;
            _almacen.AlLeerCuenta = null;
            _ = _db.CambiarCuentaAsync("cuenta", c => c.Banned = true);   // el panel, justo después de la lectura del ingreso
        };

        var t = Entrar("Aldo", "tcp:10.0.0.2:2222");

        Assert.True(await Esperar(() => t.Carteles().Any(c => c.StartsWith("Su cuenta está baneada"))), "no se enteró del baneo");
        await Vueltas(3);
        Assert.Equal(0, await EnElLoop(() => _world.Players.Count(p => p.Name == "Aldo")));
    }

    /// <summary>
    /// CON LA BASE CAÍDA TAMPOCO QUEDA COLGADO EL QUE PONE LA CLAVE: la cuenta se lee de la base antes del hash, y sin plazo el
    /// cliente no recibía nada hasta que lo cortaba el tiempo de autenticación. Pasado el plazo —10 s, acá 300 ms— se le avisa.
    /// </summary>
    [Fact]
    public async Task Si_la_base_no_trae_la_cuenta_a_tiempo_se_avisa()
    {
        CuentaConAldo();
        _dispatcher.PlazoDeLaLecturaDeEntrada = TimeSpan.FromMilliseconds(300);
        using var suelta = new ManualResetEventSlim(false);
        _almacen.AlLeerCuenta = _ => suelta.Wait(TimeSpan.FromSeconds(20));

        var t = Entrar("Aldo", "tcp:10.0.0.2:2222");

        Assert.True(await Esperar(() => t.Carteles().Any(c => c.StartsWith("No se puede entrar en este momento"))), "no se le avisó");
        _almacen.AlLeerCuenta = null;
        suelta.Set();
        await Vueltas(3);
        Assert.Equal(0, await EnElLoop(() => _world.Players.Count(p => p.Name == "Aldo")));
    }

    /// <summary>
    /// EL QUE SALE PARALIZADO VUELVE PARALIZADO, y prendido fuego (VB6 TCP.bas:1869-1882): el original guarda los dos y al
    /// entrar la parálisis vuelve entera (:1930), con el aviso al cliente antes del DumbNoMore (:158). Salir y volver a entrar
    /// no puede ser la cura.
    /// </summary>
    [Fact]
    public async Task El_que_sale_paralizado_vuelve_paralizado_y_prendido_fuego()
    {
        CuentaConAldo();
        var ficha = (await _db.LeerPersonajeAsync("Aldo"))!;
        _db.GuardarFicha(ficha with { Paralizado = true, Incinerado = true });
        _db.EsperarEscriturasPendientes();

        var t = Entrar("Aldo", "tcp:10.0.0.2:2222");

        Assert.True(await EsperarEnElLoop(() => _world.PlayerByName("Aldo") is not null), "Aldo no entró");
        var aldo = await EnElLoop(() => _world.PlayerByName("Aldo")!);
        Assert.True(await EnElLoop(() => aldo.IsParalyzed(_loop.Tick) && aldo.ClientParalyzed && aldo.MarcaParalizado), "no volvió paralizado");
        Assert.True(await EnElLoop(() => aldo.Incinerado), "no volvió prendido fuego");
        var ids = t.Recibidos.Select(x => x[0]).ToList();
        int paralizado = ids.IndexOf((byte)ServerPacketId.ParalizeOK), dumb = ids.IndexOf((byte)ServerPacketId.DumbNoMore);
        Assert.True(paralizado >= 0 && paralizado < dumb, "el ParalizeOK no llegó antes del DumbNoMore");
    }
}
