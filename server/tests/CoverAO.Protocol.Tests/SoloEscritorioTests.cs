using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// SÓLO LA APP DE ESCRITORIO (12/10, <see cref="Escritorio"/>): con <c>SoloEscritorio=1</c> el navegador ve la página de descarga
/// en vez del juego y el WebSocket pide la cabecera de la app. Viene apagado hasta que los jugadores tengan la app.
/// </summary>
public class SoloEscritorioTests
{
    [Fact]
    public void Viene_apagado_y_se_lee_del_ini()
    {
        var sin = new ServerIni();
        Assert.False(sin.SoloEscritorio);
        Assert.Equal("", sin.DescargaEscritorio);

        string ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ruta, "[INIT]\nStartPort=7666\nSoloEscritorio=1\nDescargaEscritorio= https://ejemplo.org/descargas \n");
        try
        {
            var con = ServerIni.Cargar(ruta);
            Assert.True(con.SoloEscritorio);
            Assert.Equal("https://ejemplo.org/descargas", con.DescargaEscritorio);
            Assert.True(con.InitEscrita("SoloEscritorio"));   // con --fixture, sólo así se prende
            Assert.Contains("SoloEscritorio", ServerIni.ClavesConocidas);
            Assert.Contains("DescargaEscritorio", ServerIni.ClavesConocidas);
        }
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
}
