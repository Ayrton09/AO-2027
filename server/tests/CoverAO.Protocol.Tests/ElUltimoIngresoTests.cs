using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL ÚLTIMO INGRESO Y LA ÚLTIMA IP DE CADA CUENTA (decisión D4 de fase5/postgres/DISENO.md), como el original al entrar a
/// la cuenta: <c>UltimoAcceso</c> y <c>UltimaIP</c> en el <c>.cnt</c> (VB6 <c>EntrarCuenta</c>, Cuentas.bas:443-444).
/// </summary>
public class ElUltimoIngresoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_ingreso_d4_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_raiz, recursive: true); } catch { } }

    private static Leveling Leveling() => new(new Dictionary<int, long> { [1] = 300 });

    [Fact]
    public void Entrar_anota_cuando_y_desde_donde_en_los_dos_almacenes()
    {
        foreach (var almacen in new IAlmacen[] { new AlmacenDeTexto(new FileStore(_raiz)), AlmacenSqlite.EnMemoria() })
        {
            using (var db = new Persistence(almacen, Leveling()))
            {
                db.CreateAccount("Anotada", "clave_larga");
                var antes = DateTime.UtcNow.AddSeconds(-1);

                db.AnotarIngreso("anotada", "10.9.8.7");

                var cuenta = db.LoadAccount("Anotada")!;
                Assert.Equal("10.9.8.7", cuenta.UltimaIp);
                Assert.NotNull(cuenta.UltimoIngreso);
                Assert.InRange(cuenta.UltimoIngreso!.Value, antes, DateTime.UtcNow.AddSeconds(1));
                Assert.True(cuenta.VerifyPassword("clave_larga"));   // y el resto de la cuenta, igual
            }
            (almacen as IDisposable)?.Dispose();
        }
    }

    /// <summary>El original escribe <c>Date &amp; " " &amp; Time</c> con el formato de su máquina: un <c>.cnt</c> suyo tiene
    /// que leerse, y lo que no se entienda queda en "no se sabe", no en un error.</summary>
    [Fact]
    public void La_fecha_del_cnt_del_original_se_entiende()
    {
        var acc = AccountFile.FromIni(IniFile.Parse(
            "[VIEJA]\r\nPassword=x\r\nUltimoAcceso=28/09/2026 10:15:00\r\nUltimaIP=190.1.2.3\r\n"), "vieja");
        Assert.Equal("190.1.2.3", acc.UltimaIp);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 15, 0), acc.UltimoIngreso!.Value.ToLocalTime());

        var rara = AccountFile.FromIni(IniFile.Parse("[RARA]\r\nPassword=x\r\nUltimoAcceso=ayer a la tarde\r\n"), "rara");
        Assert.Null(rara.UltimoIngreso);
    }

    [Fact]
    public void La_fecha_nuestra_ida_y_vuelta_por_el_cnt()
    {
        var acc = new Account { Name = "Ida", UltimoIngreso = new DateTime(2026, 9, 28, 13, 15, 42, DateTimeKind.Utc), UltimaIp = "1.2.3.4" };
        var vuelta = AccountFile.FromIni(IniFile.Parse(AccountFile.ToIni(acc).ToText()), "Ida");
        Assert.Equal(acc.UltimoIngreso, vuelta.UltimoIngreso);
        Assert.Equal(DateTimeKind.Utc, vuelta.UltimoIngreso!.Value.Kind);
        Assert.Equal("1.2.3.4", vuelta.UltimaIp);
    }
}
