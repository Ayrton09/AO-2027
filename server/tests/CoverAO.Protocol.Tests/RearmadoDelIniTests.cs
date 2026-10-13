using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SERVER.INI DEL HOST SE REARMA SIN CAMBIARLE NADA (ver <see cref="RearmadoDelIni"/>): sus valores quedan, las claves nuevas
/// aparecen con su explicación, lo que el server no reconoce se conserva al final, y si algo cambiara lo que lee el server no hay
/// rearmado.
/// </summary>
public class RearmadoDelIniTests
{
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    private static readonly string Ejemplo = File.ReadAllText(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")), Codec.Ansi);

    private static string Huella(string texto) => RearmadoDelIni.Huella(ServerIni.Desde(IniFile.Parse(texto)));

    [Fact]
    public void Los_valores_del_host_quedan_y_las_claves_nuevas_se_agregan_con_su_explicacion()
    {
        const string host = "[INIT]\nStartPort=7700\nExp=5\nMaxUsers=50\nDioses=1\n\n[Dioses]\nDios1=Rugura\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        var ini = IniFile.Parse(r.Texto!);
        Assert.Equal("7700", ini.GetString("INIT", "StartPort"));
        Assert.Equal("5", ini.GetString("INIT", "Exp"));
        Assert.Equal("50", ini.GetString("INIT", "MaxUsers"));
        Assert.Equal("Rugura", ini.GetString("Dioses", "Dios1"));
        Assert.Contains("ProxyDeConfianza", r.Agregadas);
        Assert.Contains("ProxyDeConfianza=", r.Texto);
        Assert.Contains("; Con un reverse-proxy delante", r.Texto);                 // la explicación del ejemplo viene con ella
        Assert.Equal(Huella(host), Huella(r.Texto!));
        Assert.Equal(8, ServerIni.Desde(ini).PrivilegioDe("Rugura"));
    }

    [Fact]
    public void Una_seccion_que_el_host_no_tiene_y_que_prenderia_una_regla_va_comentada()
    {
        // Sin [NIVELMINIMO] la regla no corre: escribir la del ejemplo la prendería.
        const string host = "[INIT]\nStartPort=7666\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        Assert.Contains("[NIVELMINIMO] 753", r.Comentadas);
        Assert.Contains(";753=20", r.Texto);
        Assert.Empty(ServerIni.Desde(IniFile.Parse(r.Texto!)).NivelMinimoPorMapa);
        Assert.Equal(Huella(host), Huella(r.Texto!));
    }

    [Fact]
    public void La_seccion_que_el_host_si_tiene_queda_con_sus_mapas()
    {
        const string host = "[INIT]\nStartPort=7666\n\n[NIVELMINIMO]\n753=25\n999=10\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        var niveles = ServerIni.Desde(IniFile.Parse(r.Texto!)).NivelMinimoPorMapa;
        Assert.Equal(25, niveles[753]);
        Assert.Equal(10, niveles[999]);                                              // uno propio, que el ejemplo no trae
        Assert.False(niveles.ContainsKey(117));                                      // el del ejemplo que el host no puso: comentado
        Assert.Equal(Huella(host), Huella(r.Texto!));
    }

    [Fact]
    public void Una_clave_mal_escrita_se_conserva_al_final_bajo_el_aviso()
    {
        const string host = "[INIT]\nStartPort=7666\nExpp=5\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        Assert.Equal(new[] { "Expp" }, r.Desconocidas);
        int aviso = r.Texto!.IndexOf(RearmadoDelIni.TituloDesconocidas, StringComparison.Ordinal);
        Assert.True(aviso > 0 && r.Texto.IndexOf("Expp=5", StringComparison.Ordinal) > aviso);
        Assert.Equal(1, ServerIni.Desde(IniFile.Parse(r.Texto)).Exp);     // se ignoraba antes y se ignora ahora
        Assert.Equal(Huella(host), Huella(r.Texto));
    }

    [Fact]
    public void Mayusculas_espacios_repetidas_y_sin_seccion_como_el_host_las_escribio()
    {
        const string host = "suelta=1\n[init]\n  exp = 5  \nMaxUsers=10\nmaxusers=20\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        var ini = IniFile.Parse(r.Texto!);
        Assert.Equal("5", ini.GetString("INIT", "Exp"));
        Assert.Equal("20", ini.GetString("INIT", "MaxUsers"));                      // la repetida: gana la última, como en el lector
        Assert.Equal("1", ini.GetString("", "suelta"));
        Assert.Equal(Huella(host), Huella(r.Texto!));
    }

    [Fact]
    public void Una_seccion_propia_del_host_se_conserva_entera()
    {
        const string host = "[INIT]\nStartPort=7666\n\n[MIAS]\nalgo=1\notra=dos\n";
        var r = RearmadoDelIni.Rearmar(host, Ejemplo);
        Assert.Null(r.Problema);
        var ini = IniFile.Parse(r.Texto!);
        Assert.Equal("1", ini.GetString("MIAS", "algo"));
        Assert.Equal("dos", ini.GetString("MIAS", "otra"));
    }

    [Fact]
    public void Rearmar_lo_rearmado_da_lo_mismo()
    {
        const string host = "[INIT]\nStartPort=7700\nExpp=5\n\n[MIAS]\nalgo=1\n";
        var una = RearmadoDelIni.Rearmar(host, Ejemplo);
        var dos = RearmadoDelIni.Rearmar(una.Texto!, Ejemplo);
        Assert.Null(dos.Problema);
        Assert.Equal(una.Texto, dos.Texto);
        Assert.Empty(dos.Agregadas);
    }

    [Fact]
    public void El_ejemplo_mismo_no_cambia()
    {
        var r = RearmadoDelIni.Rearmar(Ejemplo, Ejemplo);
        Assert.Null(r.Problema);
        Assert.Empty(r.Agregadas);
        Assert.Empty(r.Desconocidas);
        Assert.Equal(Huella(Ejemplo), Huella(r.Texto!));
    }

    [Fact]
    public void Una_clave_renombrada_conserva_el_valor_del_host()
    {
        var renombres = new Dictionary<(string, string), string> { [("INIT", "ExpViejo")] = "Exp" };
        var r = RearmadoDelIni.Rearmar("[INIT]\nExpViejo=7\n", Ejemplo, renombres);
        Assert.Null(r.Problema);
        var ini = IniFile.Parse(r.Texto!);
        Assert.Equal("7", ini.GetString("INIT", "Exp"));
        Assert.False(ini.HasKey("INIT", "ExpViejo"));
        Assert.Equal(new[] { "ExpViejo → Exp" }, r.Renombradas);
    }

    [Fact]
    public void El_arranque_sabe_que_claves_del_ini_no_existen()
    {
        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nExpRat=5\nDioses=1\n\n[Dioses]\nDios1=Rugura\n");
        try { Assert.Equal(new[] { "ExpRat" }, ServerIni.Cargar(ruta).ClavesDesconocidas); }   // el contador del staff sí existe
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void Si_el_resultado_cambiara_algo_la_verificacion_lo_frena()
    {
        var host = IniFile.Parse("[INIT]\nExp=5\n\n[Dioses]\nDios1=Rugura\n");
        string antes = RearmadoDelIni.Huella(ServerIni.Desde(host));
        var ninguna = new HashSet<(string, string)>();
        Assert.Null(RearmadoDelIni.Verificar("[INIT]\nExp=5\n\n[Dioses]\nDios1=Rugura\n", host, antes, ninguna));
        Assert.NotNull(RearmadoDelIni.Verificar("[INIT]\nExp=1\n\n[Dioses]\nDios1=Rugura\n", host, antes, ninguna));
        Assert.NotNull(RearmadoDelIni.Verificar("[INIT]\nExp=5\n", host, antes, ninguna));                  // se perdía el Dios
        Assert.NotNull(RearmadoDelIni.Verificar("[INIT]\nExp=5\n\n[Dioses]\nDios1=Otro\n", host, antes, ninguna));
    }
}
