using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS FACCIONES DEL MUNDO. Los enlistadores (NpcType 5) y los convertidores (NpcType 18) toman su rol de la ficha, como
/// en el original (Acciones.bas:214-268 rutea por NPCtype y <c>flags.Status</c>): antes sólo los del fixture lo tenían,
/// así que en el mundo nadie podía enlistarse ni hacerse republicano. Y el doble clic al enlistador hace lo del original:
/// al que ya es de su bando le da la recompensa y al que no, lo enlista.
/// </summary>
public class LasFaccionesDelMundoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_facciones_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly NpcSystem _npcs;

    private const short Jerek = 72, BarinkeFauster = 172, Demonio = 98, DemedorFalentus = 171, HalinteGanre = 173;
    private const short Mapa = 1;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
    }

    public LasFaccionesDelMundoTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _npcs = new NpcSystem(_world, _datos);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private Npc Nacido(short ficha, int x, int y)
    {
        var pos = new WorldPos(Mapa, (byte)x, (byte)y);
        _npcs.AddSpawn(_datos.Npc(ficha)!, pos);
        return _world.NpcAt(pos)!;
    }

    private (Player, ClientSession, Transporte) Conectado(int x, int y, byte bando, int nivel = 30)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Soldado", Account = "soldado", Pos = new WorldPos(Mapa, (byte)x, (byte)y),
            Heading = 3, Hp = 100, MaxHp = 100, Level = (byte)nivel, Privileges = 1, FactionStatus = bando,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task DobleClic(ClientSession s, Npc npc)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LeftClick)
            .WriteByte(npc.Pos.X).WriteByte(npc.Pos.Y).ToArray()));
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.DoubleClick)
            .WriteByte(npc.Pos.X).WriteByte(npc.Pos.Y).ToArray()));
        await Task.Delay(300);
    }

    private bool Dijo(Transporte t, Npc npc, string texto) => t.Recibio(ServerMessages.ChatOverHead(texto, npc.CharIndex));

    [Fact]
    public void Los_enlistadores_y_los_convertidores_salen_de_su_ficha()
    {
        Assert.Equal(Player.StatusArmada, Nacido(Jerek, 30, 30).FactionEnlist);
        Assert.Equal(Player.StatusMilicia, Nacido(BarinkeFauster, 32, 30).FactionEnlist);
        Assert.Equal(Player.StatusCaos, Nacido(Demonio, 34, 30).FactionEnlist);
        Assert.Equal(Player.StatusCiudadano, Nacido(DemedorFalentus, 36, 30).FactionConvert);
        Assert.Equal(Player.StatusRepublicano, Nacido(HalinteGanre, 38, 30).FactionConvert);
        var comun = _datos.Npcs.Values.First(n => n.NpcType == 0 && n.Hostile);
        var pos = new WorldPos(Mapa, 40, 30);
        _npcs.AddSpawn(comun, pos);
        Assert.Equal((0, 0), (_world.NpcAt(pos)!.FactionEnlist, _world.NpcAt(pos)!.FactionConvert));
    }

    [Fact]
    public async Task El_doble_clic_al_enlistador_de_otro_bando_lo_enlista()
    {
        var jerek = Nacido(Jerek, 50, 50);
        var (p, s, t) = Conectado(51, 50, Player.StatusCiudadano);
        p.RenegadosMatados = 20;

        await DobleClic(s, jerek);

        Assert.Equal(Player.StatusArmada, p.FactionStatus);
        Assert.Equal(1, p.FactionRank);
    }

    [Fact]
    public async Task El_doble_clic_al_enlistador_del_propio_bando_da_la_recompensa()
    {
        var jerek = Nacido(Jerek, 50, 50);
        var (p, s, t) = Conectado(51, 50, Player.StatusArmada);
        p.FactionRank = 1;

        await DobleClic(s, jerek);

        // Sólo ASCII: el texto viaja en la página de códigos del protocolo, no en UTF-8.
        Assert.Contains(t.De(ServerPacketId.ChatOverHead), x => x.AsSpan().IndexOf("para recibir tu pr"u8) >= 0
                                                             || x.AsSpan().IndexOf("tengo trabajo para darte"u8) >= 0);
        Assert.False(Dijo(t, jerek, "Ya perteneces a las tropas reales, ve a combatir enemigos."));
    }

    [Fact]
    public async Task Lejos_el_enlistador_dice_que_estas_muy_lejos()
    {
        var jerek = Nacido(Jerek, 50, 50);
        var (p, s, t) = Conectado(55, 50, Player.StatusCiudadano);
        p.RenegadosMatados = 20;

        await DobleClic(s, jerek);

        Assert.True(t.Recibio(ServerMessages.LocaleMsg(8)));
        Assert.Equal(Player.StatusCiudadano, p.FactionStatus);
    }

    [Fact]
    public async Task El_convertidor_del_mundo_hace_republicano_al_renegado()
    {
        var halinte = Nacido(HalinteGanre, 50, 50);
        var (p, s, t) = Conectado(51, 50, Player.StatusCriminal);
        p.Gold = 200_000;

        await DobleClic(s, halinte);

        Assert.Equal(Player.StatusRepublicano, p.FactionStatus);
        Assert.Equal(100_000, p.Gold);
    }

    [Fact]
    public async Task El_enlistar_por_comando_anda_con_el_enlistador_del_mundo()
    {
        var barinke = Nacido(BarinkeFauster, 50, 50);
        var (p, s, t) = Conectado(51, 50, Player.StatusRepublicano);
        p.CiudadanosMatados = 20;
        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.LeftClick)
            .WriteByte(barinke.Pos.X).WriteByte(barinke.Pos.Y).ToArray()));

        _dispatcher.HandleOne(s, new ByteQueueReader(new ByteQueueWriter().WriteByte((byte)ClientPacketId.Enlist).ToArray()));
        await Task.Delay(300);

        Assert.Equal(Player.StatusMilicia, p.FactionStatus);
    }
}
