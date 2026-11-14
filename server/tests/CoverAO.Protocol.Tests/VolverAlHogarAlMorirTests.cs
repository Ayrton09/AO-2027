using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// VOLVER AL HOGAR AL MORIR (regla de la casa <c>VolverAlHogarAlMorir</c>, el tester y el autor, 13/11): el muerto que acepta la
/// pregunta cae en la baldosa de los muertos de su hogar, como con la runa. Ni vivo, ni preso, ni a la cuna si ya no es novato.
/// </summary>
public class VolverAlHogarAlMorirTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);

    private static Player Muerto(byte hogar, int nivel = 30) =>
        new() { CharIndex = 1, Account = "fantasma", Name = "Fantasma", Level = (byte)nivel, Hp = 0, MaxHp = 100, HomeCity = hogar };

    [Fact]
    public void El_muerto_vuelve_a_la_baldosa_de_los_muertos_de_su_hogar()
        => Assert.Equal(Cities.DeadTile(Cities.Nix), PacketDispatcher.DondeVuelveElMuerto(Muerto(Cities.Nix), Datos.Value));

    [Fact]
    public void El_vivo_no_vuelve()
    {
        var p = Muerto(Cities.Nix); p.Hp = 50;
        Assert.Null(PacketDispatcher.DondeVuelveElMuerto(p, Datos.Value));
    }

    [Fact]
    public void El_preso_no_vuelve()
    {
        var p = Muerto(Cities.Nix); p.JailMinutesLeft = 10;
        Assert.Null(PacketDispatcher.DondeVuelveElMuerto(p, Datos.Value));
    }

    [Fact]
    public void A_la_cuna_vuelve_el_novato_y_el_graduado_no()
    {
        Assert.Equal(Cities.DeadTile(Cities.DungeonNewbie), PacketDispatcher.DondeVuelveElMuerto(Muerto(Cities.DungeonNewbie, 5), Datos.Value));
        Assert.Null(PacketDispatcher.DondeVuelveElMuerto(Muerto(Cities.DungeonNewbie, 30), Datos.Value));
    }
}
