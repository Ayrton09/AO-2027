using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS TRES REGLAS DE LA CASA que el tester pidió y que el ORIGINAL NO HACE: oro al subir de nivel (2.12),
/// curarse al subir (2.13) y que el equipo de novato desaparezca al pasar el nivel 15 (2.2b).
///
/// <para>Están implementadas, pero <b>apagadas</b>: cambian el balance del juego y esa decisión no es del
/// port. Lo que fija este test es justamente eso — que <b>el default sea el comportamiento del VB6</b>. Es la
/// mitad que importa: si mañana alguien le pone un default distinto "porque queda mejor", un server que se
/// creía fiel empieza a regalar oro sin que nadie lo haya decidido.</para>
///
/// <para>Y se prueban las dos direcciones. Un test que sólo mirara el default pasaría igual si las opciones no
/// se leyeran del archivo en absoluto: el interruptor tiene que ENCENDER de verdad.</para>
/// </summary>
public class ReglasDeLaCasaTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_casa_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ruta); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ruta, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ruta);
    }

    [Fact]
    public void Sin_ini_el_server_se_comporta_como_el_original()
    {
        var cfg = new ServerIni();
        Assert.Equal(0, cfg.OroAlSubirNivel);       // `SubirNivel` del VB6 no toca el oro
        Assert.False(cfg.CuraAlSubirNivel);         // sube los MÁXIMOS y deja los mínimos donde estaban
        Assert.False(cfg.QuitarEquipoNovato);       // no existe ningún `QuitarNewbieObj`
    }

    [Fact]
    public void Con_un_ini_que_no_las_menciona_siguen_apagadas()
    {
        // El caso REAL: el Server.ini que se distribuye no las trae. Un default que se aplicara sólo cuando no
        // hay archivo dejaría prendidas las reglas en todo server con ini, que son todos los de producción.
        var cfg = Cargar("MaxUsers=50\r\n");
        Assert.Equal(0, cfg.OroAlSubirNivel);
        Assert.False(cfg.CuraAlSubirNivel);
        Assert.False(cfg.QuitarEquipoNovato);
    }

    [Fact]
    public void Los_interruptores_encienden_de_verdad()
    {
        var cfg = Cargar("OroAlSubirNivel=500\r\nCuraAlSubirNivel=1\r\nQuitarEquipoNovato=1\r\n");
        Assert.Equal(500, cfg.OroAlSubirNivel);
        Assert.True(cfg.CuraAlSubirNivel);
        Assert.True(cfg.QuitarEquipoNovato);
    }

    [Fact]
    public void El_oro_por_nivel_tiene_tope_y_no_acepta_negativos()
    {
        // No es una opinión sobre cuánto está bien: es que un valor absurdo desbalancea el mercado del server
        // sin que nadie lo note, y uno negativo le sacaría oro al jugador por subir de nivel.
        Assert.Equal(0, Cargar("OroAlSubirNivel=-9999\r\n").OroAlSubirNivel);
        Assert.Equal(1_000_000, Cargar("OroAlSubirNivel=99999999\r\n").OroAlSubirNivel);
    }
    /// <summary>Ubica un archivo del repo subiendo directorios desde donde corre el test (bin/…).</summary>
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// LO QUE ELIGIÓ EL AUTOR, fijado aparte del default del SERVIDOR.
    ///
    /// <para>Los tests de arriba fijan que el server, sin ini o con un ini que no las menciona, se comporte
    /// como el VB6 — y eso no cambia. Este fija la otra mitad: qué trae el <c>Server.ini</c> que se
    /// distribuye, que es lo que decide cómo se juega en un servidor recién instalado. El 2026-08-17 el autor
    /// pidió curarse al subir y que el equipo de novato desaparezca al dejar de serlo, y dejar el oro
    /// apagado.</para>
    ///
    /// <para>Existe porque las dos cosas se leen parecido y la de arriba dice "el default tiene que ser 0":
    /// sin este test, alguien que viniera a "arreglar" la plantilla la devolvería a ceros creyendo que
    /// corrige una infidelidad, y se llevaría puesta una decisión tomada.</para>
    /// </summary>
    [Fact]
    public void El_ini_de_ejemplo_trae_lo_que_eligio_el_autor()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        Assert.True(cfg.CuraAlSubirNivel);        // pedido del tester 2.13, aprobado
        Assert.True(cfg.QuitarEquipoNovato);      // 2.2b, y saca SÓLO los "(Newbies)"
        Assert.Equal(0, cfg.OroAlSubirNivel);     // 2.12: "por ahora lo dejamos off"
    }
}
