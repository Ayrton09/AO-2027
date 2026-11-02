using System.Diagnostics;
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
/// <para><b>Mientras no responde, lo que espera se vuelca al archivo de rescate</b> (<see cref="Volcar"/>): un corte de luz o
/// un proceso matado en medio de una caída larga se llevaba todo lo de la caída, que estaba sólo en memoria. Cuando vuelve,
/// el volcado se borra recién cuando se aplicó lo último que cubre, y la cola sigue como siempre.</para>
///
/// <para><b>Un guardado que otro más nuevo ya cubre no espera</b> (<see cref="Escribir"/> con clave): con la base caída, cada
/// autoguardado sumaba una ficha por conectado y la cola crecía sin tope. El viejo de una misma cosa (el personaje, el
/// suelo, las subastas) se descarta al llegar el nuevo, que guarda el estado entero, y la cola queda en una ficha por
/// personaje.</para>
///
/// <para><b>La lectura que se puede abandonar</b> (como la cuenta del ingreso, que deja de esperar a los 10 s) no se hace si el
/// que la pidió ya se fue: sale de la cola sin tocar la base. Y las que esperan tienen tope: pasado, la lectura falla en el acto
/// con <see cref="LaBaseNoResponde"/>. Al alcanzar el límite de pedidos se pide el apagado ordenado del servidor, antes de
/// aceptar más acciones del mundo. Las escrituras de las acciones que ya cambiaron el mundo se conservan.</para>
///
/// <para><b>Al apagar</b> se espera a que se vacíe; si la base sigue sin responder, la cola se rinde y devuelve lo que no se
/// aplicó, para el archivo de rescate (<see cref="Rescates"/>).</para>
/// </summary>
public sealed class ColaDeLaBase : IDisposable
{
    /// <summary>Un pedido: qué es (para el log), cómo se hace, qué hacer si falla o si la cola se rinde, y —si es una
    /// escritura que se puede guardar para el próximo arranque— su rescate.</summary>
    private sealed class Pedido(string que, Action<IAlmacen> hacer, Action<Exception> fallo, Func<bool, Rescate?>? rescate,
        CancellationToken abandono = default, IReadOnlyList<string>? claves = null, bool superable = false)
    {
        public readonly string Que = que;
        // No son readonly: el que queda superado suelta lo que guardaba (ver Superar).
        public Action<IAlmacen> Hacer = hacer;
        public readonly Action<Exception> Fallo = fallo;
        /// <summary>Arma el rescate en el momento de apartarlo (con true) o de volcarlo (con false, sin apartarlo): el de una
        /// suma necesita saber hasta dónde llegó (<see cref="Persistence.AcreditarBovedaAsync"/>).</summary>
        public Func<bool, Rescate?>? Rescate = rescate;
        /// <summary>Lo que guarda entero (<c>personaje:NOMBRE</c>, <c>suelo</c>...): un guardado anterior de lo mismo que todavía
        /// espera queda superado por éste.</summary>
        public readonly IReadOnlyList<string> Claves = claves ?? Array.Empty<string>();
        /// <summary>Un guardado de una sola cosa, que uno más nuevo puede superar. Los que van juntos no: descartar una parte
        /// dejaría la otra sola.</summary>
        public readonly bool Superable = superable;
        /// <summary>0 esperando, 1 tomado por el escritor, 2 superado. La extracción y la consolidación se excluyen con
        /// el candado de la lista.</summary>
        public int Estado;
        /// <summary>El rescate con el que quedó apartado, o null.</summary>
        public Rescate? Rescatado;
        /// <summary>Se cancela cuando el que lo pidió deja de esperarlo. Sólo lo traen las lecturas que se pueden abandonar.</summary>
        public readonly CancellationToken Abandono = abandono;
        public readonly DateTime Encolado = DateTime.UtcNow;
        /// <summary>El lugar en la cola. Lo que no se aplicó vuelve en este orden: dos guardados del mismo personaje
        /// rescatados al revés dejarían el viejo.</summary>
        public long Orden;
        /// <summary>Ya quedó entre lo que no se aplicó: que no entre dos veces.</summary>
        public bool Apartado;
        public LinkedListNode<Pedido>? Nodo;
    }

