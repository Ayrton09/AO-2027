using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// CUÁNTO DURA UNA PARÁLISIS. Son TRES duraciones distintas y es fácil confundirlas — de hecho una ya estuvo
/// mal (la de criatura, con el comentario correcto y el número equivocado). Todas se cuentan en vueltas del
/// game-loop de 40 ms, que es el mismo <c>GAME_TIMER_INTERVAL</c> del original, así que los contadores del
/// VB6 son vueltas 1:1:
///
/// <list type="table">
///   <item><term>Hechizo Paralizar (y criatura que paraliza)</term>
///     <description>500 = 20 s. Es <c>IntervaloParalizado</c>, y el original lo lee del
///     <c>Server.ini</c> en <c>[INTERVALOS]</c> (FileIO.bas:1487) — o sea que quien hostea lo cambia.</description></item>
///   <item><term>Golpe que paraliza (gladiador y bardo, a mano limpia)</term>
///     <description>150 = 6 s, CABLEADO en <c>GolpeParalizaUsuario</c> (SistemaCombate.bas:2289). La clase
///     cambia la PROBABILIDAD (dado 1..300 en vez de 1..500), nunca la duración.</description></item>
///   <item><term>Paralizar a una CRIATURA</term>
///     <description>37 vueltas DE OTRO RELOJ: los contadores de las criaturas los baja el <c>TIMER_AI</c> del original,
///     que corre cada 380 ms (frmMain.frm:170-171), así que son 14 s y no 1,5. Es el
///     <c>IntervaloParalizadoReducido</c> de Admin.bas:58, cableado también (<c>HechizoEstadoNPC</c>,
///     modHechizos.bas:1565).</description></item>
/// </list>
///
/// Lo que este archivo fija es la primera: que el default sea el del install y que la clave del
/// <c>Server.ini</c> se lea de verdad. Las otras dos son constantes privadas del despachador y del sistema de
/// NPCs; quedan documentadas acá para que el próximo que venga a tocar una no se lleve la otra puesta.
/// </summary>
public class DuracionDeParalisisTests
{
    /// <summary>Sin archivo, el default es el del <c>Server.ini</c> del install: 500 vueltas = 20 s.</summary>
    [Fact]
    public void Sin_ini_la_paralisis_dura_lo_que_trae_el_install()
    {
        var cfg = ServerIni.Cargar(null);
        Assert.Equal(500, cfg.IntervaloParalizado);
        Assert.Equal(20_000, cfg.IntervaloParalizado * 40);   // 20 segundos
    }

    /// <summary>La clave se lee de <c>[INTERVALOS]</c>, que es donde la pone el original — no de
    /// <c>[INIT]</c>.</summary>
    [Fact]
    public void La_clave_del_ini_cambia_la_duracion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-paral-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Server.ini"), "[INTERVALOS]\nIntervaloParalizado=125\n");
            var cfg = ServerIni.Cargar(Path.Combine(dir, "Server.ini"));
            Assert.Equal(125, cfg.IntervaloParalizado);   // 5 segundos

            // Y el CONTROL de que la sección importa: la misma clave en [INIT] no la tiene que mover.
            File.WriteAllText(Path.Combine(dir, "Server.ini"), "[INIT]\nIntervaloParalizado=125\n");
            Assert.Equal(500, ServerIni.Cargar(Path.Combine(dir, "Server.ini")).IntervaloParalizado);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Un 0 dejaría la parálisis sin efecto y un número enorme clavaría a alguien para siempre: el
    /// valor se acota, como el resto de los intervalos.</summary>
    [Fact]
    public void Un_valor_absurdo_se_acota()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-paral-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Server.ini"), "[INTERVALOS]\nIntervaloParalizado=0\n");
            Assert.True(ServerIni.Cargar(Path.Combine(dir, "Server.ini")).IntervaloParalizado >= 1);
            File.WriteAllText(Path.Combine(dir, "Server.ini"), "[INTERVALOS]\nIntervaloParalizado=99999999\n");
            Assert.True(ServerIni.Cargar(Path.Combine(dir, "Server.ini")).IntervaloParalizado <= 100_000);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── EL GOLPE QUE PARALIZA, ahora configurable (pedido del tester) ───────────────────────────────────────
    //
    // Las cuatro claves son NUESTRAS: el original lleva estos números cableados. Lo que estos casos protegen no
    // es que se puedan cambiar —eso es una línea— sino que **el default sea el del original**: si alguien mueve
    // un default creyendo que "500 es mucho", el server deja de comportarse como el VB6 sin que nada lo avise, y
    // ninguna otra red lo caza (el oráculo no llega a esta rama y los flows no miden probabilidades).

