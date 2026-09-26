using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS PELEAS QUE NO SE GANAN. El original le resta la defensa de la criatura al golpe sin piso (SistemaCombate.bas:465), así
/// que el golpe que no la pasa la CURA, y un bot con un arma floja se quedaba peleando mientras le duraran las pociones: un
/// Ladrón de nivel 9 con la Daga de novato, 19 minutos seguidos contra los Goblins del mapa 12 en `oficios11`; una Ladrona de
/// nivel 12, 6 minutos contra un Esqueleto guerrero del 24 en `oficios14`. Ahora juzga la pelea con lo que le va sacando
/// (<see cref="Bot.NoLaVaAMatar"/>), que es el número que el servidor le pone sobre la cabeza a cada golpe
/// (<see cref="SentidosDelBot.TomarLoQuePego"/>).
/// </summary>
public class LasPeleasQueNoSeGananTests
{
    private const short DagaDeNovato = 460, Lobo = 501, Goblin = 505, EsqueletoGuerrero = 508;

    /// <summary>El golpe más fuerte con la Daga de novato, con la cuenta del servidor (<see cref="Combat.CalcularDano"/>).</summary>
    private static long ElGolpeMasFuerte(GameData d, byte clase, int golpeDelNivel, int fuerza)
    {
        var daga = d.Obj(DagaDeNovato)!;
        return Combat.CalcularDano(daga.MaxHit, daga.MaxHit, golpeDelNivel, fuerza, d.WeaponMod.TryGetValue(clase, out var m) ? m : 1.0);
    }

    /// <summary>LA PREMISA, con la data y la cuenta del servidor: el Ladrón de `oficios11` (nivel 9, golpe 9 a 10, Fuerza 13, del
    /// .chr) no le saca vida al Goblin ni con su mejor golpe.</summary>
    [Fact]
    public void Con_la_daga_de_novato_un_ladron_de_nivel_9_no_le_saca_vida_al_goblin()
    {
        var d = GameData.Load();
        if (d.Npc(Goblin) is not { } goblin || d.Obj(DagaDeNovato) is null) return;
        Assert.True(ElGolpeMasFuerte(d, GameLimits.ClaseLadron, golpeDelNivel: 10, fuerza: 13) - goblin.Def <= 0);
    }

    /// <summary>Diez golpes que no le sacaron nada alcanzan para dejarla; con nueve todavía no se juzga.</summary>
    [Fact]
    public void Diez_golpes_que_no_le_sacan_nada_alcanzan_para_dejarla()
    {
        var d = GameData.Load();
        if (d.Npc(Goblin) is not { } goblin) return;
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, dano: -14, goblin.MaxHp));
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, dano: 0, goblin.MaxHp));
        Assert.False(Bot.NoLaVaAMatar(golpes: 9, dano: -14, goblin.MaxHp));
    }

    /// <summary>LA QUE SE MATA SE SIGUE PELEANDO: una Ladrona de nivel 18 con la Daga de novato le pega al Lobo de 19 a 22 contra
    /// defensa 8. Aunque erre la mitad de los golpes, le quedan unos pocos más.</summary>
    [Fact]
    public void La_que_se_mata_en_pocos_golpes_se_sigue_peleando()
    {
        var d = GameData.Load();
        if (d.Npc(Lobo) is not { } lobo) return;
        Assert.False(Bot.NoLaVaAMatar(golpes: 10, dano: 5 * 11, lobo.MaxHp));
    }

    /// <summary>LA QUE PIDE DEMASIADOS GOLPES SE DEJA: la misma Ladrona contra el Esqueleto guerrero (defensa 19) saca de 0 a 3 por
    /// golpe contra 200 de vida.</summary>
    [Fact]
    public void La_que_pide_demasiados_golpes_se_deja()
    {
        var d = GameData.Load();
        if (d.Npc(EsqueletoGuerrero) is not { } esqueleto) return;
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, dano: 15, esqueleto.MaxHp));
    }
}