    /// <summary>Las esperas entre reintentos mientras la base no responde; la última se repite.</summary>
    internal static readonly TimeSpan[] Esperas = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    /// <summary>Cuántas lecturas que se pueden abandonar esperan a la vez, como mucho. El ingreso tiene una en vuelo por
    /// conexión (<c>ClientSession.TomarVerificacion</c>), y 4000 es el tope de conexiones por defecto (Program.cs,
    /// <c>ConnectionGuard</c>).</summary>
    internal const int TopeDeLecturasAbandonables = 4000;
    internal const int TopeDePedidos = 10_000;

    private readonly IAlmacen _almacen;
    // Bajo _orden. La lista permite sacar de verdad un guardado superado: dejar una marca en una cola concurrente seguía
    // acumulando un objeto Pedido por autosave mientras el escritor estaba trabado.
    private readonly LinkedList<Pedido> _pedidos = new();
    private readonly AutoResetEvent _hayPedidos = new(false);
    private bool _cerrada;
    private readonly Thread _escritor;
    private readonly object _deAUno = new();
    private readonly object _estado = new();
    private readonly ManualResetEventSlim _despertar = new(false);
    private readonly List<Pedido> _sinAplicar = new();
    private readonly TimeSpan[] _esperas;
    private readonly object _orden = new();
    private long _siguiente;
    /// <summary>El último guardado de cada clave que todavía espera (ver <see cref="Pedido.Claves"/>). Bajo <see cref="_orden"/>.</summary>
    private readonly Dictionary<string, Pedido> _esperaPorClave = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>El lugar del último pedido con respuesta (una lectura, una marca de vaciado): un guardado anterior a él no se
    /// supera, porque la lectura tiene que verlo.</summary>
    private long _ultimaRespuesta;
    private long _superados;
    private int _avisoDeCola;
    private int _pendientes;
    private int _abandonables;
    private readonly int _topeDeAbandonables;
    private readonly int _topeDePedidos;
    private int _saturada;
    private Pedido? _enCurso;
    private bool _rendida;
    private DateTime? _caidaDesde;

    /// <summary>Cada cuánto se vuelve a volcar lo que espera mientras la base no responde, si llegó algo nuevo. El primero va
    /// apenas se cae.</summary>
    internal static readonly TimeSpan CadaVolcado = TimeSpan.FromSeconds(30);

    private readonly string? _carpetaDelRescate;
    private readonly TimeSpan _cadaVolcado;
    private readonly object _volcando = new();
    /// <summary>El archivo del volcado de esta caída, o null.</summary>
    private string? _volcado;
    /// <summary>Lo del volcado todavía no se aplicó entero. False: ya se aplicó todo lo que cubre y el archivo no se pudo
    /// borrar; el próximo volcado o el rescate del apagado lo pisan.</summary>
    private bool _volcadoVigente;
    private long _proximoVolcado;
    /// <summary>El último lugar de la cola que había cuando se volcó: si no llegó nada, no se vuelve a escribir lo mismo.</summary>
    private long _volcadoHasta;

    public ColaDeLaBase(IAlmacen almacen) : this(almacen, Esperas) { }

    /// <param name="esperas">Las esperas entre reintentos. Las pruebas las acortan.</param>
    /// <param name="topeDeLecturas">El tope de las lecturas que se pueden abandonar. Las pruebas lo achican.</param>
    /// <param name="carpetaDelRescate">Dónde va el volcado mientras la base no responde (<see cref="Volcar"/>). Sin ella, no se
    /// vuelca.</param>
    /// <param name="cadaVolcado">Cada cuánto se vuelve a volcar. Las pruebas lo acortan.</param>
    /// <param name="topeDePedidos">Umbral que pide el apagado ordenado. El host detiene nuevas acciones antes de reservar
    /// espacio para los guardados de la acción en curso y del cierre.</param>
    internal ColaDeLaBase(IAlmacen almacen, TimeSpan[] esperas, int topeDeLecturas = TopeDeLecturasAbandonables,
        string? carpetaDelRescate = null, TimeSpan? cadaVolcado = null, int topeDePedidos = TopeDePedidos)
    {
        _almacen = almacen;
        _esperas = esperas;
        _topeDeAbandonables = topeDeLecturas;
        ArgumentOutOfRangeException.ThrowIfLessThan(topeDePedidos, 1);
        _topeDePedidos = topeDePedidos;
        _carpetaDelRescate = carpetaDelRescate;
        _cadaVolcado = cadaVolcado ?? CadaVolcado;
        _escritor = new Thread(Correr) { IsBackground = true, Name = "cola-de-la-base" };
        _escritor.Start();
    }

