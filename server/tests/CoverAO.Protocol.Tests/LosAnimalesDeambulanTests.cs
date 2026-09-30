using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS ANIMALES Y LOS DEMÁS NO HOSTILES DEAMBULAN, NO TE SIGUEN (decisión del autor, 30/09). Es una DIFERENCIA DELIBERADA con el
/// original: CoverAO-2021 renumeró su <c>TipoAI</c> (AI_NPC.bas:32, <c>NpcMaloAtacaUsersBuenos = 0</c>, <c>MueveAlAzar = 3</c>) y a
/// nueve fichas del <c>NPCs.dat</c> les falta la clave <c>Movement</c>: valían 0, que es ir hacia el usuario que ve
/// (<c>IrUsuarioCercano</c>, sin mirar si es hostil). El autor lo vio en las ciudades: "los npc incluido animales te persiguen
/// como si te fueran a atacar". En IAO-Clon las mismas fichas traen <c>Movement=2</c>, que en su numeración es deambular.
/// </summary>
public class LosAnimalesDeambulanTests
{
    private const int MueveAlAzar = 3, VaHaciaElUsuario = 0;

    /// <summary>Comidas y Tabernero de la cuna, el Espíritu, el Pescador, el Gallo, el Cerdo, la Vaca, la Tortuga gigante y el Pato.</summary>
    private static readonly short[] LasNueve = { 42, 43, 45, 58, 65, 66, 67, 79, 97 };

    [Fact]
    public void Las_nueve_fichas_sin_movimiento_deambulan()
    {
        var d = GameData.Load();
        if (d.Npc(65) is null) return;
        Assert.All(LasNueve, i => Assert.Equal(MueveAlAzar, d.Npc(i)!.Movement));
    }

    /// <summary>
    /// Y NINGÚN NO HOSTIL QUE NACE EN EL MUNDO VA HACIA EL USUARIO, salvo los guardias y los de las facciones, a los que la IA del
    /// original saca antes de moverlos (AI_NPC.bas:700: <c>GuardiasCity</c>, <c>Convertidores</c>, <c>facciones</c>).
    /// </summary>
    [Fact]
    public void Ningun_no_hostil_del_mundo_va_hacia_el_usuario()
    {
        var d = GameData.Load();
        if (d.MapSpawns.Count == 0) return;
        var siguen = d.MapSpawns.Values.SelectMany(l => l).Select(s => d.Npc((short)s.NpcIndex))
            .Where(n => n is { Hostile: false, Movement: VaHaciaElUsuario } && n.NpcType is not (2 or 5 or 18))
            .Select(n => $"{n!.Name} ({n.Index})").Distinct().ToList();
        Assert.Empty(siguen);
    }
}
