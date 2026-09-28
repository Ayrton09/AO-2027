using CoverAO.Server.Game;
using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// IMPORTAR Y EXPORTAR (fase5/postgres/DISENO.md, sección 6), y el primer arranque que importa solo.
///
/// <para><b>Qué se importa:</b> una carpeta de datos —<c>Charfile/</c>, <c>Cuentas/</c>, <c>Misc/</c>—, la nuestra o la de un
/// servidor del ORIGINAL (el lector entiende sus claves, y su correo va adentro de cada <c>.chr</c>), o una
/// <c>coverao.db</c> del SQLite viejo, el del texto del <c>.chr</c> en una columna.</para>
///
/// <para><b>Todo o nada.</b> Se copia a una base TEMPORAL; sólo si la verificación da bien —cada personaje releído igual— pasa
/// a ser la de verdad. Si no, se tira y no cambió nada.</para>
/// </summary>
public static class Importador
{
    /// <summary>Importa <paramref name="origen"/> a la base <paramref name="archivoDeLaBase"/>. Con una base que ya existe no
    /// hace nada, salvo con <paramref name="reemplazar"/>: ahí la de antes queda al lado, renombrada.</summary>
    public static InformeDeCopia Importar(string origen, string archivoDeLaBase, bool reemplazar = false)
    {
        if (File.Exists(archivoDeLaBase) && !reemplazar)
            throw new InvalidOperationException($"{archivoDeLaBase} ya existe: para pisarla, --reemplazar (la de antes queda al lado).");
        string temporal = archivoDeLaBase + ".importando";
        BorrarBase(temporal);
        InformeDeCopia informe;
        var (almacen, correoAparte, liberar) = AbrirOrigen(origen);
        try
        {
            using var destino = new AlmacenSqlite(temporal, paraImportar: true);
            informe = CopiaDeAlmacen.Copiar(almacen, destino, correoAparte);
        }
        catch
        {
            BorrarBase(temporal);
            throw;
        }
        finally { liberar?.Dispose(); }
        if (!informe.SalioBien)
        {
            BorrarBase(temporal);
            return informe;
        }
        AlDisco(temporal);
        if (File.Exists(archivoDeLaBase))
            ApartarBase(archivoDeLaBase, $".antes-de-importar-{DateTime.Now:yyyyMMdd-HHmmss}");
        File.Move(temporal, archivoDeLaBase);
        return informe;
    }

    /// <summary>Exporta un almacén a una carpeta de <c>.chr</c> y <c>.cnt</c>, vacía o nueva: para mirar un personaje con un
    /// editor, para las herramientas que leen <c>.chr</c> y para volver atrás.</summary>
    public static InformeDeCopia Exportar(IAlmacen origen, string carpeta)
    {
        if (Directory.Exists(carpeta) && Directory.EnumerateFileSystemEntries(carpeta).Any())
            throw new InvalidOperationException($"{carpeta} no está vacía: se exporta a una carpeta nueva o vacía.");
        return CopiaDeAlmacen.Copiar(origen, new AlmacenDeTexto(new FileStore(carpeta)));
    }

