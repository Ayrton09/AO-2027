using System.Collections.Concurrent;
using System.Text;

namespace CoverAO.Server.Net;

/// <summary>
/// REGISTRO DE ECONOMÍA: los movimientos de oro y de objetos entre jugadores, en un archivo por día aparte
/// del log general (<c>data/logs/economia-AAAA-MM-DD.log</c>). No detecta nada por sí solo; existe para que,
/// cuando un jugador aparezca con oro que no debería tener, se pueda reconstruir de dónde salió. Sin esto un
/// dupeo que se escape es invisible hasta que es tarde. Salió de la auditoría de exploits del 2026-09-05.
///
/// <para>Las líneas se ENCOLAN desde el hilo del game loop y las vuelca a disco una bomba aparte cada
/// segundo: el loop tiene 40 ms por vuelta y un disco lento no tiene por qué congelar el mundo.</para>
/// </summary>
public sealed class RegistroDeEconomia : IDisposable
{
    private readonly string _dir;
    private readonly int _dias;
    private readonly string _prefijo;
    private readonly ConcurrentQueue<string> _cola = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _bomba;

    /// <param name="prefijo">Nombre del archivo: <c>economia</c> para el oro, <c>trampas</c> para el anticheat.</param>
    public RegistroDeEconomia(string dir, int dias, string prefijo = "economia")
    {
        _dir = dir; _dias = dias; _prefijo = prefijo;
        Directory.CreateDirectory(dir);
        _bomba = Task.Run(Bombear);
    }

    public string ArchivoDeHoy => Path.Combine(_dir, $"{_prefijo}-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Encola una línea (con la hora). Nunca bloquea al que llama.</summary>
    public void Anotar(string linea) => _cola.Enqueue($"{DateTime.Now:HH:mm:ss} {linea}");

    private async Task Bombear()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { break; }
            Volcar();
        }
    }

    /// <summary>Escribe lo encolado al archivo del día. Pública para que un test la fuerce sin esperar.</summary>
    public void Volcar()
    {
        if (_cola.IsEmpty) return;
        var sb = new StringBuilder();
        while (_cola.TryDequeue(out var l)) sb.Append(l).Append('\n');
        try
        {
            File.AppendAllText(ArchivoDeHoy, sb.ToString(), new UTF8Encoding(false));   // sin BOM: es un log, lo lee grep
            Purgar();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[economia] no se pudo escribir el registro: {ex.Message}"); }
    }

    private void Purgar()
    {
        if (_dias <= 0) return;
        var limite = DateTime.Now.AddDays(-_dias);
        foreach (var f in Directory.EnumerateFiles(_dir, $"{_prefijo}-*.log"))
            if (File.GetLastWriteTime(f) < limite) { try { File.Delete(f); } catch { } }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _bomba.Wait(2000); } catch { }
        Volcar();
    }
}
