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
    private const short DagaDeNovato = 460, DagaMasUno = 165, Spatha = 569, Lobo = 501, Zombie = 502, Goblin = 505, EsqueletoGuerrero = 508;

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
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, entraron: 7, dano: -14, goblin.MaxHp));
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, entraron: 4, dano: 0, goblin.MaxHp));
        Assert.False(Bot.NoLaVaAMatar(golpes: 9, entraron: 7, dano: -14, goblin.MaxHp));
    }

    /// <summary>LA QUE SE MATA SE SIGUE PELEANDO: una Ladrona de nivel 18 con la Daga de novato le pega al Lobo de 19 a 22 contra
    /// defensa 8. Aunque erre la mitad de los golpes, le quedan unos pocos más.</summary>
    [Fact]
    public void La_que_se_mata_en_pocos_golpes_se_sigue_peleando()
    {
        var d = GameData.Load();
        if (d.Npc(Lobo) is not { } lobo) return;
        Assert.False(Bot.NoLaVaAMatar(golpes: 10, entraron: 5, dano: 5 * 11, lobo.MaxHp));
    }

    /// <summary>LA QUE PIDE DEMASIADOS GOLPES SE DEJA: la misma Ladrona contra el Esqueleto guerrero (defensa 19) saca de 0 a 3 por
    /// golpe contra 200 de vida.</summary>
    [Fact]
    public void La_que_pide_demasiados_golpes_se_deja()
    {
        var d = GameData.Load();
        if (d.Npc(EsqueletoGuerrero) is not { } esqueleto) return;
        Assert.True(Bot.NoLaVaAMatar(golpes: 10, entraron: 7, dano: 15, esqueleto.MaxHp));
    }

    /// <summary>LA LENTA SE SIGUE PELEANDO: el Leñador de `pelea_b3`, con el Martillo de novato, le había sacado 84 de 200 al
    /// Esqueleto guerrero en 22 golpes. Lo mataba en unos 52; con el tope en 50 lo dejó y salió del mapa donde tala.</summary>
    [Fact]
    public void La_que_se_mata_despacio_se_sigue_peleando()
    {
        var d = GameData.Load();
        if (d.Npc(EsqueletoGuerrero) is not { } esqueleto) return;
        Assert.False(Bot.NoLaVaAMatar(golpes: 22, entraron: 11, dano: 84, esqueleto.MaxHp));
    }

    /// <summary>
    /// LO QUE SE VE CON SUS GOLPES (ver <see cref="Bot.GolpeMedido"/>): Lira, Ladrona de nivel 12 con la Daga +1 (golpe del nivel 12 a
    /// 13 y Fuerza 21, del .chr de `early31`), pegándole a los Lobos (defensa 8), uno de cada dos golpes adentro. Con esos golpes sabe
    /// que al Esqueleto guerrero (defensa 19, 200 de vida) no lo mata —murió peleándolo en `early24` y en `early31`— y al Lobo sí.
    /// </summary>
    [Fact]
    public void Con_lo_que_le_pego_a_los_lobos_sabe_que_al_esqueleto_guerrero_no_lo_mata()
    {
        var d = GameData.Load();
        if (d.Npc(Lobo) is not { } lobo || d.Npc(EsqueletoGuerrero) is not { } esqueleto || d.Obj(DagaMasUno) is not { } daga) return;
        double mod = d.WeaponMod.TryGetValue(GameLimits.ClaseLadron, out var m) ? m : 1.0;
        var golpe = new Bot.GolpeMedido();
        for (int vuelta = 0; vuelta < 2; vuelta++)
            for (int tirada = daga.MinHit; tirada <= daga.MaxHit; tirada++)
                for (int delNivel = 12; delNivel <= 13; delNivel++)
                {
                    long crudo = Combat.CalcularDano(tirada, daga.MaxHit, delNivel, 21, mod);
                    golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: (int)crudo - lobo.Def, lobo.Def);
                    golpe.Anotar(DagaMasUno, golpes: 1, entraron: 0, dano: 0, lobo.Def);
                }
        Assert.True(golpe.NoLaVaALastimar(DagaMasUno, esqueleto.Def, esqueleto.MaxHp));
        Assert.False(golpe.NoLaVaALastimar(DagaMasUno, lobo.Def, lobo.MaxHp));
    }

    /// <summary>Con menos de diez golpes no se juzga, ni con otra arma: el golpe es otro, y al cambiar de arma lo de la anterior se
    /// olvida.</summary>
    [Fact]
    public void Sin_golpes_suficientes_o_con_otra_arma_no_se_juzga()
    {
        var golpe = new Bot.GolpeMedido();
        for (int i = 0; i < 9; i++) golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: -5, defensa: 8);
        Assert.False(golpe.NoLaVaALastimar(DagaMasUno, defensa: 8, vida: 75), "nueve golpes no alcanzan");
        golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: -5, defensa: 8);
        Assert.True(golpe.NoLaVaALastimar(DagaMasUno, defensa: 8, vida: 75));
        Assert.False(golpe.NoLaVaALastimar(DagaDeNovato, defensa: 8, vida: 75), "con otra arma no se juzga");
        golpe.Anotar(DagaDeNovato, golpes: 1, entraron: 1, dano: 20, defensa: 8);
        Assert.False(golpe.NoLaVaALastimar(DagaMasUno, defensa: 8, vida: 75), "lo de la daga anterior se olvidó");
    }

    /// <summary>
    /// LA PREMISA de <see cref="Sin_un_golpe_que_entre_no_se_juzga"/>, con la data y la cuenta del servidor: con Armas en cero, un
    /// personaje de nivel 16 le acierta al Esqueleto guerrero (evasión 80) uno de cada cinco golpes, y diez errados seguidos le pasan
    /// en una pelea de cada doce.
    /// </summary>
    [Fact]
    public void Con_armas_en_cero_diez_errados_seguidos_no_son_raros()
    {
        var d = GameData.Load();
        if (d.Npc(EsqueletoGuerrero) is not { } esqueleto) return;
        long acierto = Combat.HitChance(Combat.PoderDeAtaque(skill: 0, agility: 16, level: 16, attackMod: 1.0), esqueleto.PoderEvasion);
        Assert.Equal(22, acierto);
        Assert.InRange(Math.Pow(1 - acierto / 100.0, 10), 1 / 13.0, 1 / 11.0);
    }

    /// <summary>
    /// SIN UN GOLPE QUE ENTRE NO SE JUZGA (ver <see cref="Bot.NoLaVaAMatar"/>): diez errados no dicen cuánto le saca el golpe que
    /// entra. Contándolos como golpes que no sacan nada, la pelea se daba por perdida, y lo medido de su golpe (<see cref="Bot.GolpeMedido"/>)
    /// decía que con esa arma no lastimaba a ninguna criatura, ni al Murciélago.
    /// </summary>
    [Fact]
    public void Sin_un_golpe_que_entre_no_se_juzga()
    {
        Assert.False(Bot.NoLaVaAMatar(golpes: 10, entraron: 0, dano: 0, vidaDeLaCriatura: 200));
        var golpe = new Bot.GolpeMedido();
        for (int i = 0; i < 10; i++) golpe.Anotar(Spatha, golpes: 1, entraron: 0, dano: 0, defensa: 19);
        Assert.False(golpe.NoLaVaALastimar(Spatha, defensa: 19, vida: 200));
        Assert.False(golpe.NoLaVaALastimar(Spatha, defensa: 0, vida: 15), "ni al Murciélago");
        Assert.Null(golpe.GolpesParaMatar(Spatha, defensa: 19, vida: 200));
        golpe.Anotar(Spatha, golpes: 1, entraron: 1, dano: 80, defensa: 19);
        Assert.False(golpe.NoLaVaALastimar(Spatha, defensa: 19, vida: 200), "con uno que entró, al Esqueleto guerrero lo mata");
        Assert.Equal(28, golpe.GolpesParaMatar(Spatha, defensa: 19, vida: 200));
    }

    /// <summary>
    /// UNA RACHA NO ALCANZA PARA DEJARLA (ver <see cref="Bot.LaDejaPorImposible"/>): Quim en `alto32`, doce golpes y 22 de daño contra
    /// un Esqueleto guerrero, que con lo medido de su arma mataba en unos 21. Sin nada medido, o con lo medido diciendo lo mismo,
    /// se deja.
    /// </summary>
    [Fact]
    public void Una_racha_no_alcanza_para_dejarla()
    {
        var golpe = new Bot.GolpeMedido();
        golpe.Anotar(Spatha, golpes: 40, entraron: 16, dano: 693 - 16 * 19, defensa: 19);
        int? conLoMedido = golpe.GolpesParaMatar(Spatha, defensa: 19, vida: 200);
        Assert.Equal(21, conLoMedido);
        Assert.True(Bot.NoLaVaAMatar(golpes: 12, entraron: 1, dano: 22, vidaDeLaCriatura: 200), "premisa: la pelea sola la daba por imposible");
        Assert.False(Bot.LaDejaPorImposible(12, 1, 22, 200, conLoMedido));
        Assert.True(Bot.LaDejaPorImposible(12, 1, 22, 200, null), "sin nada medido, la pelea decide");
        Assert.True(Bot.LaDejaPorImposible(12, 1, 22, 200, 150), "y si lo medido dice lo mismo, también");
        Assert.False(Bot.LaDejaPorImposible(12, 0, 0, 200, null), "sin un golpe que entre, nunca");
    }

    /// <summary>
    /// A LO QUE NO ES PRESA PERO SE MATA RÁPIDO SE LO SACA DE ENCIMA (ver <see cref="Bot.SeLaSacaDeEncima"/>): "a un lobo de mierda
    /// debería matarlo un bot ya con cierto nivel" (el autor, 01/10). Con los golpes de Lira (Daga +1, golpe de 18 a 24 antes de la
    /// defensa, uno de cada dos adentro) el Lobo cae en doce golpes; el Zombie (defensa 12, 320 de vida), el que mataba a los bots
    /// de 107 y 122 de vida en `ropa1`, pide setenta: con ése sigue la huida.
    /// </summary>
    [Fact]
    public void Al_lobo_se_lo_saca_de_encima_y_al_zombie_no()
    {
        var d = GameData.Load();
        if (d.Npc(Lobo) is not { } lobo || d.Npc(Zombie) is not { } zombie || d.Obj(DagaMasUno) is not { } daga) return;
        double mod = d.WeaponMod.TryGetValue(GameLimits.ClaseLadron, out var m) ? m : 1.0;
        var golpe = new Bot.GolpeMedido();
        for (int vuelta = 0; vuelta < 2; vuelta++)
            for (int tirada = daga.MinHit; tirada <= daga.MaxHit; tirada++)
                for (int delNivel = 12; delNivel <= 13; delNivel++)
                {
                    long crudo = Combat.CalcularDano(tirada, daga.MaxHit, delNivel, 21, mod);
                    golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: (int)crudo - lobo.Def, lobo.Def);
                    golpe.Anotar(DagaMasUno, golpes: 1, entraron: 0, dano: 0, lobo.Def);
                }
        var alLobo = golpe.GolpesParaMatar(DagaMasUno, lobo.Def, lobo.MaxHp);
        var alZombie = golpe.GolpesParaMatar(DagaMasUno, zombie.Def, zombie.MaxHp);
        Assert.True(alLobo is <= 15, $"al lobo, {alLobo}");
        Assert.True(alZombie is > Bot.GolpesParaSacarselaDeEncima, $"al zombie, {alZombie}");
        Assert.True(Bot.SeLaSacaDeEncima(alLobo, conPociones: true, esPeligrosa: false));
        Assert.False(Bot.SeLaSacaDeEncima(alZombie, conPociones: true, esPeligrosa: false));
        Assert.False(Bot.SeLaSacaDeEncima(alLobo, conPociones: false, esPeligrosa: false), "sin pociones, no");
        Assert.False(Bot.SeLaSacaDeEncima(alLobo, conPociones: true, esPeligrosa: true), "lo que lo mata de pocos golpes, no");
        Assert.False(Bot.SeLaSacaDeEncima(null, conPociones: true, esPeligrosa: false), "sin saber cómo pega, no");
    }

    /// <summary>En cuántos golpes la mata: con lo que no le saca nada, nunca; con otra arma o pocos golpes, no se sabe.</summary>
    [Fact]
    public void En_cuantos_golpes_la_mata()
    {
        var golpe = new Bot.GolpeMedido();
        for (int i = 0; i < 10; i++) golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: 10, defensa: 0);
        Assert.Equal(8, golpe.GolpesParaMatar(DagaMasUno, defensa: 0, vida: 75));
        Assert.Equal(int.MaxValue, golpe.GolpesParaMatar(DagaMasUno, defensa: 10, vida: 75));
        Assert.Null(golpe.GolpesParaMatar(DagaDeNovato, defensa: 0, vida: 75));
        Assert.Null(new Bot.GolpeMedido().GolpesParaMatar(DagaMasUno, defensa: 0, vida: 75));
    }

    /// <summary>AL SUBIR DE NIVEL EL GOLPE CRECE, y los golpes viejos se van: pasados los que recuerda, cuentan sólo los nuevos.</summary>
    [Fact]
    public void Los_golpes_viejos_se_olvidan()
    {
        var golpe = new Bot.GolpeMedido();
        for (int i = 0; i < Bot.GolpesQueRecuerda; i++) golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: 1, defensa: 19);
        Assert.True(golpe.NoLaVaALastimar(DagaMasUno, defensa: 19, vida: 200));
        for (int i = 0; i < Bot.GolpesQueRecuerda; i++) golpe.Anotar(DagaMasUno, golpes: 1, entraron: 1, dano: 12, defensa: 19);
        Assert.False(golpe.NoLaVaALastimar(DagaMasUno, defensa: 19, vida: 200));
    }
}
