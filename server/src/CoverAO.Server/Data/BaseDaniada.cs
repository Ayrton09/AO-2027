using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// LA BASE DAÑADA AL ARRANCAR. Si <c>data/coverao.db</c> está rota ("database disk image is malformed", o no es una base), el
/// servidor no arranca, y antes se caía con la excepción cruda de SQLite y su pila: el que hostea no sabía qué hacer. Le pasó al
/// tester el 06/10. Ahora se explica qué pasó y cómo volver al último backup con <c>restaurar</c>, que aparta la base rota al
/// lado (no la borra) y verifica personaje por personaje.
/// </summary>
public static class BaseDaniada
{
    /// <summary>¿La excepción (o alguna de adentro) es de una base SQLite dañada? <c>SQLITE_CORRUPT</c> (11) o <c>SQLITE_NOTADB</c> (26).</summary>
    public static bool Es(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqliteException { SqliteErrorCode: 11 or 26 }) return true;
        return false;
    }

    /// <summary>Los backups de SQLite de <c>data/backups</c>, del más nuevo al más viejo.</summary>
    public static IReadOnlyList<string> Backups(string dataRoot)
    {
        string dir = Path.Combine(dataRoot, "backups");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
    }

    /// <summary>El aviso para el que hostea: qué pasó y el comando para volver al backup más nuevo. Sin acentos, como los demás
    /// mensajes de la consola: la de Windows no los muestra bien.</summary>
    public static string Aviso(string dataRoot, Exception ex)
    {
        var backups = Backups(dataRoot);
        string exe = OperatingSystem.IsWindows() ? "CoverAO.Server.exe" : "./CoverAO.Server";
        string carpeta = Path.Combine("data", "backups");
        var t = new System.Text.StringBuilder();
        t.AppendLine();
        t.AppendLine("!! LA BASE DE DATOS ESTA ROTA y el servidor no arranca, para no empeorarla.");
        t.AppendLine($"   {Path.Combine(dataRoot, "coverao.db")}: {ex.Message}");
        t.AppendLine("   Suele pasar si se copia o se mueve la carpeta con el servidor prendido, si un programa la sincroniza");
        t.AppendLine("   (OneDrive, Dropbox) o si la PC se apaga de golpe.");
        t.AppendLine();
        if (backups.Count == 0)
        {
            t.AppendLine($"   No hay backups en {carpeta}. Si tenes una copia de la carpeta data de antes, volve a ponerla.");
            return t.ToString();
        }
        t.AppendLine("   PARA VOLVER AL ULTIMO BACKUP, con el servidor cerrado, en esta carpeta:");
        t.AppendLine();
        t.AppendLine($"     {exe} restaurar {Path.Combine(carpeta, Path.GetFileName(backups[0]))} --reemplazar");
        t.AppendLine();
        t.AppendLine("   La base rota no se borra: queda al lado, con \"antes-de-importar\" en el nombre. Se pierde lo jugado");
        t.AppendLine("   desde ese backup. Si el restaurar dice que el backup tambien esta roto, proba con el anterior:");
        foreach (var b in backups.Take(6)) t.AppendLine($"     {Path.Combine(carpeta, Path.GetFileName(b))}");
        t.AppendLine("   Ver HOSTING.md, \"Volver atras con un backup\".");
        return t.ToString();
    }
}
