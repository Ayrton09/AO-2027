using CoverAO.Server.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CoverAO.Postgres.Tests;

/// <summary>
/// LOS DOS ESQUEMAS SON EL MISMO (fase5/postgres/DISENO.md, sección 2): las mismas versiones, las mismas tablas, las mismas
/// columnas —y las que no aceptan vacío, las mismas— y los mismos índices. Cambian los tipos, que son los de cada motor. Un
/// cambio de esquema hecho en un motor y olvidado en el otro se ve acá, y no el día que un host se pasa a Postgres.
/// </summary>
public sealed class LosEsquemasSonIgualesTests : IDisposable
{
    private readonly BaseDePrueba _base = new();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_esquemas_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _base.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    [Fact]
    public void Los_dos_motores_traen_las_mismas_versiones()
    {
        var sqlite = AlmacenRelacional.Versiones("sqlite").Select(v => (v.Version, v.Nombre));
        var postgres = AlmacenRelacional.Versiones("postgres").Select(v => (v.Version, v.Nombre));
        Assert.Equal(sqlite, postgres);
    }

    [Fact]
    public void Las_dos_bases_tienen_las_mismas_tablas_columnas_e_indices()
    {
        var (columnasSqlite, indicesSqlite) = EsquemaDeSqlite();
        var (columnasPostgres, indicesPostgres) = EsquemaDePostgres();

        Assert.NotEmpty(columnasSqlite);
        Assert.Equal(columnasSqlite.Keys.Order(), columnasPostgres.Keys.Order());
        foreach (var (tabla, columnas) in columnasSqlite)
            Assert.True(columnas.SetEquals(columnasPostgres[tabla]),
                $"{tabla}: en SQLite [{string.Join(", ", columnas.Order())}], en Postgres [{string.Join(", ", columnasPostgres[tabla].Order())}]");
        Assert.Equal(indicesSqlite.Order(), indicesPostgres.Order());
    }

    /// <summary>Cada tabla con sus columnas, cada una marcada con "!" si no acepta vacío; y los índices con nombre.</summary>
    private (Dictionary<string, HashSet<string>>, List<string>) EsquemaDeSqlite()
    {
        string archivo = Path.Combine(_raiz, "coverao.db");
        using (new AlmacenSqlite(archivo)) { }
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = archivo, Pooling = false }.ToString());
        con.Open();
        var columnas = new Dictionary<string, HashSet<string>>();
        using (var c = con.CreateCommand())
        {
            // Una clave primaria no acepta vacío aunque SQLite no lo marque (la de una tabla con rowid es el rowid mismo).
            c.CommandText = "SELECT m.name, p.name, p.\"notnull\" OR p.pk > 0 FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                            "WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'";
            using var r = c.ExecuteReader();
            while (r.Read()) Anotar(columnas, r.GetString(0), r.GetString(1), r.GetInt64(2) != 0);
        }
        var indices = new List<string>();
        using (var c = con.CreateCommand())
        {
            c.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_autoindex_%'";
            using var r = c.ExecuteReader();
            while (r.Read()) indices.Add(r.GetString(0));
        }
        return (columnas, indices);
    }

    private (Dictionary<string, HashSet<string>>, List<string>) EsquemaDePostgres()
    {
        using (new AlmacenPostgres(_base.Cadena)) { }
        using var con = new NpgsqlConnection(_base.Cadena);
        con.Open();
        var columnas = new Dictionary<string, HashSet<string>>();
        using (var c = new NpgsqlCommand("SELECT table_name, column_name, is_nullable = 'NO' FROM information_schema.columns " +
                                         "WHERE table_schema = current_schema()", con))
        using (var r = c.ExecuteReader())
            while (r.Read()) Anotar(columnas, r.GetString(0), r.GetString(1), r.GetBoolean(2));
        var indices = new List<string>();
        // Los de las claves primarias y las restricciones únicas tienen nombre propio en Postgres, y en SQLite son
        // automáticos: se comparan los que el esquema nombra.
        using (var c = new NpgsqlCommand("SELECT indexname FROM pg_indexes WHERE schemaname = current_schema() " +
                                         "AND indexname NOT LIKE '%\\_pkey' AND indexname NOT LIKE '%\\_key'", con))
        using (var r = c.ExecuteReader())
            while (r.Read()) indices.Add(r.GetString(0));
        return (columnas, indices);
    }

    private static void Anotar(Dictionary<string, HashSet<string>> columnas, string tabla, string columna, bool noAceptaVacio)
    {
        if (!columnas.TryGetValue(tabla, out var deLaTabla)) columnas[tabla] = deLaTabla = new HashSet<string>();
        deLaTabla.Add(noAceptaVacio ? columna + "!" : columna);
    }
}
