using System;
using System.IO;
using CoverAO.Server.Data;
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
///     <description>37 ≈ 1,5 s, el <c>IntervaloParalizadoReducido</c> de Admin.bas:58, cableado también
///     (<c>HechizoEstadoNPC</c>, modHechizos.bas:1565).</description></item>
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
}