    /// <summary>
    /// EL PRIMER ARRANQUE DE LA VERSIÓN CON BASE, en una instalación que ya tenía datos: los importa solo y deja lo viejo
    /// renombrado a <c>*.importado</c>, que no se vuelve a importar. Devuelve el informe, o null si no había nada que importar.
    /// Si la verificación falla, no toca nada y tira: el servidor no puede arrancar con un mundo a medias.
    /// </summary>
    public static InformeDeCopia? ImportarSiHaceFalta(string dataRoot, string archivoDeLaBase)
    {
        string origen;
        bool eraElSqliteViejo = false;
        if (File.Exists(archivoDeLaBase))
        {
            if (!EsDelSqliteViejo(archivoDeLaBase)) return null;
            // El SQLite viejo tiene el mismo nombre de archivo que la base: se lo aparta primero y se importa desde ahí.
            origen = ApartarBase(archivoDeLaBase, ".importado");
            eraElSqliteViejo = true;
        }
        else if (HayPersonajesEnArchivos(dataRoot)) origen = dataRoot;
        else return null;

        Console.WriteLine($"[base] primer arranque con base: importando {origen}");
        InformeDeCopia informe;
        try { informe = Importar(origen, archivoDeLaBase); }
        catch
        {
            if (eraElSqliteViejo) Restituir(origen, archivoDeLaBase);
            throw;
        }
        string ruta = Path.Combine(dataRoot, $"importacion-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        EscrituraAtomica.Escribir(ruta, informe.Texto(), System.Text.Encoding.UTF8);
        if (!informe.SalioBien)
        {
            if (eraElSqliteViejo) Restituir(origen, archivoDeLaBase);
            throw new InvalidOperationException($"La importación no dio bien y no se tocó nada: ver {ruta}.");
        }
        if (origen == dataRoot)
            foreach (var carpeta in new[] { "Charfile", "Cuentas", "Misc" })
            {
                string vieja = Path.Combine(dataRoot, carpeta);
                if (Directory.Exists(vieja)) Directory.Move(vieja, vieja + ".importado");
            }
        Console.WriteLine($"[base] importado: {informe.Personajes} personajes de {informe.Cuentas} cuentas, " +
                          $"{informe.Avisos.Count} aviso(s). El informe está en {ruta}.");
        return informe;
    }

    public static bool HayPersonajesEnArchivos(string dataRoot)
    {
        string personajes = Path.Combine(dataRoot, "Charfile"), cuentas = Path.Combine(dataRoot, "Cuentas");
        return (Directory.Exists(personajes) && Directory.EnumerateFiles(personajes, "*.chr").Any())
            || (Directory.Exists(cuentas) && Directory.EnumerateFiles(cuentas, "*.cnt").Any());
    }

    /// <summary>El almacén de origen, el correo que trae aparte y lo que hay que cerrar al terminar.</summary>
    private static (IAlmacen Almacen, IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> CorreoAparte, IDisposable? Liberar)
        AbrirOrigen(string origen)
    {
        var sinCorreo = new Dictionary<string, IReadOnlyList<MailEntry>>();
        if (File.Exists(origen))
        {
            var viejo = new SqliteStore(origen);
            return (new AlmacenDeTexto(viejo), sinCorreo, viejo);
        }
        if (!Directory.Exists(origen)) throw new InvalidOperationException($"no existe: {origen}");
        var store = new FileStore(origen, crearCarpetas: false);
        return (new AlmacenDeTexto(store), CorreoDeLosChr(store), null);
    }

    /// <summary>El correo del ORIGINAL, que va adentro del <c>.chr</c> de cada destinatario (ver
    /// <see cref="CharacterFile.CorreoDelOriginal"/>). En una carpeta nuestra no hay, y esto no encuentra nada.</summary>
    private static Dictionary<string, IReadOnlyList<MailEntry>> CorreoDeLosChr(FileStore store)
    {
        var correo = new Dictionary<string, IReadOnlyList<MailEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var nombre in store.AllCharacterNames())
        {
            var texto = store.ReadCharacter(nombre);
            if (texto is null) continue;
            var cartas = CharacterFile.CorreoDelOriginal(IniFile.Parse(texto));
            if (cartas.Count > 0) correo[nombre] = cartas;
        }
        return correo;
    }

    private static bool EsDelSqliteViejo(string archivo)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivo, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'characters'";
        return (long)c.ExecuteScalar()! > 0;
    }

    /// <summary>La base y sus archivos de al lado (el diario, la memoria compartida), renombrados con el mismo sufijo.
    /// Devuelve la ruta nueva de la base.</summary>
    private static string ApartarBase(string archivo, string sufijo)
    {
        SqliteConnection.ClearAllPools();
        string nuevo = archivo + sufijo;
        File.Move(archivo, nuevo);
        foreach (var lado in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(archivo + lado)) File.Move(archivo + lado, nuevo + lado);
        return nuevo;
    }

    /// <summary>Deshace <see cref="ApartarBase"/>: la base vuelve a su nombre, con sus archivos de al lado.</summary>
    private static void Restituir(string apartada, string original)
    {
        SqliteConnection.ClearAllPools();
        File.Move(apartada, original);
        foreach (var lado in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(apartada + lado)) File.Move(apartada + lado, original + lado);
    }

    private static void BorrarBase(string archivo)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { archivo, archivo + "-wal", archivo + "-shm", archivo + "-journal" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>La base temporal se llenó sin sincronizar (<c>synchronous = OFF</c>): antes de que pase a ser la de verdad, se
    /// la baja al disco entera.</summary>
    private static void AlDisco(string archivo)
    {
        SqliteConnection.ClearAllPools();
        using var f = new FileStream(archivo, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        f.Flush(flushToDisk: true);
    }
}
