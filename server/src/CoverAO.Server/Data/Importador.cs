using CoverAO.Server.Game;
using Microsoft.Data.Sqlite;

namespace CoverAO.Server.Data;

/// <summary>
/// IMPORTAR Y EXPORTAR (fase5/postgres/DISENO.md, sección 6), y el primer arranque que importa solo.
///
/// <para><b>Qué se importa:</b> una carpeta de datos —<c>Charfile/</c>, <c>Cuentas/</c>, <c>Misc/</c>—, la nuestra o la de un
/// servidor del ORIGINAL (el lector entiende sus claves, y su correo va adentro de cada <c>.chr</c>), una <c>coverao.db</c>
/// del SQLite viejo, el del texto del <c>.chr</c> en una columna, o una base de SQLite de ahora —la que se pasa a Postgres,
/// o un backup—.</para>
///
/// <para><b>Todo o nada.</b> En SQLite se copia a una base TEMPORAL que sólo pasa a ser la de verdad si la verificación da
/// bien —cada personaje releído igual—; si no, se tira. En Postgres se copia adentro de UNA transacción, que sólo se confirma
/// si la verificación da bien. En los dos casos, si algo no cierra no cambió nada.</para>
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
        var (almacen, correoAparte, ipsAparte, liberar) = AbrirOrigen(origen);
        try
        {
            using var destino = new AlmacenSqlite(temporal, paraImportar: true);
            informe = CopiaDeAlmacen.Copiar(almacen, destino, correoAparte, ipsAparte);
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

    /// <summary>
    /// Importa <paramref name="origen"/> a una base de Postgres, todo en UNA transacción que sólo se confirma si la
    /// verificación da bien. Una base con cuentas no se toca, salvo con <paramref name="reemplazar"/>: ahí antes se hace un
    /// backup de lo que hay en <paramref name="carpetaDeBackups"/>, y lo de antes se borra adentro de la misma transacción.
    /// </summary>
    public static InformeDeCopia ImportarAPostgres(string origen, AlmacenPostgres destino, bool reemplazar, string carpetaDeBackups)
    {
        bool vacia = destino.EstaVacia();
        if (!vacia && !reemplazar)
            throw new InvalidOperationException("la base de Postgres ya tiene cuentas: para pisarla, --reemplazar (antes se hace un backup de lo que hay).");
        if (!vacia)
        {
            // Sin el prefijo de los backups de siempre: la poda no lo cuenta ni lo borra.
            string backup = destino.Backup(carpetaDeBackups, $"{Backups.PrefijoDeLosApartados}importar-{DateTime.Now:yyyyMMdd-HHmmss}");
            Console.WriteLine($"Lo que había en la base quedó en {backup}.");
        }
        var (almacen, correoAparte, ipsAparte, liberar) = AbrirOrigen(origen);
        try
        {
            return destino.EnUnaSolaTransaccion(() =>
            {
                if (!vacia) destino.Vaciar();
                return CopiaDeAlmacen.Copiar(almacen, destino, correoAparte, ipsAparte);
            }, informe => informe.SalioBien);
        }
        finally { liberar?.Dispose(); }
    }

    /// <summary>
    /// Restaura un backup de Postgres —el zip de <see cref="AlmacenPostgres.Backup"/>— en la base de Postgres. Una base con
    /// cuentas no se toca, salvo con <paramref name="reemplazar"/>: ahí antes se hace un backup de lo que hay.
    /// </summary>
    public static IReadOnlyList<(string Tabla, long Filas)> RestaurarEnPostgres(string zip, AlmacenPostgres destino, bool reemplazar,
        string carpetaDeBackups)
    {
        if (!File.Exists(zip)) throw new InvalidOperationException($"no existe: {zip}");
        if (!destino.EstaVacia())
        {
            if (!reemplazar)
                throw new InvalidOperationException("la base de Postgres ya tiene cuentas: para pisarla, --reemplazar (antes se hace un backup de lo que hay).");
            string backup = destino.Backup(carpetaDeBackups, $"{Backups.PrefijoDeLosApartados}restaurar-{DateTime.Now:yyyyMMdd-HHmmss}");
            Console.WriteLine($"Lo que había en la base quedó en {backup}.");
        }
        return destino.Restaurar(zip);
    }

    /// <summary>
    /// EL PRIMER ARRANQUE CON POSTGRES: si la base está vacía y en <c>data/</c> hay un mundo —la <c>coverao.db</c> de cuando
    /// el servidor andaba con SQLite, o personajes en archivos—, se importa solo, y lo viejo queda renombrado a
    /// <c>*.importado</c>. Así se pasa de SQLite a Postgres cambiando el <c>Tipo</c>. Devuelve el informe, o null si no
    /// había nada que importar; si la verificación falla, no toca nada y tira.
    /// </summary>
    public static InformeDeCopia? ImportarAPostgresSiHaceFalta(string dataRoot, string archivoSqlite, AlmacenPostgres destino)
    {
        string origen;
        if (File.Exists(archivoSqlite)) origen = archivoSqlite;
        else if (HayPersonajesEnArchivos(dataRoot)) origen = dataRoot;
        else return null;
        if (!destino.EstaVacia())
        {
            Console.WriteLine($"[base] OJO: {origen} tiene un mundo que no se usa: la base es la de Postgres, que ya tiene cuentas.");
            return null;
        }

        Console.WriteLine($"[base] la base de Postgres está vacía: importando {origen}");
        var informe = ImportarAPostgres(origen, destino, reemplazar: false, Path.Combine(dataRoot, "backups"));
        string ruta = Path.Combine(dataRoot, $"importacion-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        EscrituraAtomica.Escribir(ruta, informe.Texto(), System.Text.Encoding.UTF8);
        if (!informe.SalioBien) throw new InvalidOperationException($"La importación no dio bien y no se tocó nada: ver {ruta}.");
        if (origen == archivoSqlite) ApartarBase(archivoSqlite, ".importado");
        else ApartarCarpetas(dataRoot);
        Console.WriteLine($"[base] importado: {informe.Personajes} personajes de {informe.Cuentas} cuentas, " +
                          $"{informe.Avisos.Count} aviso(s). El informe está en {ruta}.");
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
        if (origen == dataRoot) ApartarCarpetas(dataRoot);
        Console.WriteLine($"[base] importado: {informe.Personajes} personajes de {informe.Cuentas} cuentas, " +
                          $"{informe.Avisos.Count} aviso(s). El informe está en {ruta}.");
        return informe;
    }

    /// <summary>Las carpetas de los archivos ya importadas, renombradas a <c>*.importado</c>: quedan de respaldo y no se
    /// vuelven a importar.</summary>
    private static void ApartarCarpetas(string dataRoot)
    {
        foreach (var carpeta in new[] { "Charfile", "Cuentas", "Misc" })
        {
            string vieja = Path.Combine(dataRoot, carpeta);
            if (Directory.Exists(vieja)) Directory.Move(vieja, vieja + ".importado");
        }
    }

    public static bool HayPersonajesEnArchivos(string dataRoot)
    {
        string personajes = Path.Combine(dataRoot, "Charfile"), cuentas = Path.Combine(dataRoot, "Cuentas");
        return (Directory.Exists(personajes) && Directory.EnumerateFiles(personajes, "*.chr").Any())
            || (Directory.Exists(cuentas) && Directory.EnumerateFiles(cuentas, "*.cnt").Any());
    }

    /// <summary>El almacén de origen, el correo que trae aparte y lo que hay que cerrar al terminar.</summary>
    private static (IAlmacen Almacen, IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> CorreoAparte, IReadOnlyList<string> IpsAparte,
        IDisposable? Liberar) AbrirOrigen(string origen)
    {
        var sinCorreo = new Dictionary<string, IReadOnlyList<MailEntry>>();
        var sinIps = Array.Empty<string>();
        if (File.Exists(origen))
        {
            if (EsDelSqliteViejo(origen))
            {
                var viejo = new SqliteStore(origen);
                return (new AlmacenDeTexto(viejo), sinCorreo, sinIps, viejo);
            }
            // Una base de ahora: la de un servidor que pasa a Postgres, o un backup. Se lee de una COPIA al lado: abrirla le
            // aplicaría las versiones del esquema que le falten, y el origen —un backup, sobre todo— no se toca. Con su
            // diario, que puede tener lo último que se escribió.
            string copia = origen + ".leyendo";
            BorrarBase(copia);
            File.Copy(origen, copia);
            if (File.Exists(origen + "-wal")) File.Copy(origen + "-wal", copia + "-wal");
            var deAhora = new AlmacenSqlite(copia);
            return (deAhora, sinCorreo, sinIps, new AlCerrar(() =>
            {
                deAhora.Dispose();
                BorrarBase(copia);
            }));
        }
        if (!Directory.Exists(origen)) throw new InvalidOperationException($"no existe: {origen}");
        var store = new FileStore(origen, crearCarpetas: false);
        return (new AlmacenDeTexto(store), CorreoDeLosChr(store), IpsDeLaCarpeta(origen), null);
    }

    /// <summary>Las IPs baneadas de una carpeta de datos, que van en un archivo suelto: <c>banned-ips.txt</c> en una nuestra,
    /// <c>Dat/BanIps.dat</c> en la del original (una por renglón; lo que no parece una IP —el "0" que trae su instalación— no
    /// se toma).</summary>
    private static List<string> IpsDeLaCarpeta(string carpeta)
    {
        var ips = new List<string>();
        foreach (var archivo in new[] { Path.Combine(carpeta, "banned-ips.txt"), Path.Combine(carpeta, "Dat", "BanIps.dat") })
            if (File.Exists(archivo))
                ips.AddRange(File.ReadAllLines(archivo).Select(l => l.Trim()).Where(l => l.Contains('.') || l.Contains(':')));
        return ips;
    }

    private sealed class AlCerrar(Action cerrar) : IDisposable
    {
        public void Dispose() => cerrar();
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
