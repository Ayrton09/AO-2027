using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El anticheat con memoria (2026-09-05): las trampas se cuentan en la CUENTA y sobreviven al relog, los
/// umbrales salen del Server.ini, y los movimientos de oro van a un registro propio. Lo que hace el
/// dispatcher con el contador (echar, banear, avisar) lo mide <c>trampas_escalonadas_flow</c>; acá van las
/// tres piezas que se pueden fijar sin un mundo vivo.
/// </summary>
public class AnticheatEscalonadoTests
{
    [Fact]
    public void Los_umbrales_tienen_default_y_se_leen_del_ini()
    {
        var sin = new ServerIni();
        Assert.Equal(3, sin.TrampasParaEchar);
        Assert.Equal(6, sin.TrampasParaBanear);
        Assert.Equal(1000, sin.EconomiaUmbralOro);

        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nTrampasParaEchar=2\nTrampasParaBanear=0\nEconomiaUmbralOro=0\n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.Equal(2, con.TrampasParaEchar);
            Assert.Equal(0, con.TrampasParaBanear);   // 0 = ese escalón apagado
            Assert.Equal(0, con.EconomiaUmbralOro);   // 0 = se anota todo
            // Las tres claves son conocidas: no tienen que salir en el aviso de "opciones nuevas".
            Assert.Contains("TrampasParaEchar", ServerIni.ClavesConocidas);
            Assert.Contains("TrampasParaBanear", ServerIni.ClavesConocidas);
            Assert.Contains("EconomiaUmbralOro", ServerIni.ClavesConocidas);
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void Las_trampas_de_la_cuenta_sobreviven_al_guardado()
    {
        var acc = new Account { Name = "tramposo", PasswordHash = "h", Salt = "s", Trampas = 4 };
        var ini = AccountFile.ToIni(acc);
        var vuelta = AccountFile.FromIni(ini, "tramposo");
        Assert.Equal(4, vuelta.Trampas);
        // CONTROL: una cuenta vieja, sin la clave, arranca en 0 y no revienta.
        var vieja = AccountFile.FromIni(AccountFile.ToIni(new Account { Name = "limpia", PasswordHash = "h", Salt = "s" }), "limpia");
        Assert.Equal(0, vieja.Trampas);
    }

    [Fact]
    public void El_registro_de_economia_escribe_al_archivo_del_dia()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao_eco_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var reg = new RegistroDeEconomia(dir, dias: 30);
            reg.Anotar("transferencia Rugura -> Merlin: 5000 oro");
            reg.Volcar();
            string texto = File.ReadAllText(reg.ArchivoDeHoy);
            Assert.Contains("transferencia Rugura -> Merlin: 5000 oro", texto);
            Assert.EndsWith("\n", texto);
            // Sin BOM: es un log para grep, y el marcador rompe la primera línea de cada archivo.
            var bytes = File.ReadAllBytes(reg.ArchivoDeHoy);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "el archivo arranca con BOM");
            // CONTROL: sin nada encolado no toca el disco (el archivo no crece).
            long antes = new FileInfo(reg.ArchivoDeHoy).Length;
            reg.Volcar();
            Assert.Equal(antes, new FileInfo(reg.ArchivoDeHoy).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
