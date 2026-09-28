namespace CoverAO.Server.Data;

/// <summary>
/// LA BASE DESDE LA LÍNEA DE COMANDOS (fase5/postgres/DISENO.md, sección 6), contra la base que diga el <c>Server.ini</c>:
///
/// <code>
/// CoverAO.Server importar &lt;carpeta de datos | coverao.db vieja&gt; [--reemplazar]
/// CoverAO.Server exportar &lt;carpeta nueva o vacía&gt;
/// </code>
///
/// Hacen lo suyo, dicen cómo les fue y salen: el servidor no arranca. Van con el servidor APAGADO: la base es de un solo
/// proceso a la vez.
/// </summary>
public static class ComandosDeLaBase
{
    public static bool EsUno(string comando)
        => comando.Equals("importar", StringComparison.OrdinalIgnoreCase) || comando.Equals("exportar", StringComparison.OrdinalIgnoreCase);

    public static int Correr(string[] args, string dataRoot, ServerIni ini)
    {
        if (ini.TipoDeBase == "archivos")
        {
            Console.WriteLine("El Server.ini dice [BASEDEDATOS] Tipo=archivos: no hay base de donde exportar ni adonde importar.");
            return 1;
        }
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        try
        {
            InformeDeCopia informe;
            if (args[0].Equals("importar", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Uso: CoverAO.Server importar <carpeta de datos o coverao.db vieja> [--reemplazar]");
                    return 1;
                }
                bool reemplazar = args.Any(a => a.Equals("--reemplazar", StringComparison.OrdinalIgnoreCase));
                informe = Importador.Importar(args[1], archivo, reemplazar);
                Console.WriteLine(informe.Texto());
                Console.WriteLine(informe.SalioBien ? $"Importado a {archivo}." : "No se importó nada: la verificación no dio bien.");
            }
            else
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Uso: CoverAO.Server exportar <carpeta nueva o vacía>");
                    return 1;
                }
                if (!File.Exists(archivo))
                {
                    Console.WriteLine($"No hay base en {archivo}.");
                    return 1;
                }
                using var almacen = new AlmacenSqlite(archivo);
                informe = Importador.Exportar(almacen, args[1]);
                Console.WriteLine(informe.Texto());
                Console.WriteLine(informe.SalioBien ? $"Exportado a {args[1]}." : "La exportación no dio bien: ver arriba.");
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
