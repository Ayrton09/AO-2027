using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// USAR OBJETOS, REORDENAR LA BÓVEDA Y ASIGNAR SKILLS CON LOS PAQUETES DEL ORIGINAL. Lo que se gasta al usarlo termina
/// con el delta de la ranura (<c>UpdateUserInventario</c> → ChangeInventorySlotUser acción 3), no con la ranura entera;
/// la poción sin rama se consume en silencio; reordenar la bóveda manda las 40 ranuras y el BankOK; asignar skills no
/// contesta nada. El oráculo lo mide con <c>UseItem_pocion</c>, <c>MoveBank_abajo</c> y <c>ModifySkills_uno</c>.
/// </summary>
public class UsarObjetosComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_usar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short PocionRojaNovato = 461, ScrollX2Experiencia = 509, SaetaIgnea = 197, PequenoSacoDeOro = 606;
    private const short PaseRinkelANuevaEsperanza = 595;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public List<byte[]> De(ServerPacketId id) => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)id).ToList();
        public List<ServerPacketId> Ids() => Recibidos.Where(x => x.Length > 0).Select(x => (ServerPacketId)x[0]).ToList();
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
    }

    public UsarObjetosComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(new FileStore(_root), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private (Player, ClientSession, Transporte) Conectado(short mapa = 1)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Usuario", Account = "usuario", Pos = new WorldPos(mapa, 50, 50),
            Heading = 3, Hp = 50, MaxHp = 100, Mana = 10, MaxMana = 100, Level = 30, Privileges = 1, Gold = 100,
            FreeSkillPoints = 10,
        };
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession s, ByteQueueWriter w)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(w.ToArray()));
        await Task.Delay(250);
    }

    private Task Usar(ClientSession s, byte ranura) =>
        Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.UseItem).WriteByte(ranura));

    [Fact]
    public async Task Tomar_una_pocion_manda_el_delta_de_la_ranura()
    {
        var (p, s, t) = Conectado();
        p.Inventory[5] = new InventorySlot { ObjIndex = PocionRojaNovato, Amount = 30 };

        await Usar(s, 5);

        Assert.Equal(29, p.Inventory[5].Amount);
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(5, 3, 29)));
        Assert.Empty(t.De(ServerPacketId.ChangeInventorySlot));
    }

    [Fact]
    public async Task La_pocion_sin_rama_se_gasta_en_silencio()
    {
        var (p, s, t) = Conectado();
        p.Inventory[2] = new InventorySlot { ObjIndex = ScrollX2Experiencia, Amount = 2 };

        await Usar(s, 2);

        Assert.Equal(1, p.Inventory[2].Amount);
        Assert.Empty(t.De(ServerPacketId.ConsoleMsg));
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(2, 3, 1)));
    }

    [Fact]
    public async Task Las_monedas_pasan_a_la_billetera_con_el_delta_y_despues_el_oro()
    {
        var (p, s, t) = Conectado();
        p.Inventory[3] = new InventorySlot { ObjIndex = (short)GameLimits.ObjOro, Amount = 250 };

        await Usar(s, 3);

        Assert.Equal(350, p.Gold);
        var ids = t.Ids();
        Assert.Equal(new[] { ServerPacketId.ChangeInventorySlotUser, ServerPacketId.UpdateGold }, ids);
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(3, 3, 0)));
    }

    [Fact]
    public async Task El_pergamino_ya_sabido_avisa_y_manda_la_ranura_igual()
    {
        var (p, s, t) = Conectado();
        short hechizo = (short)_datos.Obj(SaetaIgnea)!.HechizoIndex;
        p.Spells[1] = hechizo;
        p.Inventory[4] = new InventorySlot { ObjIndex = SaetaIgnea, Amount = 1 };

        await Usar(s, 4);

        Assert.True(t.Recibio(ServerMessages.LocaleMsg(396)));
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(4, 3, 1)));
        Assert.Equal(1, p.Inventory[4].Amount);
    }

    [Fact]
    public async Task El_pergamino_nuevo_se_aprende_y_manda_el_delta()
    {
        var (p, s, t) = Conectado();
        p.Inventory[4] = new InventorySlot { ObjIndex = SaetaIgnea, Amount = 1 };

        await Usar(s, 4);

        Assert.Contains((short)_datos.Obj(SaetaIgnea)!.HechizoIndex, p.Spells);
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(4, 3, 0)));
        Assert.Empty(t.De(ServerPacketId.ChangeInventorySlot));
    }

    [Fact]
    public async Task La_bolsa_manda_el_delta_al_final()
    {
        var (p, s, t) = Conectado();
        p.Inventory[6] = new InventorySlot { ObjIndex = PequenoSacoDeOro, Amount = 1 };

        await Usar(s, 6);

        Assert.Equal(ServerPacketId.ChangeInventorySlotUser, t.Ids()[^1]);
        Assert.Empty(t.De(ServerPacketId.ChangeInventorySlot));
    }

    [Fact]
    public async Task El_pasaje_se_gasta_con_el_delta()
    {
        var pase = _datos.Obj(PaseRinkelANuevaEsperanza)!;
        var (p, s, t) = Conectado((short)pase.PasajeDesdeMap);
        p.Skills[GameLimits.SkillNavegacion] = 100;
        p.Inventory[7] = new InventorySlot { ObjIndex = PaseRinkelANuevaEsperanza, Amount = 1 };
        var pirata = new Npc { CharIndex = _world.AllocateCharIndex(), Template = _datos.Npcs.Values.First(n => n.NpcType == 7),
                               Pos = new WorldPos(p.Pos.Map, 51, 50), IsTransportador = true };
        _world.AddNpc(pirata);
        p.TargetNpc = pirata.CharIndex;

        await Usar(s, 7);

        Assert.Equal(pase.PasajeHastaMap, p.Pos.Map);
        Assert.True(t.Recibio(ServerMessages.ChangeInventorySlotUser(7, 3, 0)));
        Assert.Empty(t.De(ServerPacketId.ChangeInventorySlot).Where(x => x.Length > 1 && x[1] == 7));
    }

    [Fact]
    public async Task Reordenar_la_boveda_manda_las_40_ranuras_y_el_BankOK()
    {
        var (p, s, t) = Conectado();
        p.BankInventory[1] = new InventorySlot { ObjIndex = PocionRojaNovato, Amount = 5 };

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.MoveBank).WriteBoolean(false).WriteByte(1));

        Assert.Equal(PocionRojaNovato, p.BankInventory[2].ObjIndex);
        var ids = t.Ids();
        Assert.Equal(GameLimits.MaxBankSlots, ids.Count(i => i == ServerPacketId.ChangeBankSlot));
        Assert.Equal(ServerPacketId.BankOK, ids[^1]);
        Assert.Empty(t.De(ServerPacketId.ConsoleMsg));
    }

    [Fact]
    public async Task Asignar_skills_no_contesta_nada()
    {
        var (p, s, t) = Conectado();
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.ModifySkills).WriteByte(3);
        for (int i = 2; i <= GameLimits.NumSkills; i++) w.WriteByte(0);

        await Mandar(s, w);

        Assert.Equal(3, p.Skills[1]);
        Assert.Equal(7, p.FreeSkillPoints);
        Assert.Empty(t.Recibidos);
    }
}
