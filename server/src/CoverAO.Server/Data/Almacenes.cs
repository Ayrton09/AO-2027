namespace CoverAO.Server.Data;

/// <summary>
/// DÓNDE GUARDA EL SERVIDOR (fase5/postgres/DISENO.md, sección 5). Lo elige el que hostea con <c>[BASEDEDATOS] Tipo</c>:
/// <c>sqlite</c> —una base en un archivo, <c>data/coverao.db</c>— o <c>archivos</c>, un <c>.chr</c> por personaje y un
/// <c>.cnt</c> por cuenta, como el original.
///
/// <para><b>Sin <c>Tipo</c>, la base.</b> Y lo que no puede pasar es dejar de ver un mundo que ya existe: una instalación que
/// tiene personajes en archivos —o una <c>coverao.db</c> del SQLite viejo— y ninguna base los IMPORTA en el primer arranque
/// (<see cref="Importador.ImportarSiHaceFalta"/>), y lo viejo queda al lado, renombrado.</para>
/// </summary>
public static class Almacenes
{
    public static (IAlmacen Almacen, string Descripcion) Abrir(ServerIni ini, string dataRoot)
    {
        string archivo = Path.Combine(dataRoot, ini.ArchivoDeBase);
        switch (ini.TipoDeBase)
        {
            case "sqlite":
                break;
            case "archivos":
                return (new AlmacenDeTexto(new FileStore(dataRoot)), "archivos .chr/.cnt");
            case "":
                break;
            default:
                throw new InvalidOperationException(
                    $"[BASEDEDATOS] Tipo={ini.TipoDeBase}: no se conoce. Los que hay son sqlite y archivos.");
        }
        var importado = Importador.ImportarSiHaceFalta(dataRoot, archivo);
        return (new AlmacenSqlite(archivo), $"SQLite ({ini.ArchivoDeBase})" +
                (importado is null ? "" : $", recién importada: {importado.Personajes} personajes de {importado.Cuentas} cuentas"));
    }
}
