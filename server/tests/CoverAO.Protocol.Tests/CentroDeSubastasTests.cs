using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS REGLAS DEL CENTRO DE SUBASTAS (regla de la casa, 06/10; ver <see cref="CentroDeSubastas"/>), sin red: el depósito,
/// las ofertas, la compra directa, el vencimiento y lo que se le debe a cada uno.
/// </summary>
public class CentroDeSubastasTests
{
    private const long Ahora = 1_800_000_000;
    private const short Pocion = 38;

    [Theory]
    [InlineData(3, 1, 0, 30)]            // la captura del tester: 3 hs y precio 1 → 30
    [InlineData(36, 1, 0, 360)]
    [InlineData(3, 10_000, 0, 130)]      // + el 1 % del inicial
    [InlineData(3, 10_000, 50_000, 530)] // + el 1 % del final, que es el más alto
    public void El_deposito_es_10_por_hora_mas_el_uno_por_ciento(int horas, long inicial, long final, long esperado) =>
        Assert.Equal(esperado, CentroDeSubastas.Deposito(horas, inicial, final));

    [Theory]
    [InlineData(0, 0, 3)]       // sin precio inicial
    [InlineData(100, 100, 3)]   // el final tiene que superar al inicial
    [InlineData(100, 50, 3)]
    public void Precios_que_no_se_pueden_publicar(long inicial, long final, int horas) =>
        Assert.Equal(ResultadoDeSubasta.PrecioInvalido, new CentroDeSubastas().PuedePublicar("Aldo", inicial, final, horas));

    [Theory]
    [InlineData(1)]
    [InlineData(48)]
    public void Solo_las_duraciones_de_la_lista(int horas) =>
        Assert.Equal(ResultadoDeSubasta.DuracionInvalida, new CentroDeSubastas().PuedePublicar("Aldo", 10, 0, horas));

    [Fact]
    public void Un_vendedor_no_puede_tener_mas_del_tope()
    {
        var c = new CentroDeSubastas();
        for (int i = 0; i < CentroDeSubastas.MaximoPorVendedor; i++) c.Publicar("Aldo", Pocion, 1, 10, 0, 3, Ahora);
        Assert.Equal(ResultadoDeSubasta.DemasiadasSubastas, c.PuedePublicar("aldo", 10, 0, 3));
        Assert.Equal(ResultadoDeSubasta.Ok, c.PuedePublicar("Bruma", 10, 0, 3));
    }

