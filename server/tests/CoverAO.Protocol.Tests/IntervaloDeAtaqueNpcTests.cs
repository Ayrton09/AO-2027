using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// CADA CUÁNTO PEGA UNA CRIATURA. El original lo decide con un contador POR CRIATURA de 3000 ms
/// (<c>IntervaloPermiteAtacarNpc</c>, VB6 modNuevoTimer.bas:52), que consumen por igual el golpe a un
/// jugador (<c>NpcAtacaUser</c>, SistemaCombate.bas:713), el golpe a otra criatura (<c>NpcAtacaNpc</c>,
/// :831) y el casteo (modHechizos.bas:100 y :145).
///
/// El port no tenía ese contador: sólo la cadencia global de la fase de ataque del loop, una vez por
/// segundo. O sea que <b>cada criatura pegaba tres veces más seguido que en el original</b> — el triple de
/// daño recibido en todo el PvE. No lo veía ningún test porque todos miraban SI pega, no CUÁNTAS VECES.
///
/// Se descubrió tirando del hilo de otra cosa: al hacer que recibir un golpe entrenara Tácticas, la
/// cadencia dejó de ser sólo daño y pasó a ser también paquetes — tres subidas de habilidad por cada una
/// que manda el original.
/// </summary>
public class IntervaloDeAtaqueNpcTests
{
    // ESTE ARCHIVO AFIRMA SOBRE SEGUNDOS, no sobre vueltas: "en 30 segundos pega ~10 veces, una cada 3 s".
    // Por eso las vueltas se CALCULAN desde el tiempo y la cadencia se le pregunta al sistema. Con el número
    // copiado a mano, bajar el barrido de 25 a 10 ticks dejaba el mismo bucle midiendo 12 segundos y
    // comparándolos contra un umbral de 30.
    private const int TicksPorVuelta = NpcSystem.AttackEveryTicks;
    private const int Segundos = 30;
    private const int Vueltas = Segundos * 1000 / (TicksPorVuelta * GameLoop.TickMs);

    private static NpcInfo Template() =>
        new(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);

    private static Npc Lobo(World world, byte y) => new()
    {
        CharIndex = world.AllocateCharIndex(), Template = Template(), Hp = 1000,
        Pos = new WorldPos(1, 10, y), Movement = 1, Hostile = true, OriginallyHostile = true,
    };

    /// <summary>Un jugador que aguanta todo, con <paramref name="cuantosLobos"/> hostiles pegados.</summary>
    private static (Npc primero, Player player, NpcSystem sys) Escena(int cuantosLobos = 1)
    {
        var world = new World();
        var player = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        player.Pos = new WorldPos(1, 10, 11);
        player.MaxHp = player.Hp = 1_000_000;   // no se muere: lo que se cuenta son los golpes
        world.Add(player);

        // uno arriba y, si se piden dos, otro abajo — los dos adyacentes
        var arriba = Lobo(world, 10);
        world.AddNpc(arriba);
        if (cuantosLobos > 1) world.AddNpc(Lobo(world, 12));

        return (arriba, player, new NpcSystem(world, new GameData()));
    }

    private static int GolpesEn30Segundos(NpcSystem sys, Player player)
    {
        for (int i = 1; i <= Vueltas; i++) sys.Update(TicksPorVuelta * i);
        return player.MaxHp - player.Hp;   // 1 de daño por golpe: la resta ES el número de golpes
    }

    /// <summary>
    /// En 30 segundos una criatura pega ~10 veces (una cada 3 s), no ~30. Se afirma un RANGO y no un número
    /// exacto porque la tirada de acierto se clampea al 90% (<c>Combat.HitChance</c>).
    /// </summary>
    [Fact]
    public void Una_criatura_pega_una_vez_cada_tres_segundos_y_no_una_por_segundo()
    {
        var (_, player, sys) = Escena();
        Assert.InRange(GolpesEn30Segundos(sys, player), 1, 12);   // ~10 con el intervalo; sin él, ~27
    }

    /// <summary>
    /// El contador es POR CRIATURA, no uno global: dos lobos rodeando al mismo jugador le pegan cada uno lo
    /// suyo. Si el freno fuera compartido, dos criaturas pegarían lo mismo que una — el error opuesto al
    /// que se acaba de arreglar, y hay que descartarlo explícitamente.
    /// </summary>
    [Fact]
    public void El_intervalo_es_de_cada_criatura_y_no_uno_global()
    {
        var (_, player, sys) = Escena(cuantosLobos: 2);
        int golpes = GolpesEn30Segundos(sys, player);
        Assert.True(golpes >= 14,
            $"dos criaturas dieron {golpes} golpes: si el intervalo fuera global darían ~10, como una sola");
    }

    /// <summary>
    /// El control negativo del propio test: forzando el contador a cero en cada vuelta —que es exactamente
    /// lo que hacía el port antes— el mismo escenario tiene que dar MUCHOS más golpes. Sin esto, un cambio
    /// que rompiera el conteo dejaría el test de arriba verde por medir de menos.
    /// </summary>
    [Fact]
    public void Sin_el_intervalo_los_golpes_serian_muchos_mas()
    {
        var (npc, player, sys) = Escena();
        for (int i = 1; i <= Vueltas; i++)
        {
            npc.NextAttackTick = 0;   // el port sin el arreglo: ningún freno por criatura
            sys.Update(TicksPorVuelta * i);
        }
        int sinFreno = player.MaxHp - player.Hp;
        Assert.True(sinFreno >= 20,
            $"sin intervalo tendrían que entrar ~27 golpes y entraron {sinFreno}: el test no mide la cadencia");
    }
}
