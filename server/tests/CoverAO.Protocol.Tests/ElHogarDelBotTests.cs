using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HOGAR DEL BOT, DONDE LE VENDEN (29/09). En `tardeM1` doce de los dieciocho bots de nivel 16 a 28 no encontraban dónde comprar
/// pociones, con decenas de miles de monedas: cazaban del lado de Nix y de Ullathorpe, donde nadie le vende a un ciudadano, y la
/// runa —que es la vuelta a casa— sólo se usaba en un mapa seguro, y para cuatro de ellos la casa era Nix. El bot pone el hogar en
/// una ciudad donde le venden, no se gradúa adentro del dungeon (que lo manda a Nix) y, para ir a comprar, camina hasta un mapa
/// seguro y usa la runa.
/// </summary>
public class ElHogarDelBotTests
{
    [Fact]
    public void Rinkel_puede_ser_el_hogar_de_cualquiera_y_las_ciudades_de_un_bando_solo_de_los_suyos()
    {
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusCiudadano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusCiudadano), "Nix, del imperio");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusRepublicano), "Illiandor, de la república");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusCiudadano));
        Assert.False(PacketDispatcher.PuedeSerHogarDe(37, Player.StatusCiudadano), "el dungeon de novatos no es una ciudad");
    }

    /// <summary>Una vez, y otra al dejar de ser novato: graduarse adentro del dungeon le cambia el hogar a Nix.</summary>
    [Theory]
    // nivel con el que lo puso (0: nunca) | nivel ahora | ¿lo vuelve a pedir?
    [InlineData(0, 5, true)]
    [InlineData(5, 10, false)]
    [InlineData(13, 15, false)]
    [InlineData(13, 16, true)]
    [InlineData(16, 20, false)]
    public void El_hogar_se_pide_una_vez_y_otra_al_dejar_de_ser_novato(int nivelDelHogar, int nivel, bool loPide)
    {
        Assert.Equal(loPide, Bot.VuelveAPonerElHogar(nivelDelHogar, nivel));
    }

    [Fact]
    public void En_el_ultimo_nivel_de_novato_no_se_caza_en_el_dungeon()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(37)), "la cuna, en el último nivel de novato");
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(208)), "el otro mapa de novatos");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel - 1, d.MapZone(37)), "antes, sí");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(20)), "afuera del dungeon, sí");
    }

    /// <summary>Y en Rinkel le venden a un ciudadano, y al sacerdote se llega desde la entrada de la cuna —por donde el bot entra a
    /// comprar— abriendo una puerta del templo: sin abrirla, a cinco baldosas no se llega, y el /HOGAR pide cinco o menos.</summary>
    [Fact]
    public void En_Rinkel_le_venden_y_al_sacerdote_se_llega_abriendo_el_templo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.ElMercaderLeVende(null, d.MapBattleMode(20), Player.StatusCiudadano));
        Assert.False(Bot.ElMercaderLeVende(null, d.MapBattleMode(34), Player.StatusCiudadano), "premisa: en Nix no le venden");
        var nav = new Navegante(d);
        var templo = d.MapSpawns[20].First(s => d.Npc((short)s.NpcIndex)?.NpcType == 1);
        var llegada = d.MapExits.First(e => e.Key.Map == 37 && e.Value.DestMap == 20).Value;
        var desde = ((byte)llegada.DestX, (byte)llegada.DestY);
        Assert.Null(nav.HayComoLlegar(20, desde, ((byte)templo.X, (byte)templo.Y), 5));
        Assert.True(d.MapObjects[20].Any(o => d.Obj((short)o.ObjIndex) is { IsDoor: true }
                                          && nav.AbrirlaDaCamino(20, desde, ((byte)o.X, (byte)o.Y), f => d.Npc((short)f)?.NpcType == 1)),
            "ninguna puerta de Rinkel da camino al sacerdote");
    }
}
