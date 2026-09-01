using System;
using System.IO;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL JUEGO ENTRA POR EL PUERTO DEL CLIENTE WEB.
///
/// <para>El servidor abre cuatro puertos, y hasta ahora un jugador de afuera necesitaba DOS: el de la página
/// y el del WebSocket. Abrir uno solo no daba ningún error visible —la página cargaba, el login aparecía, y
/// el socket se moría contra un puerto cerrado, sin dejar un renglón del lado del servidor, porque esa
/// conexión no llegaba nunca—. Ahora el WebSocket también atiende en <c>/ws</c> del puerto que sirve la
/// página, así que en el router se abre uno.</para>
///
/// <para>Lo que se fija acá es <b>lo que el servidor le anuncia al cliente</b>, que es de donde sale la
/// decisión. Que el socket de verdad se abra y se juegue por ahí lo prueba <c>ws-smoke.mjs</c> contra un
/// servidor andando, y eso ahora lo corre la puerta (paso "Protocolo por WebSocket") por los dos caminos.</para>
/// </summary>
public class UnSoloPuertoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_1puerto_" + Guid.NewGuid().ToString("N"));

    public UnSoloPuertoTests() => Directory.CreateDirectory(_raiz);
    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    /// <summary>Con el WebSocket enganchado, el cliente recibe la RUTA y con eso abre el socket contra la
    /// misma dirección de la que se bajó la página.</summary>
    [Fact]
    public void Se_le_anuncia_la_ruta_del_juego()
    {
        var con = new CoverAO.Server.Net.StaticFileServer(
            7669, _raiz, 7667, "", 0, null, (ctx, ct) => System.Threading.Tasks.Task.CompletedTask);
        Assert.Contains("\"wsPath\":\"/ws\"", con.ConfigJson);
    }

    /// <summary>Sin enganchar, <c>wsPath</c> viaja en <c>null</c> y el cliente se cae al puerto de siempre.
    /// Importa que sea explícito y no que falte la clave: un cliente que ve la ruta y la usa contra un
    /// servidor que no la atiende queda sin conectar, que es el defecto que esto viene a sacar — al revés.</summary>
    [Fact]
    public void Sin_enganchar_no_se_anuncia_ninguna_ruta()
    {
        var sin = new CoverAO.Server.Net.StaticFileServer(7669, _raiz, 7667);
        Assert.Contains("\"wsPath\":null", sin.ConfigJson);
    }

    /// <summary>Y el puerto suelto SE SIGUE ANUNCIANDO, incluso con la ruta puesta. Es el camino de
    /// compatibilidad: un <c>index.html</c> viejo, o la página servida por un <c>http.server</c> de
    /// desarrollo o por un nginx que no pasa por este servidor, no conocen <c>/ws</c> y tienen que poder
    /// entrar igual. El remapeo de puertos del hosting sigue mandando sobre ese número.</summary>
    [Fact]
    public void El_puerto_suelto_se_sigue_anunciando()
    {
        var con = new CoverAO.Server.Net.StaticFileServer(
            7669, _raiz, 7667, "", 0, null, (ctx, ct) => System.Threading.Tasks.Task.CompletedTask);
        Assert.Contains("\"wsPort\":7667", con.ConfigJson);

        var remapeado = new CoverAO.Server.Net.StaticFileServer(
            7669, _raiz, 7667, "", 25001, null, (ctx, ct) => System.Threading.Tasks.Task.CompletedTask);
        Assert.Contains("\"wsPort\":25001", remapeado.ConfigJson);
        Assert.Contains("\"wsPath\":\"/ws\"", remapeado.ConfigJson);
    }

    /// <summary>El cliente tiene que MIRAR esas dos claves. El servidor puede anunciar la ruta perfecta y no
    /// servir de nada si el cliente no la lee — es el mismo agujero que el catálogo de textos que nadie
    /// emite. Se mira el fuente porque no hay otro lugar donde esté escrito: es una página de un archivo.</summary>
    [Fact]
    public void El_cliente_lee_la_ruta_y_arma_la_direccion_con_el_puerto()
    {
        string raizRepo = RaizDelRepo();
        string cliente = File.ReadAllText(Path.Combine(raizRepo, "client-web", "index.html"));

        Assert.Contains("cfg.wsPath", cliente);
        // `location.host` (con puerto), no `location.hostname`: es la diferencia entre entrar por la misma
        // direccion y entrar por el puerto 80 de un servidor que escucha en otro.
        Assert.Contains("${wsProto}//${location.host}${wsPath}", cliente);
    }

    /// <summary>Y el armador tiene que llevarse la página nueva al paquete. Copiar el <c>index.html</c> a
    /// mano dejaba cualquier otra página en 404 <b>sólo en el paquete</b>, que es el único lugar donde nadie
    /// la prueba.</summary>
    [Fact]
    public void El_armador_copia_todas_las_paginas()
    {
        string raizRepo = RaizDelRepo();
        string armador = File.ReadAllText(Path.Combine(raizRepo, "hosting", "publish.ps1"));
        Assert.Contains("client-web\\*.html", armador);

        foreach (var pagina in Directory.GetFiles(Path.Combine(raizRepo, "client-web"), "*.html"))
            Assert.True(File.Exists(pagina), pagina);
    }

    /// <summary>Sube hasta la raíz del repo desde donde corren los tests.</summary>
    private static string RaizDelRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "client-web"))) d = d.Parent;
        Assert.True(d is not null, "no se encontro la raiz del repo desde " + AppContext.BaseDirectory);
        return d!.FullName;
    }
}
