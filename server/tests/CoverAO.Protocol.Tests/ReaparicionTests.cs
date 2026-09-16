using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DÓNDE REAPARECE UNA CRIATURA (VB6 ReSpawnNpc → CrearNPC). La que trae `PosOrig=1` vuelve a su baldosa; el resto reaparece en una
/// posición legal al azar, lejos de los jugadores. El port las reaparecía a todas en su baldosa, y en `verdad1` un Escorpión quedó
/// parado en la misma baldosa que un jugador, donde no se le puede pegar.
/// </summary>
public class ReaparicionTests
{
    // SE PREGUNTA, NO SE COPIA: aca decia 125 a mano y quedo desincronizado al volver el respawn
    // configurable. Estos tests construyen el NpcSystem SIN config, asi que les toca el escalon comun.
    private const int RespawnTicks = NpcSystem.RespawnSegundosSinConfig * 1000 / GameLoop.TickMs;

    /// <summary>Estática desde su ficha: un paso en la misma vuelta de la reaparición no tiene que mover lo que se mide.</summary>
    private static NpcInfo Bicho(bool posOrig) =>
        new(Index: 1, Name: "Murciélago", Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Movement: 1, PosOrig: posOrig);

    private static (World world, NpcSystem sys, Player player) Escena()
    {
        var world = new World();
        var player = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        player.Pos = new WorldPos(1, 50, 50);
        world.Add(player);
        return (world, new NpcSystem(world, new GameData()), player);
    }

    /// <summary>La mata, deja pasar la espera y devuelve la que reapareció.</summary>
    private static Npc Reaparecida(World world, NpcSystem sys, ref long tick)
    {
        world.RemoveNpc(world.Npcs.Single().CharIndex);
        sys.Update(++tick);                  // se entera de la muerte
        tick += RespawnTicks + 1;
        sys.Update(tick);                    // y reaparece
        return world.Npcs.Single();
    }

    /// <summary>SIN PosOrig NO REAPARECE ENCIMA NI A LA VISTA DEL JUGADOR: fuera del área de 8 por 6 de HayPCarea, aunque su
    /// spawn sea la baldosa del jugador.</summary>
    [Fact]
    public void Sin_posorig_reaparece_lejos_del_jugador()
    {
        var (world, sys, player) = Escena();
        sys.AddSpawn(Bicho(posOrig: false), player.Pos, hostile: true);
        long tick = 0;
        for (int vez = 0; vez < 20; vez++)
        {
            var npc = Reaparecida(world, sys, ref tick);
            Assert.False(Math.Abs(npc.Pos.X - player.Pos.X) <= 8 && Math.Abs(npc.Pos.Y - player.Pos.Y) <= 6,
                $"reapareció en ({npc.Pos.X},{npc.Pos.Y}), a la vista del jugador en ({player.Pos.X},{player.Pos.Y})");
        }
    }

    /// <summary>CON PosOrig VUELVE A SU BALDOSA, como el sacerdote o el mercader.</summary>
    [Fact]
    public void Con_posorig_vuelve_a_su_baldosa()
    {
        var (world, sys, _) = Escena();
        var suBaldosa = new WorldPos(1, 30, 30);
        sys.AddSpawn(Bicho(posOrig: true), suBaldosa);
        long tick = 0;
        var npc = Reaparecida(world, sys, ref tick);
        Assert.Equal(suBaldosa, npc.Pos);
    }

    /// <summary>LA QUE CREA UN GM REAPARECE AL AZAR aunque su ficha traiga PosOrig: <c>SpawnNpc</c> no le guarda <c>Orig</c>,
    /// que sólo lo guarda la carga del mapa.</summary>
    [Fact]
    public void La_que_crea_un_gm_reaparece_al_azar_aunque_traiga_posorig()
    {
        var (world, sys, _) = Escena();
        var dondeLaCreo = new WorldPos(1, 30, 30);
        sys.GmSpawn(Bicho(posOrig: true), dondeLaCreo);
        long tick = 0;
        var npc = Reaparecida(world, sys, ref tick);
        Assert.NotEqual(dondeLaCreo, npc.Pos);
    }
}
