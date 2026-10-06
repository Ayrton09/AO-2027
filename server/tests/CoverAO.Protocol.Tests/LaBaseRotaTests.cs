using CoverAO.Server.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA BASE ROTA AL ARRANCAR (el tester, 06/10: "database disk image is malformed" y el servidor se caía con la pila de SQLite). Se
/// reconoce el error y el aviso dice cómo volver al backup más nuevo.
/// </summary>
public class LaBaseRotaTests
{
    [Fact]
    public void Una_base_rota_se_reconoce_y_el_aviso_da_el_restaurar_del_backup_mas_nuevo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "base-rota-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(dir, "data");
        Directory.CreateDirectory(Path.Combine(data, "backups"));
        try
        {
            string db = Path.Combine(data, "coverao.db");
            File.WriteAllBytes(db, new byte[8192]);   // no es una base: SQLITE_NOTADB
            File.WriteAllText(Path.Combine(data, "backups", "coverao-20261005-120000.db"), "");
            File.WriteAllText(Path.Combine(data, "backups", "coverao-20261006-090000.db"), "");

            Exception? atrapada = null;
            try
            {
                using var con = new SqliteConnection($"Data Source={db};Pooling=False");
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master";
                cmd.ExecuteScalar();
            }
            catch (Exception ex) { atrapada = ex; }

            Assert.True(BaseDaniada.Es(atrapada));
            Assert.False(BaseDaniada.Es(new InvalidOperationException("otra cosa")));
            string aviso = BaseDaniada.Aviso(data, atrapada!);
            Assert.Contains("restaurar", aviso);
            Assert.Contains("coverao-20261006-090000.db --reemplazar", aviso);
            Assert.Contains("coverao-20261005-120000.db", aviso);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
