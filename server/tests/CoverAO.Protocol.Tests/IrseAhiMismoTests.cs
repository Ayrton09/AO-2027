using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// ¿SE VA AHÍ MISMO, EN UN MAPA PK? (<c>Bot.SeVaAca</c>) Al vencer la sesión el bot caminaba al mapa seguro más cercano para salir sin
/// la cuenta del servidor, y al volver caminaba de vuelta al coto. Con la vida alta, nada pegado y nada hostil cerca, sale ahí.
/// </summary>
public class IrseAhiMismoTests
{
    [Theory]
    [InlineData(1.0, false, false, true)]
    [InlineData(0.8, false, false, true)]
    [InlineData(0.79, false, false, false)]
    [InlineData(1.0, true, false, false)]
    [InlineData(1.0, false, true, false)]
    public void Se_va_con_la_vida_alta_y_nada_cerca(double salud, bool algoPegado, bool hostilCerca, bool seVa) =>
        Assert.Equal(seVa, Bot.SeVaAca(salud, algoPegado, hostilCerca));
}