    /// <summary>Cuántos pedidos esperan o están corriendo.</summary>
    public int Pendientes => Volatile.Read(ref _pendientes);

    /// <summary>Se alcanzó el límite: no se admiten nuevas acciones del mundo y el servidor inicia su apagado ordenado.
    /// No vuelve a false: reanudar en medio del apagado permitiría productores nuevos sobre un rescate en curso.</summary>
    public bool Saturada => Volatile.Read(ref _saturada) != 0;

    /// <summary>Una sola vez y fuera de los candados de la cola. El host debe detener los productores y guardar el mundo.
    /// No se rechaza la escritura que lo disparó: el llamador puede haber descontado ya oro u objetos.</summary>
    public event Action? LimiteAlcanzado;

    /// <summary>Cuántos guardados se descartaron porque uno más nuevo de lo mismo ya esperaba.</summary>
    public long Superados => Interlocked.Read(ref _superados);

    /// <summary>El volcado de la caída, si hay uno en el disco. Para las pruebas.</summary>
    internal string? Volcado { get { lock (_volcando) return _volcado; } }

    /// <summary>¿Es el hilo del escritor el que llama? Para los que no pueden esperar a la cola desde adentro de ella.</summary>
    public bool EsElEscritor => Thread.CurrentThread == _escritor;

    /// <summary>Desde cuándo la base no responde, o null si anda. Para el panel.</summary>
    public DateTime? CaidaDesde { get { lock (_estado) return _caidaDesde; } }

    /// <summary>Desde cuándo espera el pedido más viejo —el que está corriendo—, o null si no hay ninguno. Para el panel.</summary>
    public DateTime? EsperandoDesde { get { lock (_estado) return _enCurso?.Encolado; } }

    /// <summary>Encola una escritura y sigue: no espera nada. Si falla, se anota con <paramref name="que"/>. Con
    /// <paramref name="rescate"/>, si al apagar la base no respondía, va al archivo de rescate en vez de perderse.
    ///
    /// <para>Con <paramref name="clave"/>, guarda el estado ENTERO de esa cosa: el guardado anterior de la misma clave que
    /// todavía espera se descarta (<see cref="Superar"/>). Con <paramref name="cubre"/>, es una escritura de varias cosas juntas
    /// que descarta los guardados anteriores de cada una, pero a la que nadie descarta.</para></summary>
    public void Escribir(string que, Action<IAlmacen> escritura, Rescate? rescate = null, string? clave = null,
        IReadOnlyList<string>? cubre = null)
        => Encolar(new Pedido(que, escritura, ex => Console.Error.WriteLine($"[base] {que}: {ex.Message}"),
                              rescate is null ? null : _ => rescate,
                              claves: clave is not null ? new[] { clave } : cubre, superable: clave is not null));

