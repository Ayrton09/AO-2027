using System.Collections.Concurrent;

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
/// <para>Un pedido que falla se anota y la cola sigue: un personaje que no se pudo guardar no puede frenar a los demás.</para>
/// </summary>
public sealed class ColaDeLaBase : IDisposable
{
    private readonly IAlmacen _almacen;
    private readonly BlockingCollection<Action> _pedidos = new();
    private readonly Thread _escritor;
    private readonly object _deAUno = new();
    private int _pendientes;

    public ColaDeLaBase(IAlmacen almacen)
    {
        _almacen = almacen;
        _escritor = new Thread(Correr) { IsBackground = true, Name = "cola-de-la-base" };
        _escritor.Start();
    }

    /// <summary>Cuántos pedidos esperan o están corriendo.</summary>
    public int Pendientes => Volatile.Read(ref _pendientes);

    /// <summary>¿Es el hilo del escritor el que llama? Para los que no pueden esperar a la cola desde adentro de ella.</summary>
    public bool EsElEscritor => Thread.CurrentThread == _escritor;

    /// <summary>Encola una escritura y sigue: no espera nada. Si falla, se anota con <paramref name="que"/>.</summary>
    public void Escribir(string que, Action<IAlmacen> escritura) => Encolar(() =>
    {
        try { escritura(_almacen); }
        catch (Exception ex) { Console.Error.WriteLine($"[base] {que}: {ex.Message}"); }
    });

    /// <summary>
    /// Pide algo al almacén y devuelve una tarea con la respuesta. La continuación NO corre en el escritor
    /// (<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>): lo que venga después —un hash de contraseña, por
    /// ejemplo— no puede frenar a la cola.
    /// </summary>
    public Task<T> PedirAsync<T>(string que, Func<IAlmacen, T> pedido)
    {
        var respuesta = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Encolar(() =>
        {
            try { respuesta.SetResult(pedido(_almacen)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[base] {que}: {ex.Message}");
                respuesta.SetException(ex);
            }
        });
        return respuesta.Task;
    }

    /// <summary>A la cola, o —si ya se cerró, al final del apagado— en el acto: lo que llega tarde se guarda igual. En el
    /// acto pero DE A UNO: el escritor puede estar todavía vaciando lo que quedaba, y el almacén no se toca desde dos hilos
    /// (una conexión de SQLite, por ejemplo, no lo aguanta). Para el escritor, el candado nunca tiene a nadie esperando.</summary>
    private void Encolar(Action pedido)
    {
        Interlocked.Increment(ref _pendientes);
        void Contado()
        {
            try { lock (_deAUno) pedido(); }
            finally { Interlocked.Decrement(ref _pendientes); }
        }
        try { _pedidos.Add(Contado); }
        catch (InvalidOperationException) { Contado(); }
    }

    /// <summary>
    /// Espera a que se aplique todo lo encolado HASTA AHORA. La llaman el apagado —que no puede cortar un guardado a la
    /// mitad— y los tests. Devuelve false si se venció el plazo.
    /// </summary>
    public bool Vaciar(TimeSpan plazo)
    {
        if (EsElEscritor) return true;   // desde adentro de la cola no hay nada que esperar: lo anterior ya corrió
        return PedirAsync("marca de vaciado", _ => true).Wait(plazo);
    }

    private void Correr()
    {
        foreach (var pedido in _pedidos.GetConsumingEnumerable())
            pedido();
    }

    /// <summary>Deja de aceptar pedidos y espera a que corra lo que quedaba.</summary>
    public void Dispose()
    {
        _pedidos.CompleteAdding();
        if (!EsElEscritor) _escritor.Join(TimeSpan.FromSeconds(30));
    }
}
