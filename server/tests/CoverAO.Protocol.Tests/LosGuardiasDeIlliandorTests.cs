using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS OCHO GUARDIAS DE ILLIANDOR SON REPUBLICANOS (decisión del autor, 30/09). El mapa del original ponía en (59,29) del 194 la ficha
/// 86, el Mago imperial —la misma que cuida Ullathorpe—, entre siete guardias republicanos. En el original no se notaba, porque los
/// guardias no peleaban; con <c>GuardiasAtacan=1</c> ese Mago iba por los republicanos en su propia capital, y los bots
/// republicanos se iban de ahí. Ahora ese spawn es el Mago republicano (117), el de los otros tres Magos de Illiandor.
/// </summary>
public class LosGuardiasDeIlliandorTests
{
    private const int Illiandor = 194, Ullathorpe = 1;
    private const short MagoImperial = 86, MagoRepublicano = 117;

    [Fact]
    public void Todos_los_guardias_de_Illiandor_son_republicanos()
    {
        var d = GameData.Load();
        if (d.MapSpawns.Count == 0) return;
        var guardias = d.MapSpawns[Illiandor].Select(s => d.Npc((short)s.NpcIndex)).Where(n => n?.NpcType == 2).ToList();
        Assert.Equal(8, guardias.Count);
        Assert.All(guardias, n => Assert.Equal(d.Npc(MagoRepublicano)!.Status, n!.Status));
        Assert.Contains(d.MapSpawns[Illiandor], s => s.X == 59 && s.Y == 29 && s.NpcIndex == MagoRepublicano);
    }

    /// <summary>La ficha imperial no se tocó: sigue cuidando Ullathorpe, y ahí sigue yendo por los republicanos.</summary>
    [Fact]
    public void El_Mago_imperial_sigue_en_Ullathorpe_y_en_Illiandor_nadie_va_por_los_republicanos()
    {
        var d = GameData.Load();
        if (d.MapSpawns.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        Assert.True(ini.GuardiasAtacan, "premisa: el ini que se distribuye trae los guardias que atacan");
        Assert.Contains(d.MapSpawns[Ullathorpe], s => s.NpcIndex == MagoImperial);
        Assert.NotEqual(d.Npc(MagoImperial)!.Status, d.Npc(MagoRepublicano)!.Status);
        Assert.False(Bot.HayGuardiaQueVaPor(Illiandor, Player.StatusRepublicano, 23, d, ini));
        Assert.True(Bot.HayGuardiaQueVaPor(Ullathorpe, Player.StatusRepublicano, 23, d, ini));
    }
}
