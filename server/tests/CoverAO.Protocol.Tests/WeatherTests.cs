using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>OJO: tests de LÓGICA C#, no de paridad VB6 — el original <b>no tiene reloj de clima</b>.
/// `Lloviendo` (Admin.bas:90) es una global que sólo conmuta el comando GM <c>/LLUVIA</c>
/// (Protocol.bas:9282); el login la lee para mandarle el estado al que entra. El reloj es un agregado del
/// port para que el mundo tenga clima sin un GM despierto.
///
/// Los asserts van sobre el comportamiento y no sobre ticks mágicos: los de antes fijaban el ciclo de la
/// demo —llovía la MITAD del tiempo en tandas de 20 segundos— y al hacerlo jugable se caían por el motivo
/// equivocado.</summary>
public class WeatherTests
{
    [Fact]
    public void Llueve_al_principio_del_ciclo_y_despeja_despues()
    {
        int ciclo = Weather.CicloTicks;
        long finDeLluvia = (long)ciclo * Weather.LluviaPorciento / 100;
        Assert.Equal(WeatherState.Lluvia, Weather.At(0));
        Assert.Equal(WeatherState.Lluvia, Weather.At(finDeLluvia - 1));
        Assert.Equal(WeatherState.Despejado, Weather.At(finDeLluvia));
        Assert.Equal(WeatherState.Despejado, Weather.At(ciclo - 1));
        Assert.Equal(WeatherState.Lluvia, Weather.At(ciclo));           // el ciclo vuelve a empezar
    }

    [Fact]
    public void La_lluvia_es_un_evento_no_la_mitad_del_tiempo()
    {
        // Lo que se veía al entrar: lluvia y día/noche alternando cada pocos segundos, con el chat lleno de
        // "Comienza a llover / La lluvia ha cesado". Debe llover una fracción CHICA del tiempo.
        Assert.InRange(Weather.LluviaPorciento, 1, 35);
        long msDelCiclo = (long)Weather.CicloTicks * GameLoop.TickMs;
        Assert.True(msDelCiclo >= 5 * 60_000, $"el ciclo de clima dura {msDelCiclo / 1000}s; se esperan minutos");
    }

    [Fact]
    public void El_porcentaje_de_lluvia_es_configurable()
    {
        int ciclo = Weather.CicloTicks, pct = Weather.LluviaPorciento;
        try
        {
            Weather.CicloTicks = 100; Weather.LluviaPorciento = 10;
            Assert.Equal(WeatherState.Lluvia, Weather.At(0));
            Assert.Equal(WeatherState.Lluvia, Weather.At(9));
            Assert.Equal(WeatherState.Despejado, Weather.At(10));
            Weather.LluviaPorciento = 0;                                 // nunca llueve
            Assert.Equal(WeatherState.Despejado, Weather.At(0));
        }
        finally { Weather.CicloTicks = ciclo; Weather.LluviaPorciento = pct; }
    }

    [Fact]
    public void ClimaByteMatchesTheState()
    {
        Assert.Equal(1, Weather.Clima(WeatherState.Lluvia));
        Assert.Equal(0, Weather.Clima(WeatherState.Despejado));
    }
}
