namespace CoverAO.Server.Data;

/// <summary>
/// LA BASE DESDE LA LÍNEA DE COMANDOS (fase5/postgres/DISENO.md, sección 6), contra la base que diga el <c>Server.ini</c>:
///
/// <code>
/// CoverAO.Server importar &lt;carpeta de datos | archivo de SQLite&gt; [--reemplazar]
/// CoverAO.Server exportar &lt;carpeta nueva o vacía&gt;
/// CoverAO.Server pasar-a-postgres [archivo de SQLite] [--reemplazar]
/// CoverAO.Server restaurar &lt;backup&gt; [--reemplazar]
/// CoverAO.Server crear-usuario-web [nombre]
/// </code>
///
/// <c>crear-usuario-web</c> crea el usuario de Postgres de la página web, que sólo puede leer las vistas de <c>publico</c>. Lo
/// hace un administrador de Postgres: su usuario y su clave van en <c>COVERAO_PG_ADMIN_USUARIO</c> (si no, <c>postgres</c>) y
/// <c>COVERAO_PG_ADMIN_CLAVE</c>, no en la línea de comandos.
///
/// <c>restaurar</c> vuelve a un backup de <c>data/backups/</c>: el <c>.zip</c> de Postgres se restaura en Postgres; el
/// <c>.db</c> de SQLite es una base, y se importa (a SQLite o a Postgres).
///
/// <c>pasar-a-postgres</c> copia la base de SQLite —la de <c>[BASEDEDATOS] Archivo</c>, si no se dice otra— a la de Postgres
/// de <c>[BASEDEDATOS]</c>, con el <c>Tipo</c> que sea: se pasa, se mira el informe, y recién después se cambia el
/// <c>Tipo</c>. Hacen lo suyo, dicen cómo les fue y salen: el servidor no arranca. Van con el servidor APAGADO: la base es de
/// un solo proceso a la vez.
/// </summary>
public static class ComandosDeLaBase
{
    private static readonly string[] Comandos = { "importar", "exportar", "pasar-a-postgres", "restaurar", "crear-usuario-web" };

    public static bool EsUno(string comando) => Comandos.Contains(comando, StringComparer.OrdinalIgnoreCase);

