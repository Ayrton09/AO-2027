using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Las UNIDADES de los contadores del game-loop (docs/unidades-del-loop.md).
///
/// El original corre su loop cada <c>GAME_TIMER_INTERVAL = 40</c> ms y suma <c>DeltaTick ≈ 1</c> por vuelta, o
/// sea que los números del <c>Server.ini</c> son VUELTAS, no milisegundos ni segundos. El <c>TickMs</c> del
/// port es también 40, así que la conversión es 1 a 1 — el número se copia tal cual.
///
/// Este archivo existe porque el error era invisible: las constantes "se veían razonables" (100 ticks, 250
/// ticks, 30000 ticks) y sólo comparándolas contra la fuente se ve que la sed iba 40 veces más rápido y una
/// poción duraba 25 veces de más. Cada test cita su línea para que corregir "hacia atrás" rompa algo.
/// </summary>
public class LoopUnitsTests
{
    [Fact]
    public void El_tick_del_port_mide_lo_mismo_que_la_vuelta_del_original()
        // frmMain.frm:175 (Timer.Interval = 40) y mMainLoop.bas:7 (GAME_TIMER_INTERVAL = 40). Si esto cambiara,
        // TODAS las constantes de abajo habría que reescalarlas.
        => Assert.Equal(40, GameLoop.TickMs);

    [Theory]
    // Server.ini [INTERVALOS], copiados tal cual. El tiempo real sale de multiplicar por 40 ms.
    [InlineData(4000, 160)]   // IntervaloSed    -> 160 s por cada -10 de agua
    [InlineData(4500, 180)]   // IntervaloHambre -> 180 s por cada -10 de comida
    public void El_hambre_y_la_sed_se_miden_en_vueltas_del_loop(int ticks, int segundos)
        => Assert.Equal(segundos, ticks * GameLoop.TickMs / 1000);

    [Fact]
    public void La_sed_tarda_media_hora_en_vaciarse_no_cuarenta_segundos()
    {
        // El síntoma que destapó el bug: con los 100 ticks viejos, 100 de agua se iban en 40 segundos.
        int bajadas = 100 / Survival.DecayStep;                                  // 10 bajadas de 10
        double segundos = bajadas * Survival.ThirstIntervalTicks * GameLoop.TickMs / 1000.0;
        Assert.True(segundos > 1500, $"la sed se vacía en {segundos:0} s; el original tarda ~1600");
        Assert.Equal(4000, Survival.ThirstIntervalTicks);
        Assert.Equal(4500, Survival.HungerIntervalTicks);
    }

    [Fact]
    public void Los_intervalos_de_regeneracion_salen_del_call_site_no_del_ini()
    {
        // La trampa de este bloque: el Server.ini define CUATRO constantes y mMainLoop.bas:186-212 usa sólo dos.
        //   de pie      -> Sanar(SanaIntervaloSinDescansar = 1600), RecStamina(50 literal)
        //   descansando -> Sanar(StaminaIntervaloSinDescansar = 5), RecStamina(50 literal)
        // Sí: descansando, la VIDA usa la constante de la ENERGÍA. Es un error de la fuente (el `Else` hasta
        // tiene el comentario invertido) y se porta igual. SanaIntervaloDescansar=100 y
        // StaminaIntervaloDescansar=2 nunca se usan: son constantes muertas del .ini.
        Assert.Equal(1600, Survival.HealIntervalTicks);
        Assert.Equal(5, Survival.HealRestingIntervalTicks);
        Assert.Equal(50, Survival.StaminaIntervalTicks);
        // Descansar cura 320 veces más rápido que estar de pie: es la razón de ser del /DESCANSAR.
        Assert.Equal(320, Survival.HealIntervalTicks / Survival.HealRestingIntervalTicks);
    }

    [Fact]
    public void La_curacion_natural_sale_de_la_energia_maxima_no_de_la_vida()
    {
        // VB6 Sanar: mashit = RandomNumber(2, Porcentaje(MaxSta, 5)). Es una rareza de la fuente —la vida se
        // regenera en función de la ENERGÍA— y se porta tal cual. Con rnd = tomar el techo:
        Assert.Equal(10, Survival.HealAmount(200, (lo, hi) => hi));   // 5% de 200 = 10
        Assert.Equal(2, Survival.HealAmount(200, (lo, hi) => lo));    // el piso es 2
        Assert.Equal(2, Survival.HealAmount(10, (lo, hi) => hi));     // 5% de 10 = 0 -> el piso evita el rango invertido
    }

