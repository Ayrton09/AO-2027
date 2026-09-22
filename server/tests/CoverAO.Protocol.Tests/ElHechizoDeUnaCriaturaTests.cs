using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HECHIZO DE UNA CRIATURA SOBRE UN JUGADOR, en el orden del original (NpcLanzaSpellSobreUser, modHechizos.bas:96-252):
/// el contador de ataque va PRIMERO y el mapa sin magia después, así que ahí la criatura maga gasta el turno y no pega;
/// el nombre del hechizo sobre su cabeza sale al final en la cura y el daño, y la parálisis no lo dice.
/// </summary>
public class ElHechizoDeUnaCriaturaTests
{
    private const short Cuna = 37;
    private const short Paralizar = 9, HechizoDeNpc = 36;

    private sealed class Conexion : IConnection
    {
        public readonly List<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Add(packet);
        public void Close(string reason) { }
    }

    private static (Player, Npc, Conexion) Escena(GameData d, short hechizo)
    {
        var world = new World();
        var conexion = new Conexion();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test", Connection = conexion };
        jugador.Pos = new WorldPos(Cuna, 50, 50);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        world.Add(jugador);
        var ficha = new NpcInfo(Index: 1, Name: "Chamán", Body: 1, Head: 1, Heading: 4, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000, Hostile: true, Spells: new[] { (int)hechizo });
        var npc = new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(Cuna, 51, 50),
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4,
        };
        world.AddNpc(npc);
        var sys = new NpcSystem(world, d);
        for (int i = 1; i <= 750; i++) sys.Update(i);
        return (jugador, npc, conexion);
    }

    /// <summary>Los índices, en lo que le llegó al jugador, de los carteles con el nombre del hechizo de esa criatura.</summary>
    private static List<int> Nombres(Conexion c, Npc npc) => c.Recibidos
        .Select((p, i) => (p, i))
        .Where(x => x.p.Length >= 8 && x.p[0] == (byte)ServerPacketId.ChatOverHeadLocale
                    && BitConverter.ToInt16(x.p, 1) == npc.CharIndex && x.p[7] == 1)
        .Select(x => x.i).ToList();

    [Fact]
    public void En_un_mapa_sin_magia_la_criatura_maga_no_castea_ni_pega()
    {
        var d = GameData.Load();
        var (jugador, _, _) = Escena(d, HechizoDeNpc);
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: con magia le tira");

        var sinMagia = GameData.Load();
        sinMagia.SetMapNoMagic(Cuna, true);
        var (otro, _, _) = Escena(sinMagia, HechizoDeNpc);
        Assert.Equal(otro.MaxHp, otro.Hp);
    }

    [Fact]
    public void La_paralisis_de_una_criatura_no_dice_el_nombre_del_hechizo()
    {
        var (jugador, npc, conexion) = Escena(GameData.Load(), Paralizar);
        Assert.True(jugador.IsParalyzed(750), "premisa: lo paralizó");
        Assert.Empty(Nombres(conexion, npc));
    }

    [Fact]
    public void El_nombre_del_hechizo_de_dano_sale_despues_del_UpdateHP()
    {
        var (jugador, npc, conexion) = Escena(GameData.Load(), HechizoDeNpc);
        Assert.True(jugador.Hp < jugador.MaxHp, "premisa: le tiró");
        var nombres = Nombres(conexion, npc);
        Assert.NotEmpty(nombres);
        int primerHp = conexion.Recibidos.FindIndex(p => p.Length > 0 && p[0] == (byte)ServerPacketId.UpdateHP);
        Assert.True(primerHp >= 0 && primerHp < nombres[0], "el nombre tiene que llegar después del UpdateHP");
    }
}
