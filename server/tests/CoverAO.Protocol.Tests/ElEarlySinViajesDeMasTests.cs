using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS VIAJES DE MÁS DEL EARLY (DECIDIDO POR EL AUTOR, 07/10): el envenenado con pociones para pelear no camina a un templo de otro mapa
/// (<c>Bot.VaAlTemploPorElVeneno</c>), y nadie sale de viaje por una pieza de equipo de menos de 1000 (<c>Bot.CuestaComoParaElViaje</c>).
/// </summary>
public class ElEarlySinViajesDeMasTests
{
    /// <summary>Las pociones de vida que el bot quiere para pelear: la constante privada de <c>Bot</c>, leída y no copiada.</summary>
    private static int PocionesParaPelear =>
        (int)typeof(Bot).GetField("PocionesParaPelear", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    [Fact]
    public void Envenenado_con_pociones_no_va_a_un_templo_de_otro_mapa()
    {
        Assert.False(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 200, temploEnEsteMapa: false));
        Assert.False(Bot.VaAlTemploPorElVeneno(pocionesDeVida: PocionesParaPelear, temploEnEsteMapa: false));
    }

    [Fact]
    public void Envenenado_sin_pociones_o_con_el_templo_aca_va()
    {
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: PocionesParaPelear - 1, temploEnEsteMapa: false));
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 0, temploEnEsteMapa: false));
        Assert.True(Bot.VaAlTemploPorElVeneno(pocionesDeVida: 200, temploEnEsteMapa: true));
    }

    [Fact]
    public void Los_escudos_baratos_no_valen_el_viaje_y_la_primera_armadura_si()
    {
        Assert.False(Bot.CuestaComoParaElViaje(300));    // Escudo de Tortuga
        Assert.False(Bot.CuestaComoParaElViaje(500));    // Escudo de Madera
        Assert.False(Bot.CuestaComoParaElViaje(Bot.PrecioQueValeElViaje - 1));
        Assert.True(Bot.CuestaComoParaElViaje(Bot.PrecioQueValeElViaje));
        Assert.True(Bot.CuestaComoParaElViaje(3_000));   // Armadura de Cuero
        Assert.True(Bot.CuestaComoParaElViaje(350, esArma: true));   // Espada Larga
    }

    /// <summary>
    /// EL CABLEADO DEL FILTRO: <c>ValeElViajePorEso</c>, la pregunta del viaje al mercader, con un bot sin nada puesto y plata de sobra.
    /// El escudo barato no vale el viaje; lo que cuesta 1000 o más y lo mejora, sí, y un arma que lo mejora también aunque sea barata (la
    /// Espada Larga a 350 del Herrero de Armas: el autor, 08/10).
    /// </summary>
    [Fact]
    public void El_viaje_al_mercader_no_se_hace_por_un_escudo_barato_y_por_un_arma_barata_si()
    {
        var datos = GameData.Load();
        Assert.True(datos.Objects.Count > 0, "No se cargó gamedata/obj.json");
        var vale = typeof(Bot).GetMethod("ValeElViajePorEso", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entorno = new EntornoDeBots(null!, null!, 0, 0, datos, null!, null!);
        var sentidos = new SentidosDelBot();
        bool Vale(Bot bot, short obj) => (bool)vale.Invoke(bot, new object[] { sentidos, obj, 100_000d })!;

        const short EscudoDeMadera = 997, EspadaLarga = 2, ArmaduraDeCuero = 30, EspadaCorta = 164;
        Assert.True(datos.Obj(EscudoDeMadera)!.Value < Bot.PrecioQueValeElViaje);
        Assert.True(datos.Obj(EspadaLarga)!.Value < Bot.PrecioQueValeElViaje);
        Assert.True(datos.Obj(ArmaduraDeCuero)!.Value >= Bot.PrecioQueValeElViaje);
        Assert.True(datos.Obj(EspadaCorta)!.Value >= Bot.PrecioQueValeElViaje);

        var bots = PobladoDeBots.Nombres.Select(n => new Bot(n, null, entorno, 1)).ToList();
        Assert.All(bots, bot => Assert.False(Vale(bot, EscudoDeMadera)));
        Assert.Contains(bots, bot => Vale(bot, EspadaLarga));
        // El control: sin él el test pasaría igual si ningún bot pudiera usar nada.
        Assert.Contains(bots, bot => Vale(bot, ArmaduraDeCuero));
        Assert.Contains(bots, bot => Vale(bot, EspadaCorta));
    }
}
