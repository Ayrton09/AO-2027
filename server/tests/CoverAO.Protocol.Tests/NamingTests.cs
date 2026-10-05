using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Reglas de nombre. La distinción entre CUENTA y PERSONAJE no es cosmética: el <c>CharacterCreate</c> de un
/// NPC manda su NÚMERO de ficha en el campo del nombre, y el cliente distingue NPC de usuario preguntando si
/// ese nombre es numérico (VB6 <c>IsNumeric</c>, Protocol.bas:1733). Que el original prohíba los dígitos en el
/// nombre de personaje (<c>ValidarNombre</c>, TCP.bas:181) es lo que hace seguro ese truco.
/// </summary>
public class NamingTests
{
    [Theory]
    [InlineData("Rugura")]
    [InlineData("Don Pepito")]      // el espacio interno sí vale
    [InlineData("Ayrton09")]        // con números, si empieza con letra (el autor, 05/10)
    [InlineData("Rug500")]
    [InlineData("Don Pepito 2")]
    public void Nombre_de_personaje_valido(string name) => Assert.True(Naming.IsValidCharName(name));

    [Theory]
    [InlineData("500")]             // el peligroso: se renderizaría como la criatura 500
    [InlineData("1Rugura")]         // empieza con un número
    [InlineData("1e5")]             // y no puede parecer un número, ni en notación científica
    [InlineData("Rug_ura")]         // el guión bajo lo acepta la CUENTA, no el personaje
    [InlineData("")]
    [InlineData(" Rugura")]         // espacio al borde
    [InlineData("Rug  ura")]        // espacio doble
    public void Nombre_de_personaje_invalido(string name) => Assert.False(Naming.IsValidCharName(name));

    [Fact]
    public void La_cuenta_sigue_admitiendo_digitos_y_guion_bajo()
    {
        // Los flows y el fixture usan "cuenta_test"; el VB6 valida las cuentas con otra regla (AsciiValidos),
        // más laxa que la del personaje. Este test es el que impide "arreglar" de más.
        Assert.True(Naming.IsValidName("cuenta_test"));
        Assert.True(Naming.IsValidName("cuenta2"));
        Assert.False(Naming.IsValidCharName("cuenta_test"));
    }

    [Fact]
    public void El_personaje_tope_en_18_como_el_VB6()
    {
        // ValidarNombre: `If Len(Nombre) < 1 Or Len(Nombre) > 18`. La CUENTA admite hasta 30 (MaxNameLength).
        Assert.True(Naming.IsValidCharName(new string('A', 18)));
        Assert.False(Naming.IsValidCharName(new string('A', 19)));
        Assert.True(Naming.IsValidName(new string('a', 19)));   // la cuenta no se toca
    }

    [Theory]
    // ValidarCabeza (TCP.bas:125-178): el rango depende de raza+género y los tramos son DISJUNTOS.
    [InlineData(1, 1, 1, true)]     [InlineData(1, 1, 30, true)]    [InlineData(1, 1, 31, false)]
    [InlineData(1, 2, 70, true)]    [InlineData(1, 2, 80, true)]    [InlineData(1, 2, 1, false)]   // el bug: mujer con cara de varón
    [InlineData(2, 1, 101, true)]   [InlineData(6, 1, 514, true)]   [InlineData(6, 2, 574, false)]
    [InlineData(1, 1, 0, false)]    [InlineData(7, 1, 1, false)]    // raza inexistente
    public void La_cara_se_valida_por_raza_y_genero(int raza, int genero, int cara, bool vale) =>
        Assert.Equal(vale, CoverAO.Server.Game.Equipment.IsValidHead((byte)raza, (byte)genero, cara));

    [Fact]
    public void Un_nombre_numerico_no_puede_hacerse_pasar_por_criatura()
    {
        // El caso que motiva todo: sin este gate, un jugador llamado "503" saldría en el cliente con el nombre,
        // el color y la regla de etiqueta del Esqueleto.
        Assert.False(Naming.IsValidCharName("503"));
        Assert.True(Naming.IsValidCharName("Esqueleto"));   // el NOMBRE de la criatura sí es un nombre válido
    }
}
