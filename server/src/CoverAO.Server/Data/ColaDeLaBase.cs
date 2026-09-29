using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CoverAO.Server.Data;

/// <summary>
/// LA COLA DE LA BASE: el único que le habla al almacén mientras el juego corre (fase5/postgres/DISENO.md, sección 3).
///
/// <para><b>El hilo del mundo no toca la base.</b> Todo corre en una vuelta de 40 ms, y una lectura o una escritura hecha
/// desde ahí —un <c>fsync</c> de archivos, un viaje a Postgres— congela el mundo para todos mientras dura. Acá el loop sólo
/// ENCOLA: un hilo propio aplica los pedidos uno detrás de otro, en el orden en que llegaron, y la respuesta vuelve a
/// quien la pidió.</para>
///
/// <para><b>El orden es la garantía.</b> Como hay un solo escritor, una lectura ve todo lo que se pidió escribir antes: el
/// que sale y vuelve a entrar enseguida lee lo que se guardó al salir, sin carrera. Y un cambio a un personaje
/// desconectado (leer, cambiar, guardar) corre entero dentro del escritor, así que dos cambios seguidos al mismo personaje
/// no se pisan.</para>
///
/// <para><b>Si la base no responde</b> —Postgres reiniciándose, un disco lleno, el archivo tomado por un antivirus—, el
/// pedido se reintenta con esperas crecientes (1, 2, 5 y 10 s) y lo demás espera detrás, en orden, en memoria: se avisa una
/// vez cuando se cae y otra cuando vuelve (DISENO.md, 3.5). Un pedido que falla por OTRA cosa se anota y la cola sigue: un
/// personaje que no se pudo guardar no puede frenar a los demás.</para>
///
/// <para><b>Al apagar</b> se espera a que se vacíe; si la base sigue sin responder, la cola se rinde y devuelve lo que no se
/// aplicó, para el archivo de rescate (<see cref="Rescates"/>).</para>
/// </summary>
public sealed class ColaDeLaBase : IDisposable
{
    /// <summary>Un pedido: qué es (para el log), cómo se hace, qué hacer si falla o si la cola se rinde, y —si es una
    /// escritura que se puede guardar para el próximo arranque— su rescate.</summary>
    private sealed class Pedido(string que, Action<IAlmacen> hacer, Action<Exception> fallo, Rescate? rescate)
    {
        public readonly string Que = que;
        public readonly Action<IAlmacen> Hacer = hacer;
        public readonly Action<Exception> Fallo = fallo;
        public readonly Rescate? Rescate = rescate;
        public readonly DateTime Encolado = DateTime.UtcNow;
        /// <summary>El lugar en la cola. Lo que no se aplicó vuelve en este orden: dos guardados del mismo personaje
        /// rescatados al revés dejarían el viejo.</summary>
        public long Orden;
        /// <summary>Ya quedó entre lo que no se aplicó: que no entre dos veces.</summary>
        public bool Apartado;
    }

    /// <summary>Las esperas entre reintentos mientras la base no responde; la última se repite.</summary>
    internal static readonly TimeSpan[] Esperas = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    private readonly IAlmacen _almacen;
    private readonly BlockingCollection<Pedido> _pedidos = new();
    private readonly Thread _escritor;
    private readonly object _deAUno = new();
    private readonly object _estado = new();
    private readonly ManualResetEventSlim _despertar = new(false);
    private readonly List<Pedido> _sinAplicar = new();
    private readonly TimeSpan[] _esperas;
    private readonly object _orden = new();
    private long _siguiente;
    private int _pendientes;
    private Pedido? _enCurso;
    private bool _rendida;
    private DateTime? _caidaDesde;

    public ColaDeLaBase(IAlmacen almacen) : this(almacen, Esperas) { }

    /// <param name="esperas">Las esperas entre reintentos. Las pruebas las acortan.</param>
    internal ColaDeLaBase(IAlmacen almacen, TimeSpan[] esperas)
    {
        _almacen = almacen;
        _esperas = esperas;
        _escritor = new Thread(Correr) { IsBackground = true, Name = "cola-de-la-base" };
        _escritor.Start();
    }

    /// <summary>Cuántos pedidos esperan o están corriendo.</summary>
    public int Pendientes => Volatile.Read(ref _pendientes);

    /// <summary>¿Es el hilo del escritor el que llama? Para los que no pueden esperar a la cola desde adentro de ella.</summary>
    public bool EsElEscritor => Thread.CurrentThread == _escritor;