    [Fact]
    public void La_energia_suma_de_a_cinco_por_tirada_con_la_supervivencia_adentro()
    {
        // VB6 RecStamina: massta = 5 * RandomNumber(1, Porcentaje(MaxSta + Supervivencia, 5)).
        Assert.Equal(5 * 15, Survival.StaminaAmount(200, 100, (lo, hi) => hi));   // 5% de 300 = 15
        Assert.Equal(5, Survival.StaminaAmount(200, 100, (lo, hi) => lo));
        Assert.Equal(5, Survival.StaminaAmount(1, 0, (lo, hi) => hi));            // piso en 1
    }

    [Fact]
    public void Las_duraciones_de_los_buffs_son_segundos_no_minutos()
    {
        // modHechizos.bas:1336 (1200) y :1353 (700), en vueltas -> 48 s y 28 s. Antes eran 30000 y 17500
        // porque se habían leído como segundos: una poción de agilidad duraba veinte minutos.
        Assert.Equal(48, PacketDispatcher.AttrBuffDurationTicks * GameLoop.TickMs / 1000);
        Assert.Equal(28, PacketDispatcher.AttrDebuffDurationTicks * GameLoop.TickMs / 1000);
    }

    [Fact]
    public void El_anti_mapeo_de_PvP_son_SIETE_SEGUNDOS_no_siete_vueltas()
    {
        // La excepción que confirma por qué este archivo existe: TiempoDeMapeo también vive en `Counters`, pero
        // no lo baja el loop de 40 ms sino PasarSegundo, que cuelga del timer Auditoria de frmMain
        // (frmMain.frm:182, Interval = 1000). O sea que su 7 son 7 segundos y no 0,28 — copiarlo "tal cual"
        // como se hace con los del Server.ini habría dejado la traba en un cuarto de segundo, que es
        // exactamente igual a no tenerla.
        Assert.Equal(175, PacketDispatcher.TicksAntiMapeo);
        Assert.Equal(7, PacketDispatcher.TicksAntiMapeo * GameLoop.TickMs / 1000);
    }

    [Fact]
    public void Las_constantes_que_el_port_ya_habia_acertado_siguen_igual()
    {
        // No todo estaba mal, y conviene fijarlo: donde el port copió el número del Server.ini acertó.
        // IntervaloInvocacion = 1001, IntervaloInvisible = 500, IntervaloOculto = 500.
        Assert.Equal(1001, PacketDispatcher.SummonLifetimeTicks);
        Assert.Equal(500, PacketDispatcher.InvisibilityDurationTicks);
        Assert.Equal(500, PacketDispatcher.OcultoDurationTicks);
    }

    /// <summary>
    /// EL DESNUDO NO RECUPERA ENERGÍA, salvo montado. Es lo primero que hace <c>RecStamina</c> (VB6
    /// General.bas:1083-1085) y el port no lo tenía: un personaje sin armadura recuperaba igual que uno vestido.
    /// </summary>
    [Theory]
    // ¿desnudo? | ¿montado? | ¿recupera?
    [InlineData(false, false, true)]    // vestido: recupera, como siempre
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]    // desnudo y a pie: NO
    [InlineData(true, true, true)]      // desnudo pero montado: sí — la excepción del original
    public void El_desnudo_no_recupera_energia_salvo_montado(bool desnudo, bool montado, bool recupera)
    {
        Assert.Equal(recupera, Survival.RecuperaEnergia(desnudo, montado));
    }

    /// <summary>Y la fórmula sigue siendo la del original, con la skill de Supervivencia adentro: es lo que el
    /// tester preguntaba. Sube el TECHO del tiro, no el piso, y por eso se siente aleatoria.</summary>
    [Fact]
    public void La_supervivencia_sube_el_techo_de_la_energia_que_vuelve()
    {
        int sinSkill = Survival.StaminaAmount(200, 0, (lo, hi) => hi);
        int conSkill = Survival.StaminaAmount(200, 100, (lo, hi) => hi);

        Assert.True(conSkill > sinSkill, "la Supervivencia dejó de influir en la energía que vuelve");
        Assert.Equal(Survival.StaminaAmount(200, 0, (lo, hi) => lo),
                     Survival.StaminaAmount(200, 100, (lo, hi) => lo));   // el PISO no se mueve: es el del original
    }
}
