using System.Net;
using System.Net.Sockets;
using CoverAO.Protocol.Tests;
using CoverAO.Server.Data;
using Npgsql;

namespace CoverAO.Postgres.Tests;

/// <summary>Un pasamanos TCP entre el almacén y Postgres, que se puede cortar y volver a abrir: Postgres "se cae" para el que
/// pasa por acá —las conexiones abiertas se cortan y las nuevas se rechazan— sin tocar el Postgres de las otras pruebas.</summary>
internal sealed class Pasamanos : IDisposable
{
    private readonly IPEndPoint _destino;
    private readonly object _deAUno = new();
    private readonly List<TcpClient> _abiertos = new();
    private TcpListener? _escucha;

    public int Puerto { get; }

    public Pasamanos(int puertoDePostgres)
    {
        _destino = new IPEndPoint(IPAddress.Loopback, puertoDePostgres);
        _escucha = new TcpListener(IPAddress.Loopback, 0);
        _escucha.Start();
        Puerto = ((IPEndPoint)_escucha.LocalEndpoint).Port;
        _ = Aceptar(_escucha);
    }

    public void Cortar()
    {
        lock (_deAUno)
        {
            _escucha?.Stop();
            _escucha = null;
            foreach (var c in _abiertos) c.Dispose();
            _abiertos.Clear();
        }
    }

    public void Abrir()
    {
        lock (_deAUno)
        {
            var escucha = new TcpListener(IPAddress.Loopback, Puerto);
            escucha.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            escucha.Start();
            _escucha = escucha;
            _ = Aceptar(escucha);
        }
    }

    private async Task Aceptar(TcpListener escucha)
    {
        while (true)
        {
            TcpClient entrante;
            try { entrante = await escucha.AcceptTcpClientAsync(); }
            catch { return; }   // cortado
            var saliente = new TcpClient();
            try { await saliente.ConnectAsync(_destino); }
            catch { entrante.Dispose(); saliente.Dispose(); continue; }
            lock (_deAUno) { _abiertos.Add(entrante); _abiertos.Add(saliente); }
            _ = Pasar(entrante, saliente);
            _ = Pasar(saliente, entrante);
        }
    }

    private static async Task Pasar(TcpClient de, TcpClient a)
    {
        try { await de.GetStream().CopyToAsync(a.GetStream()); } catch { }
        de.Dispose();
        a.Dispose();
    }

    public void Dispose() => Cortar();
}

/// <summary>
/// POSTGRES SE CAE CON EL SERVIDOR ANDANDO (fase5/postgres/DISENO.md, 3.5): lo que se guarda espera en la cola, en orden, y
/// cuando vuelve entra todo —la conexión se reabre sola—. Y si al apagar sigue caído, lo que quedó va al rescate.
/// </summary>
public sealed class LaBaseQueSeCaeTests : IDisposable
{
    private static readonly TimeSpan[] Rapido = { TimeSpan.FromMilliseconds(50) };
    private readonly BaseDePrueba _base = new();
    private readonly Pasamanos _pasamanos;
    private readonly AlmacenPostgres _almacen;

    public LaBaseQueSeCaeTests()
    {
        _pasamanos = new Pasamanos(new NpgsqlConnectionStringBuilder(_base.Cadena).Port);
        _almacen = new AlmacenPostgres(new NpgsqlConnectionStringBuilder(_base.Cadena) { Port = _pasamanos.Puerto, Timeout = 3 }.ToString());
        var cuenta = new Account { Name = "Duena", PasswordHash = "pbkdf2$1$a$b", Salt = "" };
        _almacen.GuardarCuenta(cuenta);
        Assert.True(_almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Uno")));
        Assert.True(_almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Dos")));
    }

    public void Dispose()
    {
        _almacen.Dispose();
        _pasamanos.Dispose();
        _base.Dispose();
    }

    [Fact]
    public async Task Lo_que_se_guarda_con_la_base_caida_entra_cuando_vuelve_y_en_orden()
    {
        using var cola = new ColaDeLaBase(_almacen, Rapido);
        var uno = FichasDePrueba.Llena("Uno", semilla: 7);
        var dos = FichasDePrueba.Llena("Dos", semilla: 8);

        _pasamanos.Cortar();
        cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(uno));
        var leida = cola.PedirAsync("leer a Uno", a => a.LeerPersonaje("Uno"));
        cola.Escribir("guardar a Dos", a => a.GuardarPersonaje(dos));
        Assert.True(SpinWait.SpinUntil(() => cola.CaidaDesde is not null, TimeSpan.FromSeconds(15)));
        Assert.False(leida.IsCompleted);

        _pasamanos.Abrir();
        Assert.True(cola.Vaciar(TimeSpan.FromSeconds(30)));
        Assert.Null(cola.CaidaDesde);
        FichasDePrueba.AssertIgual(uno, (await leida)!);
        FichasDePrueba.AssertIgual(dos, _almacen.LeerPersonaje("Dos")!);
    }

    /// <summary>Con Postgres caído al apagar, el guardado va al archivo de rescate, y el próximo arranque —con Postgres de
    /// vuelta— lo aplica.</summary>
    [Fact]
    public void Si_al_apagar_sigue_caida_lo_que_quedo_va_al_rescate_y_el_arranque_lo_aplica()
    {
        string data = Path.Combine(Path.GetTempPath(), "coverao_rescate_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cola = new ColaDeLaBase(_almacen, Rapido);
            var uno = FichasDePrueba.Llena("Uno", semilla: 5);
            _pasamanos.Cortar();
            cola.Escribir("guardar a Uno", a => a.GuardarPersonaje(uno), new RescateDePersonaje(uno));

            string? ruta = Rescates.Escribir(data, cola.Cerrar(TimeSpan.FromMilliseconds(500)));
            Assert.NotNull(ruta);

            _pasamanos.Abrir();
            Assert.Equal(1, Rescates.AplicarLosPendientes(data, _almacen));
            FichasDePrueba.AssertIgual(uno, _almacen.LeerPersonaje("Uno")!);
        }
        finally { try { Directory.Delete(data, recursive: true); } catch { } }
    }
}
