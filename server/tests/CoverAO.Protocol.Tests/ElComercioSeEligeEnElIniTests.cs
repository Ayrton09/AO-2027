using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// A QUIÉN LE VENDE EL MERCADER, OPCIONAL EN EL Server.ini (pedido del autor, 2026-09-27): <c>ComercioEnRinkel</c>,
/// <c>ComercioEnLindos</c>, <c>ComercioEnCiudadesViejas</c> y <c>ComercioEnElResto</c>, con los nombres de las facciones. Sin
/// tocar nada es la regla del original (VB6 Protocol.bas:5203-5220, ver <see cref="ComercioPorFaccionTests"/>), y el bot mira la
/// misma para saber dónde comprar.
/// </summary>
public class ElComercioSeEligeEnElIniTests
{
    private static ServerIni Ini(string cuerpo = "") => ServerIni.Desde(IniFile.Parse("[INIT]\r\n" + cuerpo));

    private static Player Con(byte faccion) => new() { CharIndex = 1, Name = "Prueba", Account = "cuenta", FactionStatus = faccion };

    private static readonly byte[] Todas =
    {
        Player.StatusCriminal, Player.StatusCiudadano, Player.StatusRepublicano, Player.StatusCaos, Player.StatusArmada,
        Player.StatusMilicia,
    };

    /// <summary>Los tres <c>If</c> del original, escritos de nuevo acá para comparar contra ellos.</summary>
    private static bool ElOriginalLeVende(byte faccion, int battleMode) => faccion switch
    {
        Player.StatusCiudadano or Player.StatusArmada or Player.StatusRepublicano or Player.StatusMilicia => battleMode is 2 or 3,
        _ => battleMode == 3,
    };

    [Fact]
    public void Sin_tocar_nada_es_la_del_original()
    {
        var ini = Ini();
        foreach (byte f in Todas)
            foreach (int modo in new[] { 0, 1, 2, 3, 4 })
            {
                bool original = ElOriginalLeVende(f, modo);
                Assert.Equal(original, ini.MercaderLeVende(modo, f));
                Assert.Equal(original, Con(f).MercaderMeAtiende(modo));
                Assert.Equal(original, Con(f).MercaderMeAtiende(modo, ini));
            }
    }

    [Fact]
    public void En_las_ciudades_viejas_se_le_puede_vender_al_ciudadano()
    {
        var ini = Ini("ComercioEnCiudadesViejas=Ciudadano, Armada\r\n");
        Assert.True(Con(Player.StatusCiudadano).MercaderMeAtiende(1, ini));
        Assert.True(ini.MercaderLeVende(1, Player.StatusArmada));
        Assert.False(ini.MercaderLeVende(1, Player.StatusCaos));
        Assert.True(ini.MercaderLeVende(3, Player.StatusCaos));        // lo demás, como estaba
        Assert.False(ini.MercaderLeVende(0, Player.StatusCiudadano));  // el campo es otra zona
    }

    [Fact]
    public void Escrita_y_vacia_no_le_vende_a_nadie()
    {
        var ini = Ini("ComercioEnRinkel=\r\n");
        Assert.All(Todas, f => Assert.False(ini.MercaderLeVende(3, f)));
    }

    [Fact]
    public void Una_faccion_mal_escrita_se_avisa_y_las_otras_valen()
    {
        var ini = Ini("ComercioEnLindos=Ciudadano, Renegao\r\n");
        Assert.Contains("Renegao", ini.FaccionesDesconocidas);
        Assert.True(ini.MercaderLeVende(2, Player.StatusCiudadano));
        Assert.False(ini.MercaderLeVende(2, Player.StatusCriminal));
    }

    [Fact]
    public void Las_claves_se_conocen()
    {
        foreach (var clave in new[] { "ComercioEnRinkel", "ComercioEnLindos", "ComercioEnCiudadesViejas", "ComercioEnElResto" })
            Assert.Contains(clave, ServerIni.ClavesConocidas);
    }

    [Fact]
    public void El_bot_usa_la_misma_regla()
    {
        Assert.False(Bot.ElMercaderLeVende(null, 1, Player.StatusCiudadano));   // Nix sin ini: como el original
        Assert.True(Bot.ElMercaderLeVende(null, 3, Player.StatusCiudadano));
        Assert.True(Bot.ElMercaderLeVende(Ini("ComercioEnCiudadesViejas=Ciudadano\r\n"), 1, Player.StatusCiudadano));
    }
}