    /// <summary>Desde cuándo la base no responde, o null si anda. Para el panel.</summary>
    public DateTime? CaidaDesde { get { lock (_estado) return _caidaDesde; } }

    /// <summary>Desde cuándo espera el pedido más viejo —el que está corriendo—, o null si no hay ninguno. Para el panel.</summary>
    public DateTime? EsperandoDesde { get { lock (_estado) return _enCurso?.Encolado; } }

    /// <summary>Encola una escritura y sigue: no espera nada. Si falla, se anota con <paramref name="que"/>. Con
    /// <paramref name="rescate"/>, si al apagar la base no respondía, va al archivo de rescate en vez de perderse.</summary>
    public void Escribir(string que, Action<IAlmacen> escritura, Rescate? rescate = null)
        => Encolar(new Pedido(que, escritura, ex => Console.Error.WriteLine($"[base] {que}: {ex.Message}"), rescate));

    /// <summary>
    /// Pide algo al almacén y devuelve una tarea con la respuesta. La continuación NO corre en el escritor
    /// (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>): lo que venga después —un hash de contraseña, por
    /// ejemplo— no puede frenar a la cola.
    /// </summary>
    public Task<T> PedirAsync<T>(string que, Func<IAlmacen, T> pedido)
    {
        var respuesta = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // TrySet: si la cola se rindió al apagar, el que esperaba ya se enteró, y lo que estaba corriendo puede terminar igual.
        Encolar(new Pedido(que, a => respuesta.TrySetResult(pedido(a)), ex =>
        {
            Console.Error.WriteLine($"[base] {que}: {ex.Message}");
            respuesta.TrySetException(ex);
        }, rescate: null));
        return respuesta.Task;
    }

    /// <summary>A la cola, o —si ya se cerró, al final del apagado— en el acto: lo que llega tarde se guarda igual. En el
    /// acto pero DE A UNO: el escritor puede estar todavía vaciando lo que quedaba, y el almacén no se toca desde dos hilos
    /// (una conexión de SQLite, por ejemplo, no lo aguanta). Para el escritor, el candado nunca tiene a nadie esperando.</summary>
    private void Encolar(Pedido pedido)
    {
        Interlocked.Increment(ref _pendientes);
        try
        {
            lock (_orden)
            {
                pedido.Orden = ++_siguiente;
                _pedidos.Add(pedido);
            }
        }
        catch (InvalidOperationException)
        {
            try { lock (_deAUno) Aplicar(pedido, reintentar: false); }
            finally { Interlocked.Decrement(ref _pendientes); }
        }
    }

    /// <summary>
    /// Espera a que se aplique todo lo encolado HASTA AHORA. La llaman el backup —que no puede sacar la foto antes de que
    /// lleguen los guardados— y los tests. Devuelve false si se venció el plazo.
    /// </summary>
    public bool Vaciar(TimeSpan plazo)
    {
        if (EsElEscritor) return true;   // desde adentro de la cola no hay nada que esperar: lo anterior ya corrió
        try { return PedirAsync("marca de vaciado", _ => true).Wait(plazo); }
        catch (AggregateException) { return false; }   // la cola se rindió antes de llegar a la marca
    }

    private void Correr()
    {
        foreach (var pedido in _pedidos.GetConsumingEnumerable())
        {
            try
            {
                lock (_estado)
                {
                    if (_rendida) { Apartar(pedido); continue; }
                    _enCurso = pedido;
                }
                lock (_deAUno) Aplicar(pedido, reintentar: true);
            }
            finally
            {
                lock (_estado) _enCurso = null;
                Interlocked.Decrement(ref _pendientes);
            }
        }
    }

    /// <summary>Hace el pedido. Si la base no responde, lo reintenta —con lo demás esperando detrás— hasta que vuelva o hasta
    /// que el apagado se rinda. Si falla por otra cosa, se anota y listo.</summary>
    private void Aplicar(Pedido pedido, bool reintentar)
    {
        for (int intento = 0; ; intento++)
        {
            try
            {
                pedido.Hacer(_almacen);
                Volvio();
                return;
            }
            catch (Exception ex) when (reintentar && EsQueLaBaseNoResponde(ex))
            {
                Cayo(ex, pedido.Que);
                if (EsperarParaReintentar(intento)) continue;
                lock (_estado) Apartar(pedido);
                return;
            }
            catch (Exception ex)
            {
                pedido.Fallo(ex);
                return;
            }
        }
    }

