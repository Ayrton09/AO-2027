using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class LaFichaSigueAlPersonajeTests
{
    [Theory]
    [InlineData("retirar")]
    [InlineData("cambiar mapa")]
    public void Un_indice_reutilizado_no_hereda_el_nivel_del_personaje_anterior(string cambio)
    {
        var sentidos = new SentidosDelBot();
        var yo = new Player { CharIndex = 500, Name = "Rufo", Account = "rufo", Level = 21 };
        var anterior = new Player { CharIndex = 501, Name = "Elmo", Account = "elmo", Level = 40 };
        sentidos.Recibir(ServerMessages.CharacterCreate(anterior));
        sentidos.Recibir(ServerMessages.CharMsgStatus(anterior, yo, 0));
        Assert.Equal(SentidosDelBot.NivelEscondido, sentidos.NivelDe(501, 60_000));

        sentidos.Recibir(cambio == "retirar" ? ServerMessages.CharacterRemove(501, false) : ServerMessages.ChangeMap(37));
        sentidos.Recibir(ServerMessages.CharacterCreate(new Player { CharIndex = 501, Name = "Nilo", Account = "nilo", Level = 1 }));
        Assert.Null(sentidos.NivelDe(501, 60_000));
    }

    [Fact]
    public void Moverse_o_retirar_a_otro_personaje_no_borra_la_ficha_conocida()
    {
        var sentidos = new SentidosDelBot();
        var yo = new Player { CharIndex = 500, Name = "Rufo", Account = "rufo", Level = 21 };
        var otro = new Player { CharIndex = 501, Name = "Kael", Account = "kael", Level = 20 };
        sentidos.Recibir(ServerMessages.CharacterCreate(otro));
        sentidos.Recibir(ServerMessages.CharMsgStatus(otro, yo, 0));
        sentidos.Recibir(ServerMessages.CharacterMove(501, 51, 50));
        sentidos.Recibir(ServerMessages.CharacterRemove(502, false));
        Assert.Equal((short)20, sentidos.NivelDe(501, 60_000));
    }
}
