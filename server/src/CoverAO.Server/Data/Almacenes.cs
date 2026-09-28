namespace CoverAO.Server.Data;

/// <summary>
/// DÓNDE GUARDA EL SERVIDOR (fase5/postgres/DISENO.md, sección 5). Lo elige el que hostea con <c>[BASEDEDATOS] Tipo</c>:
/// <c>sqlite</c> —una base en un archivo, <c>data/coverao.db</c>— o <c>archivos</c>, un <c>.chr</c> por personaje y un
/// <c>.cnt</c> por cuenta, como el original.
///
/// <para><b>Sin <c>Tipo</c>, el servidor elige</b>, y lo que no puede hacer es dejar de ver un mundo que ya existe. Una
/// instalación nueva arranca en SQLite. Una que ya tiene personajes en archivos —y ninguna base— sigue con los archivos,
/// y lo dice: pasarlos a la base es un paso aparte, que el host decide.</para>
/// </summary>
public static class Almacenes
{
    public static (IAlmacen Almacen, string Descripcion) Abrir(ServerIni ini, string dataRoot)
    {
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        switch (ini.TipoDeBase)
        {
            case "sqlite":
                return (new AlmacenSqlite(archivo), $"SQLite ({ini.ArchivoDeBase})");
            case "archivos":
                return (new AlmacenDeTexto(new FileStore(dataRoot)), "archivos .chr/.cnt");
            case "":
                break;
            default:
                throw new InvalidOperationException(
                    $"[BASEDEDATOS] Tipo={ini.TipoDeBase}: no se conoce. Los que hay son sqlite y archivos.");
        }
        // El SQLite viejo —el texto del .chr en una columna—, que se prendía con esta variable. Sigue andando igual hasta
        // que se lo pase a la base nueva.
        if (string.Equals(Environment.GetEnvironmentVariable("COVERAO_DB"), "sqlite", StringComparison.OrdinalIgnoreCase))
            return (new AlmacenDeTexto(new SqliteStore(archivo)), $"el SQLite viejo ({ini.ArchivoDeBase}, COVERAO_DB=sqlite)");
        if (!File.Exists(archivo) && HayPersonajesEnArchivos(dataRoot))
            return (new AlmacenDeTexto(new FileStore(dataRoot)),
                "archivos .chr/.cnt: ya había personajes guardados así. Para usar la base, [BASEDEDATOS] Tipo=sqlite " +
                "después de pasarlos");
        return (new AlmacenSqlite(archivo), $"SQLite ({ini.ArchivoDeBase})");
    }

    private static bool HayPersonajesEnArchivos(string dataRoot)
    {
        string personajes = Path.Combine(dataRoot, "Charfile"), cuentas = Path.Combine(dataRoot, "Cuentas");
        return (Directory.Exists(personajes) && Directory.EnumerateFiles(personajes, "*.chr").Any())
            || (Directory.Exists(cuentas) && Directory.EnumerateFiles(cuentas, "*.cnt").Any());
    }
}