    /// <summary>
    /// Pide algo al almacén y devuelve una tarea con la respuesta. La continuación NO corre en el escritor
    /// (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>): lo que venga después —un hash de contraseña, por
    /// ejemplo— no puede frenar a la cola.
    ///
    /// <para>Con <paramref name="abandono"/>, es una lectura que el que la pide puede dejar de esperar: cancelado, la lectura
    /// sale de la cola sin hacerse. Si ya hay <see cref="TopeDeLecturasAbandonables"/> de ésas esperando, falla en el acto con
    /// <see cref="LaBaseNoResponde"/>.</para>
    ///
    /// <para>Con <paramref name="rescate"/>, si al apagar la base no respondía, va al archivo de rescate con lo que devuelva
    /// <paramref name="rescate"/>(true) en ese momento, y la tarea queda sin terminar: lo suyo se aplica en el próximo arranque, y
    /// avisarle que falló lo haría deshacer algo que igual se va a hacer. Con false es el volcado de una caída, que no lo
    /// aparta: el pedido se sigue haciendo cuando la base vuelva.</para>
    /// </summary>
    public Task<T> PedirAsync<T>(string que, Func<IAlmacen, T> pedido, CancellationToken abandono = default,
        Func<bool, Rescate?>? rescate = null)
    {
        if (abandono.CanBeCanceled && Interlocked.Increment(ref _abandonables) > _topeDeAbandonables)
        {
            Interlocked.Decrement(ref _abandonables);
            return Task.FromException<T>(new LaBaseNoResponde($"{que}: ya hay {_topeDeAbandonables} lecturas esperando a la base"));
        }
        var respuesta = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // TrySet: si la cola se rindió al apagar, el que esperaba ya se enteró, y lo que estaba corriendo puede terminar igual.
        Encolar(conRespuesta: true, pedido: new Pedido(que, a => respuesta.TrySetResult(pedido(a)), ex =>
        {
            // La abandonada no se anota: el que la pidió ya se fue.
            if (ex is OperationCanceledException cancelada && abandono.CanBeCanceled && cancelada.CancellationToken == abandono)
            {
                respuesta.TrySetCanceled(abandono);
                return;
            }
            Console.Error.WriteLine($"[base] {que}: {ex.Message}");
            respuesta.TrySetException(ex);
        }, rescate, abandono));
        return respuesta.Task;
    }

    /// <summary>A la cola, o —si ya se cerró, al final del apagado— en el acto: lo que llega tarde se guarda igual. En el
    /// acto pero DE A UNO: el escritor puede estar todavía vaciando lo que quedaba, y el almacén no se toca desde dos hilos
    /// (una conexión de SQLite, por ejemplo, no lo aguanta). Para el escritor, el candado nunca tiene a nadie esperando.</summary>
    private void Encolar(Pedido pedido, bool conRespuesta = false)
    {
        Interlocked.Increment(ref _pendientes);
        bool cerrada;
        lock (_orden)
        {
            pedido.Orden = ++_siguiente;
            cerrada = _cerrada;
            if (!cerrada)
            {
                if (conRespuesta) _ultimaRespuesta = pedido.Orden;
                foreach (var clave in pedido.Claves)
                {
                    if (_esperaPorClave.Remove(clave, out var anterior) && anterior.Orden > _ultimaRespuesta) Superar(anterior);
                    if (pedido.Superable) _esperaPorClave[clave] = pedido;
                }
                pedido.Nodo = _pedidos.AddLast(pedido);
            }
        }
        if (cerrada)
        {
            try
            {
                // El host puede rendir la cola antes de tomar las últimas fichas del mundo. Esas fichas tienen que ir al
                // mismo rescate, sin volver a tocar una base que sigue caída ni bloquear detrás del escritor trabado.
                lock (_estado)
                    if (_rendida) { Apartar(pedido); return; }
                lock (_deAUno)
                {
                    lock (_estado)
                        if (_rendida) { Apartar(pedido); return; }
                    Aplicar(pedido, reintentar: false);
                }
            }
            finally { Salio(pedido); }
            return;
        }
        _hayPedidos.Set();
        int pendientes = Pendientes;
        if (pendientes >= _topeDePedidos && Interlocked.Exchange(ref _saturada, 1) == 0)
        {
            Console.Error.WriteLine($"[base] se alcanzó el límite de {_topeDePedidos} pedidos: se inicia el apagado ordenado para conservar lo pendiente.");
            LimiteAlcanzado?.Invoke();
        }
        if (CaidaDesde is not null) AvisarSiCrece(pendientes);
    }

    /// <summary>
    /// UN GUARDADO QUE OTRO MÁS NUEVO DE LO MISMO YA CUBRE, todavía sin tomar: no se va a hacer, y suelta lo que tenía (la ficha,
    /// la foto del suelo) para que la memoria no crezca con la caída. Que el nuevo lo cubra es seguro porque los dos guardan el
    /// estado entero de la misma cosa, y entre los dos no hay ninguna lectura que tuviera que ver el viejo
    /// (<see cref="_ultimaRespuesta"/>). Las escrituras de varias cosas juntas no se superan nunca.
    /// </summary>
    private void Superar(Pedido anterior)
    {
        if (anterior.Estado != 0 || anterior.Nodo is null) return;   // el escritor ya lo tomó; todo bajo _orden
        anterior.Estado = 2;
        _pedidos.Remove(anterior.Nodo);
        anterior.Nodo = null;
        anterior.Hacer = _ => { };
        anterior.Rescate = null;
        Interlocked.Increment(ref _superados);
        Salio(anterior);
    }

