namespace CoverAO.Server.Net;

public sealed partial class PacketDispatcher
{
    private sealed class RespuestaDeLaBase(Task pedido, bool obligatoria)
    {
        public Task Pedido { get; } = pedido;
        public bool Obligatoria { get; } = obligatoria;
        public TaskCompletionSource Encolada { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _respuestasGate = new();
    private readonly HashSet<RespuestaDeLaBase> _respuestasDeLaBase = new();
    private long _respuestasRegistradas;
    private int _reintentosDeEntrada;

    // Las pruebas pueden demorar la entrega sin depender de la carga del pool ni dormir una cantidad de ticks.
    internal TaskScheduler PlanificadorDeRespuestas { get; set; } = TaskScheduler.Default;

    private void SeguirRespuestaDeLaBase<T>(Task<T> pedido, Action<T> alVolver, Action? alFallar,
        bool obligatoria = false, TaskScheduler? planificador = null)
    {
        var respuesta = new RespuestaDeLaBase(pedido, obligatoria);
        lock (_respuestasGate)
        {
            _respuestasDeLaBase.Add(respuesta);
            _respuestasRegistradas++;
        }
        _ = pedido.ContinueWith(t =>
        {
            try
            {
                Action? terminar = t.IsCompletedSuccessfully ? () => alVolver(t.Result) : alFallar;
                if (terminar is null)
                {
                    lock (_respuestasGate) _respuestasDeLaBase.Remove(respuesta);
                    return;
                }
                _loop.EnqueueContinuacion(() =>
                {
                    try { terminar(); }
                    finally { lock (_respuestasGate) _respuestasDeLaBase.Remove(respuesta); }
                });
            }
            finally { respuesta.Encolada.TrySetResult(); }
        }, CancellationToken.None, TaskContinuationOptions.None, planificador ?? PlanificadorDeRespuestas);
    }

    // El borrado posee una reserva hasta volver al loop, incluso si termina rechazado. No es una escritura
    // rescatable que pueda dejar su Task pendiente: sus lecturas fallan al rendir la cola y deben liberar la reserva.
    private void SeguirTrabajoDeFondo(Func<Action> trabajo, Action alFallar)
        => SeguirRespuestaDeLaBase(Task.Run(trabajo), terminar => terminar(), alFallar,
            obligatoria: true, planificador: TaskScheduler.Default);

    private void ReintentarEntradaParaElCierre(Action reintentar)
    {
        lock (_respuestasGate) _reintentosDeEntrada++;
        _loop.EnqueueContinuacion(() =>
        {
            try { reintentar(); }
            finally { lock (_respuestasGate) _reintentosDeEntrada--; }
        });
    }

    /// <summary>Con la admisión del mundo cerrada y el loop todavía andando, termina las respuestas de la base antes de
    /// tomar la foto final. Incluye las nuevas lecturas que disparan esas respuestas (por ejemplo, devolver una transferencia
    /// al emisor desconectado). Los pedidos apartados para el rescate quedan sin completar y no se esperan.</summary>
    public async Task<bool> CompletarRespuestasDeLaBaseAsync()
    {
        bool apartada = false;
        while (true)
        {
            long registradas;
            lock (_respuestasGate) registradas = _respuestasRegistradas;

            // La marca queda detrás de los pedidos de esta ronda. Si una respuesta abre otra lectura, la siguiente ronda
            // espera su marca también. Si la base no responde, apartar completa los fallos de lectura y conserva sin fallar
            // las operaciones que se harán desde el rescate, evitando una devolución de oro que luego se acreditaría igual.
            if (!await Task.Run(() => _db.Cola.Vaciar(TimeSpan.FromSeconds(2))).ConfigureAwait(false))
            {
                _db.Cola.Cerrar(TimeSpan.Zero);
                apartada = true;
            }

            // El trabajo de fondo puede abrir otra consulta DESPUÉS de la marca anterior. Si queda esperando
            // esa consulta, rendir la cola la hace fallar y el trabajo vuelve al loop a liberar su reserva.
            Task[] obligatorias;
            lock (_respuestasGate)
                obligatorias = _respuestasDeLaBase.Where(r => r.Obligatoria).Select(r => r.Pedido).ToArray();
            try { await Task.WhenAll(obligatorias).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _db.Cola.Cerrar(TimeSpan.Zero);
                apartada = true;
            }
            catch (Exception) { /* El callback registrado completa también el trabajo que falló. */ }

            Task[] entregas;
            lock (_respuestasGate)
                entregas = _respuestasDeLaBase.Where(r => r.Obligatoria || r.Pedido.IsCompleted)
                    .Select(r => r.Encolada.Task).ToArray();
            await Task.WhenAll(entregas).ConfigureAwait(false);
            await _loop.SincronizarContinuacionesAsync().ConfigureAwait(false);

            lock (_respuestasGate)
                if (registradas == _respuestasRegistradas && _reintentosDeEntrada == 0 &&
                    !_respuestasDeLaBase.Any(r => r.Obligatoria || r.Pedido.IsCompleted))
                    return apartada;
        }
    }
}
