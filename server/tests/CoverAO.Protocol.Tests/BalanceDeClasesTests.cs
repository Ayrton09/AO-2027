using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE SE CAMBIÓ DEL BALANCE DEL ORIGINAL (decisión del autor, 10/10): `gamedata/Balance.json` ya no es idéntico al `Balance.dat`. Si
/// alguien lo vuelve a generar desde el `.dat`, esto falla.
/// </summary>
public class BalanceDeClasesTests
{
    [Fact]
    public void El_gladiador_ataca_con_armas_a_0_85_y_no_al_0_6_del_original()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "el test necesita la gamedata en disco");
        const int gladiador = 8, guerrero = 3, herrero = 12;
        Assert.Equal(0.85, d.AttackMod[gladiador]);
        Assert.Equal(1.0, d.AttackMod[guerrero]);   // premisa: el resto sigue como el original
        Assert.Equal(0.6, d.AttackMod[herrero]);
    }
}
