using System.Diagnostics;

namespace CoverAO.Server.Game;

/// <summary>
/// The explicit, single-owner game tick. The VB6 server ran on hidden Form timers with no clear
/// ownership; here the loop is one place, one cadence (40 ms, matching the original), and all
/// scheduled game work funnels through it. This is also the concurrency boundary: queued actions
/// run on the loop thread, so handlers don't race on shared state (a risk the single-threaded VB6
/// never had — see the strategy's concurrency-drift risk).
/// </summary>
public sealed class GameLoop
{
    public const int TickMs = 40; // 25 ticks/second, as in the original server

    private readonly Queue<Action> _pending = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public long Tick { get; private set; }

    // --- Costo del tick -------------------------------------------------------------------------------
    // El presupuesto de una vuelta es TickMs (40 ms). Pasarse no tira ningún error: el loop resincroniza y el
    // mundo simplemente corre más lento, así que un servidor ahogado se ve como lag y no como una falla. Medir
    // es lo único que distingue "el server anda bien" de "el server llega justo". Cuesta dos lecturas del
    // Stopwatch por vuelta (~20 ns), así que se mide siempre y se imprime sólo si el host lo pide con --perf.
    private readonly double[] _muestras = new double[256];
    private int _muestraIdx;
    private long _muestrasTotales;

    /// <summary>Costo de las últimas vueltas en ms: promedio, peor caso y cuántas se pasaron del presupuesto.</summary>
    public (double Promedio, double Max, int Excedidas, long Total) EstadisticasDeTick()
    {
        int n = (int)Math.Min(_muestrasTotales, _muestras.Length);
        if (n == 0) return (0, 0, 0, 0);
        double suma = 0, max = 0; int excedidas = 0;
        for (int i = 0; i < n; i++)
        {
            double v = _muestras[i];
            suma += v;
            if (v > max) max = v;
            if (v > TickMs) excedidas++;
        }
        return (suma / n, max, excedidas, _muestrasTotales);
    }

    /// <summary>Invoked every tick with the current tick number (e.g. NPC AI). Runs on the loop thread.</summary>
    public Action<long>? OnTickHandler { get; set; }

    /// <summary>Queue work to run on the next tick (thread-safe entry point from I/O threads).</summary>
    public void Enqueue(Action action)
    {
        lock (_gate) _pending.Enqueue(action);
    }

    public Task RunAsync(CancellationToken outerToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        return Task.Run(() => Loop(_cts.Token), _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    [ThreadStatic] private static bool _enUnaVuelta;

    /// <summary>¿Corre esto ADENTRO de una vuelta del mundo? Lo mira la persistencia para avisar de una lectura que frena
    /// el loop (ver <see cref="Data.Persistence"/>). Vale durante el drenaje y el tick, que son sincrónicos: entre
    /// vueltas el hilo es del pool y la marca está apagada.</summary>
    public static bool EnUnaVuelta => _enUnaVuelta;

    private async Task Loop(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var next = sw.ElapsedMilliseconds;
        while (!token.IsCancellationRequested)
        {
            long t0 = Stopwatch.GetTimestamp();
            _enUnaVuelta = true;
            try
            {
                Drain();
                OnTick();
            }
            finally { _enUnaVuelta = false; }
            _muestras[_muestraIdx] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _muestraIdx = (_muestraIdx + 1) % _muestras.Length;
            _muestrasTotales++;
            Tick++;

            next += TickMs;
            var delay = next - sw.ElapsedMilliseconds;
            if (delay > 0)
            {
                try { await Task.Delay((int)delay, token); }
                catch (OperationCanceledException) { break; }
            }
            else
            {
                next = sw.ElapsedMilliseconds; // fell behind; resync instead of spiraling
            }
        }
    }

    /// <summary>
    /// Ejecuta SÓLO lo que ya estaba encolado al empezar la vuelta. Lo que se encole DURANTE el drenaje espera
    /// a la vuelta siguiente.
    ///
    /// El `while (true)` que había acá vaciaba la cola entera, así que una acción que se re-encola a sí misma
    /// volvía a salir en el MISMO drenaje y giraba sin fin adentro del tick — el mundo colgado, sin un solo
    /// error en el log. Y no es hipotético: el reintento de <c>VerificarCuenta</c> hace exactamente eso, y su
    /// propio comentario ya daba por hecho lo que este método no cumplía ("se reintenta en la vuelta
    /// siguiente, o sea ~40 ms"). Ahora sí lo cumple.
    ///
    /// Tomar el conteo una sola vez también acota el trabajo por vuelta: una ráfaga de paquetes ya no puede
    /// estirar un tick indefinidamente, se reparte entre varias vueltas.
    /// </summary>
    private void Drain()
    {
        int cuantas;
        lock (_gate) cuantas = _pending.Count;
        for (int i = 0; i < cuantas; i++)
        {
            Action? action;
            lock (_gate) action = _pending.Count > 0 ? _pending.Dequeue() : null;
            if (action is null) break;
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine($"[loop] action error: {ex.Message}"); }
        }
    }

    private void OnTick()
    {
        try { OnTickHandler?.Invoke(Tick); }
        catch (Exception ex) { Console.Error.WriteLine($"[tick] {ex.Message}"); }
    }
}
