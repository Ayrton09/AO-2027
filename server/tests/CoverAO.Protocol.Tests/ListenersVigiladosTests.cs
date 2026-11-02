using System.Net;
using System.Net.Sockets;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class ListenersVigiladosTests
{
    [Fact]
    public async Task TerminarSinExcepcionAntesDelApagadoEsUnFallo()
    {
        using var apagar = new CancellationTokenSource();
        var otra = Task.Delay(Timeout.Infinite, apagar.Token);
        var fallo = await Escuchas.HastaQueTermineOFalleAlguna(
            new (string, Task)[] { ("web", Task.CompletedTask), ("otra", otra) }, apagar);

        Assert.Equal("web", fallo);
        Assert.True(apagar.IsCancellationRequested);
        Assert.True(otra.IsCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaCancelacionAjenaAlApagadoTambienEsUnFallo(bool comoExcepcion)
    {
        using var apagar = new CancellationTokenSource();
        Task cancelada = comoExcepcion ? Task.FromException(new OperationCanceledException())
            : Task.FromCanceled(new CancellationToken(true));
        Assert.Equal("web", await Escuchas.HastaQueTermineOFalleAlguna(
            new (string, Task)[] { ("web", cancelada) }, apagar));
        Assert.True(apagar.IsCancellationRequested);
    }

    [Fact]
    public async Task ElApagadoAdmiteEscuchasCanceladasYTerminadasSinError()
    {
        using var apagar = new CancellationTokenSource();
        var escucha = Task.Delay(Timeout.Infinite, apagar.Token);
        apagar.Cancel();
        Assert.Null(await Escuchas.HastaQueTermineOFalleAlguna(
            new (string, Task)[] { ("web", escucha), ("admin", Task.CompletedTask) }, apagar));
    }

    [Fact]
    public async Task LasEscuchasDeshabilitadasNoSeRegistran()
    {
        using var apagar = new CancellationTokenSource();
        Assert.Null(await Escuchas.HastaQueTermineOFalleAlguna(Array.Empty<(string, Task)>(), apagar));
        Assert.False(apagar.IsCancellationRequested);
    }

    [Fact]
    public async Task UnaEscuchaQueNoRespondeAlApagadoNoOcultaElFallo()
    {
        using var apagar = new CancellationTokenSource();
        var colgada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal("web", await Escuchas.HastaQueTermineOFalleAlguna(
            new (string, Task)[] { ("web", Task.FromException(new HttpListenerException(5))), ("colgada", colgada.Task) },
            apagar, TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(apagar.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnPuertoHttpOcupadoFallaInclusoSiIntentaLoopback(bool soloLocal)
    {
        using var ocupado = OcuparPuerto();
        int puerto = ((IPEndPoint)ocupado.LocalEndpoint).Port;
        var error = Assert.Throws<InvalidOperationException>(() =>
            HttpBind.Abrir(puerto, "puerto de prueba", CancellationToken.None, soloLocal));
        Assert.Contains(puerto.ToString(), error.Message);
        Assert.IsType<HttpListenerException>(error.InnerException);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("websocket")]
    [InlineData("admin")]
    public async Task CadaServidorHttpPropagaSuFalloDeArranqueAlSupervisor(string nombre)
    {
        using var ocupado = OcuparPuerto();
        int puerto = ((IPEndPoint)ocupado.LocalEndpoint).Port;
        using var apagar = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Ninguna dependencia del juego debe usarse antes de conseguir el puerto.
        Task escucha = nombre switch
        {
            "web" => new StaticFileServer(puerto, Path.GetTempPath(), 7667).RunAsync(apagar.Token),
            "websocket" => new WebSocketGameServer(puerto, null!).RunAsync(apagar.Token),
            _ => new AdminServer(puerto, "token-prueba", null!, null!, null!, default, _ => { }, null, null!)
                .RunAsync(apagar.Token),
        };
        Assert.True(escucha.IsFaulted);
        Assert.IsType<InvalidOperationException>(escucha.Exception!.InnerException);
        var otra = Task.Delay(Timeout.Infinite, apagar.Token);
        Assert.Equal(nombre, await Escuchas.HastaQueTermineOFalleAlguna(
            new (string, Task)[] { (nombre, escucha), ("otra", otra) }, apagar));
        Assert.True(otra.IsCanceled);
    }

    [Fact]
    public void UnApagadoYaSolicitadoNoIntentaAbrirElPuerto()
    {
        Assert.Throws<OperationCanceledException>(() =>
            HttpBind.Abrir(7669, "web", new CancellationToken(true)));
    }

    private static TcpListener OcuparPuerto()
    {
        var ocupado = new TcpListener(IPAddress.Any, 0);
        ocupado.Server.ExclusiveAddressUse = true;
        ocupado.Start();
        return ocupado;
    }
}
