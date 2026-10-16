using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PRESA QUE MÁS RINDE POR EL TIEMPO QUE CUESTA (<c>Bot.RindeLaPresa</c>): el bot elegía lo que paga más cercano, y en el 13 un
/// Esqueleto (66 de experiencia) a dos pasos le ganaba a un Zombi (295) a diez. Un golpe vale como cuatro pasos.
/// </summary>
public class LaPresaQueMasRindeTests
{
    [Fact]
    public void El_Zombi_a_diez_pasos_rinde_mas_que_el_Esqueleto_a_dos()
    {
        // Con unos 50 por golpe: el Zombi (320 de vida) en 7 golpes, el Esqueleto en 3.
        Assert.True(Bot.RindeLaPresa(295, 10, 7) > Bot.RindeLaPresa(66, 2, 3));
    }

    [Fact]
    public void Entre_dos_iguales_rinde_mas_la_cercana() =>
        Assert.True(Bot.RindeLaPresa(295, 3, 7) > Bot.RindeLaPresa(295, 9, 7));

    [Fact]
    public void La_que_cuesta_muchos_golpes_rinde_menos() =>
        Assert.True(Bot.RindeLaPresa(295, 5, 40) < Bot.RindeLaPresa(115, 5, 3));
}
