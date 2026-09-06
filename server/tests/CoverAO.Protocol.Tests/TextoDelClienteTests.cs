using System;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE EL CLIENTE ESCRIBE Y EL SERVIDOR GUARDA O DIFUNDE (auditoría del 2026-09-06).
///
/// Tres compuertas que faltaban, las tres del mismo tipo: un campo cuyo contenido y cuyo largo los elige
/// entero el que manda el paquete. El tope de cadena vive en el lector —no en cada handler— porque el
/// dispatcher lee ciento ocho y la tanda anterior le puso techo a los cuatro textos del panel del clan y se
/// le pasaron los MISMOS cuatro por el paquete de fundar.
/// </summary>
public class TextoDelClienteTests
{
    private static ReadOnlyMemory<byte> CadenaDe(int largo)
    {
        var w = new ByteQueueWriter();
        w.WriteAsciiString(new string('x', largo));
        return w.ToArray();
    }

    [Fact]
    public void Una_cadena_por_encima_del_tope_no_se_lee()
    {
        // Justo en el tope entra…
        var justo = new ByteQueueReader(CadenaDe(2048), maxStringLength: 2048);
        Assert.Equal(2048, justo.ReadAsciiString().Length);

        // …y uno más ya no. La excepción tiene que ser la SUYA: si fuera NotEnoughData, la sesión se quedaría
        // esperando bytes que nunca faltaron en vez de cortar.
        var pasado = new ByteQueueReader(CadenaDe(2049), maxStringLength: 2048);
        Assert.Throws<StringTooLongException>(() => pasado.ReadAsciiString());

        // CONTROL: sin tope (0 = el default) la MISMA cadena se lee entera, así que lo que corta es la regla
        // y no que el buffer se quede corto.
        var sinTope = new ByteQueueReader(CadenaDe(2049));
        Assert.Equal(2049, sinTope.ReadAsciiString().Length);
    }

    [Fact]
    public void El_tope_se_mira_antes_de_pedir_los_bytes()
    {
        // Un largo declarado enorme con el cuerpo TODAVÍA sin llegar: el caso real de un cliente que anuncia
        // treinta mil caracteres. Sin el orden correcto esto sería "faltan datos" —esperar— en vez de cortar.
        var w = new ByteQueueWriter();
        w.WriteInteger(30000);
        var r = new ByteQueueReader(w.ToArray(), maxStringLength: 2048);
        Assert.Throws<StringTooLongException>(() => r.ReadAsciiString());
    }

    [Theory]
    [InlineData("Los Pibes")]
    [InlineData("a")]
    [InlineData("Clan De Prueba")]
    public void El_nombre_de_clan_valido_pasa(string nombre) => Assert.True(Naming.IsValidGuildName(nombre));

    [Theory]
    [InlineData("")]                     // vacío
    [InlineData("Clan123")]              // dígitos: el original sólo acepta letras
    [InlineData("Clan<GM>")]             // el nombre viaja DENTRO del nombre del personaje, entre <>
    [InlineData("Clan\0Otro")]           // NUL: las listas de clanes viajan separadas por NUL
    [InlineData("Clan\nOtro")]           // salto de línea
    [InlineData("Clan_Guion")]           // guión bajo: lo acepta una CUENTA, no un clan
    [InlineData("Clán")]                 // acento (el LCase$ del original lo deja fuera de a-z)
    public void El_nombre_de_clan_invalido_no_pasa(string nombre) => Assert.False(Naming.IsValidGuildName(nombre));

    [Fact]
    public void El_nombre_de_clan_tiene_largo_maximo()
    {
        Assert.True(Naming.IsValidGuildName(new string('a', Naming.MaxGuildNameLength)));
        Assert.False(Naming.IsValidGuildName(new string('a', Naming.MaxGuildNameLength + 1)));
    }

    [Fact]
    public void La_cola_de_aspirantes_de_un_clan_tiene_fondo()
    {
        var g = new Guild { Name = "Los Probadores", LeaderName = "Rugura" };
        Assert.False(g.AspirantesLleno);
        for (int i = 0; i < GameLimits.MaxAspirantes - 1; i++) g.JoinRequests["aspirante" + i] = "quiero entrar";
        Assert.False(g.AspirantesLleno);          // CONTROL: con uno menos todavía entra una solicitud
        g.JoinRequests["elultimo"] = "quiero entrar";
        Assert.True(g.AspirantesLleno);
    }

    [Fact]
    public void El_registro_de_clanes_tiene_fondo()
    {
        var world = new World();
        Assert.False(world.RegistroDeClanesLleno);
        var lista = new List<Guild>();
        for (int i = 0; i < GameLimits.MaxClanes; i++) lista.Add(new Guild { Name = "clan" + i, LeaderName = "x" });
        world.LoadGuilds(lista);
        Assert.Equal(GameLimits.MaxClanes, world.GuildCount);
        Assert.True(world.RegistroDeClanesLleno);
        // CONTROL: uno menos y todavía se puede fundar (el tope es el mil, no "muchos").
        world.LoadGuilds(lista.GetRange(0, GameLimits.MaxClanes - 1));
        Assert.False(world.RegistroDeClanesLleno);
    }
}
