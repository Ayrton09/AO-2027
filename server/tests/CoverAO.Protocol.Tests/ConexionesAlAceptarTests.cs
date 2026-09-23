using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS DOS CUPOS AL ACEPTAR UNA CONEXIÓN (VB6 wskapiAO.bas:501-552). El guardia dice cuál de los dos frenó al que llega,
/// porque el original los trata distinto: el servidor lleno (<c>MaxUsers</c>) se avisa con un cartel y el tope por IP
/// (<c>aDos.MaxConexiones</c>) corta callado. El global se mira primero, como el <c>NextOpenUser</c> de allá.
/// </summary>
public class ConexionesAlAceptarTests
{
    [Fact]
    public void Con_los_lugares_ocupados_el_servidor_esta_lleno()
    {
        var guardia = new ConnectionGuard(maxTotal: 2, maxPerIp: 10);

        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("1.1.1.1"));
        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("2.2.2.2"));
        Assert.Equal(ConnectionGuard.Reserva.ServidorLleno, guardia.Reservar("3.3.3.3"));
    }

    [Fact]
    public void Con_lugar_pero_la_ip_en_su_tope_corta_por_la_ip()
    {
        var guardia = new ConnectionGuard(maxTotal: 10, maxPerIp: 1);

        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("1.1.1.1"));
        Assert.Equal(ConnectionGuard.Reserva.TopeDeLaIp, guardia.Reservar("1.1.1.1"));
        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("2.2.2.2"));
    }

    /// <summary>Con los dos cupos agotados manda el global, el que lleva cartel.</summary>
    [Fact]
    public void Con_los_dos_cupos_agotados_el_servidor_esta_lleno()
    {
        var guardia = new ConnectionGuard(maxTotal: 1, maxPerIp: 1);

        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("1.1.1.1"));
        Assert.Equal(ConnectionGuard.Reserva.ServidorLleno, guardia.Reservar("1.1.1.1"));
    }

    [Fact]
    public void Un_rechazo_no_toma_lugar_y_al_liberar_se_vuelve_a_entrar()
    {
        var guardia = new ConnectionGuard(maxTotal: 1, maxPerIp: 10);
        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("1.1.1.1"));
        Assert.Equal(ConnectionGuard.Reserva.ServidorLleno, guardia.Reservar("2.2.2.2"));
        Assert.Equal(ConnectionGuard.Reserva.ServidorLleno, guardia.Reservar("2.2.2.2"));

        guardia.Release("1.1.1.1");

        Assert.Equal(ConnectionGuard.Reserva.Hecha, guardia.Reservar("2.2.2.2"));
        Assert.False(guardia.TryAcquire("3.3.3.3"));
    }

    [Fact]
    public void Los_carteles_son_los_del_original()
    {
        Assert.Equal("El servidor se encuentra lleno en este momento. Disculpe las molestias ocasionadas.",
                     ConnectionGuard.AvisoServidorLleno);
        Assert.Equal("Su IP se encuentra bloqueada en este servidor.", ConnectionGuard.AvisoIpBloqueada);
    }
}