    /// <summary>La espera antes del próximo intento. False si en el medio el apagado se rindió.</summary>
    private bool EsperarParaReintentar(int intento)
    {
        _despertar.Wait(_esperas[Math.Min(intento, _esperas.Length - 1)]);
        lock (_estado) return !_rendida;
    }

    /// <summary>Un pedido que no se va a aplicar: queda para el rescate, y el que esperaba una respuesta se entera.</summary>
    private void Apartar(Pedido pedido)
    {
        if (pedido.Apartado) return;
        pedido.Apartado = true;
        _sinAplicar.Add(pedido);
        if (pedido.Rescate is null) pedido.Fallo(new InvalidOperationException("la base no respondía y el servidor se apagó"));
    }

    private void Cayo(Exception ex, string que)
    {
        lock (_estado)
        {
            if (_caidaDesde is not null) return;
            _caidaDesde = DateTime.UtcNow;
        }
        Console.Error.WriteLine($"[base] LA BASE NO RESPONDE ({ex.Message}). Se reintenta sola; lo que se guarda espera en orden, en " +
                                $"memoria. El primero que espera: {que}.");
    }

    private void Volvio()
    {
        DateTime desde;
        lock (_estado)
        {
            if (_caidaDesde is not { } caida) return;
            desde = caida;
            _caidaDesde = null;
        }
        Console.Error.WriteLine($"[base] la base volvió después de {(DateTime.UtcNow - desde).TotalSeconds:0} s; sigue la cola " +
                                $"({Pendientes} pedido(s)).");
    }

    /// <summary>
    /// ¿Falló porque la base no responde —y reintentar tiene sentido— o por otra cosa? No responde: Postgres caído o
    /// reiniciándose (lo que Npgsql llama transitorio), SQLite con el archivo tomado, lleno o sin poder abrirse, un archivo
    /// tomado por otro programa o el disco lleno, la red. NO cualquier error de disco: uno que falla siempre —un nombre de
    /// archivo inválido— reintentado frenaría la cola para siempre.
    /// </summary>
    internal static bool EsQueLaBaseNoResponde(Exception ex) => ex switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        // BUSY, LOCKED, IOERR, FULL, CANTOPEN
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 or 10 or 13 or 14,
        IOException io => EsArchivoTomadoODiscoLleno(io),
        SocketException or TimeoutException => true,
        _ => ex.InnerException is { } interna && EsQueLaBaseNoResponde(interna),
    };

    /// <summary>Windows: ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL. Linux: ENOSPC.</summary>
    private static bool EsArchivoTomadoODiscoLleno(IOException io)
        => OperatingSystem.IsWindows() ? (io.HResult & 0xFFFF) is 32 or 33 or 39 or 112 : io.HResult == 28;

    /// <summary>
    /// EL APAGADO: deja de aceptar pedidos y espera, hasta <paramref name="plazo"/>, a que corra lo que quedaba. Si se vence
    /// —la base no responde—, la cola se rinde: devuelve lo que no se aplicó, en orden, y el que tenga rescate va al archivo
    /// de rescate. Lo que estaba corriendo justo puede terminar igual después; su rescate aplicado otra vez da lo mismo,
    /// porque cada uno guarda un estado entero.
    /// </summary>
    public IReadOnlyList<(string Que, Rescate? Rescate)> Cerrar(TimeSpan plazo)
    {
        _pedidos.CompleteAdding();
        if (EsElEscritor || _escritor.Join(plazo)) return Resultado();
        lock (_estado)
        {
            _rendida = true;
            if (_enCurso is { } corriendo) Apartar(corriendo);
            while (_pedidos.TryTake(out var resto))
            {
                Apartar(resto);
                Interlocked.Decrement(ref _pendientes);
            }
        }
        // Si el escritor quedó trabado adentro de la base, no se lo espera más que esto: lo suyo ya quedó apartado arriba.
        _despertar.Set();
        _escritor.Join(plazo < TimeSpan.FromSeconds(5) ? plazo : TimeSpan.FromSeconds(5));
        return Resultado();

        IReadOnlyList<(string, Rescate?)> Resultado()
        {
            lock (_estado) return _sinAplicar.OrderBy(p => p.Orden).Select(p => (p.Que, p.Rescate)).ToList();
        }
    }

    /// <summary>Deja de aceptar pedidos y espera a que corra lo que quedaba (hasta 30 s).</summary>
    public void Dispose() => Cerrar(TimeSpan.FromSeconds(30));
}
