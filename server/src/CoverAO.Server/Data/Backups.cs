namespace CoverAO.Server.Data;

/// <summary>
/// Copias de seguridad del almacén, con el servidor andando.
///
/// Es la pieza que le faltaba al que HOSTEA: hasta acá, el único resguardo era apagar el server y copiar
/// <c>data/</c> a mano. Un disco que se llena a mitad de un guardado, un <c>/BORRAR</c> mal tipeado o una
/// corrupción del .db se llevaban puestos todos los personajes sin vuelta atrás.
///
/// El snapshot lo hace el backend (<see cref="IAlmacen.Backup"/>): SQLite con <c>VACUUM INTO</c>, que es
/// consistente aunque haya escrituras en curso. Acá vive lo de arriba: el nombre con fecha, y la PODA —
/// sin poda, un backup cada 30 minutos llena el disco en una semana y el remedio termina peor que la
/// enfermedad.
/// </summary>
public static class Backups
{
    public const string Prefijo = "coverao-";

    /// <summary>El de los backups que se APARTAN antes de pisar la base —<c>antes-de-importar-…</c>—: van a la misma
    /// carpeta sin <see cref="Prefijo"/>, así la poda no los cuenta ni los borra.</summary>
    public const string PrefijoDeLosApartados = "antes-de-";

    /// <summary>El de un backup A MEDIO HACER: se arma con este prefijo y recién entero pasa a su nombre, así uno cortado a la
    /// mitad —un corte de luz, un reinicio de Windows, el disco lleno— no queda con el nombre de uno bueno, y justo el último,
    /// el que se va a querer restaurar. Un prefijo y no una extensión: la retención lista con <see cref="Prefijo"/> y un
    /// <c>coverao-….escribiendo</c> le entraría igual (lo mismo que el <c>parcial-</c> de las carpetas de archivos).</summary>
    public const string PrefijoTemporal = "parcial-";

    /// <summary>Dónde se arma el backup <paramref name="archivo"/> antes de tener su nombre. Borra las sobras de los que se
    /// cortaron: no las mira nadie y ocupan disco. Corre un backup por vez, así que no hay otro armándose.</summary>
    internal static string Temporal(string destDir, string archivo)
    {
        foreach (var viejo in Directory.EnumerateFiles(destDir, PrefijoTemporal + "*"))
            try { File.Delete(viejo); }
            catch (Exception ex) { Console.Error.WriteLine($"[backup] no se pudo limpiar {Path.GetFileName(viejo)}: {ex.Message}"); }
        return Path.GetFullPath(Path.Combine(destDir, PrefijoTemporal + archivo));
    }

    /// <summary>Nombre base de un backup: <c>coverao-AAAAMMDD-HHMMSS</c>. Ordena cronológicamente como texto,
    /// que es de lo que depende la poda (y evita depender de la fecha del filesystem).</summary>
    public static string Stem(DateTime cuando) => Prefijo + cuando.ToString("yyyyMMdd-HHmmss");

