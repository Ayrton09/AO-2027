using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

// LA BATERÍA DE CONTRATO (ContratoDelAlmacenTests.cs) contra cada almacén de este proyecto. La de Postgres corre en
// CoverAO.Postgres.Tests, que comparte el archivo de la batería.

public sealed class ContratoDelAlmacenDeArchivos : ContratoDelAlmacen
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_contrato_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenDeTexto _almacen;

    public ContratoDelAlmacenDeArchivos() => _almacen = new AlmacenDeTexto(new FileStore(_raiz));
    protected override IAlmacen Almacen => _almacen;
    public override void Dispose() { try { Directory.Delete(_raiz, recursive: true); } catch { } }
}

public sealed class ContratoDelAlmacenSqlite : ContratoDelAlmacen
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_contrato_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenSqlite _almacen;

    public ContratoDelAlmacenSqlite() => _almacen = new AlmacenSqlite(Path.Combine(_raiz, "coverao.db"));
    protected override IAlmacen Almacen => _almacen;

    public override void Dispose()
    {
        _almacen.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }
}

/// <summary>La misma batería contra la base en memoria, que es la que van a usar los tests que sólo necesitan dónde dejar
/// personajes.</summary>
public sealed class ContratoDelAlmacenSqliteEnMemoria : ContratoDelAlmacen
{
    private readonly AlmacenSqlite _almacen = AlmacenSqlite.EnMemoria();
    protected override IAlmacen Almacen => _almacen;
    public override void Dispose() => _almacen.Dispose();
}
