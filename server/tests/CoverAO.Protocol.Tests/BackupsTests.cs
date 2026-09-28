using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Copias de seguridad del almacén. Lo que se testea acá es la parte que puede DESTRUIR datos —la poda— y la
/// que los tiene que preservar —el snapshot con el servidor andando—. Un backup que no se puede restaurar, o
/// una poda que se lleva puesto el último backup bueno, son peores que no tener backups: dan falsa confianza.
/// </summary>
public class BackupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coverao-bk-" + Guid.NewGuid().ToString("N")[..8]);
    public BackupsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void El_nombre_ordena_cronologicamente_como_texto()
    {
        // De esto depende la poda: se ordena por NOMBRE, no por la fecha del filesystem (que una copia o un
        // rsync pueden reescribir).
        var viejo = Backups.Stem(new DateTime(2026, 7, 26, 23, 59, 59));
        var nuevo = Backups.Stem(new DateTime(2026, 7, 27, 0, 0, 0));
        Assert.Equal("coverao-20260726-235959", viejo);
        Assert.True(string.CompareOrdinal(viejo, nuevo) < 0);
    }

    [Fact]
    public void La_poda_deja_los_mas_nuevos()
    {
        foreach (var d in new[] { 1, 2, 3, 4, 5 })
            File.WriteAllText(Path.Combine(_dir, Backups.Stem(new DateTime(2026, 7, d)) + ".db"), "x");

        Assert.Equal(2, Backups.Podar(_dir, conservar: 3));
        var quedan = Backups.Listar(_dir).Select(Path.GetFileNameWithoutExtension).ToArray();
        Assert.Equal(new[] { "coverao-20260705-000000", "coverao-20260704-000000", "coverao-20260703-000000" }, quedan);
    }

    [Fact]
    public void Conservar_cero_no_borra_nada()
    {
        // Retención infinita es una decisión válida del host. Y sobre todo: un 0 mal puesto en el ini NO puede
        // significar "borrá todos los backups" — sería la peor interpretación posible de un valor ambiguo.
        File.WriteAllText(Path.Combine(_dir, Backups.Stem(new DateTime(2026, 7, 1)) + ".db"), "x");
        Assert.Equal(0, Backups.Podar(_dir, conservar: 0));
        Assert.Single(Backups.Listar(_dir));
    }

    [Fact]
    public void La_poda_no_toca_lo_que_no_es_un_backup()
    {
        File.WriteAllText(Path.Combine(_dir, "coverao.db"), "la base VIVA, si alguien apuntó el dir mal");
        File.WriteAllText(Path.Combine(_dir, "notas.txt"), "x");
        for (int d = 1; d <= 3; d++)
            File.WriteAllText(Path.Combine(_dir, Backups.Stem(new DateTime(2026, 7, d)) + ".db"), "x");

        Backups.Podar(_dir, conservar: 1);
        Assert.True(File.Exists(Path.Combine(_dir, "coverao.db")));
        Assert.True(File.Exists(Path.Combine(_dir, "notas.txt")));
    }

    [Fact]
    public void El_backup_de_SQLite_se_puede_volver_a_abrir_y_tiene_los_datos()
    {
        // La prueba que importa: no que el archivo exista, sino que se pueda RESTAURAR.
        string dbPath = Path.Combine(_dir, "vivo.db");
        string ruta;
        using (var store = new SqliteStore(dbPath))
        {
            store.WriteCharacter("Rugura", "[FLAGS]\r\nPrivilegios=1\r\n");
            store.WriteAccount("cuenta_test", "[INIT]\r\nName=cuenta_test\r\n");
            store.WriteMisc("GUILDS", "{}");
            ruta = Backups.Crear(new AlmacenDeTexto(store), Path.Combine(_dir, "backups"), new DateTime(2026, 7, 27, 12, 0, 0));
            // Sigue viva y escribible DESPUÉS del snapshot: VACUUM INTO no cierra ni bloquea la conexión.
            store.WriteCharacter("Merlin", "[FLAGS]\r\nPrivilegios=1\r\n");
        }

        Assert.True(File.Exists(ruta));
        using var copia = new SqliteStore(ruta);
        Assert.Equal("[FLAGS]\r\nPrivilegios=1\r\n", copia.ReadCharacter("Rugura"));
        Assert.Equal("{}", copia.ReadMisc("GUILDS"));
        Assert.True(copia.AccountExists("cuenta_test"));
        Assert.False(copia.CharacterExists("Merlin"));   // el snapshot es de ANTES: es una foto, no un espejo
    }

    [Fact]
    public void El_backup_de_archivos_copia_los_tres_directorios()
    {
        string raiz = Path.Combine(_dir, "data");
        var store = new FileStore(raiz);
        store.WriteCharacter("Rugura", "[FLAGS]\r\n");
        store.WriteAccount("cuenta_test", "[INIT]\r\n");
        store.WriteMisc("GUILDS", "{}");

        string ruta = Backups.Crear(new AlmacenDeTexto(store), Path.Combine(_dir, "backups"), new DateTime(2026, 7, 27, 12, 0, 0));
        Assert.True(File.Exists(Path.Combine(ruta, "Charfile", "RUGURA.chr")));
        Assert.True(File.Exists(Path.Combine(ruta, "Cuentas", "CUENTA_TEST.cnt")));
        Assert.True(File.Exists(Path.Combine(ruta, "Misc", "GUILDS.json")));
    }
}