    public static int Correr(string[] args, string dataRoot, ServerIni ini)
    {
        string comando = args[0].ToLowerInvariant();
        bool reemplazar = args.Any(a => a.Equals("--reemplazar", StringComparison.OrdinalIgnoreCase));
        var resto = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        string backups = Path.Combine(dataRoot, "backups");
        bool postgres = ini.TipoDeBase == "postgres";
        try
        {
            if (comando == "crear-usuario-web")
            {
                if (!postgres)
                {
                    Console.WriteLine("El usuario de la web es de Postgres: [BASEDEDATOS] Tipo=postgres en el Server.ini.");
                    return 1;
                }
                string usuario = resto.Length > 0 ? resto[0] : "coverao_web";
                var (clave, existia) = AlmacenPostgres.CrearUsuarioWeb(ini, usuario,
                    Environment.GetEnvironmentVariable("COVERAO_PG_ADMIN_USUARIO") is { Length: > 0 } admin ? admin : "postgres",
                    Environment.GetEnvironmentVariable("COVERAO_PG_ADMIN_CLAVE") ?? "");
                Console.WriteLine($"Usuario de la web: {usuario}, con clave {clave}");
                Console.WriteLine("Guardala: no se vuelve a mostrar (correr esto de nuevo le pone otra). " +
                                  (existia ? "El usuario ya existía: además del esquema publico, conserva los permisos que ya tuviera."
                                           : "Sólo puede leer el esquema publico."));
                return 0;
            }
            if (comando == "restaurar" && resto.Length > 0 && resto[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (!postgres)
                {
                    Console.WriteLine("Un backup de Postgres (.zip) se restaura en Postgres: [BASEDEDATOS] Tipo=postgres en el Server.ini.");
                    return 1;
                }
                using var pg = Almacenes.AbrirPostgres(ini);
                var filas = Importador.RestaurarEnPostgres(resto[0], pg, reemplazar, backups);
                foreach (var (tabla, cuantas) in filas.Where(f => f.Filas > 0)) Console.WriteLine($"  {tabla}: {cuantas}");
                Console.WriteLine($"Restaurado {resto[0]} en la base de Postgres ({AlmacenPostgres.Donde(ini)}).");
                AvisarRescatesApartados(dataRoot);
                return 0;
            }
            InformeDeCopia informe;
            switch (comando)
            {
                case "importar" or "restaurar":   // un backup de SQLite es una base: se importa
                {
                    if (resto.Length < 1)
                    {
                        Console.WriteLine(comando == "importar"
                            ? "Uso: CoverAO.Server importar <carpeta de datos o archivo de SQLite> [--reemplazar]"
                            : "Uso: CoverAO.Server restaurar <backup .zip o .db de data/backups> [--reemplazar]");
                        return 1;
                    }
                    string destino;
                    if (postgres)
                    {
                        using var pg = Almacenes.AbrirPostgres(ini);
                        informe = Importador.ImportarAPostgres(resto[0], pg, reemplazar, backups);
                        destino = $"la base de Postgres ({AlmacenPostgres.Donde(ini)})";
                    }
                    else
                    {
                        informe = Importador.Importar(resto[0], archivo, reemplazar);
                        destino = archivo;
                    }
                    Console.WriteLine(informe.Texto());
                    Console.WriteLine(informe.SalioBien ? $"Importado a {destino}." : "No se importó nada: la verificación no dio bien.");
                    if (informe.SalioBien) AvisarRescatesApartados(dataRoot);
                    break;
                }
                case "exportar":
                {
                    if (resto.Length < 1)
                    {
                        Console.WriteLine("Uso: CoverAO.Server exportar <carpeta nueva o vacía>");
                        return 1;
                    }
                    if (!postgres && !File.Exists(archivo))
                    {
                        Console.WriteLine($"No hay base en {archivo}.");
                        return 1;
                    }
                    using AlmacenRelacional origen = postgres ? Almacenes.AbrirPostgres(ini) : new AlmacenSqlite(archivo);
                    informe = Importador.Exportar(origen, resto[0]);
                    Console.WriteLine(informe.Texto());
                    Console.WriteLine(informe.SalioBien ? $"Exportado a {resto[0]}." : "La exportación no dio bien: ver arriba.");
                    break;
                }
                default:   // pasar-a-postgres
                {
                    string sqlite = resto.Length > 0 ? resto[0] : archivo;
                    if (!File.Exists(sqlite))
                    {
                        Console.WriteLine($"No hay base de SQLite en {sqlite}.");
                        return 1;
                    }
                    using var pg = Almacenes.AbrirPostgres(ini);
                    informe = Importador.ImportarAPostgres(sqlite, pg, reemplazar, backups);
                    Console.WriteLine(informe.Texto());
                    Console.WriteLine(!informe.SalioBien
                        ? "No se pasó nada: la verificación no dio bien."
                        : $"Pasado a la base de Postgres ({AlmacenPostgres.Donde(ini)}). {sqlite} quedó como estaba." +
                          (postgres ? "" : " Para usarla, [BASEDEDATOS] Tipo=postgres en el Server.ini."));
                    break;
                }
            }
            return informe.SalioBien ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("No se pudo: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Lo rescatado de un apagado con la base caída es de la base de ANTES: después de restaurar o importar no se aplica
    /// (ver <see cref="Rescates.ApartarLosPendientes"/>). Pasar a Postgres no lo aparta: es la misma base, en otro motor.</summary>
    private static void AvisarRescatesApartados(string dataRoot)
    {
        var apartados = Rescates.ApartarLosPendientes(dataRoot);
        if (apartados.Count > 0)
            Console.WriteLine($"Había {apartados.Count} rescate(s) de un apagado con la base caída, escritos sobre la base de antes: " +
                              $"quedaron en data/ sin aplicar ({string.Join(", ", apartados)}).");
    }
}
