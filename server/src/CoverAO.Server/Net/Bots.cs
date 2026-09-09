using System.Security.Cryptography;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>Cómo va a intentar entrar el bot en su próxima vida. Si un intento falla se pasa a la siguiente,
/// así que en tres vidas prueba las tres formas: la cuenta y el personaje ya existen, la cuenta existe pero
/// le falta el personaje, y no existe nada.</summary>
internal enum ComoEntrar { Entrar, SoloPersonaje, Alta }

/// <summary>
/// Un bot: una cuenta, un personaje y una conducta. Habla el protocolo AO por un
/// <see cref="TransporteDeBot"/>, así que para el resto del servidor es un jugador más — pasa por el mismo
/// login, los mismos intervalos, el mismo anticheat y los mismos topes de cadencia.
///
/// <para><b>Lo que sabe hacer hoy es vivir</b>: entrar, caminar con pausas, aguantar horas y volver a entrar
/// si se cae. No caza, no comercia y no habla. Tampoco mira lo que el servidor le manda, salvo dos paquetes
/// —el <c>Logged</c>, que trae la clave de sesión, y el <c>ConsoleMsg</c>, para poder decir POR QUÉ no pudo
/// entrar—: un bot que no interpreta el mundo no puede reaccionar a él, y ése es el límite de esta etapa.</para>
///
/// <para><b>El ritmo no es decorativo.</b> Un bot que camina a intervalos clavados se distingue de un humano
/// desde la primera pantalla, y además choca contra los relojes del servidor: el paso mínimo lo pone
/// <c>IntervaloParaCaminar</c> y el techo de paquetes por segundo lo corta la sesión. Los números de acá
/// están holgados contra los dos.</para>
/// </summary>
public sealed class Bot
{
    // ── El ritmo humano ───────────────────────────────────────────────────────────────────────────────
    private const int PasoMinMs = 180, PasoMaxMs = 420;         // entre dos pasos de una misma caminata
    private const int PasosMin = 2, PasosMax = 7;               // largo de una caminata antes de doblar
    private const int PausaMinMs = 1_500, PausaMaxMs = 9_000;   // parado entre caminata y caminata
    private const int QuedoMinMs = 15_000, QuedoMaxMs = 60_000; // el rato largo sin hacer nada
    private const int UnaDeCadaTantas = 8;                      // cada cuántas pausas toca una larga
    private const int MirarAlrededorMs = 900;                   // lo que tarda en dar el primer paso al entrar

    /// <summary>Cuánto se le da al servidor para contestar un paso del login. Generoso a propósito: verificar
    /// una contraseña son 210.000 vueltas de PBKDF2 y, con varios bots entrando a la vez, esperan en fila.</summary>
    private const int PlazoDeRespuestaMs = 20_000;

    /// <summary>Intentos seguidos de entrar sin lograrlo antes de que el bot se dé por vencido y lo diga. No
    /// se reintenta para siempre: si el nombre lo tiene un jugador de verdad, o el mundo está lleno, insistir
    /// cada quince segundos hasta que alguien apague el servidor no arregla nada y ensucia el log.</summary>
    private const int IntentosAntesDeRendirse = 3;

    private const int ReconectaMinS = 5, ReconectaMaxS = 20;

    /// <summary>La MAC que declara el cliente. Los bots declaran todos la misma y no molesta: el servidor la
    /// registra pero no cuenta cupos por ella (los cupos son por dirección, y cada bot tiene la suya).</summary>
    private const string Mac = "00-00-00-00-00-00";

    public string Nombre { get; }
    private readonly string _cuenta;
    private readonly EntornoDeBots _entorno;
    private readonly Random _rng;
    private string? _clave;
    private ComoEntrar _como;

    public Bot(string nombre, string? clave, EntornoDeBots entorno, int semilla)
    {
        Nombre = nombre;
        _cuenta = nombre + "_bot";
        _clave = clave;
        _entorno = entorno;
        _rng = new Random(semilla);
        _como = clave is null ? ComoEntrar.Alta : ComoEntrar.Entrar;
    }

