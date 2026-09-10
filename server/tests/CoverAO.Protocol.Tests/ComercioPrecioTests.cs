using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS TRES CUENTAS DEL MERCADER, que se parecen y no son la misma: lo que cobra redondea al par con la
/// cantidad adentro, lo que canta en la vitrina no redondea nada, y lo que paga trunca y con otro divisor.
///
/// <para>Se testean acá porque las tres vivieron sueltas adentro del despachador y las tres se portaron mal
/// alguna vez. Los números esperados salen de leer el <c>Comercio.bas</c>, no de correr el nuestro.</para>
/// </summary>
public class ComercioPrecioTests
{
    // ── Lo que COBRA (VB6 Comercio.bas:89) ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CLng</c> REDONDEA AL PAR. El comentario del original dice que busca el techo —"siempre va a darte
    /// el próximo número"— pero no es lo que hace: con el descuento en 1, un objeto de valor par cuesta lo
    /// que vale y uno de valor impar cuesta uno más. El cast de C# truncaba las dos.
    /// </summary>
    [Theory]
    [InlineData(200, 200)]   // CLng(200,5) -> 200
    [InlineData(7, 8)]       // CLng(7,5)   -> 8
    [InlineData(6, 6)]       // CLng(6,5)   -> 6
    [InlineData(3, 4)]       // CLng(3,5)   -> 4
    [InlineData(10, 10)]     // CLng(10,5)  -> 10
    public void Sin_regateo_el_precio_redondea_al_par(int valor, long esperado) =>
        Assert.Equal(esperado, Comercio.PrecioDeCompra(valor, skillComerciar: 0, cantidad: 1));

    /// <summary>
    /// LA CANTIDAD VA ADENTRO DEL REDONDEO. Redondear una unidad y multiplicar después no es la misma cuenta,
    /// y la diferencia la paga el jugador en cada compra grande.
    /// </summary>
    [Fact]
    public void La_cantidad_entra_antes_de_redondear()
    {
        // Valor 10, Comerciar 3: el original cobra CLng(10/1,03*10 + 0,5) = 98 por las diez.
        Assert.Equal(98, Comercio.PrecioDeCompra(10, skillComerciar: 3, cantidad: 10));
        // Y redondear de a una da 10 por unidad, o sea 100: dos monedas de más, que era lo que se cobraba.
        Assert.NotEqual(Comercio.PrecioDeCompra(10, 3, 1) * 10, Comercio.PrecioDeCompra(10, 3, 10));
    }

    /// <summary>Regatear ABARATA, y la mitad del skill no puede salir más cara que ninguno.</summary>
    [Fact]
    public void Regatear_abarata()
    {
        Assert.True(Comercio.PrecioDeCompra(1000, 100, 1) < Comercio.PrecioDeCompra(1000, 0, 1));
        Assert.Equal(500, Comercio.PrecioDeCompra(1000, 100, 1));   // 1000 / 2
    }

    /// <summary>SIN PISO. Había un <c>Math.Max(1, …)</c> que el original no tiene: le ponía precio a lo que
    /// no vale nada, y un personaje recién nacido no tiene una moneda.</summary>
    [Fact]
    public void Lo_que_no_vale_nada_no_cuesta_nada()
    {
        Assert.Equal(0, Comercio.PrecioDeCompra(0, 0, 1));
        Assert.Equal(0, Comercio.PrecioDeCompra(0, 0, 1000));
    }

    // ── Lo que CANTA en la vitrina (VB6 Comercio.bas:277) ─────────────────────────────────────────────

    /// <summary>La etiqueta NO es la caja: el original manda la división cruda, en coma flotante y sin
    /// redondear. Unificarla con lo que cobra sería inventar.</summary>
    [Fact]
    public void La_vitrina_canta_la_division_cruda()
    {
        Assert.Equal(200f, Comercio.PrecioDeVitrina(200, 0));
        Assert.Equal(80f, Comercio.PrecioDeVitrina(100, 25));
        // Un objeto de valor impar: la vitrina dice 7 y la caja cobra 8.
        Assert.Equal(7f, Comercio.PrecioDeVitrina(7, 0));
        Assert.Equal(8, Comercio.PrecioDeCompra(7, 0, 1));
    }

    // ── Lo que PAGA (VB6 Comercio.bas:158 + SalePrice) ────────────────────────────────────────────────

    /// <summary>
    /// <c>Fix</c> trunca —al revés que el <c>CLng</c> de la compra— y trunca al FINAL, después de multiplicar.
    /// Dividir primero paga de menos en todo lo que no dé exacto.
    /// </summary>
    [Fact]
    public void Lo_que_paga_trunca_recien_al_final()
    {
        var d = GameData.Load();
        if (d.ItemCount == 0) return;
        var queso = d.Obj(28);      // Queso de Cabra, Valor 5
        if (queso is null || queso.Value != 5) return;

        Assert.Equal(3, Comercio.PrecioDeVenta(queso, 2));   // Fix(5/3 * 2) = Fix(3,33) = 3
        Assert.Equal(1, Comercio.PrecioDeVenta(queso, 1));   // Fix(1,66) = 1
        // Dividiendo primero serían 2 por las dos unidades: un punto menos, y crece con la cantidad.
        Assert.NotEqual(Comercio.PrecioDeVenta(queso, 1) * 2, Comercio.PrecioDeVenta(queso, 2));
    }

    /// <summary>El equipo de novato no se vende: son ~1700 unidades por personaje creado, y con un piso de una
    /// moneda cada alta imprimía 1700 de la nada.</summary>
    [Fact]
    public void El_equipo_de_novato_no_se_vende_y_lo_que_no_existe_tampoco()
    {
        var d = GameData.Load();
        if (d.ItemCount == 0) return;
        var manzanaNewbie = d.Obj(573);
        if (manzanaNewbie is not null)
        {
            Assert.True(manzanaNewbie.IsNewbie, "el 573 tendría que ser el del kit de novato");
            Assert.Equal(0, Comercio.PrecioDeVenta(manzanaNewbie, 100));
        }
        Assert.Equal(0, Comercio.PrecioDeVenta(null, 100));
    }
}