    [Fact]
    public void No_se_oferta_en_la_propia_ni_por_debajo_ni_vencida()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        Assert.Equal(ResultadoDeSubasta.EsTuya, c.PuedeOfertar(s.Id, "ALDO", 500, Ahora));
        Assert.Equal(ResultadoDeSubasta.OfertaBaja, c.PuedeOfertar(s.Id, "Bruma", 99, Ahora));
        Assert.Equal(ResultadoDeSubasta.Ok, c.PuedeOfertar(s.Id, "Bruma", 100, Ahora));   // sin ofertas, alcanza el inicial
        c.Ofertar(s.Id, "Bruma", 100);
        Assert.Equal(ResultadoDeSubasta.OfertaBaja, c.PuedeOfertar(s.Id, "Ciro", 100, Ahora));   // con ofertas, hay que superarla
        Assert.Equal(ResultadoDeSubasta.YaTermino, c.PuedeOfertar(s.Id, "Ciro", 500, s.Termina));
        Assert.Equal(ResultadoDeSubasta.NoExiste, c.PuedeOfertar(999, "Ciro", 500, Ahora));
    }

    [Fact]
    public void Al_superado_se_le_devuelve_su_oferta()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        c.Ofertar(s.Id, "Bruma", 150);
        c.Ofertar(s.Id, "Ciro", 200);

        Assert.Equal(new[] { new EntregaDeSubasta("Bruma", 0, 0, 150) }, c.Pendientes);
        var abierta = c.Buscar(s.Id)!;
        Assert.Equal("Ciro", abierta.Comprador);
        Assert.Equal(200, abierta.PrecioActual);
    }

    [Fact]
    public void Al_vencer_el_objeto_va_al_que_gano_y_el_oro_al_vendedor()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        c.Ofertar(s.Id, "Bruma", 300);

        Assert.Equal(0, c.Vencer(s.Termina - 1));
        Assert.Equal(1, c.Vencer(s.Termina));
        Assert.Empty(c.Abiertas);
        Assert.Equal(new[] { new EntregaDeSubasta("Bruma", Pocion, 5, 0), new EntregaDeSubasta("Aldo", 0, 0, 300) }, c.Pendientes);
    }

    [Fact]
    public void Sin_ofertas_el_objeto_vuelve_al_vendedor()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        c.Vencer(s.Termina);
        Assert.Equal(new[] { new EntregaDeSubasta("Aldo", Pocion, 5, 0) }, c.Pendientes);
    }

    [Fact]
    public void La_compra_directa_paga_el_final_y_cierra()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 1000, 3, Ahora);
        c.Ofertar(s.Id, "Bruma", 150);

        Assert.Equal(ResultadoDeSubasta.Ok, c.PuedeComprar(s.Id, "Ciro", Ahora));
        c.Comprar(s.Id, "Ciro");

        Assert.Empty(c.Abiertas);
        Assert.Equal(new[]
        {
            new EntregaDeSubasta("Bruma", 0, 0, 150),       // la oferta superada
            new EntregaDeSubasta("Ciro", Pocion, 5, 0),
            new EntregaDeSubasta("Aldo", 0, 0, 1000),
        }, c.Pendientes);
    }

    [Fact]
    public void Una_oferta_que_llega_al_final_cuesta_el_final_y_cierra()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 1000, 3, Ahora);
        Assert.Equal(1000, c.CostoDeLaOferta(s.Id, 5000));
        Assert.True(c.Ofertar(s.Id, "Bruma", 5000));
        Assert.Empty(c.Abiertas);
        Assert.Contains(new EntregaDeSubasta("Aldo", 0, 0, 1000), c.Pendientes);
    }

    [Fact]
    public void Sin_precio_final_no_hay_compra_directa()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        Assert.Equal(ResultadoDeSubasta.SinCompraDirecta, c.PuedeComprar(s.Id, "Bruma", Ahora));
    }

    [Fact]
    public void Se_cancela_solo_la_propia_y_sin_ofertas()
    {
        var c = new CentroDeSubastas();
        var s = c.Publicar("Aldo", Pocion, 5, 100, 0, 3, Ahora);
        Assert.Equal(ResultadoDeSubasta.NoEsTuya, c.Cancelar(s.Id, "Bruma"));
        c.Ofertar(s.Id, "Bruma", 100);
        Assert.Equal(ResultadoDeSubasta.TieneOfertas, c.Cancelar(s.Id, "Aldo"));

        var otra = c.Publicar("Aldo", Pocion, 2, 100, 0, 3, Ahora);
        Assert.Equal(ResultadoDeSubasta.Ok, c.Cancelar(otra.Id, "Aldo"));
        Assert.Contains(new EntregaDeSubasta("Aldo", Pocion, 2, 0), c.Pendientes);
    }

    [Fact]
    public void Entregar_saca_solo_lo_que_se_pudo_y_de_ese_personaje()
    {
        var c = new CentroDeSubastas();
        c.Cargar(Array.Empty<Subasta>(), new[]
        {
            new EntregaDeSubasta("Aldo", Pocion, 5, 0), new EntregaDeSubasta("Bruma", 0, 0, 10), new EntregaDeSubasta("aldo", 0, 0, 20),
        });
        // La bóveda de Aldo no tiene lugar para objetos: el oro entra, la poción espera.
        Assert.Equal(1, c.Entregar("Aldo", e => e.Objeto == 0));
        Assert.Equal(new[] { new EntregaDeSubasta("Aldo", Pocion, 5, 0), new EntregaDeSubasta("Bruma", 0, 0, 10) }, c.Pendientes);
    }

    [Fact]
    public void Al_cargar_los_ids_siguen_desde_el_mayor()
    {
        var c = new CentroDeSubastas();
        c.Cargar(new[] { new Subasta(41, "Aldo", Pocion, 1, 10, 0, 0, null, Ahora) }, Array.Empty<EntregaDeSubasta>());
        Assert.Equal(42, c.Publicar("Bruma", Pocion, 1, 10, 0, 3, Ahora).Id);
    }
}