    /// <summary>La vida del bot: entrar, vivir, y volver a entrar cuando se cae. Termina cuando se apaga el
    /// servidor o cuando se da por vencido.</summary>
    public async Task VivirAsync(CancellationToken ct)
    {
        int fracasos = 0;
        while (!ct.IsCancellationRequested && fracasos < IntentosAntesDeRendirse)
        {
            bool entro;
            try { entro = await UnaVidaAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[bot {Nombre}] error: {ex.Message}"); entro = false; }

            if (entro) fracasos = 0;
            else { fracasos++; _como = _como == ComoEntrar.Alta ? ComoEntrar.Entrar : _como + 1; }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(_rng.Next(ReconectaMinS, ReconectaMaxS + 1)), ct); }
            catch (OperationCanceledException) { break; }
        }
        if (fracasos >= IntentosAntesDeRendirse)
            Console.WriteLine($"[bot {Nombre}] no pudo entrar {fracasos} veces seguidas; se queda afuera.");
    }

    /// <summary>Una conexión, de punta a punta. Devuelve si llegó a estar adentro del mundo, para distinguir
    /// "se cayó jugando" —que se reintenta sin castigo— de "no pudo entrar", que cuenta como fracaso.</summary>
    private async Task<bool> UnaVidaAsync(CancellationToken ct)
    {
        var transporte = new TransporteDeBot(Nombre);
        var enElMundo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tieneCuenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cerrada = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string aviso = "";

        transporte.Recibido += p =>
        {
            if (p.Length == 0) return;
            switch ((ServerPacketId)p[0])
            {
                case ServerPacketId.Logged: enElMundo.TrySetResult(); break;
                case ServerPacketId.AbrirFormularios: tieneCuenta.TrySetResult(); break;
                case ServerPacketId.ConsoleMsg: aviso = TextoDeConsola(p); break;
            }
        };
        transporte.Cerrado += motivo =>
        {
            cerrada.TrySetResult(motivo);
            // Destraba al que esté esperando un paso del login: sin esto, un rechazo se pagaría con el plazo
            // entero de espera aunque la sesión ya esté cerrada.
            enElMundo.TrySetException(new IOException(motivo));
            tieneCuenta.TrySetException(new IOException(motivo));
        };

        var ini = _entorno.Ini;
        var sesion = new ClientSession(transporte, _entorno.Dispatcher, _entorno.AuthTimeoutMs, _entorno.MaxInboundBytes,
            paquetesPorSegundo: ini.MaxPaquetesPorSegundo, rafagaDePaquetes: ini.RafagaDePaquetes,
            msPorMensaje: ini.MsEntreMensajes, rafagaMensajes: ini.RafagaDeMensajes,
            msPorConsulta: ini.MsEntreConsultas, rafagaConsultas: ini.RafagaDeConsultas);
        // El bucle de recepción corre aparte, igual que en el aceptador de TCP: es el que framea lo que manda
        // el bot y se lo pasa al dispatcher.
        var lector = Task.Run(() => sesion.ReceiveLoopAsync(ct), CancellationToken.None);

        try
        {
            if (!await EntrarAlMundoAsync(transporte, enElMundo, tieneCuenta, ct))
            {
                Console.WriteLine($"[bot {Nombre}] no entró{(aviso.Length > 0 ? ": " + aviso : "")}");
                return false;
            }
            _como = ComoEntrar.Entrar;   // ya existen la cuenta y el personaje: de acá en más entra como cualquiera
            await ComportarseAsync(transporte, cerrada.Task, ct);
            return true;
        }
        finally
        {
            transporte.Terminar();
            try { await lector.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>El login, por la forma que toque. false = el servidor lo rechazó, o no contestó.</summary>
    private async Task<bool> EntrarAlMundoAsync(TransporteDeBot transporte, TaskCompletionSource enElMundo,
        TaskCompletionSource tieneCuenta, CancellationToken ct)
    {
        if (_como == ComoEntrar.Alta)
        {
            _clave ??= NuevaClave();
            if (!transporte.Mandar(CrearCuenta())) return false;
            if (!await Espera(tieneCuenta.Task, ct)) return false;
            // La cuenta ya existe en el disco, así que la contraseña se guarda ACÁ y no al final. Si el
            // servidor se cayera entre la creación de la cuenta y la del personaje, sin este guardado quedaría
            // una cuenta cuya clave no conoce nadie y el bot no podría volver a entrar nunca.
            _entorno.GuardarClave(Nombre, _clave);
            _como = ComoEntrar.SoloPersonaje;
        }

        byte[] paquete = _como == ComoEntrar.SoloPersonaje ? CrearPersonaje() : Entrar();
        if (!transporte.Mandar(paquete)) return false;
        return await Espera(enElMundo.Task, ct);
    }

    /// <summary>Caminar por ahí hasta que la sesión se caiga o se apague el servidor.</summary>
    private async Task ComportarseAsync(TransporteDeBot transporte, Task cerrada, CancellationToken ct)
    {
        await Task.Delay(_rng.Next(MirarAlrededorMs, MirarAlrededorMs * 4), ct);
        int pausas = 0;
        while (!ct.IsCancellationRequested && !cerrada.IsCompleted)
        {
            // UNA CAMINATA: varios pasos en la misma dirección, como quien va a algún lado. Si hay una pared
            // adelante el servidor no lo mueve, y el bot dobla igual al terminar la tanda; que se quede unos
            // pasos empujando una pared es lo mismo que hace un jugador distraído.
            byte rumbo = (byte)_rng.Next(1, 5);   // 1=N, 2=E, 3=S, 4=O
            int pasos = _rng.Next(PasosMin, PasosMax);
            for (int i = 0; i < pasos && !cerrada.IsCompleted; i++)
            {
                if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return;
                await Task.Delay(_rng.Next(PasoMinMs, PasoMaxMs), ct);
            }
            bool larga = ++pausas % UnaDeCadaTantas == 0;
            await Task.Delay(larga ? _rng.Next(QuedoMinMs, QuedoMaxMs) : _rng.Next(PausaMinMs, PausaMaxMs), ct);
        }
    }

    // ── Los paquetes ──────────────────────────────────────────────────────────────────────────────────

    private byte[] CrearCuenta() => Paquete(ClientPacketId.CreateNewAccount, w => w
        .WriteAsciiString(_cuenta).WriteAsciiString(_clave!).WriteAsciiString(CodigoDeRecuperacion())
        .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision));

    private byte[] CrearPersonaje()
    {
        var (raza, genero, clase, hogar, cabeza) = Aspecto();
        return Paquete(ClientPacketId.LoginNewChar, w => w
            .WriteAsciiString(_cuenta).WriteAsciiString(_clave!)
            .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision)
            .WriteAsciiString(Nombre)
            .WriteByte(raza).WriteByte(genero).WriteByte(clase).WriteByte(hogar)
            .WriteInteger(cabeza)
            .WriteAsciiString(Mac).WriteLong(0));
    }

    private byte[] Entrar() => Paquete(ClientPacketId.LoginExistingChar, w => w
        .WriteAsciiString(_cuenta).WriteAsciiString(_clave!)
        .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision)
        .WriteAsciiString(Nombre).WriteAsciiString(Mac).WriteLong(0));

    private static byte[] Paquete(ClientPacketId id, Action<ByteQueueWriter> campos)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id);
        campos(w);
        return w.ToArray();
    }

    // ── Quién es este bot ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Raza, género, clase, tierra natal y cara. Sale de una semilla derivada del NOMBRE y no del
    /// azar del arranque: así el bot se ve siempre igual, aunque alguna vez haya que recrearle el personaje.</summary>
    private (byte Raza, byte Genero, byte Clase, byte Hogar, short Cabeza) Aspecto()
    {
        int semilla = 17;
        foreach (char c in Nombre) semilla = unchecked(semilla * 31 + c);
        var r = new Random(semilla);
        byte genero = (byte)r.Next(1, 3);
        byte raza = (byte)r.Next(1, 7);            // 1..6: las que tienen tabla de cabezas (Equipment.HeadRange)
        byte clase = (byte)r.Next(1, 19);
        byte hogar = (byte)r.Next(1, Cities.MaxTierraNatal + 1);
        var rango = Equipment.HeadRange(raza, genero);
        short cabeza = rango is null ? (short)1 : (short)r.Next(rango.Value.Min, rango.Value.Max + 1);
        return (raza, genero, clase, hogar, cabeza);
    }

    /// <summary>Contraseña larga y al azar. NO se deriva del nombre: una clave que se puede calcular sabiendo
    /// de quién es la cuenta es una puerta de atrás a esa cuenta, aunque la cuenta sea de un bot.</summary>
    private static string NuevaClave() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>El PIN de recuperación: cuatro números, como pide el alta de cuentas. Al azar, y no se guarda
    /// en ningún lado — con la contraseña alcanza para volver a entrar, y con el PIN se le cambia la
    /// contraseña a la cuenta.</summary>
    private static string CodigoDeRecuperacion() => RandomNumberGenerator.GetInt32(1000, 10000).ToString();

    /// <summary>La versión que declara el bot es la de ESTE servidor, así entra igual si el host prende el
    /// control de versión del cliente (la clave <c>Version</c> del Server.ini) con la que repartió.</summary>
    private static readonly (byte Mayor, byte Menor, byte Revision) Version = LeerVersion();

    private static (byte, byte, byte) LeerVersion()
    {
        var partes = Build.Version.Split('.');
        byte Parte(int i) => partes.Length > i && byte.TryParse(partes[i], out byte b) ? b : (byte)0;
        return (Parte(0), Parte(1), Parte(2));
    }

    // ── Ayudas ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El texto de un <c>ConsoleMsg</c>: id, Int16 de largo, y los bytes. Se decodifica a mano
    /// justamente porque es el ÚNICO paquete con contenido que el bot mira, y sólo para poder contar por qué
    /// el servidor lo rechazó. Interpretar el resto del mundo es la etapa siguiente.</summary>
    private static string TextoDeConsola(byte[] p)
    {
        if (p.Length < 3) return "";
        int largo = p[1] | (p[2] << 8);
        if (largo <= 0 || 3 + largo > p.Length) return "";
        return Codec.Ansi.GetString(p, 3, largo);
    }

    /// <summary>Espera un paso del login, con plazo. false = el servidor no contestó, o cerró la sesión.</summary>
    private static async Task<bool> Espera(Task senal, CancellationToken ct)
    {
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var demora = Task.Delay(PlazoDeRespuestaMs, plazo.Token);
        var cual = await Task.WhenAny(senal, demora);
        plazo.Cancel();   // corta la demora si ganó la señal
        return cual == senal && senal.IsCompletedSuccessfully;   // el cierre de la sesión la completa con excepción
    }
}
