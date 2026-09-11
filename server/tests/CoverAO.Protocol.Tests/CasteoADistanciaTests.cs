using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CRIATURA MAGA CASTEA MIENTRAS SE ACERCA, no sólo cuando queda pegada (VB6 <c>AI_NPC.bas:346</c>, dentro de
/// <c>IrUsuarioCercano</c>). El port porteó de ese bucle la parte del movimiento y se salteó la del hechizo, así que un Arquero
/// Pirata o cualquier bicho lanzador te dejaba caminar hasta él sin tirarte nada. El tester lo reportó como que los bichos que
/// atacan con magia sólo te pegan si estás al lado.
/// </summary>
public class CasteoADistanciaTests
{
    private const short MapaDePrueba = 37;   // la cuna: se permite magia

    /// <summary>La primera ficha del install que tenga un hechizo de DAÑO, para no atarse a un número a mano.</summary>
    private static NpcInfo? UnaCriaturaMaga(GameData d)
    {
        for (short ficha = 1; ficha < 1000; ficha++)
            if (d.Npc(ficha) is { Hostile: true, Spells: { Count: > 0 } hechizos } n
                && hechizos.Any(id => d.Spell(id) is { IsDamage: true }))
                return n;
        return null;
    }

    /// <summary>DESDE LEJOS TE PEGA. Se mide A QUE DISTANCIA ocurrio el primer dano: la criatura con <c>Movement = 0</c> camina
    /// hacia el jugador, asi que si el unico dano llega cuando ya esta pegada, eso es el golpe de al lado y no el hechizo.</summary>
    [Fact]
    public void La_criatura_maga_castea_desde_lejos()
    {
        var d = GameData.Load();
        if (UnaCriaturaMaga(d) is not { } ficha || d.MapNoMagic(MapaDePrueba)) return;

        var world = new World();
        var player = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        player.Pos = new WorldPos(MapaDePrueba, 50, 50);
        player.MaxHp = 100_000; player.Hp = 100_000;      // que aguante: lo que se mide es que le peguen, no que lo maten
        player.MaxMana = 0; player.Mana = 0;
        world.Add(player);

        var sys = new NpcSystem(world, d);
        // A OCHO EN X: el borde de su ventana de vision, para que tenga que caminar un rato antes de quedar pegada.
        sys.AddSpawn(ficha with { Movement = 0 }, new WorldPos(MapaDePrueba, 58, 50), hostile: true, posOrig: true);

        int distanciaAlPegar = -1;
        for (long tick = 1; tick <= 600; tick++)
        {
            sys.Update(tick);
            if (player.Hp >= player.MaxHp) continue;
            var bicho = world.Npcs.Single();
            distanciaAlPegar = Math.Abs(bicho.Pos.X - player.Pos.X) + Math.Abs(bicho.Pos.Y - player.Pos.Y);
            break;
        }

        Assert.True(distanciaAlPegar > 1, distanciaAlPegar < 0
                    ? $"la criatura {ficha.Name} (ficha {ficha.Index}) no le tiró nada al jugador que tenía a ocho baldosas"
                    : $"le pegó recién pegada, a {distanciaAlPegar}: eso es el golpe de al lado, no el hechizo de lejos");
    }
}
