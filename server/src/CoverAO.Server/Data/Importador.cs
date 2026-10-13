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
    /// hace nada, salvo con <paramref name="reemplazar"/>: ahí la de antes queda al lado, renombrada. Con
    /// <paramref name="ipsDeMas"/>, esas IPs baneadas se suman a las del origen (salvo de una base de ahora, que pasa entera).</summary>
    public static InformeDeCopia Importar(string origen, string archivoDeLaBase, bool reemplazar = false,
        IReadOnlyList<string>? ipsDeMas = null)
    {
        if (File.Exists(archivoDeLaBase) && !reemplazar)
            throw new InvalidOperationException($"{archivoDeLaBase} ya existe: para pisarla, --reemplazar (la de antes queda al lado).");
        // Con el servidor andando sobre ella, pisarla le cambiaría la base abajo de los pies.
        using var candado = AlmacenSqlite.Candado(archivoDeLaBase);
        string temporal = archivoDeLaBase + ".importando";
        BorrarBase(temporal);
        InformeDeCopia informe;
        var (almacen, correoAparte, ipsAparte, cortados, liberar) = AbrirOrigen(origen);
        try
        {
            if (almacen is AlmacenSqlite deAhora)
            {
                // UNA BASE DE AHORA —un backup que se restaura— PASA ENTERA: es la misma base, así que se copia el archivo, ya
                // puesto al día, con las fechas y los personajes apartados. Personaje por personaje se perdía lo que la ficha no
                // lleva. Y se verifica igual.
                deAhora.CopiarA(temporal);
                informe = new InformeDeCopia();
                using var copia = new AlmacenSqlite(temporal);
                CopiaDeAlmacen.Contar(copia, informe);
                CopiaDeAlmacen.Verificar(deAhora, copia, informe);
            }
            else
            {
                using var destino = new AlmacenSqlite(temporal, paraImportar: true);
                informe = CopiaDeAlmacen.Copiar(almacen, destino, correoAparte, [.. ipsAparte, .. ipsDeMas ?? []]);
                informe.Avisos.AddRange(cortados);
            }
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
        string? apartada = File.Exists(archivoDeLaBase)
            ? ApartarBase(archivoDeLaBase, $".antes-de-importar-{DateTime.Now:yyyyMMdd-HHmmss}")
            : null;
        try { File.Move(temporal, archivoDeLaBase); }
        catch
        {
            // Sin la nueva en su lugar —un antivirus que la tiene tomada—, la de antes vuelve al suyo: si no, el próximo
            // arranque encontraría el lugar vacío y levantaría un mundo nuevo, sin nadie.
            if (apartada is not null)
            {
                try { Restituir(apartada, archivoDeLaBase); }
                catch (Exception ex) { Console.Error.WriteLine($"OJO: la base de antes quedó en {apartada} y no se pudo volver a poner: {ex.Message}"); }
            }
            throw;
        }
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
        var (almacen, correoAparte, ipsAparte, cortados, liberar) = AbrirOrigen(origen);
        try
        {
            return destino.EnUnaSolaTransaccion(() =>
            {
                if (!vacia) destino.Vaciar();
                // Una base de SQLite de ahora es la misma base en otro motor: va entera, tabla por tabla.
                if (almacen is AlmacenSqlite deAhora) return destino.CopiarTablasDe(deAhora);
                var informe = CopiaDeAlmacen.Copiar(almacen, destino, correoAparte, ipsAparte);
                informe.Avisos.AddRange(cortados);
                return informe;
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
        bool eraElSqliteViejo = false, encimaDeLaVacia = false;
        (IReadOnlyList<string> Ips, List<string> Avisos) deLaVacia = ([], []);
        if (File.Exists(archivoDeLaBase))
        {
            if (EsDelSqliteViejo(archivoDeLaBase))
            {
                // El SQLite viejo tiene el mismo nombre de archivo que la base: se lo aparta primero y se importa desde ahí.
                origen = ApartarBase(archivoDeLaBase, ".importado");
                eraElSqliteViejo = true;
            }
            else if (!HayPersonajesEnArchivos(dataRoot)) return null;
            // UNA BASE NUEVA SIN NADIE NO TAPA LOS ARCHIVOS: un arranque sobre una carpeta limpia la deja creada, y los
            // Charfile/ y Cuentas/ copiados después no se importaban nunca, sin aviso. Vacía, se importa encima (la de antes
            // queda al lado); con cuentas, se avisa, como en Postgres.
            else if (NoTieneANadie(archivoDeLaBase))
            {
                origen = dataRoot;
                encimaDeLaVacia = true;
                deLaVacia = LoQueGuardabaLaVacia(archivoDeLaBase);
            }
            else
            {
                Console.WriteLine($"[base] OJO: {dataRoot} tiene personajes en archivos que no se usan: la base {archivoDeLaBase} ya " +
                                  $"tiene cuentas. Para importarlos encima: CoverAO.Server importar {dataRoot} --reemplazar.");
                return null;
            }
        }
        else if (HayPersonajesEnArchivos(dataRoot)) origen = dataRoot;
        else return null;

        Console.WriteLine($"[base] primer arranque con base: importando {origen}");
        InformeDeCopia informe;
        try { informe = Importar(origen, archivoDeLaBase, reemplazar: encimaDeLaVacia, ipsDeMas: deLaVacia.Ips); }
        catch
        {
            if (eraElSqliteViejo) Restituir(origen, archivoDeLaBase);
            throw;
        }
        informe.Avisos.AddRange(deLaVacia.Avisos);
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

    /// <summary>
    /// LO QUE GUARDA UNA BASE SIN NADIE. Sin cuentas ni personajes puede tener las IPs baneadas desde el panel y, de personajes
    /// que ya no están, el suelo, las llaves vendidas, las subastas, los clanes, el correo o el récord. Importar encima arma la
    /// base nueva con los archivos: las IPs se suman a las que traen, y de lo demás se avisa en el informe (sigue en la base de
    /// antes, que queda al lado).
    /// </summary>
    private static (IReadOnlyList<string> Ips, List<string> Avisos) LoQueGuardabaLaVacia(string archivo)
    {
        const string Sigue = "no se importa, sigue en ella (apartada al lado)";
        var avisos = new List<string>();
        try
        {
            using var vieja = new AlmacenSqlite(archivo);
            void Si(int cuantos, string que)
            {
                if (cuantos > 0) avisos.Add($"la base de antes, sin cuentas ni personajes, tenía {que} ({cuantos}): {Sigue}");
            }
            Si(vieja.LeerSuelo().Count, "objetos en el suelo");
            Si(vieja.LeerLlavesVendidas().Count, "llaves vendidas");
            var subastas = vieja.LeerSubastas();
            Si(subastas.Abiertas.Count + subastas.Pendientes.Count, "subastas o entregas de subasta");
            Si(vieja.LeerClanes().Count, "clanes");
            Si(vieja.LeerCorreo().Values.Sum(c => c.Count), "cartas");
            Si(vieja.LeerRecord(), "un récord de conectados");
            return (vieja.LeerIpsBaneadas(), avisos);
        }
        catch (Exception ex)
        {
            avisos.Add($"la base de antes, sin cuentas ni personajes, no se pudo leer ({ex.Message}): lo que tuviera, como las IPs " +
                       $"baneadas, {Sigue}");
            return ([], avisos);
        }
    }

    /// <summary>Las carpetas de los archivos ya importadas, renombradas a <c>*.importado</c>: quedan de respaldo y no se
    /// vuelven a importar.</summary>
    private static void ApartarCarpetas(string dataRoot)
    {
        foreach (var carpeta in new[] { "Charfile", "Cuentas", "Misc" })
        {
            string vieja = Path.Combine(dataRoot, carpeta);
            if (Directory.Exists(vieja)) Directory.Move(vieja, NombreLibre(vieja + ".importado"));
        }
    }

    /// <summary><paramref name="ruta"/>, o —si ya hay algo con ese nombre, de una importación anterior— la misma con la fecha:
    /// lo que se apartó antes no se pisa, y lo de ahora no falla por eso (con la base ya importada y el servidor sin
    /// arrancar).</summary>
    private static string NombreLibre(string ruta)
    {
        string libre = ruta;
        for (int i = 1; File.Exists(libre) || Directory.Exists(libre); i++)
            libre = $"{ruta}-{DateTime.Now:yyyyMMdd-HHmmss}" + (i > 1 ? $"-{i}" : "");
        return libre;
    }

    public static bool HayPersonajesEnArchivos(string dataRoot)
    {
        string personajes = Path.Combine(dataRoot, "Charfile"), cuentas = Path.Combine(dataRoot, "Cuentas");
        return (Directory.Exists(personajes) && Directory.EnumerateFiles(personajes, "*.chr").Any())
            || (Directory.Exists(cuentas) && Directory.EnumerateFiles(cuentas, "*.cnt").Any());
    }

    /// <summary>El almacén de origen, el correo que trae aparte, el aviso de los <c>.chr</c> cortados y lo que hay que cerrar al
    /// terminar.</summary>
    private static (IAlmacen Almacen, IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> CorreoAparte, IReadOnlyList<string> IpsAparte,
        IReadOnlyList<string> Cortados, IDisposable? Liberar) AbrirOrigen(string origen)
    {
        var sinCorreo = new Dictionary<string, IReadOnlyList<MailEntry>>();
        var sinIps = Array.Empty<string>();
        if (File.Exists(origen))
        {
            if (EsDelSqliteViejo(origen))
            {
                var viejo = new SqliteStore(origen);
                return (new AlmacenDeTexto(viejo), sinCorreo, sinIps, [], viejo);
            }
            // Una base de ahora: la de un servidor que pasa a Postgres, o un backup. Se lee de una COPIA al lado: abrirla le
            // aplicaría las versiones del esquema que le falten, y el origen —un backup, sobre todo— no se toca. Con su
            // diario, que puede tener lo último que se escribió.
            string copia = origen + ".leyendo";
            BorrarBase(copia);
            // Con un servidor andando sobre ella, la copia saldría a medio escribir: su candado lo dice.
            using (AlmacenSqlite.Candado(origen))
            {
                File.Copy(origen, copia);
                if (File.Exists(origen + "-wal")) File.Copy(origen + "-wal", copia + "-wal");
            }
            var deAhora = new AlmacenSqlite(copia);
            return (deAhora, sinCorreo, sinIps, [], new AlCerrar(() =>
            {
                deAhora.Dispose();
                BorrarBase(copia);
            }));
        }
        if (!Directory.Exists(origen)) throw new InvalidOperationException($"no existe: {origen}");
        var store = new FileStore(origen, crearCarpetas: false);
        var (correo, cortados) = LoQueTraenLosChr(store);
        return (new AlmacenDeTexto(store), correo, IpsDeLaCarpeta(origen), cortados, null);
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

    /// <summary>De los <c>.chr</c> de una carpeta: el correo del ORIGINAL, que va adentro del de cada destinatario (ver
    /// <see cref="CharacterFile.CorreoDelOriginal"/>; en una carpeta nuestra no hay), y el aviso de los que están CORTADOS
    /// (<see cref="CharacterFile.SeccionesQueFaltan"/>): se importan con lo que les falta vacío o en cero, y la verificación no
    /// lo ve, porque relee lo mismo.</summary>
    private static (Dictionary<string, IReadOnlyList<MailEntry>> Correo, List<string> Cortados) LoQueTraenLosChr(FileStore store)
    {
        var correo = new Dictionary<string, IReadOnlyList<MailEntry>>(StringComparer.OrdinalIgnoreCase);
        var cortados = new List<string>();
        foreach (var nombre in store.AllCharacterNames())
        {
            var texto = store.ReadCharacter(nombre);
            if (texto is null) continue;
            var ini = IniFile.Parse(texto);
            var cartas = CharacterFile.CorreoDelOriginal(ini);
            if (cartas.Count > 0) correo[nombre] = cartas;
            if (CharacterFile.SeccionesQueFaltan(ini) is { Count: > 0 } faltan)
                cortados.Add($"{nombre}: a su .chr le faltan {string.Join(", ", faltan.Select(s => $"[{s}]"))}, está cortado o roto; " +
                             "se importa con eso vacío o en cero");
        }
        return (correo, cortados);
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

    /// <summary>¿Una base de ahora sin cuentas ni personajes (como <see cref="AlmacenRelacional.EstaVacia"/>)? Sin las tablas
    /// —un archivo vacío—, también.</summary>
    private static bool NoTieneANadie(string archivo)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivo, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('cuentas', 'personajes')";
        if ((long)c.ExecuteScalar()! < 2) return true;
        c.CommandText = "SELECT EXISTS (SELECT 1 FROM cuentas) OR EXISTS (SELECT 1 FROM personajes)";
        return (long)c.ExecuteScalar()! == 0;
    }

    /// <summary>La base y sus archivos de al lado (el diario, la memoria compartida), renombrados con el mismo sufijo.
    /// Devuelve la ruta nueva de la base.</summary>
    private static string ApartarBase(string archivo, string sufijo)
    {
        SqliteConnection.ClearAllPools();
        string nuevo = NombreLibre(archivo + sufijo);
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
