using CoverAO.Protocol.Tests;
using CoverAO.Server.Data;
using Npgsql;

namespace CoverAO.Postgres.Tests;

/// <summary>
/// EL POSTGRES DE LAS PRUEBAS: el que prende la puerta (<c>verificar.ps1</c>), con la cadena de su base de administración en
/// <see cref="Variable"/>. Cada prueba se hace una base nueva y la tira al terminar. Sin Postgres las pruebas FALLAN, no se
/// saltean: un salteo no mide nada.
/// </summary>
internal static class PostgresDePrueba
{
    public const string Variable = "COVERAO_PRUEBAS_POSTGRES";

    public static string Admin => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } cadena
        ? cadena
        : throw new InvalidOperationException(
            $"No hay Postgres para las pruebas: falta {Variable}. Las corre la puerta (verificar.ps1), que prende uno; a mano, " +
            "con fase5/herramientas/postgres/postgres_de_prueba.ps1.");

    /// <summary>Una base nueva y vacía: su nombre y su cadena de conexión.</summary>
    public static (string Nombre, string Cadena) CrearBase()
    {
        string nombre = "coverao_prueba_" + Guid.NewGuid().ToString("N")[..12];
        Admin_(nombre, $"CREATE DATABASE {nombre}");
        return (nombre, new NpgsqlConnectionStringBuilder(Admin) { Database = nombre }.ToString());
    }

    public static void TirarBase(string nombre) => Admin_(nombre, $"DROP DATABASE IF EXISTS {nombre} WITH (FORCE)");

    /// <summary>Corta las conexiones de otros a <paramref name="nombre"/>, como un reinicio de Postgres.</summary>
    public static void CortarConexiones(string nombre)
        => Admin_(nombre, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{nombre}' AND pid <> pg_backend_pid()");

    private static void Admin_(string nombre, string sql)
    {
        if (!nombre.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ArgumentException(nombre);
        using var con = new NpgsqlConnection(Admin);
        con.Open();
        using var c = new NpgsqlCommand(sql, con);
        c.ExecuteNonQuery();
    }
}

/// <summary>Una base de prueba que se tira sola.</summary>
internal sealed class BaseDePrueba : IDisposable
{
    public string Nombre { get; }
    public string Cadena { get; }

    public BaseDePrueba() => (Nombre, Cadena) = PostgresDePrueba.CrearBase();

    public void Dispose() => PostgresDePrueba.TirarBase(Nombre);
}

/// <summary>La batería de contrato contra Postgres: la misma lista de pruebas que corre contra los archivos y SQLite.</summary>
public sealed class ContratoDelAlmacenPostgres : ContratoDelAlmacen
{
    private readonly BaseDePrueba _base = new();
    private readonly AlmacenPostgres _almacen;

    public ContratoDelAlmacenPostgres() => _almacen = new AlmacenPostgres(_base.Cadena);
    protected override IAlmacen Almacen => _almacen;

    public override void Dispose()
    {
        _almacen.Dispose();
        _base.Dispose();
    }
}
