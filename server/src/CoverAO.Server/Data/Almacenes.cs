namespace CoverAO.Server.Data;

/// <summary>
/// DÓNDE GUARDA EL SERVIDOR (fase5/postgres/DISENO.md, sección 5). Lo elige el que hostea con <c>[BASEDEDATOS] Tipo</c>:
/// <c>sqlite</c> —una base en un archivo, <c>data/coverao.db</c>— o <c>postgres</c> —un servidor con páginas web, que leen
/// la misma base—. Los <c>.chr</c> y <c>.cnt</c> del original ya no son un lugar donde guardar: se importan (y se exportan,
/// para mirarlos o para las herramientas que los leen).
///
/// <para><b>Sin <c>Tipo</c>, SQLite.</b> Y lo que no puede pasar es dejar de ver un mundo que ya existe: una base vacía, con
/// personajes en archivos o en otra base al lado, los IMPORTA en el primer arranque (<see cref="Importador"/>), y lo viejo
/// queda renombrado. Así se pasa de archivos a SQLite, y de SQLite a Postgres, con sólo cambiar el <c>Tipo</c>.</para>
/// </summary>
public static class Almacenes
{
    public static (IAlmacen Almacen, string Descripcion) Abrir(ServerIni ini, string dataRoot)
    {
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        switch (ini.TipoDeBase)
        {
            case "" or "sqlite":
            {
                var importado = Importador.ImportarSiHaceFalta(dataRoot, archivo);
                return (new AlmacenSqlite(archivo), $"SQLite ({ini.ArchivoDeBase})" + Importado(importado));
            }
            case "postgres":
            {
                var almacen = AbrirPostgres(ini);
                try
                {
                    var importado = Importador.ImportarAPostgresSiHaceFalta(dataRoot, archivo, almacen);
                    return (almacen, $"Postgres ({AlmacenPostgres.Donde(ini)})" + Importado(importado));
                }
                catch
                {
                    almacen.Dispose();
                    throw;
                }
            }
            case "archivos":
                throw new InvalidOperationException(
                    "[BASEDEDATOS] Tipo=archivos ya no existe: los personajes se guardan en una base. Sacá la línea (o poné " +
                    "sqlite) y el primer arranque importa solo los .chr y .cnt; para verlos como archivos, CoverAO.Server exportar.");
            default:
                throw new InvalidOperationException(
                    $"[BASEDEDATOS] Tipo={ini.TipoDeBase}: no se conoce. Los que hay son sqlite y postgres.");
        }
    }

    /// <summary>La base de Postgres de <c>[BASEDEDATOS]</c>, con su esquema al día. Si no se puede, el servidor no arranca y
    /// dice por qué, sin mostrar la clave.</summary>
    public static AlmacenPostgres AbrirPostgres(ServerIni ini)
    {
        try { return new AlmacenPostgres(AlmacenPostgres.Cadena(ini)); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"No se pudo abrir la base de Postgres ({AlmacenPostgres.Donde(ini)}, usuario {ini.UsuarioDeBase}): {ex.Message}. " +
                "Fijate que Postgres esté andando, que la base exista y los datos de [BASEDEDATOS] en el Server.ini.", ex);
        }
    }

    private static string Importado(InformeDeCopia? informe)
        => informe is null ? "" : $", recién importada: {informe.Personajes} personajes de {informe.Cuentas} cuentas";
}
