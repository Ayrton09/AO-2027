using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El mundo real exige la app de escritorio incluso con archivos de configuración anteriores.
/// </summary>
public class SoloEscritorioTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Siempre_se_exige_en_el_mundo_real_y_el_fixture_lee_el_ini(int valor, bool fixtureSoloEscritorio)
    {
        var sin = new ServerIni();
        Assert.True(sin.ExigirClienteDeEscritorio(fixture: false));
        Assert.False(sin.ExigirClienteDeEscritorio(fixture: true));
        Assert.Equal(ServerIni.DescargaOficial, sin.DescargaEscritorio);

        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, $"[INIT]\nStartPort=7666\nSoloEscritorio={valor}\nDescargaEscritorio= https://ejemplo.org/descargas \n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.True(con.ExigirClienteDeEscritorio(fixture: false));
            Assert.Equal(fixtureSoloEscritorio, con.ExigirClienteDeEscritorio(fixture: true));
            Assert.Equal("https://ejemplo.org/descargas", con.DescargaEscritorio);
            Assert.Contains("SoloEscritorio", ServerIni.ClavesConocidas);
            Assert.Contains("DescargaEscritorio", ServerIni.ClavesConocidas);
        }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void Sin_enlace_propio_la_pagina_manda_a_la_descarga_oficial()
    {
        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nDescargaEscritorio=\n");
        try { Assert.Equal(ServerIni.DescargaOficial, ServerIni.Cargar(ruta).DescargaEscritorio); }
        finally { File.Delete(ruta); }
    }

    [Fact]
    public void El_navegador_ve_la_pagina_de_descarga_y_la_app_el_juego()
    {
        Assert.Equal(Escritorio.PaginaDeDescarga, Escritorio.RutaAServir("index.html", soloEscritorio: true, esLaApp: false));
        Assert.Equal("index.html", Escritorio.RutaAServir("index.html", soloEscritorio: true, esLaApp: true));
        Assert.Equal("index.html", Escritorio.RutaAServir("index.html", soloEscritorio: false, esLaApp: false));
        // Lo demás se sirve igual: la página pública de estado, los gráficos, la misma de descarga.
        Assert.Equal("estado.html", Escritorio.RutaAServir("estado.html", soloEscritorio: true, esLaApp: false));
        Assert.Equal("assets/login/logo.webp", Escritorio.RutaAServir("assets/login/logo.webp", soloEscritorio: true, esLaApp: false));
    }

    [Fact]
    public void La_app_es_la_que_trae_la_cabecera()
    {
        Assert.False(Escritorio.EsLaApp(null));
        Assert.False(Escritorio.EsLaApp(""));
        Assert.False(Escritorio.EsLaApp("  "));
        Assert.True(Escritorio.EsLaApp("1.0.0"));
    }

    [Fact]
    public void El_enlace_de_descarga_viaja_en_la_config_del_cliente()
    {
        var web = new StaticFileServer(0, Path.GetTempPath(), 7667, soloEscritorio: true, descargaEscritorio: "https://ejemplo.org/d");
        Assert.Contains("\"descarga\":\"https://ejemplo.org/d\"", web.ConfigJson);
    }

    [Fact]
    public void La_bienvenida_reemplaza_el_enlace_privado_sin_cambiar_otros_repositorios()
    {
        var directorio = Directory.CreateTempSubdirectory("coverao-motd-");
        string ruta = Path.Combine(directorio.FullName, "motd.txt");
        try
        {
            File.WriteAllLines(ruta, new[]
            {
                "Reportes: https://github.com/Ayrton09/CoverAO-privado/issues",
                "Copia: https://github.com/Ayrton09/CoverAO-privado-backup",
            });
            var motd = ServerContent.Load(directorio.FullName).Motd;
            Assert.Equal("Reportes: https://github.com/Ayrton09/CoverAO/issues", motd[0]);
            Assert.Equal("Copia: https://github.com/Ayrton09/CoverAO-privado-backup", motd[1]);
        }
        finally { File.Delete(ruta); directorio.Delete(); }
    }
}