    /// <summary>Hace un backup en <paramref name="dir"/> y devuelve la ruta creada.</summary>
    /// <param name="dataRoot">La carpeta <c>data/</c>. Se le copian tambien los archivos SUELTOS que hay ahi
    /// —hoy <c>banned-ips.txt</c>— porque el backend solo sabe de Charfile/Cuentas/Misc (y en SQLite, de la
    /// base). Sin esto la copia se veia completa y no lo era: restaurar en otra maquina traia los personajes
    /// y perdia la lista de baneos por IP, en silencio. Un backup que no alcanza para levantar el servidor
    /// entero es peor que no tenerlo, porque uno se confia.</param>
    /// <param name="sinCopiar">Acá van los sueltos que no se pudieron copiar (un archivo tomado por otro proceso no voltea el
    /// backup, pero tampoco se calla: queda en el log, y quien pide la lista decide si le alcanza).</param>
    public static string Crear(IAlmacen almacen, string dir, DateTime cuando, string? dataRoot = null, ICollection<string>? sinCopiar = null)
    {
        Directory.CreateDirectory(dir);
        string ruta = almacen.Backup(dir, Stem(cuando));

        if (dataRoot is not null && Directory.Exists(dataRoot))
        {
            // Si el backend devolvio un ARCHIVO (SQLite: el .db), los sueltos van al lado, en una carpeta con
            // el mismo nombre. Si devolvio una carpeta (FileStore), van adentro.
            string destino = Directory.Exists(ruta) ? ruta : Path.Combine(dir, Stem(cuando));
            Directory.CreateDirectory(destino);
            foreach (var f in Directory.EnumerateFiles(dataRoot))
            {
                // LA BASE NO: su copia es la de arriba, consistente. Copiar el archivo vivo —y su diario— da una base a
                // medio escribir, del doble de tamaño, que encima parece un backup.
                if (EsDeLaBase(f)) continue;
                // EL TOKEN DEL PANEL TAMPOCO: vale mientras dure la sesión, y un backup se baja, se manda y se muda.
                // Ni su temporal, que queda si falló el renombrado (auditoría de la 1.0.13).
                if (Path.GetFileName(f).Equals(Net.AdminServer.NombreDelArchivoDelToken, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(f).Equals(Net.AdminServer.NombreDelArchivoDelToken + ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(f, Path.Combine(destino, Path.GetFileName(f)), overwrite: true); }
                catch (IOException ex)
                {
                    Console.Error.WriteLine($"[backup] no se pudo copiar {Path.GetFileName(f)}: {ex.Message}");
                    sinCopiar?.Add(Path.GetFileName(f));
                }
            }
        }
        return ruta;
    }

    /// <summary>¿La base, su diario o una COPIA de la base? Las copias apartadas —la de antes de importar, la del SQLite viejo ya
    /// importado, las temporales de una importación— tampoco van: cada backup llevaría otra copia entera de una base que ya no
    /// se usa, y con la retención de siempre son decenas.</summary>
    private static bool EsDeLaBase(string archivo)
    {
        string nombre = Path.GetFileName(archivo);
        return nombre.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || nombre.Contains(".db-", StringComparison.OrdinalIgnoreCase)    // -wal, -shm, -journal
            || nombre.Contains(".db.", StringComparison.OrdinalIgnoreCase);   // .importado, .antes-de-importar-…, .importando, .leyendo
    }

    /// <summary>Los backups que hay en el directorio, del más nuevo al más viejo. UNO por backup: con SQLite
    /// cada backup deja DOS entradas —el <c>.db</c> y la carpeta con los archivos sueltos de <c>data/</c>— y las
    /// dos empiezan con el mismo prefijo. Contándolas por separado, "conservar 3" guardaba un backup y medio.</summary>
    public static string[] Listar(string dir) => PorBackup(dir).Select(g => g.First()).ToArray();

    /// <summary>Las partes de cada backup, agrupadas por su nombre y del más nuevo al más viejo. Dentro de cada
    /// grupo va primero el archivo (el <c>.db</c>, que es lo que se restaura) y después la carpeta.</summary>
    private static IEnumerable<IGrouping<string, string>> PorBackup(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<IGrouping<string, string>>();
        return Directory.EnumerateFileSystemEntries(dir, Prefijo + "*")
            .OrderBy(p => Directory.Exists(p) ? 1 : 0)
            // El `?? p` no es defensa contra nada: `GetFileNameWithoutExtension` sólo devuelve null si la ruta
            // es null, y `EnumerateFileSystemEntries` nunca la da. Está para que el build quede en CERO avisos,
            // que es lo que hace que el próximo aviso —uno de verdad— se vea.
            .GroupBy(p => Path.GetFileNameWithoutExtension(p) ?? p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Deja sólo los <paramref name="conservar"/> más nuevos y borra el resto. Devuelve cuántos borró.
    /// Con <paramref name="conservar"/> &lt;= 0 no borra nada — retención infinita es una decisión válida del
    /// host, y no queremos que un 0 mal puesto en el ini borre TODOS los backups.</summary>
    public static int Podar(string dir, int conservar)
    {
        if (conservar <= 0) return 0;
        int borrados = 0;
        foreach (var grupo in PorBackup(dir).Skip(conservar))
        {
            bool algo = false;
            foreach (var parte in grupo)   // el .db Y la carpeta de sueltos: dejar la mitad es dejar basura
            {
                try
                {
                    if (Directory.Exists(parte)) Directory.Delete(parte, recursive: true);
                    else File.Delete(parte);
                    algo = true;
                }
                catch (Exception ex) { Console.Error.WriteLine($"[backup] no se pudo borrar {parte}: {ex.Message}"); }
            }
            if (algo) borrados++;
        }
        return borrados;
    }
}
