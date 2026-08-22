using System.Linq;

namespace CoverAO.Server.Game;

/// <summary>
/// Contenido informativo del login (VB6 History.bas): el MOTD (mensaje del día, enviado como líneas
/// ConsoleMsg) y las novedades (Update/changelog, enviadas como EjecutarAccion Accion=4). El VB6 los lee
/// de <c>Dat/Motd.ini</c> y <c>Dat/Actualizaciones.ini</c> en <c>SendMOTD</c>/<c>SendUpdate</c>; acá se leen
/// de <c>data/motd.txt</c> y <c>data/updates.txt</c> (una línea por fila; '#' = comentario) con un default
/// sensato si faltan. Es contenido por-servidor (como en todo AO): el texto es nuestro, el MECANISMO es fiel.
/// </summary>
public sealed class ServerContent
{
    public const byte MotdFont = 8;   // FontIndex del MOTD en el oráculo VB6 (ConnectUser)

    /// <summary>Líneas de bienvenida (se emiten como ConsoleMsg con FontIndex 8).</summary>
    public IReadOnlyList<string> Motd { get; }
    /// <summary>Líneas de novedades (se emiten como EjecutarAccion Accion=4, en el orden dado).</summary>
    public IReadOnlyList<string> Updates { get; }

    public ServerContent(IReadOnlyList<string> motd, IReadOnlyList<string> updates)
    { Motd = motd; Updates = updates; }

    private static readonly string[] DefaultMotd =
    {
        "CoverAO — servidor moderno (.NET 10). ¡Conexión aceptada!",
        "¡Bienvenido a CoverAO! El mundo de Argentum te espera.",
        "Escribí /AYUDA para ver los comandos, o /GM para hablar con un administrador.",
    };
    private static readonly string[] DefaultUpdates =
    {
        "Servidor modernizado a .NET 10 con paridad byte-a-byte contra el AO clásico.",
        "Cliente web con gráficos HD, iluminación dinámica y panel de administración.",
    };

    public static ServerContent Default => new(DefaultMotd, DefaultUpdates);

    /// <summary>
    /// Carga MOTD/novedades. Prioridad: (1) los .txt editables de data/ —para que un operador pueda cambiar
    /// el mensaje sin recompilar—, (2) el Motd.dat / ACTUALIZACIONES.dat REALES del install, transcodeados a
    /// Motd.json / ACTUALIZACIONES.json, (3) el texto por defecto.
    ///
    /// El paso (2) faltaba: el loader miraba sólo un data/motd.txt que nadie sembraba nunca, así que todo
    /// servidor arrancaba con el texto genérico y el MOTD auténtico —que ya estaba transcodeado— no se usaba.
    /// </summary>
    public static ServerContent Load(string dataDir, string? gameDataDir = null) => new(
        ReadLinesOr(Path.Combine(dataDir, "motd.txt"),
                    ReadIniLines(gameDataDir, "Motd.json", "Motd", DefaultMotd)),
        ReadLinesOr(Path.Combine(dataDir, "updates.txt"),
                    ReadIniLines(gameDataDir, "ACTUALIZACIONES.json", "UPDATE", DefaultUpdates)));

    /// <summary>Lee las "Line1..LineN" de una sección del .dat transcodeado (formato {meta:{SECCION:{Line1:…}}}).</summary>
    private static string[] ReadIniLines(string? gameDataDir, string file, string section, string[] fallback)
    {
        if (gameDataDir is null) return fallback;
        var path = Path.Combine(gameDataDir, file);
        if (!File.Exists(path)) return fallback;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("meta", out var meta)) return fallback;
            if (!meta.TryGetProperty(section, out var sec)) return fallback;
            var lines = new List<string>();
            for (int i = 1; ; i++)
            {
                if (!sec.TryGetProperty($"Line{i}", out var line)) break;
                var text = line.GetString();
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text!);
            }
            return lines.Count > 0 ? lines.ToArray() : fallback;
        }
        catch (System.Text.Json.JsonException) { return fallback; }
    }

    private static string[] ReadLinesOr(string path, string[] fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path)
                    .Select(l => l.TrimEnd())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToArray();
                if (lines.Length > 0) return lines;
            }
        }
        catch { /* cae al default */ }
        return fallback;
    }
}
