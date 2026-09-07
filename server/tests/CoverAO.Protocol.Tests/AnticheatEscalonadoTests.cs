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
    public void El_tope_por_ip_y_la_clave_minima_tienen_default_y_se_leen_del_ini()
    {
        var sin = new ServerIni();
        Assert.Equal(10, sin.MaxPersonajesPorIp);
        Assert.Equal(6, sin.ClaveMinima);
        Assert.False(sin.InitEscrita("MaxPersonajesPorIp"));   // sin archivo, nada está escrito

        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nMaxPersonajesPorIp=2\nClaveMinima=0\n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.Equal(2, con.MaxPersonajesPorIp);
            Assert.Equal(0, con.ClaveMinima);
            // Escrita: es lo que hace que el --fixture NO la apague (un flow la pide a propósito).
            Assert.True(con.InitEscrita("MaxPersonajesPorIp"));
            Assert.False(con.InitEscrita("TrampasParaEchar"));
            Assert.Contains("MaxPersonajesPorIp", ServerIni.ClavesConocidas);
            Assert.Contains("ClaveMinima", ServerIni.ClavesConocidas);
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void El_cupo_de_cuentas_nuevas_por_ip_tiene_default_y_se_lee_del_ini()
    {
        // Nació cableado en el Program y SIEMPRE encendido, también con --fixture: el barrido del oráculo crea
        // una cuenta por acción desde 127.0.0.1 y se quedaba sin poder crear a partir de la cuarta, con lo que
        // no medía nada. Ahora es una clave, y el fixture la apaga salvo que el ini del flow la escriba.
        var sin = new ServerIni();
        Assert.Equal(3, sin.CuentasNuevasPorIp);
        Assert.False(sin.InitEscrita("CuentasNuevasPorIp"));

        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nCuentasNuevasPorIp=5\n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.Equal(5, con.CuentasNuevasPorIp);
            Assert.True(con.InitEscrita("CuentasNuevasPorIp"));
            Assert.Contains("CuentasNuevasPorIp", ServerIni.ClavesConocidas);
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void La_espera_tras_castear_es_cero_como_el_original_y_se_lee_del_ini()
    {
        Assert.Equal(0, new ServerIni().IntervaloHechizoUsar);
        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\n[INTERVALOS]\nIntervaloHechizoUsar=3000\n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.Equal(3000, con.IntervaloHechizoUsar);
            Assert.True(con.IntervaloEscrito("IntervaloHechizoUsar"));   // el fixture no lo apaga si el flow lo pide
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void El_rate_limit_de_login_purga_las_ips_vencidas()
    {
        // Antes el mapa crecía para siempre: una entrada por cada IP que falló un login alguna vez.
        var t = new LoginThrottle(maxFails: 20, windowMs: 1, blockMs: 1);
        for (int i = 0; i < 1000; i++) t.RecordFailure($"10.1.{i / 256}.{i % 256}");
        Thread.Sleep(5);                       // vencen las ventanas
        for (int i = 0; i < 300; i++) t.RecordFailure($"10.2.{i / 256}.{i % 256}");   // 300 altas → al menos una purga
        Assert.True(t.Anotadas < 1300, $"quedaron {t.Anotadas} IPs anotadas: no purgó");
        // CONTROL: una IP bloqueada NO se purga aunque su ventana haya vencido.
        var b = new LoginThrottle(maxFails: 1, windowMs: 1, blockMs: 60_000);
        b.RecordFailure("10.9.9.9");           // bloqueada un minuto
        Thread.Sleep(5);
        for (int i = 0; i < 600; i++) b.RecordFailure($"10.3.{i / 256}.{i % 256}");
        Assert.True(b.IsBlocked("10.9.9.9"));
    }

    [Fact]
    public void El_registro_de_trampas_es_un_archivo_aparte_del_de_economia()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao_tr_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var eco = new RegistroDeEconomia(dir, 30);
            using var tr = new RegistroDeEconomia(dir, 30, "trampas");
            tr.Anotar("cuenta_test Merlin ip:127.0.0.1 #1: speedhack"); tr.Volcar();
            eco.Anotar("deposito Rugura: 1500 oro"); eco.Volcar();
            Assert.NotEqual(eco.ArchivoDeHoy, tr.ArchivoDeHoy);
            Assert.Contains("speedhack", File.ReadAllText(tr.ArchivoDeHoy));
            Assert.DoesNotContain("speedhack", File.ReadAllText(eco.ArchivoDeHoy));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
