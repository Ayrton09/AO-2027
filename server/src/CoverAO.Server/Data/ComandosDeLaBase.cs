namespace CoverAO.Server.Data;

/// <summary>
/// LA BASE DESDE LA LÍNEA DE COMANDOS (fase5/postgres/DISENO.md, sección 6), contra la base que diga el <c>Server.ini</c>:
///
/// <code>
/// CoverAO.Server importar &lt;carpeta de datos | archivo de SQLite&gt; [--reemplazar]
/// CoverAO.Server exportar &lt;carpeta nueva o vacía&gt;
/// CoverAO.Server pasar-a-postgres [archivo de SQLite] [--reemplazar]
/// </code>
///
/// <c>pasar-a-postgres</c> copia la base de SQLite —la de <c>[BASEDEDATOS] Archivo</c>, si no se dice otra— a la de Postgres
/// de <c>[BASEDEDATOS]</c>, con el <c>Tipo</c> que sea: se pasa, se mira el informe, y recién después se cambia el
/// <c>Tipo</c>. Hacen lo suyo, dicen cómo les fue y salen: el servidor no arranca. Van con el servidor APAGADO: la base es de
/// un solo proceso a la vez.
/// </summary>
public static class ComandosDeLaBase
{
    private static readonly string[] Comandos = { "importar", "exportar", "pasar-a-postgres" };

    public static bool EsUno(string comando) => Comandos.Contains(comando, StringComparer.OrdinalIgnoreCase);

    public static int Correr(string[] args, string dataRoot, ServerIni ini)
    {
        string comando = args[0].ToLowerInvariant();
        bool reemplazar = args.Any(a => a.Equals("--reemplazar", StringComparison.OrdinalIgnoreCase));
        var resto = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        string backups = Path.Combine(dataRoot, "backups");
        bool postgres = ini.TipoDeBase == "postgres";
        if (ini.TipoDeBase == "archivos" && comando != "pasar-a-postgres")
        {
            Console.WriteLine("El Server.ini dice [BASEDEDATOS] Tipo=archivos: no hay base de donde exportar ni adonde importar.");
            return 1;
        }
        try
        {
            InformeDeCopia informe;
            switch (comando)
            {
                case "importar":
                {
                    if (resto.Length < 1)
                    {
                        Console.WriteLine("Uso: CoverAO.Server importar <carpeta de datos o archivo de SQLite> [--reemplazar]");
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
}
