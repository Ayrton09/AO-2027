using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>OJO: test de LÓGICA C#, no de paridad VB6 — CoverAO <b>no tiene</b> ciclo día/noche. Buscá
/// `Amanecer`, `Anochecer` o `DeNoche` en la fuente y no hay nada: en el original el mundo es siempre de
/// día. Es un modelo inventado para el port, del que el cliente web saca el tinte de la escena.
///
/// Por eso los asserts van sobre el COMPORTAMIENTO (que cicle en orden, que dé la vuelta, que respete la
/// duración configurada) y no sobre ticks mágicos: los números de antes eran los de la demo —125 vueltas,
/// o sea 5 segundos por período— y al subirlos a algo jugable el test se caía por el motivo equivocado.</summary>
public class DayNightTests
{
    [Fact]
    public void Los_cuatro_periodos_ciclan_en_orden_y_dan_la_vuelta()
    {
        long p = DayNight.PeriodTicks;
        Assert.Equal(WorldPeriod.Amanecer, DayNight.PeriodAt(0));
        Assert.Equal(WorldPeriod.Amanecer, DayNight.PeriodAt(p - 1));   // hasta el último tick del período
        Assert.Equal(WorldPeriod.Dia, DayNight.PeriodAt(p));
        Assert.Equal(WorldPeriod.Atardecer, DayNight.PeriodAt(p * 2));
        Assert.Equal(WorldPeriod.Noche, DayNight.PeriodAt(p * 3));
        Assert.Equal(WorldPeriod.Amanecer, DayNight.PeriodAt(p * 4));   // día completo → vuelve a empezar
    }

    [Fact]
    public void Un_dia_completo_dura_lo_que_dice_la_config()
    {
        int previo = DayNight.PeriodTicks;
        try
        {
            DayNight.PeriodTicks = 10;
            Assert.Equal(WorldPeriod.Dia, DayNight.PeriodAt(10));
            Assert.Equal(WorldPeriod.Amanecer, DayNight.PeriodAt(40));
        }
        finally { DayNight.PeriodTicks = previo; }
    }

    [Fact]
    public void El_dia_por_defecto_dura_minutos_no_segundos()
    {
        // La regresión concreta que motivó el cambio: con 125 vueltas por período el mundo amanecía y
        // anochecía mientras cruzabas una plaza, y el chat era una lista de avisos pisándose.
        long msDelDia = (long)DayNight.PeriodTicks * 4 * GameLoop.TickMs;
        Assert.True(msDelDia >= 10 * 60_000, $"un día dura {msDelDia / 1000}s; se esperan minutos");
    }
}
