using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El techo de paquetes por segundo de una conexión. El reloj entra por parámetro, así que estos casos no
/// dependen de esperar ni de la carga de la máquina: se le dice qué hora es y se mira qué contesta.
/// </summary>
public class LimiteDeCadenciaTests
{
    [Fact]
    public void La_rafaga_se_perdona_entera_y_de_una()
    {
        // El tráfico legítimo llega a tirones: el cliente junta varias acciones en un segmento TCP. Si la
        // ráfaga no se perdonara, un jugador con lag se comería la desconexión por jugar normal.
        var l = new LimiteDeCadencia(porSegundo: 50, rafaga: 200, ahoraMs: 0);
        for (int i = 0; i < 200; i++) Assert.True(l.Permite(0), $"el paquete {i} de la ráfaga tendría que pasar");
        Assert.False(l.Permite(0));   // el 201 ya no
    }

    [Fact]
    public void El_ritmo_sostenido_es_el_techo()
    {
        var l = new LimiteDeCadencia(porSegundo: 50, rafaga: 50, ahoraMs: 0);
        for (int i = 0; i < 50; i++) Assert.True(l.Permite(0));
        Assert.False(l.Permite(0));
        // Un segundo después el cubo está lleno otra vez, pero ni un paquete más que el techo.
        for (int i = 0; i < 50; i++) Assert.True(l.Permite(1000), $"tras un segundo, el paquete {i} tendría que pasar");
        Assert.False(l.Permite(1000));
    }

    [Fact]
    public void Un_jugador_normal_no_lo_toca_ni_de_lejos()
    {
        // 15 paquetes por segundo durante un minuto: caminar a cadencia (~7/s) más clics, chat y hechizos.
        var l = new LimiteDeCadencia(porSegundo: 50, rafaga: 200, ahoraMs: 0);
        for (int seg = 0; seg < 60; seg++)
            for (int i = 0; i < 15; i++)
                Assert.True(l.Permite(seg * 1000L + i * 66), $"segundo {seg}, paquete {i}");
        Assert.Equal(15, l.Pico);
    }

    [Fact]
    public void El_que_inunda_cae_y_no_se_recupera_solo()
    {
        // Mil por segundo, que es lo que hace una consola del navegador en un `for`.
        var l = new LimiteDeCadencia(porSegundo: 50, rafaga: 200, ahoraMs: 0);
        int pasaron = 0;
        for (int i = 0; i < 1000; i++) if (l.Permite(i)) pasaron++;   // 1000 paquetes en 1 s
        // Pasan la ráfaga más lo que el goteo repuso en ese segundo; el resto rebota.
        Assert.InRange(pasaron, 200, 260);
        Assert.True(l.Pico >= 900, $"el pico tendría que reflejar la inundación, y dio {l.Pico}");
    }

    [Fact]
    public void Con_cero_no_hay_limite_pero_se_sigue_midiendo()
    {
        // El host que lo apaga no pierde la medición: el pico es lo que después le permite elegir un número.
        var l = new LimiteDeCadencia(porSegundo: 0, rafaga: 200, ahoraMs: 0);
        for (int i = 0; i < 5000; i++) Assert.True(l.Permite(0));
        Assert.Equal(5000, l.Pico);
    }

    [Fact]
    public void El_pico_es_por_segundo_de_reloj_y_no_acumulado()
    {
        var l = new LimiteDeCadencia(porSegundo: 0, rafaga: 10, ahoraMs: 0);
        for (int i = 0; i < 30; i++) l.Permite(0);        // 30 en el primer segundo
        for (int i = 0; i < 10; i++) l.Permite(5000);     // 10 en otro segundo, más tarde
        Assert.Equal(30, l.Pico);
    }
}