    /// <summary>Sin archivo, los cuatro valen lo que el original tiene cableado.</summary>
    [Fact]
    public void Sin_ini_el_golpe_paralizante_es_el_del_original()
    {
        var cfg = ServerIni.Cargar(null);
        Assert.Equal(295, cfg.GolpeParalizanteVueltas);           // SistemaCombate.bas:2235 — 30 vueltas del TIMER_AI = 11,4 s
        Assert.Equal(GameLimits.TicksDeUnContadorDeCriatura(30), cfg.GolpeParalizanteVueltas);
        Assert.Equal(150, cfg.GolpeParalizanteVueltasPvP);        // SistemaCombate.bas:2289 — 6 s
        Assert.Equal(500, cfg.GolpeParalizanteDado);              // RandomNumber(1, 500)
        Assert.Equal(300, cfg.GolpeParalizanteDadoGladiadorBardo);// RandomNumber(1, 300)
    }

    /// <summary>Y las cuatro se leen de <c>[INIT]</c>, que es donde viven las reglas de la casa.</summary>
    [Fact]
    public void Las_claves_del_ini_mueven_el_golpe_paralizante()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-paral-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string ruta = Path.Combine(dir, "Server.ini");
            File.WriteAllText(ruta, "[INIT]\nGolpeParalizanteVueltas=75\nGolpeParalizanteVueltasPvP=250\n"
                                  + "GolpeParalizanteDado=120\nGolpeParalizanteDadoGladiadorBardo=80\n");
            var cfg = ServerIni.Cargar(ruta);
            Assert.Equal(75, cfg.GolpeParalizanteVueltas);
            Assert.Equal(250, cfg.GolpeParalizanteVueltasPvP);
            Assert.Equal(120, cfg.GolpeParalizanteDado);
            Assert.Equal(80, cfg.GolpeParalizanteDadoGladiadorBardo);

            // Y el CONTROL de la sección, igual que arriba: en [INTERVALOS] no las tiene que encontrar.
            File.WriteAllText(ruta, "[INTERVALOS]\nGolpeParalizanteDado=120\n");
            Assert.Equal(500, ServerIni.Cargar(ruta).GolpeParalizanteDado);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Un dado en 0 no puede quedar en 0: <c>Next(1, 0 + 1)</c> devuelve SIEMPRE 1, o sea que
    /// paralizaría con cada golpe — lo contrario de lo que espera quien escribe un cero.</summary>
    [Fact]
    public void El_dado_en_cero_se_acota()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-paral-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string ruta = Path.Combine(dir, "Server.ini");
            File.WriteAllText(ruta, "[INIT]\nGolpeParalizanteDado=0\nGolpeParalizanteVueltas=0\n");
            var cfg = ServerIni.Cargar(ruta);
            Assert.True(cfg.GolpeParalizanteDado >= 1);
            Assert.True(cfg.GolpeParalizanteVueltas >= 1);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>El aviso de "claves que no mencionaste" tiene que nombrarlas: una perilla que el host no sabe
    /// que existe es una perilla que no existe.</summary>
    [Fact]
    public void Las_cuatro_claves_salen_en_el_aviso_de_ausentes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "coverao-paral-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string ruta = Path.Combine(dir, "Server.ini");
            File.WriteAllText(ruta, "[INIT]\nStartPort=7666\n");
            var ausentes = ServerIni.Cargar(ruta).ClavesAusentes;
            Assert.Contains("GolpeParalizanteVueltas", ausentes);
            Assert.Contains("GolpeParalizanteVueltasPvP", ausentes);
            Assert.Contains("GolpeParalizanteDado", ausentes);
            Assert.Contains("GolpeParalizanteDadoGladiadorBardo", ausentes);
        }
        finally { Directory.Delete(dir, true); }
    }
}
