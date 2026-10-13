namespace CoverAO.Server.Net;

/// <summary>
/// LAS LÍNEAS DE LA RED (conectado, rechazado, cerrado, los errores del servidor de archivos), CON CUPO. Cada conexión dejaba
/// dos o tres líneas en el log del disco sin límite, y un bucle de conectar y cortar llenaba el disco del host; el recorte del
/// Origin del 12/10 achicó una línea pero no la cantidad (auditoría del 13/10). Pasan hasta <see cref="Rafaga"/> seguidas y
/// después <see cref="PorSegundo"/> por segundo; las que no entran se cuentan y se avisa una vez por minuto.
///
/// <para>Y ESCRIBIR NUNCA TIRA: con el disco lleno o la salida rota, la línea de "conectado" tiraba entre que la conexión tomaba
/// su lugar en el cupo y la sesión arrancaba, y ese lugar quedaba tomado para siempre.</para>
/// </summary>
public static class LogDeRed
{
    private const double PorSegundo = 20, Rafaga = 200;
    private static readonly object _gate = new();
    private static double _fichas = Rafaga;
    private static long _ultimoMs = Environment.TickCount64, _ultimoAvisoMs;
    private static long _suprimidas;

    public static void Linea(string texto, bool error = false)
    {
        bool pasa;
        string? aviso = null;
        lock (_gate)
        {
            long ahora = Environment.TickCount64;
            _fichas = Math.Min(Rafaga, _fichas + (ahora - _ultimoMs) * PorSegundo / 1000.0);
            _ultimoMs = ahora;
            pasa = _fichas >= 1;
            if (pasa) _fichas--; else _suprimidas++;
            if (_suprimidas > 0 && ahora - _ultimoAvisoMs >= 60_000)
            {
                aviso = $"[red] {_suprimidas} línea(s) de conexiones sin escribir (demasiadas seguidas)";
                _suprimidas = 0;
                _ultimoAvisoMs = ahora;
            }
        }
        try
        {
            if (aviso is not null) Console.Error.WriteLine(aviso);
            if (pasa) (error ? Console.Error : Console.Out).WriteLine(texto);
        }
        catch (Exception) { }   // ver el resumen: una consola rota no corta nada
    }
}
