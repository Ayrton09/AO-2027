using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Persistencia del subsistema DONADOR. El flag premium vive en DOS capas (VB6): el .chr (sección [DONADOR]:
/// flag + créditos) y el .cnt de la cuenta (flag que, al loguear, fuerza donador en el char). Ambos deben
/// sobrevivir un roundtrip; si no, un donador perdería su estado al reiniciar el server.
/// </summary>
public class DonadorPersistTests
{
    private static Player RoundTripChar(Player p)
    {
        var ini = CharacterFile.ToIni(p);
        return CharacterFile.FromIni(IniFile.Parse(ini.ToText()), p.CharIndex, p.Name, p.Account);
    }

    private static Account RoundTripAccount(Account a)
    {
        var ini = AccountFile.ToIni(a);
        return AccountFile.FromIni(IniFile.Parse(ini.ToText()), a.Name);
    }

    private static Player NewChar() => new()
    {
        CharIndex = 1, Name = "Reo", Account = "cuenta", Class = 1, Level = 1,
        MaxHp = 100, Hp = 100, MaxStamina = 100, Stamina = 100,
    };

    [Fact]
    public void Char_donor_flag_and_credit_survive_a_roundtrip()
    {
        var p = NewChar();
        Assert.False(p.Donador);
        Assert.Equal(0, p.CreditoDonador);

        p.Donador = true;
        p.CreditoDonador = 180;
        var loaded = RoundTripChar(p);
        Assert.True(loaded.Donador, "el flag donador del char tiene que persistir en el .chr [DONADOR]");
        Assert.Equal(180, loaded.CreditoDonador);
    }

    [Fact]
    public void Account_donor_flag_survives_a_roundtrip()
    {
        var a = new Account { Name = "cuenta" };
        Assert.False(a.Donador);

        a.Donador = true;
        var loaded = RoundTripAccount(a);
        Assert.True(loaded.Donador, "el flag donador de la cuenta tiene que persistir en el .cnt");

        loaded.Donador = false;
        Assert.False(RoundTripAccount(loaded).Donador, "quitar el donador también persiste");
    }

    /// <summary>Un .chr viejo sin la sección [DONADOR] se carga como NO donador con 0 créditos (default seguro).</summary>
    [Fact]
    public void Missing_donor_section_loads_as_not_donor()
    {
        var p = NewChar();
        var text = CharacterFile.ToIni(p).ToText()
            .Replace("Donador=0", "").Replace("Puntos=0", "");   // simula un .chr previo a este campo
        var loaded = CharacterFile.FromIni(IniFile.Parse(text), 1, "Reo", "cuenta");
        Assert.False(loaded.Donador);
        Assert.Equal(0, loaded.CreditoDonador);
    }
}