    /// <summary>El pedido ya no está en la cola: aplicado, abandonado, apartado o superado.</summary>
    private void Salio(Pedido pedido)
    {
        if (pedido.Superable)
            lock (_orden)
                foreach (var clave in pedido.Claves)
                    if (_esperaPorClave.TryGetValue(clave, out var esperando) && esperando == pedido) _esperaPorClave.Remove(clave);
        Interlocked.Decrement(ref _pendientes);
        if (pedido.Abandono.CanBeCanceled) Interlocked.Decrement(ref _abandonables);
    }

    /// <summary>Con la base caída, avisa cuando la cola pasa de mil, diez mil y cien mil pedidos esperando.</summary>
    private void AvisarSiCrece(int pendientes)
    {
        int nivel = pendientes >= 100_000 ? 3 : pendientes >= 10_000 ? 2 : pendientes >= 1_000 ? 1 : 0;
        if (nivel == 0 || nivel <= Volatile.Read(ref _avisoDeCola)) return;
        if (Interlocked.Exchange(ref _avisoDeCola, nivel) >= nivel) return;
        Console.Error.WriteLine($"[base] OJO: {pendientes} pedidos esperan a la base ({Superados} guardados viejos ya descartados " +
                                "porque había uno más nuevo de lo mismo).");
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
        while (true)
        {
            Pedido? pedido = null;
            bool cerrada;
            lock (_estado)
                lock (_orden)
                {
                    cerrada = _cerrada;
                    if (_pedidos.First is { } primero)
                    {
                        pedido = primero.Value;
                        _pedidos.RemoveFirst();
                        pedido.Nodo = null;
                        pedido.Estado = 1;
                        _enCurso = pedido;
                    }
                }
            if (pedido is null)
            {
                if (cerrada) return;
                _hayPedidos.WaitOne();
                continue;
            }
            try
            {
                lock (_estado)
                {
                    if (_rendida) { Apartar(pedido); continue; }
                }
                lock (_deAUno) Aplicar(pedido, reintentar: true);
            }
            finally
            {
                lock (_estado) _enCurso = null;
                Salio(pedido);
                SiSeAplicoTodoElVolcado(pedido);
            }
        }
    }

