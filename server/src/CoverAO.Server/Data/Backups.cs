namespace CoverAO.Server.Data;

/// <summary>
/// Copias de seguridad del almacén, con el servidor andando.
///
/// Es la pieza que le faltaba al que HOSTEA: hasta acá, el único resguardo era apagar el server y copiar
/// <c>data/</c> a mano. Un disco que se llena a mitad de un guardado, un <c>/BORRAR</c> mal tipeado o una
/// corrupción del .db se llevaban puestos todos los personajes sin vuelta atrás.
///
/// El snapshot lo hace el backend (<see cref="IStore.Backup"/>): SQLite con <c>VACUUM INTO</c>, que es
/// consistente aunque haya escrituras en curso. Acá vive lo de arriba: el nombre con fecha, y la PODA —
/// sin poda, un backup cada 30 minutos llena el disco en una semana y el remedio termina peor que la
/// enfermedad.
/// </summary>
public static class Backups
{
    public const string Prefijo = "coverao-";

    /// <summary>Nombre base de un backup: <c>coverao-AAAAMMDD-HHMMSS</c>. Ordena cronológicamente como texto,
    /// que es de lo que depende la poda (y evita depender de la fecha del filesystem).</summary>
    public static string Stem(DateTime cuando) => Prefijo + cuando.ToString("yyyyMMdd-HHmmss");

    /// <summary>Hace un backup en <paramref name="dir"/> y devuelve la ruta creada.</summary>
    /// <param name="dataRoot">La carpeta <c>data/</c>. Se le copian tambien los archivos SUELTOS que hay ahi
    /// —hoy <c>banned-ips.txt</c>— porque el backend solo sabe de Charfile/Cuentas/Misc (y en SQLite, de la
    /// base). Sin esto la copia se veia completa y no lo era: restaurar en otra maquina traia los personajes
    /// y perdia la lista de baneos por IP, en silencio. Un backup que no alcanza para levantar el servidor
    /// entero es peor que no tenerlo, porque uno se confia.</param>
    public static string Crear(IStore store, string dir, DateTime cuando, string? dataRoot = null)
    {
        Directory.CreateDirectory(dir);
        string ruta = store.Backup(dir, Stem(cuando));

        if (dataRoot is not null && Directory.Exists(dataRoot))
        {
            // Si el backend devolvio un ARCHIVO (SQLite: el .db), los sueltos van al lado, en una carpeta con
            // el mismo nombre. Si devolvio una carpeta (FileStore), van adentro.
            string destino = Directory.Exists(ruta) ? ruta : Path.Combine(dir, Stem(cuando));
            Directory.CreateDirectory(destino);
            foreach (var f in Directory.EnumerateFiles(dataRoot))
            {
                try { File.Copy(f, Path.Combine(destino, Path.GetFileName(f)), overwrite: true); }
                catch (IOException) { /* un archivo tomado por otro proceso no puede voltear el backup */ }
            }
        }
        return ruta;
    }

    /// <summary>Los backups que hay en el directorio, del más nuevo al más viejo.</summary>
    public static string[] Listar(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFileSystemEntries(dir, Prefijo + "*")
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Deja sólo los <paramref name="conservar"/> más nuevos y borra el resto. Devuelve cuántos borró.
    /// Con <paramref name="conservar"/> &lt;= 0 no borra nada — retención infinita es una decisión válida del
    /// host, y no queremos que un 0 mal puesto en el ini borre TODOS los backups.</summary>
    public static int Podar(string dir, int conservar)
    {
        if (conservar <= 0) return 0;
        int borrados = 0;
        foreach (var viejo in Listar(dir).Skip(conservar))
        {
            try
            {
                if (Directory.Exists(viejo)) Directory.Delete(viejo, recursive: true);
                else File.Delete(viejo);
                borrados++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[backup] no se pudo borrar {viejo}: {ex.Message}"); }
        }
        return borrados;
    }
}
