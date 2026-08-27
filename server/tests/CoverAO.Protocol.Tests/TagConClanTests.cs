using System.Text;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL NOMBRE QUE VE EL RESTO LLEVA EL CLAN. El original arma el tag como <c>"Nick &lt;Clan&gt;"</c>
/// (Modulo_UsUaRiOs.bas:215-223: <c>ClanTag = " &lt;" &amp; GuildName &amp; "&gt;"</c>) y lo manda tanto en
/// <c>UpdateTagAndStatus</c> como en el <c>CharacterCreate</c> del spawn. El cliente lo parte
/// (<c>getTagPosition</c>, General.bas:1324) y dibuja el clan como segundo renglón, 15 px debajo del nick.
///
/// Acá se mandaba el nick pelado, así que el clan no aparecía sobre la cabeza de nadie — y es la señal con
/// la que se reconoce a un aliado de un enemigo antes de que empiece la pelea.
/// </summary>
public class TagConClanTests
{
    private static Player Jugador(string nombre, string clan) =>
        new() { CharIndex = 7, Name = nombre, Account = "cuenta_test", GuildName = clan, ShowName = true };

    /// <summary>El texto que viaja adentro del paquete, para no depender del layout de los otros campos.</summary>
    private static string TextoDelPaquete(byte[] pkt)
    {
        var s = Encoding.GetEncoding(1252).GetString(pkt);
        return s;
    }

    [Fact]
    public void Sin_clan_va_el_nick_pelado()
    {
        var pkt = ServerMessages.UpdateTagAndStatus(Jugador("Rugura", ""));
        var s = TextoDelPaquete(pkt);
        Assert.Contains("Rugura", s);
        Assert.DoesNotContain("<", s);
    }

    [Fact]
    public void Con_clan_el_tag_lleva_el_nombre_del_clan_entre_angulos()
    {
        var pkt = ServerMessages.UpdateTagAndStatus(Jugador("Rugura", "Los Templarios"));
        Assert.Contains("Rugura <Los Templarios>", TextoDelPaquete(pkt));
    }

    /// <summary>Y también en el spawn, que es por donde entra el que aparece en tu pantalla.</summary>
    [Fact]
    public void El_spawn_tambien_lleva_el_clan()
    {
        var p = Jugador("Merlin", "Los Templarios");
        var pkt = ServerMessages.CharacterCreate(p);
        Assert.Contains("Merlin <Los Templarios>", TextoDelPaquete(pkt));
    }

    /// <summary>Un GM con <c>/SHOWNAME</c> apagado no manda NADA, ni nick ni clan (Modulo_UsUaRiOs.bas:309).</summary>
    [Fact]
    public void Sin_showName_no_va_ni_el_clan()
    {
        var p = Jugador("Rugura", "Los Templarios");
        p.ShowName = false;
        var s = TextoDelPaquete(ServerMessages.UpdateTagAndStatus(p));
        Assert.DoesNotContain("Templarios", s);
        Assert.DoesNotContain("Rugura", s);
    }
}
