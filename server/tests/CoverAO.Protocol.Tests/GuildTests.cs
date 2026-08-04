using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// La ELECCIÓN DE LÍDER de un clan. Es el único lugar del subsistema de clanes con una regla numérica, y es
/// una donde equivocarse sale caro y en silencio: quien gane la elección se queda con el clan.
///
/// El umbral es <b>mayoría absoluta de los miembros</b>, no "el más votado hasta ahora". El recuento corre
/// después de CADA voto, así que con la regla floja el primer voto de la elección instalaría líder al
/// instante — un solo miembro se apodera del clan sin que nadie más llegue a votar. Y "mayoría de los votos
/// emitidos" tiene el mismo agujero, porque al principio los votos emitidos son uno.
///
/// Los flows de clan (guild_admin, guild_offer_details, persist_guild) cubren el camino feliz por el socket;
/// lo que no pueden es empujar el umbral hasta el borde. Eso se hace acá.
/// </summary>
public class GuildTests
{
    [Fact]
    public void El_primer_voto_no_instala_lider()
    {
        // El agujero que motiva todo esto: 1 voto de 5 miembros no es mayoría de nada.
        Assert.Null(Guild.GanadorDeLaEleccion(new[] { "Ana" }, cantidadDeMiembros: 5));
    }

    [Fact]
    public void Con_mayoria_absoluta_gana()
    {
        Assert.Equal("Ana", Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Ana", "Beto" }, 5));
    }

    [Fact]
    public void La_mitad_justa_no_alcanza()
    {
        // 2 de 4 es empate, no mayoría: acá es donde un >= en vez de un > entrega el clan.
        Assert.Null(Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Beto", "Beto" }, 4));
        // Y con 3 de 6 tampoco, aunque sea el más votado.
        Assert.Null(Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Ana", "Beto", "Beto" }, 6));
    }

    [Fact]
    public void Con_padron_impar_la_mayoria_es_la_mitad_mas_uno()
    {
        // 5 miembros: 3 es mayoría, 2 no. (5/2 = 2 en enteros, así que el corte es > 2.)
        Assert.Equal("Ana", Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Ana" }, 5));
        Assert.Null(Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Beto" }, 5));
    }

    [Fact]
    public void Ganar_no_depende_de_que_voten_todos()
    {
        // 4 de 6 alcanza aunque falten dos por votar: la elección se resuelve en cuanto hay mayoría.
        Assert.Equal("Ana", Guild.GanadorDeLaEleccion(new[] { "Ana", "Ana", "Ana", "Ana" }, 6));
    }

    [Fact]
    public void Sin_votos_no_gana_nadie()
    {
        Assert.Null(Guild.GanadorDeLaEleccion(Array.Empty<string>(), 5));
    }

    [Fact]
    public void El_voto_no_distingue_mayusculas()
    {
        // Members y Votes son diccionarios OrdinalIgnoreCase: "ana" y "Ana" son el mismo candidato, o
        // escribir el nombre distinto partiría los votos y nadie llegaría nunca a la mayoría.
        var ganador = Guild.GanadorDeLaEleccion(new[] { "Ana", "ana", "ANA" }, 5);
        Assert.Equal("Ana", ganador, ignoreCase: true);
    }

    [Fact]
    public void Un_clan_de_un_solo_miembro_se_elige_a_si_mismo()
    {
        // Borde del entero: 1/2 = 0, así que un voto alcanza. Es lo correcto — el clan es de esa persona.
        Assert.Equal("Ana", Guild.GanadorDeLaEleccion(new[] { "Ana" }, 1));
    }
}
