using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CRIATURA GIRA HACIA LO QUE ATACA (VB6 ChangeNPCChar, MODULO_NPCs.bas:624): pone la dirección y le manda el
/// CharacterChange al área. La hostil gira ANTES de castear, pueda o no (AI_NPC.bas:179), y antes de ir contra una
/// mascota (:191); las dos —hostil y pacífica— giran después de un golpe que se llegó a intentar (:184, :235). La
/// pacífica castea sin girar. Acá una criatura no giraba nunca: sólo cambiaba la dirección al caminar.
/// </summary>
public class LaCriaturaGiraHaciaSuBlancoTests
{
    private const int Vueltas = 750;                     // 30 segundos de loop
    private const byte Norte = 1, Oeste = 4;

    private sealed class Conexion : IConnection
    {
        public readonly List<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Add(packet);
        public void Close(string reason) { }
    }

    private static (World Mundo, Player Jugador, Conexion Conexion) Escena(short mapa = 1, byte x = 10, byte y = 10)
    {
        var world = new World();
        var conexion = new Conexion();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test", Connection = conexion };
        jugador.Pos = new WorldPos(mapa, x, y);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        world.Add(jugador);
        return (world, jugador, conexion);
    }

    private static Npc Bicho(World world, NpcInfo ficha, short mapa, byte x, byte y, bool hostil)
    {
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = ficha.MaxHp, Pos = new WorldPos(mapa, x, y),
            Movement = 1, OriginalMovement = 1, Hostile = hostil, OriginallyHostile = hostil, Heading = Norte,
        };
        world.AddNpc(npc);
        return npc;
    }

    private static readonly NpcInfo Lobo = new(Index: 1, Name: "Lobo", Body: 7, Head: 0, Heading: Norte, MaxHp: 1000,
        MinHit: 1, MaxHit: 1, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);

    /// <summary>Las direcciones que le llegaron al jugador en CharacterChange de esta criatura.</summary>
    private static List<byte> Giros(Conexion c, Npc npc) => c.Recibidos
        .Where(p => p.Length >= 8 && p[0] == (byte)ServerPacketId.CharacterChange && BitConverter.ToInt16(p, 1) == npc.CharIndex)
        .Select(p => p[7]).ToList();

    private static void Correr(NpcSystem sys, int vueltas = Vueltas)
    {
        for (int i = 1; i <= vueltas; i++) sys.Update(i);
    }

    [Fact]
    public void La_que_pega_gira_hacia_su_blanco()
    {
        var (world, jugador, conexion) = Escena();
        var lobo = Bicho(world, Lobo, 1, 11, 10, hostil: true);                  // mira al norte; el jugador, al oeste
        Correr(new NpcSystem(world, new GameData()));
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: le pegó");
        Assert.Equal(Oeste, lobo.Heading);
        Assert.Contains(Oeste, Giros(conexion, lobo));
    }

    /// <summary>Si el intervalo no deja intentar el golpe, no gira (`If NpcAtacaUser(...) Then`).</summary>
    [Fact]
    public void La_que_no_llega_a_intentar_el_golpe_no_gira()
    {
        var (world, jugador, conexion) = Escena();
        var lobo = Bicho(world, Lobo, 1, 11, 10, hostil: true);
        lobo.NextAttackTick = long.MaxValue;
        Correr(new NpcSystem(world, new GameData()));
        Assert.Equal(jugador.MaxHp, jugador.Hp);
        Assert.Equal(Norte, lobo.Heading);
        Assert.Empty(Giros(conexion, lobo));
    }

    /// <summary>La pacífica provocada también gira después del golpe (HostilBuenoAI, :235).</summary>
    [Fact]
    public void La_pacifica_provocada_gira_despues_del_golpe()
    {
        var (world, jugador, conexion) = Escena();
        var lobo = Bicho(world, Lobo, 1, 11, 10, hostil: false);
        lobo.AttackedBy = jugador.Name;
        Correr(new NpcSystem(world, new GameData()), vueltas: 20);
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: le pegó");
        Assert.Equal(Oeste, lobo.Heading);
    }

    [Fact]
    public void La_hostil_gira_hacia_la_mascota_que_ataca()
    {
        var (world, amo, conexion) = Escena(1, 30, 30);
        var mascota = Bicho(world, Lobo, 1, 10, 10, hostil: false);
        mascota.OwnerCharIndex = amo.CharIndex;
        var lobo = Bicho(world, Lobo, 1, 11, 10, hostil: true);                  // la mascota, al oeste
        Correr(new NpcSystem(world, new GameData()), vueltas: 20);
        Assert.Equal(Oeste, lobo.Heading);
    }

    // ---- las que saben magia ------------------------------------------------------------------------------------

    private static NpcInfo? UnaMaga(GameData d) =>
        Enumerable.Range(1, 999).Select(i => d.Npc((short)i))
            .FirstOrDefault(n => n is { Hostile: true, NpcType: not (2 or 5 or 18), Spells: { Count: > 0 } h }
                                 && h.All(id => d.Spell(id) is { IsDamage: true }));   // una hostil que corre IA

    /// <summary>La hostil que sabe magia gira ANTES de castear, aunque el intervalo no la deje (:179).</summary>
    [Fact]
    public void La_hostil_maga_gira_antes_de_castear_aunque_no_pueda()
    {
        const short Cuna = 37;
        var d = GameData.Load();
        var maga = UnaMaga(d);
        Assert.NotNull(maga);                                                      // premisa
        var (world, jugador, conexion) = Escena(Cuna, 50, 50);
        var npc = Bicho(world, maga! with { Movement = 1 }, Cuna, 51, 50, hostil: true);
        npc.NextAttackTick = long.MaxValue;
        Correr(new NpcSystem(world, d), vueltas: 20);
        Assert.Equal(jugador.MaxHp, jugador.Hp);                                  // no llegó a castear
        Assert.Equal(Oeste, npc.Heading);
        Assert.Contains(Oeste, Giros(conexion, npc));
    }

    /// <summary>La pacífica que sabe magia castea sin girar (HostilBuenoAI, :229), y el golpe no se llega a intentar
    /// porque el hechizo consumió el intervalo.</summary>
    [Fact]
    public void La_pacifica_maga_castea_sin_girar()
    {
        const short Cuna = 37;
        var d = GameData.Load();
        var maga = UnaMaga(d);
        Assert.NotNull(maga);
        Assert.False(d.MapNoMagic(Cuna));
        var (world, jugador, conexion) = Escena(Cuna, 50, 50);
        var npc = Bicho(world, maga! with { Movement = 1 }, Cuna, 51, 50, hostil: false);
        npc.AttackedBy = jugador.Name;
        Correr(new NpcSystem(world, d), vueltas: 20);
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: le tiró un hechizo");
        Assert.Equal(Norte, npc.Heading);
        Assert.Empty(Giros(conexion, npc));
    }
}
