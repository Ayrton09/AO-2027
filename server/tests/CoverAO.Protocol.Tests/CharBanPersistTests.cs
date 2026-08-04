using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Persistencia del baneo POR PERSONAJE (VB6 flags.Ban, escrito por /BAN). Es un flag distinto del baneo
/// por cuenta: vive en el .chr, así que tiene que sobrevivir un roundtrip a través de CharacterFile.
/// Si no persistiera, un char baneado volvería a entrar en cuanto el server reiniciara.
/// </summary>
public class CharBanPersistTests
{
    private static Player RoundTrip(Player p)
    {
        var ini = CharacterFile.ToIni(p);
        return CharacterFile.FromIni(IniFile.Parse(ini.ToText()), p.CharIndex, p.Name, p.Account);
    }

    private static Player NewChar() => new()
    {
        CharIndex = 1, Name = "Reo", Account = "cuenta", Class = 1, Level = 1,
        MaxHp = 100, Hp = 100, MaxStamina = 100, Stamina = 100,
    };

    [Fact]
    public void Ban_flag_survives_a_save_load_roundtrip()
    {
        var p = NewChar();
        Assert.False(p.Banned);   // por defecto no está baneado

        p.Banned = true;
        var loaded = RoundTrip(p);
        Assert.True(loaded.Banned, "el flag de baneo por personaje tiene que persistir en el .chr");
    }

    [Fact]
    public void Unban_also_persists()
    {
        var p = NewChar();
        p.Banned = true;
        p = RoundTrip(p);
        Assert.True(p.Banned);

        p.Banned = false;
        var loaded = RoundTrip(p);
        Assert.False(loaded.Banned, "desbanear también tiene que persistir");
    }

    /// <summary>Un .chr viejo sin la clave FLAGS/Ban se carga como NO baneado (default seguro).</summary>
    [Fact]
    public void Missing_ban_key_loads_as_not_banned()
    {
        var p = NewChar();
        var ini = CharacterFile.ToIni(p);
        var text = ini.ToText().Replace("Ban=0", "");   // simula un .chr previo a este campo
        var loaded = CharacterFile.FromIni(IniFile.Parse(text), 1, "Reo", "cuenta");
        Assert.False(loaded.Banned);
    }
}