    /// <summary>Hace el pedido. Si la base no responde, lo reintenta —con lo demás esperando detrás— hasta que vuelva o hasta
    /// que el apagado se rinda. Si falla por otra cosa, se anota y listo.</summary>
    private void Aplicar(Pedido pedido, bool reintentar)
    {
        for (int intento = 0; ; intento++)
        {
            // La abandonada no se hace, tampoco a mitad de los reintentos: con la base caída, el resto esperaba detrás de ella.
            if (pedido.Abandono.IsCancellationRequested)
            {
                pedido.Fallo(new OperationCanceledException(pedido.Abandono));
                return;
            }
            try
            {
                pedido.Hacer(_almacen);
                Volvio();
                return;
            }
            catch (Exception ex) when (reintentar && EsQueLaBaseNoResponde(ex))
            {
                Cayo(ex, pedido.Que);
                Volcar(pedido);
                if (EsperarParaReintentar(intento)) continue;
                lock (_estado) Apartar(pedido);
                return;
            }
            catch (Exception ex)
            {
                // Lo que el apagado ya apartó con su rescate no falla: va al próximo arranque.
                lock (_estado) if (pedido.Rescatado is not null) return;
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

    /// <summary>Un pedido que no se va a aplicar: queda para el rescate, y el que esperaba una respuesta se entera. La lectura
    /// abandonada no: no la espera nadie.</summary>
    private void Apartar(Pedido pedido)
    {
        if (pedido.Apartado || pedido.Estado == 2 || pedido.Abandono.IsCancellationRequested) return;
        pedido.Apartado = true;
        pedido.Rescatado = pedido.Rescate?.Invoke(true);
        _sinAplicar.Add(pedido);
        if (pedido.Rescatado is null) pedido.Fallo(new InvalidOperationException("la base no respondía y el servidor se apagó"));
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
        Interlocked.Exchange(ref _avisoDeCola, 0);
        // EL VOLCADO SE QUEDA hasta que se aplique lo último que cubre (SiSeAplicoTodoElVolcado): borrado acá, con el primer
        // pedido que entró, un corte en el medio perdía todo lo que seguía esperando en memoria (auditoría externa del 23/10).
        // Si se vuelve a caer antes, el próximo volcado va enseguida.
        lock (_volcando) _proximoVolcado = 0;
    }

    /// <summary>
    /// ¿El pedido que terminó era el último que cubría el volcado? Entonces ya no hace falta, y se borra. Hasta ahí se queda: lo
    /// que cubre es la cabeza de la cola (que va en orden), y aplicarlo de nuevo en el próximo arranque, si el proceso muere
    /// en el medio, deja lo mismo, porque cada escritura guarda un estado entero y lo que vino después todavía no se aplicó.
    /// La suma a una bóveda, que no es así, anota su saldo en el volcado antes de sumar (<see cref="LaBaseResponde"/>).
    /// </summary>
    private void SiSeAplicoTodoElVolcado(Pedido terminado)
    {
        lock (_volcando)
        {
            if (_volcado is null || !_volcadoVigente || terminado.Orden < _volcadoHasta) return;
            lock (_estado) if (_caidaDesde is not null || _rendida) return;   // sigue caída o se rindió: el apagado escribe el suyo
            _volcadoVigente = false;
        }
        // No puede pasar el próximo pedido hasta retirar el archivo viejo: si entrara un estado más nuevo y el proceso
        // muriera, el siguiente arranque lo pisaría con este rescate ya aplicado. Se espera solamente en el escritor; el
        // mundo puede pedir el apagado y dejar un rescate con los pedidos todavía pendientes.
        for (int intento = 0; ; intento++)
        {
            bool retirado;
            lock (_volcando) retirado = BorrarElVolcado();
            if (retirado) { Volvio(); return; }
            Cayo(new IOException("no se pudo retirar el rescate ya aplicado"), "retirar el rescate");
            if (!EsperarParaReintentar(intento)) return;
        }
    }

    /// <summary>Desde adentro de un pedido: la base ya contestó, y lo que sigue NO se puede aplicar dos veces (una suma a una
    /// bóveda). Si hay un volcado vigente, se reescribe ya, con el saldo de antes que el pedido acaba de anotar, antes de que
    /// sume: sin eso, un corte justo después la sumaba otra vez en el próximo arranque
    /// (<see cref="Persistence.AcreditarBovedaAsync"/>). Si no se puede reescribir, se conserva el archivo y se reintenta antes
    /// de sumar: borrarlo perdería también los otros pedidos que todavía esperan.</summary>
    internal void LaBaseResponde()
    {
        Volvio();
        lock (_volcando)
        {
            if (_volcado is null || !_volcadoVigente) return;
            Pedido? enCurso;
            lock (_estado) enCurso = _enCurso;
            if (enCurso is not null && Volcar(enCurso, forzar: true)) return;
            throw new LaBaseNoResponde("no se pudo actualizar el rescate antes de acreditar; se conserva y se reintenta");
        }
    }

    /// <summary>
    /// EL VOLCADO DE UNA CAÍDA: lo que espera —el pedido que se reintenta y lo que está detrás, en orden— va al archivo de
    /// rescate de <c>data/</c> (<see cref="Rescates"/>), el mismo que escribe el apagado. Si el proceso muere con la base caída,
    /// el próximo arranque lo aplica como a cualquier rescate. Va apenas se cae y después cada <see cref="CadaVolcado"/>, si
    /// llegó algo nuevo, siempre al MISMO archivo: dos volcados de la misma caída aplicarían dos veces la misma suma. Cuando la
    /// base vuelve se queda hasta que se aplica lo último que cubre (<see cref="SiSeAplicoTodoElVolcado"/>): borrado antes, se
    /// perdía lo que seguía esperando; y no se queda más, porque en el próximo arranque pisaría lo que se guardó después. Corre en
    /// el escritor, entre un reintento y otro.
    /// </summary>
    /// <returns>true si quedó escrito.</returns>
    private bool Volcar(Pedido enCurso, bool forzar = false)
    {
        if (_carpetaDelRescate is null) return false;
        lock (_volcando)
        {
            long hasta;
            List<(string, Rescate?)> pendientes;
            lock (_estado)
            {
                if (_rendida) return false;   // el apagado escribe el suyo
                // La foto y su último número pertenecen al mismo instante. Encolar usa este mismo candado: un pedido que
                // entra después no puede terminar en el archivo con una marca anterior a él.
                lock (_orden)
                {
                    var foto = _pedidos.Prepend(enCurso)
                        .Where(p => !p.Apartado && p.Estado != 2 && !p.Abandono.IsCancellationRequested)
                        .OrderBy(p => p.Orden).ToArray();
                    if (foto.Length == 0) return false;
                    hasta = foto[^1].Orden;
                    if (!forzar && _volcadoVigente && (Environment.TickCount64 < _proximoVolcado || hasta == _volcadoHasta)) return false;
                    pendientes = foto
                        .Select(p => (p.Que, p.Rescate?.Invoke(false)))
                        .ToList();
                }
            }
            // Un volcado que falla —el disco lleno es una de las caídas— no puede frenar a la cola.
            bool escrito = false;
            try
            {
                _volcado = Rescates.Escribir(_carpetaDelRescate, pendientes, _volcado) ?? _volcado;
                _volcadoVigente = _volcado is not null;
                escrito = _volcadoVigente;
                if (escrito) _volcadoHasta = hasta;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[base] no se pudo volcar lo que espera a la base: {ex.Message}");
            }
            _proximoVolcado = Environment.TickCount64 + (long)_cadaVolcado.TotalMilliseconds;
            return escrito;
        }
    }

    /// <summary>Borra el volcado que ya no vale. Si no se puede, el escritor espera antes de aplicar otro pedido; el apagado
    /// puede reemplazarlo por el rescate de lo que todavía falta.</summary>
    private bool BorrarElVolcado()
    {
        if (_volcado is not { } ruta) return true;
        if (_volcadoVigente) return false;
        try
        {
            File.Delete(ruta);
            _volcado = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[base] no se pudo borrar {ruta} ({ex.Message}); el escritor espera para no dejar estados más nuevos detrás de un rescate viejo.");
            return false;
        }
    }

    /// <summary>
    /// EL RESCATE DEL APAGADO: lo que <see cref="Cerrar"/> devolvió sin aplicar, a <paramref name="carpeta"/>. Va ENCIMA del
    /// volcado de la caída, si hay uno —los dos aplicarían dos veces lo mismo—, y sin nada que escribir, el volcado que ya no
    /// vale se borra. Devuelve la ruta, o null si no había nada.
    /// </summary>
    public string? EscribirElRescate(string carpeta, IReadOnlyList<(string Que, Rescate? Rescate)> sinAplicar)
    {
        lock (_volcando)
        {
            if (sinAplicar.Count == 0)
            {
                BorrarElVolcado();
                return null;
            }
            string? ruta;
            try { ruta = Rescates.Escribir(carpeta, sinAplicar, _volcado); }
            catch (Exception ex) when (!_volcadoVigente && _volcado is not null && ex is IOException or UnauthorizedAccessException)
            {
                // El archivo viejo YA entró entero y no se pudo retirar (por ejemplo, un antivirus lo tiene abierto).
                // Se puede escribir sólo lo pendiente aparte, después de él en el orden de arranque. No es seguro hacer
                // esto con uno vigente: contendría operaciones todavía pendientes y una suma podría aplicarse dos veces.
                string posterior = _volcado + ".posterior-" + Guid.NewGuid().ToString("N") + ".json";
                ruta = Rescates.Escribir(carpeta, sinAplicar, posterior);
                Console.Error.WriteLine($"[base] el rescate ya aplicado sigue abierto; lo pendiente quedó en {Path.GetFileName(posterior)}, que se aplica después.");
            }
            _volcado = null;
            _volcadoVigente = false;
            return ruta;
        }
    }

    /// <summary>
    /// ¿Falló porque la base no responde —y reintentar tiene sentido— o por otra cosa? No responde: Postgres caído o
    /// reiniciándose (lo que Npgsql llama transitorio), SQLite con el archivo tomado, lleno o sin poder abrirse, un archivo
    /// tomado por otro programa o el disco lleno, la red. NO cualquier error de disco: uno que falla siempre —un nombre de
    /// archivo inválido— reintentado frenaría la cola para siempre.
    /// </summary>
    internal static bool EsQueLaBaseNoResponde(Exception ex) => ex switch
    {
        LaBaseNoResponde => true,
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
    /// EL APAGADO: deja de aceptar pedidos y espera, hasta <paramref name="plazo"/>, a que corra lo que quedaba. Si se vence,
    /// o si la base NO RESPONDE —porque ya estaba caída o porque se cae en el medio—, la cola se rinde en el acto: devuelve lo
    /// que no se aplicó, en orden, y el que tenga rescate va al archivo de rescate. A la base caída no se la espera: cerrar la
    /// ventana en Windows le da al proceso unos 5 s antes de matarlo, y el rescate tiene que quedar escrito antes. Lo que
    /// estaba corriendo justo puede terminar igual después; su rescate aplicado otra vez da lo mismo, porque cada uno guarda
    /// un estado entero.
    ///
    /// <para>Al escritor trabado adentro de la base tampoco lo espera: lo suyo queda apartado. Esperarlo —para cerrar la
    /// conexión sin que la esté usando— es <see cref="EsperarAlEscritor"/>, después de escribir el rescate.</para>
    /// </summary>
    public IReadOnlyList<(string Que, Rescate? Rescate)> Cerrar(TimeSpan plazo)
    {
        lock (_orden) _cerrada = true;
        _hayPedidos.Set();
        if (EsElEscritor) return Resultado();
        var reloj = Stopwatch.StartNew();
        while (!_escritor.Join(Vistazo))
        {
            if (CaidaDesde is null && reloj.Elapsed < plazo) continue;
            lock (_estado)
            {
                _rendida = true;
                if (_enCurso is { } corriendo) Apartar(corriendo);
                lock (_orden)
                while (_pedidos.First is { } primero)
                {
                    var resto = primero.Value;
                    _pedidos.RemoveFirst();
                    resto.Nodo = null;
                    Apartar(resto);
                    Salio(resto);
                }
            }
            // El que esperaba para reintentar se entera ya, y sale: un momento para que termine. Al que quedó trabado adentro
            // de la base no se lo espera acá (ver EsperarAlEscritor).
            _despertar.Set();
            _escritor.Join(Vistazo * 2);
            break;
        }
        return Resultado();

        IReadOnlyList<(string, Rescate?)> Resultado()
        {
            lock (_estado) return _sinAplicar.OrderBy(p => p.Orden).Select(p => (p.Que, p.Rescatado)).ToList();
        }
    }

    /// <summary>Cada cuánto mira el apagado, mientras espera, si la base se cayó.</summary>
    private static readonly TimeSpan Vistazo = TimeSpan.FromMilliseconds(50);

    /// <summary>Espera hasta <paramref name="plazo"/> a que el escritor termine lo que haya quedado haciendo adentro de la base
    /// después de rendirse la cola: para cerrar la conexión sin que la esté usando. False si no terminó.</summary>
    public bool EsperarAlEscritor(TimeSpan plazo) => EsElEscritor || _escritor.Join(plazo);

    /// <summary>Deja de aceptar pedidos y espera a que corra lo que quedaba (hasta 30 s), y al escritor (hasta 5 s más).</summary>
    public void Dispose()
    {
        Cerrar(TimeSpan.FromSeconds(30));
        EsperarAlEscritor(TimeSpan.FromSeconds(5));
    }
}

/// <summary>La base no responde, por algo que el motor no marca como pasajero: la conexión que no se pudo volver a abrir. La cola
/// la reintenta como a cualquier caída (<see cref="ColaDeLaBase.EsQueLaBaseNoResponde"/>).</summary>
public sealed class LaBaseNoResponde(string mensaje, Exception? causa = null) : Exception(mensaje, causa);
