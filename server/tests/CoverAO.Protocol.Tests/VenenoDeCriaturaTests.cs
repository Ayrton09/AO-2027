using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL VENENO DE UNA CRIATURA (VB6 NpcEnvenenarUser, MODULO_NPCs.bas:785): en cada golpe que entra tira el dado y, con
/// menos de 30, prende el veneno y avisa con el 182. No mira si el jugador ya estaba envenenado, así que el aviso se
/// repite. Acá no se tiraba si ya lo estaba.
/// </summary>
public class VenenoDeCriaturaTests
{
    private sealed class Conexion : IConnection
    {
        public readonly List<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Add(packet);
        public void Close(string reason) { }
    }

    [Fact]
    public void La_criatura_venenosa_avisa_aunque_ya_este_envenenado()
    {
        var world = new World();
        var conexion = new Conexion();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test", Connection = conexion };
        jugador.Pos = new WorldPos(1, 10, 10);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        jugador.PoisonedUntilTick = long.MaxValue;                                 // ya envenenado
        world.Add(jugador);
        var ficha = new NpcInfo(Index: 1, Name: "Araña", Body: 1, Head: 1, Heading: 4, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000, Veneno: 1);
        world.AddNpc(new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(1, 11, 10),
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4,
        });
        var sys = new NpcSystem(world, new GameData());
        for (int i = 1; i <= 7500; i++) sys.Update(i);                         // cien intentos de golpe
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: le pegó");
        Assert.Contains(conexion.Recibidos, p => p.Length >= 3 && p[0] == (byte)ServerPacketId.LocaleMsg
                                                  && BitConverter.ToInt16(p, 1) == 182);
    }
}
