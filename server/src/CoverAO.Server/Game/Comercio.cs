using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

/// <summary>
/// EL <c>Comercio.bas</c> DEL ORIGINAL: qué cobra un mercader y qué paga.
///
/// <para>Existe como archivo propio por el mismo motivo que <see cref="Matematicas"/>: las tres cuentas de
/// acá son parecidas entre sí y NO son la misma, y mientras vivieron sueltas adentro del despachador se
/// portaron mal una tras otra. Lo que se cobra redondea AL PAR con la cantidad adentro; lo que se canta en la
/// vitrina no redondea nada; lo que se paga TRUNCA y con otro divisor. Tenerlas juntas es lo que hace visible
/// que son distintas.</para>
/// </summary>
public static class Comercio
{
    /// <summary>VB6 <c>REDUCTOR_PRECIOVENTA</c>: el mercader paga un tercio de lo que vale.</summary>
    public const int ReductorPrecioVenta = 3;

    /// <summary>
    /// El regateo (VB6 <c>Descuento</c>, Comercio.bas:244): <c>1 + Comerciar / 100</c>.
    ///
    /// <para>Es un <c>Single</c> en el original y acá también. La precisión simple mueve el resultado en el
    /// sexto decimal, y todo esto termina en un redondeo: es justo donde una diferencia así se ve o no se
    /// ve.</para>
    /// </summary>
    public static float Descuento(int skillComerciar) => 1f + skillComerciar / 100f;

    /// <summary>
    /// LO QUE COBRA por <paramref name="cantidad"/> unidades. VB6 Comercio.bas:89:
    /// <c>Precio = CLng((Valor / Descuento * Cantidad) + 0.5)</c>.
    ///
    /// <para><b>La cantidad va ADENTRO del redondeo.</b> Antes se redondeaba el precio de UNA unidad y
    /// después se multiplicaba, que no es la misma cuenta: con Valor 10 y Comerciar 3, diez unidades salen 98
    /// en el original y salían 100.</para>
    ///
    /// <para><b>Y <c>CLng</c> redondea al par, no trunca</b> (<see cref="Matematicas.AEntero"/>). El
    /// comentario del original dice que busca el techo, pero <c>CLng(7.5)</c> da 8 y <c>CLng(6.5)</c> da 6:
    /// los de valor impar cuestan uno más. El cast de C# truncaba las dos.</para>
    ///
    /// <para><b>Y no hay piso.</b> Había un <c>Math.Max(1, …)</c> que el original no tiene, y hacía costar una
    /// moneda a los objetos de Valor 0.</para>
    /// </summary>
    public static long PrecioDeCompra(int valor, int skillComerciar, int cantidad) =>
        Matematicas.AEntero(valor / (double)Descuento(skillComerciar) * cantidad + 0.5);

    /// <summary>
    /// LO QUE CANTA EN LA VITRINA, que NO es lo que cobra: el original manda <c>Valor / Descuento</c> crudo,
    /// en coma flotante y sin redondear (Comercio.bas:277, y viaja con <c>WriteSingle</c>). La etiqueta y la
    /// caja son dos cuentas distintas en el original, y unificarlas sería inventar.
    /// </summary>
    public static float PrecioDeVitrina(int valor, int skillComerciar) => valor / Descuento(skillComerciar);

    /// <summary>
    /// LO QUE PAGA por <paramref name="cantidad"/> unidades. VB6 <c>Precio = Fix(SalePrice(ObjIndex) *
    /// Cantidad)</c> (Comercio.bas:158) con <c>SalePrice</c> (Comercio.bas:306): un objeto que no está en la
    /// data o que es de novato vale 0; el resto, <c>Valor / 3</c>.
    ///
    /// <para><c>Fix</c> TRUNCA —al revés que el <c>CLng</c> de la compra— y trunca al FINAL, después de
    /// multiplicar: con Valor 5 y cantidad 2 el original paga <c>Fix(1,67 × 2) = 3</c>, y dividir antes daba
    /// 2. El cero tampoco tiene piso: el kit de novato son ~1700 unidades de Valor 0 que con un piso de 1
    /// serían 1700 monedas de la nada por personaje creado.</para>
    /// </summary>
    public static long PrecioDeVenta(ObjInfo? o, int cantidad) =>
        o is null || o.IsNewbie ? 0 : (long)System.Math.Floor(o.Value / (double)ReductorPrecioVenta * cantidad);
}
