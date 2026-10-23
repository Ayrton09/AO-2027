namespace CoverAO.Server.Net;

/// <summary>
/// LOS LISTENERS DEL SERVIDOR, VIGILADOS: el puerto clásico, el WebSocket, el panel y el cliente web corren a la vez y no
/// terminan nunca mientras el servidor anda. Si uno se cae por algo que no es el apagado (el puerto ocupado por otro
/// servidor, el caso de siempre), se dice cuál y por qué y se apaga todo: un servidor con un puerto muerto parecía andando.
/// </summary>
public static class Escuchas
{
    /// <summary>Espera hasta que terminen todos o falle uno. Si falla uno, lo dice, cancela <paramref name="apagar"/> (los
    /// demás terminan con eso) y espera a que terminen, hasta <paramref name="plazo"/>. Devuelve el nombre del que falló, o
    /// null si terminaron todos bien (el apagado de siempre).</summary>
    public static async Task<string?> HastaQueTermineOFalleAlguna(IReadOnlyList<(string Nombre, Task Tarea)> escuchas,
        CancellationTokenSource apagar, TimeSpan? plazo = null)
    {
        var quedan = escuchas.ToList();
        string? fallo = null;
        while (quedan.Count > 0)
        {
            var termino = await Task.WhenAny(quedan.Select(e => e.Tarea));
            var cual = quedan.First(e => e.Tarea == termino);
            quedan.Remove(cual);
            if (!termino.IsFaulted || termino.Exception?.InnerException is OperationCanceledException) continue;
            fallo = cual.Nombre;
            Console.Error.WriteLine($"[apagado] se cayó {cual.Nombre}: {termino.Exception!.InnerException?.Message}. El servidor se apaga.");
            break;
        }
        if (fallo is null) return null;
        apagar.Cancel();
        // Los demás, que el apagado ya cortó: un rato para que suelten sus puertos, sin dejar que una excepción de ellos tape la
        // que importa.
        try { await Task.WhenAll(quedan.Select(e => e.Tarea)).WaitAsync(plazo ?? TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* cancelados, o el plazo: ya se dijo qué falló */ }
        return fallo;
    }
}
