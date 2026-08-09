using System.Diagnostics;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA COLA DE SALIDA, que es lo que impide que UN cliente congele el mundo entero.
///
/// El servidor corre en un solo hilo con vueltas de 40 ms y TODO broadcast sale de ahí. Antes,
/// <c>ClientSession.Send</c> hacía <c>_stream.Write</c> directo: una escritura a un socket TCP BLOQUEA cuando
/// el cliente dejó de leer y se llenan el buffer de envío y la ventana del par, y con el tope que le pusimos
/// eso son hasta 3 segundos de mundo detenido PARA TODOS. No hace falta ningún exploit — alcanza con
/// suspender el proceso del cliente.
///
/// Estos tests fijan la garantía de forma DETERMINISTA, que es lo que no se pudo hacer llenando una ventana
/// TCP de verdad: ahí hacen falta ~64 KB de tráfico hacia el cliente trabado y con cinco jugadores caminando
/// no se junta esa cantidad en un minuto. Acá el transporte tarda a propósito y el reloj lo dice todo.
/// </summary>
public class ColaDeSalidaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_cola_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;

    /// <summary>Un transporte que NO drena: cada escritura se queda esperando hasta que se lo libera. Es el
    /// cliente que dejó de leer, sin depender de buffers del sistema operativo.</summary>
    private sealed class TransporteTrabado : ITransport
    {
        public string RemoteEndpoint => "trabado";
        public readonly ManualResetEventSlim Liberar = new(false);
        private int _enviados;
        public int Enviados => Volatile.Read(ref _enviados);
        public volatile bool Cerrado;

        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data)
        {
            Liberar.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Increment(ref _enviados);
        }
        // Cerrar el socket es justamente lo que destraba una escritura colgada.
        public void Close(string reason) { Cerrado = true; Liberar.Set(); }

        /// <summary>Espera a que el escritor de esta sesión termine y suelte el hilo del pool.
        ///
        /// Sin esto, un test dejaba su escritor trabado en el <c>Wait</c> de arriba y el siguiente arrancaba
        /// con el pool ocupado: su propio escritor no llegaba a correr en tres segundos y le ganaba el cierre
        /// forzado. Daba un rojo cada doce corridas que parecía del producto y era de la limpieza del test.</summary>
        public void EsperarQueTermine()
        {
            Liberar.Set();
            SpinWait.SpinUntil(() => Cerrado, TimeSpan.FromSeconds(15));
        }
    }

    public ColaDeSalidaTests()
    {
        Directory.CreateDirectory(_root);
        var data = new GameData();
        var world = new World();
        var leveling = new Leveling(data.LevelExp);
        var db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(world, new GameLoop(world), db, data, leveling);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Un_cliente_que_no_drena_no_frena_al_que_manda()
    {
        var t = new TransporteTrabado();
        var sesion = new ClientSession(t, _dispatcher);

        // 50 paquetes contra un transporte que no responde. Con la escritura directa de antes, el PRIMERO ya
        // se quedaba colgado acá adentro y el game loop con él.
        var reloj = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) sesion.Send(new byte[] { (byte)i });
        reloj.Stop();

        Assert.True(reloj.ElapsedMilliseconds < 250,
            $"mandar 50 paquetes a un cliente trabado tardó {reloj.ElapsedMilliseconds} ms: alguien está esperando al socket");
        // Y no es que se hayan tirado: en cuanto el cliente drena, salen.
        t.Liberar.Set();
        SpinWait.SpinUntil(() => t.Enviados == 50, TimeSpan.FromSeconds(10));
        Assert.Equal(50, t.Enviados);
        sesion.Close("fin del test");
        t.EsperarQueTermine();
    }

    [Fact]
    public void El_orden_de_los_paquetes_se_conserva()
    {
        // Importa porque el protocolo AO no lleva número de secuencia: si dos paquetes salen al revés, el
        // cliente no tiene cómo darse cuenta ni cómo recuperarse.
        var recibidos = new List<byte>();
        var t = new TransporteOrdenado(recibidos);
        var sesion = new ClientSession(t, _dispatcher);
        for (int i = 0; i < 200; i++) sesion.Send(new byte[] { (byte)i });
        SpinWait.SpinUntil(() => recibidos.Count == 200, TimeSpan.FromSeconds(10));
        Assert.Equal(200, recibidos.Count);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), recibidos);
        sesion.Close("fin del test");
    }

    private sealed class TransporteOrdenado : ITransport
    {
        private readonly List<byte> _out;
        public TransporteOrdenado(List<byte> destino) => _out = destino;
        public string RemoteEndpoint => "ordenado";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { lock (_out) _out.Add(data[0]); }
        public void Close(string reason) { }
    }

    [Fact]
    public void Al_cliente_que_se_atrasa_sin_fin_se_lo_desconecta()
    {
        // La cola es acotada a propósito. Un cliente que nunca drena no puede hacer crecer la memoria del
        // servidor sin límite, y DESCARTAR paquetes sueltos tampoco es opción: sin reenvío, un hueco le deja
        // al cliente un estado que no existe. Así que se lo echa.
        var t = new TransporteTrabado();
        var sesion = new ClientSession(t, _dispatcher);
        var reloj = Stopwatch.StartNew();
        for (int i = 0; i < 8000 && !sesion.Cerrada; i++) sesion.Send(new byte[] { 1 });
        reloj.Stop();

        Assert.True(sesion.Cerrada, "con la cola llena, la sesión tiene que cerrarse");
        Assert.True(reloj.ElapsedMilliseconds < 2000,
            $"llenar la cola tardó {reloj.ElapsedMilliseconds} ms: no debería haber ninguna espera");
        t.EsperarQueTermine();
    }

    [Fact]
    public void El_ultimo_mensaje_llega_antes_de_que_se_baje_el_socket()
    {
        // Reject() manda el motivo y CIERRA en la línea siguiente. Si el cierre bajara el socket antes de que
        // el escritor drene, el jugador vería la conexión caerse sin enterarse de por qué.
        var recibidos = new List<byte>();
        var t = new TransporteQueRegistraElCierre(recibidos);
        var sesion = new ClientSession(t, _dispatcher);
        sesion.Send(new byte[] { 42 });
        sesion.Close("motivo del rechazo");

        // Se espera por las DOS cosas: que el paquete haya salido Y que el socket ya se haya bajado. Esperar
        // por una sola deja el test leyendo un estado a medio hacer, y de las dos formas da un rojo mentiroso:
        // esperando sólo el cierre, la comparación corre con la lista todavía vacía; esperando sólo la
        // entrega, la bandera de orden se lee en su valor inicial porque el cierre aún no ocurrió.
        SpinWait.SpinUntil(() => { lock (recibidos) return recibidos.Count == 1 && t.Cerrado; }, TimeSpan.FromSeconds(10));
        lock (recibidos) Assert.Equal(new byte[] { 42 }, recibidos);
        Assert.True(t.Cerrado, "el socket tiene que terminar bajando");
        Assert.True(t.PrimerCierreTrasMandar, "el último paquete tiene que salir ANTES del cierre");
    }

    private sealed class TransporteQueRegistraElCierre : ITransport
    {
        private readonly List<byte> _out;
        public TransporteQueRegistraElCierre(List<byte> destino) => _out = destino;
        public string RemoteEndpoint => "cierre";
        public volatile bool Cerrado;
        /// <summary>Si al PRIMER cierre ya había salido algo. Sólo el primero: el socket se baja dos veces
        /// —el escritor cuando termina de drenar y el cierre forzado de red—, y mirar el último diría que
        /// todo salió a tiempo aunque el primero hubiera llegado con la cola sin vaciar.</summary>
        public volatile bool PrimerCierreTrasMandar;
        private int _cierres;
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { lock (_out) _out.Add(data[0]); }
        public void Close(string reason)
        {
            if (Interlocked.Increment(ref _cierres) == 1)
                lock (_out) PrimerCierreTrasMandar = _out.Count > 0;
            Cerrado = true;
        }
    }
}
