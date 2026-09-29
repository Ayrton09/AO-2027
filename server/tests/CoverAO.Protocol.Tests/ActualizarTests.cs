using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CoverAO.Server;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// ACTUALIZAR UN SERVIDOR QUE YA ESTÁ EN PRODUCCIÓN.
///
/// <para>El procedimiento es descomprimir la versión nueva encima: el armador conserva <c>data/</c> y el
/// <c>Server.ini</c> del host y reemplaza todo lo demás. El cliente web lo sirve el propio servidor, así que
/// el jugador no instala nada — su navegador revalida cada archivo contra el ETag y baja sólo lo que cambió.
/// Nada de eso hay que verificarlo acá; lo que se cuida son las tres cosas que sí se pueden romper en
/// silencio al pasar de una versión a otra.</para>
/// </summary>
public class ActualizarTests : IDisposable
{
    private readonly List<string> _basura = new();

    public void Dispose()
    {
        foreach (var p in _basura)
        {
            try { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); } catch { }
        }
    }

    private string CarpetaTemporal()
    {
        string d = Path.Combine(Path.GetTempPath(), "coverao_upd_" + Guid.NewGuid().ToString("N")[..8]);
        _basura.Add(d);
        return d;
    }

    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    // ── 1. UNA SOLA VERSIÓN ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// El número de versión vive en <see cref="Build.Version"/> y el armador lo repite en su parámetro
    /// <c>-Version</c>, que es lo que nombra al zip. Si se separan, el paquete dice una cosa y el servidor que
    /// trae adentro dice otra — y el que recibe el zip no tiene cómo darse cuenta.
    /// </summary>
    [Fact]
    public void El_armador_y_el_servidor_dicen_la_misma_version()
    {
        string ps1 = File.ReadAllText(BuscarEnElRepo(Path.Combine("hosting", "publish.ps1")));
        var m = Regex.Match(ps1, @"\$Version\s*=\s*""(?<v>[^""]+)""");
        Assert.True(m.Success, "no encontré el default de -Version en hosting/publish.ps1");
        Assert.Equal(Build.Version, m.Groups["v"].Value);
    }

    /// <summary>
    /// EL PAQUETE NO LLEVA LA BASE DEL QUE PUBLICA. La carpeta de salida es también la instalación que corre el que arma el
    /// paquete, y desde que los personajes y las cuentas viven en <c>data/coverao.db</c>, ésa es su cuenta —con el hash de
    /// la contraseña— y su personaje: lo mismo que la lista ya dejaba afuera cuando eran <c>.chr</c> y <c>.cnt</c>.
    /// </summary>
    [Fact]
    public void El_paquete_no_lleva_la_base_del_que_publica()
    {
        string ps1 = File.ReadAllText(BuscarEnElRepo(Path.Combine("hosting", "publish.ps1")));
        var lista = Regex.Match(ps1, @"\$noViajan\s*=\s*@\((?<l>.*?)\)\s*\r?\nfunction", RegexOptions.Singleline);
        Assert.True(lista.Success, "no encontré la lista $noViajan en hosting/publish.ps1");
        foreach (var queda in new[] { "'data/coverao.db'", "'data/rescate-'", "'data/importacion-'", "'data/bots.txt'" })
            Assert.Contains(queda, lista.Groups["l"].Value);
    }

    // ── 2. LAS OPCIONES NUEVAS SE AVISAN ──────────────────────────────────────────────────────────────

    /// <summary>La lista está escrita a mano; esto la cruza contra las claves que el código LEE de verdad. Sin
    /// esto, agregar una opción y olvidarse de la lista deja al host sin el aviso justamente de la nueva.</summary>
    [Fact]
    public void La_lista_de_claves_conocidas_no_se_queda_vieja()
    {
        string fuente = File.ReadAllText(BuscarEnElRepo(Path.Combine(
            "server", "src", "CoverAO.Server", "Data", "ServerIni.cs")));
        var leidas = Regex.Matches(fuente, @"ini\.Get\w+\(\s*""INIT""\s*,\s*""(?<clave>[A-Za-z]+)""")
                          .Select(x => x.Groups["clave"].Value)
                          .Distinct(StringComparer.Ordinal)
                          .OrderBy(k => k, StringComparer.Ordinal)
                          .ToList();
        Assert.True(leidas.Count >= 20, $"el regex encontró {leidas.Count} claves; ¿cambió la forma de leer el ini?");
        Assert.Equal(leidas, ServerIni.ClavesConocidas.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    /// <summary>El caso real de una actualización: el host tiene su archivo de la versión anterior, sin la
    /// opción nueva. Tiene que salir listada.</summary>
    [Fact]
    public void Un_ini_viejo_lista_las_opciones_que_le_faltan()
    {
        var ini = IniFile.Parse("[INIT]\r\nStartPort=7666\r\nMaxUsers=50\r\n");
        var cfg = ServerIni.Desde(ini, "C:\\server\\Server.ini");
        Assert.Contains("HipocamposEnMarAbierto", cfg.ClavesAusentes);
        Assert.DoesNotContain("StartPort", cfg.ClavesAusentes);
        Assert.DoesNotContain("MaxUsers", cfg.ClavesAusentes);
    }

    /// <summary>Un ini que las trae TODAS no reporta ninguna: si no, el aviso sería ruido en cada arranque y
    /// el host aprendería a ignorarlo, que es la forma de que un aviso deje de servir.</summary>
    [Fact]
    public void Un_ini_completo_no_reporta_nada()
    {
        string cuerpo = "[INIT]\r\n" + string.Join("\r\n", ServerIni.ClavesConocidas.Select(k => k + "=0")) + "\r\n";
        var cfg = ServerIni.Desde(IniFile.Parse(cuerpo), "C:\\server\\Server.ini");
        Assert.Empty(cfg.ClavesAusentes);
    }

    /// <summary>Sin archivo TODO es default, así que listar las 32 no informaría nada.</summary>
    [Fact]
    public void Sin_archivo_no_se_lista_nada()
    {
        Assert.Empty(ServerIni.Desde(new IniFile()).ClavesAusentes);
    }

    /// <summary>
    /// EL AVISO TIENE QUE SER SEÑAL, NO RUIDO. El <c>Server.ini.ejemplo</c> que se distribuye trae TODAS las
    /// claves, así que un host recién instalado no ve ninguna listada y el aviso sólo aparece cuando una
    /// versión agrega algo. Si alguna quedara fuera del ejemplo, todo host la vería en cada arranque para
    /// siempre — y un aviso que sale siempre es un aviso que se aprende a ignorar.
    /// </summary>
    [Fact]
    public void El_ini_de_ejemplo_trae_todas_las_claves()
    {
        string ejemplo = File.ReadAllText(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        // Puestas de verdad, no comentadas: una linea comentada la explica pero el servidor no la lee, así
        // que igual saldría en el aviso.
        var puestas = Regex.Matches(ejemplo, @"^[ \t]*([A-Za-z]+)[ \t]*=", RegexOptions.Multiline)
                           .Select(m => m.Groups[1].Value)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var faltan = ServerIni.ClavesConocidas.Where(k => !puestas.Contains(k)).ToList();
        Assert.True(faltan.Count == 0,
            "estas claves no están puestas en hosting/Server.ini.ejemplo, así que todo host las vería en el "
            + "aviso de arranque para siempre: " + string.Join(", ", faltan));
    }

    // ── 3. EL SELLO DEL MUNDO ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Un_mundo_nuevo_queda_sellado_con_la_version_actual()
    {
        string d = CarpetaTemporal();
        Assert.Equal(SelloDelMundo.Nuevo, Build.SellarMundo(d, out _));
        Assert.Equal(Build.Version, File.ReadAllText(Path.Combine(d, Build.ArchivoDelSello)).Trim());
    }

    [Fact]
    public void Volver_a_arrancar_la_misma_version_no_dice_nada()
    {
        string d = CarpetaTemporal();
        Build.SellarMundo(d, out _);
        Assert.Equal(SelloDelMundo.Igual, Build.SellarMundo(d, out var previa));
        Assert.Equal(Build.Version, previa);
    }

    /// <summary>El caso que justifica todo esto: el mundo lo creó otra versión. Y queda RE-sellado, así que
    /// el aviso sale una vez y no en cada arranque.</summary>
    [Fact]
    public void Un_mundo_de_otra_version_se_reporta_y_se_resella()
    {
        string d = CarpetaTemporal();
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, Build.ArchivoDelSello), "0.9.9\r\n");
        Assert.Equal(SelloDelMundo.Distinta, Build.SellarMundo(d, out var previa));
        Assert.Equal("0.9.9", previa);
        Assert.Equal(SelloDelMundo.Igual, Build.SellarMundo(d, out _));
    }

    /// <summary>Un mundo anterior a que el sello existiera: hay personajes pero no hay archivo. NO es un mundo
    /// nuevo, y distinguirlo importa — es el único caso en que no se puede saber de dónde viene.</summary>
    [Fact]
    public void Un_mundo_anterior_al_sello_se_distingue_de_uno_nuevo()
    {
        string d = CarpetaTemporal();
        Directory.CreateDirectory(Path.Combine(d, "Charfile"));
        File.WriteAllText(Path.Combine(d, "Charfile", "RUGURA.chr"), "[INIT]\r\n");
        Assert.Equal(SelloDelMundo.SinSello, Build.SellarMundo(d, out _));
        Assert.True(File.Exists(Path.Combine(d, Build.ArchivoDelSello)));
    }

    // ── 4. EL PROCEDIMIENTO, ESCRITO ──────────────────────────────────────────────────────────────────

    /// <summary>La guía de hosting tiene que explicar cómo actualizar. Es lo único de todo esto que el host
    /// necesita ANTES de tocar nada, y hasta ahora no estaba escrito en ningún lado.</summary>
    [Fact]
    public void La_guia_explica_como_actualizar()
    {
        string doc = File.ReadAllText(BuscarEnElRepo(Path.Combine("docs", "HOSTING.md")));
        Assert.Contains("## Actualizar", doc, StringComparison.OrdinalIgnoreCase);
        // Las dos cosas que NO se pisan, nombradas: son las que separan un update de una reinstalación.
        Assert.Contains("data/", doc, StringComparison.Ordinal);
        Assert.Contains("Server.ini", doc, StringComparison.Ordinal);
    }
}
