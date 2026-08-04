using System.Text;
using CoverAO.Protocol;

namespace CoverAO.Server.Data;

/// <summary>
/// One-shot importer of the legacy flat-file store (Charfile/*.chr, Cuentas/*.cnt, Misc/*.json) into a
/// SQLite database — the Fase-4 "importador .chr→SQLite" gate. Both backends share the exact same INI
/// serialization (see <see cref="IStore"/>), so migration is a verbatim row-by-row copy: read each
/// file with its original encoding, write it into the SQLite table. Idempotent (upserts by name).
/// Invoked from the server CLI: <c>CoverAO.Server migrate &lt;dataDir&gt; [dbPath]</c>.
/// </summary>
public static class Migrator
{
    public static int Run(string dataRoot, string dbPath)
    {
        if (!Directory.Exists(dataRoot))
        {
            Console.WriteLine($"[migrate] No existe el directorio de datos: {dataRoot}");
            return 1;
        }
        Console.WriteLine($"[migrate] {dataRoot} → SQLite {dbPath}");
        using var sqlite = new SqliteStore(dbPath);
        int chars = Copy(Path.Combine(dataRoot, "Charfile"), "*.chr", Codec.Ansi, sqlite.WriteCharacter);
        int accts = Copy(Path.Combine(dataRoot, "Cuentas"), "*.cnt", Codec.Ansi, sqlite.WriteAccount);
        int misc = Copy(Path.Combine(dataRoot, "Misc"), "*.json", Encoding.UTF8, sqlite.WriteMisc);
        Console.WriteLine($"[migrate] listo: {chars} personajes, {accts} cuentas, {misc} blobs misc → {dbPath}");
        return 0;
    }

    private static int Copy(string dir, string glob, Encoding enc, Action<string, string> write)
    {
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (var file in Directory.GetFiles(dir, glob))
        {
            write(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file, enc));
            n++;
        }
        return n;
    }
}
