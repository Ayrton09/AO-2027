using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Dos cupos que salieron de la auditoría de exploits del 2026-09-05, los dos contra el DISCO y la CPU del
/// que hostea y no contra el juego: las líneas de log por acción del jugador (una por paquete, sin freno) y
/// la creación de cuentas desde una misma IP (sin tope, porque el rate-limit de login sólo cuenta fallos).
/// </summary>
public class CuposDeAbusoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_cupos_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;

    private sealed class TransporteMudo : ITransport
    {
        public string RemoteEndpoint => "tcp:10.0.0.1:1";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public CuposDeAbusoTests()
    {
        Directory.CreateDirectory(_root);
        var data = new GameData();
        var leveling = new Leveling(data.LevelExp);
        _dispatcher = new PacketDispatcher(new World(), new GameLoop(), new Persistence(new FileStore(_root), leveling), data, leveling);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void El_log_por_accion_admite_diez_lineas_cada_diez_segundos_y_despues_calla()
    {
        var sesion = new ClientSession(new TransporteMudo(), _dispatcher);
        long t = 1_000_000;
        for (int i = 1; i <= ClientSession.LogLineasPorVentana; i++)
            Assert.True(sesion.PermiteLog(t + i), $"la línea {i} tenía que entrar");
        Assert.False(sesion.PermiteLog(t + 50));                       // la undécima, no
        // La ventana arranca en la PRIMERA línea (t + 1), no en t.
        Assert.False(sesion.PermiteLog(t + 1 + ClientSession.LogVentanaMs - 1));
        Assert.True(sesion.PermiteLog(t + 1 + ClientSession.LogVentanaMs));  // ventana nueva: vuelve a entrar
    }

    [Fact]
    public void El_cupo_de_cuentas_nuevas_deja_tres_y_bloquea_la_cuarta()
    {
        // La misma clase que el rate-limit de login, usada como contador de creaciones que SALEN.
        var cupo = new LoginThrottle(maxFails: 3, windowMs: 600_000, blockMs: 600_000);
        const string ip = "10.0.0.7";
        for (int i = 1; i <= 3; i++)
        {
            Assert.False(cupo.IsBlocked(ip), $"antes de la cuenta {i} no tenía que estar bloqueada");
            cupo.RecordFailure(ip);
        }
        Assert.True(cupo.IsBlocked(ip));
        Assert.False(cupo.IsBlocked("10.0.0.8"));   // CONTROL: otra IP no hereda el bloqueo
    }
}
