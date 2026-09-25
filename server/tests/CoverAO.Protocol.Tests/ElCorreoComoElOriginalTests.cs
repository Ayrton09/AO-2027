using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL CORREO, COMO EL ORIGINAL (HandleEnviarCorreo, Protocol.bas:16020, y mod_Correos.bas). El adjunto viaja
/// como RANURA del inventario (el cliente de stock manda <c>lstInv.ListIndex + 1</c>), los avisos son los del
/// original, uno se puede mandar una carta a sí mismo, y retirar o borrar no vuelve a mandar la bandeja. La
/// respuesta de un GM por correo la firma "CoverAO Staff".
/// </summary>
public class ElCorreoComoElOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_correo_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;
    private readonly Persistence _db;

    private const short PocionRoja = 38;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(byte[] paquete) => Recibidos.Any(x => x.SequenceEqual(paquete));
        public bool RecibioId(ServerPacketId id) => Recibidos.Any(x => x.Length > 0 && x[0] == (byte)id);
        public List<string> Textos() => Recibidos.Where(x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg)
            .Select(x => { var r = new ByteQueueReader(x); r.ReadByte(); return r.ReadAsciiString(); }).ToList();
    }

    public ElCorreoComoElOriginalTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _db = new Persistence(new FileStore(_root), leveling);
        _dispatcher = new PacketDispatcher(_world, _loop, _db, _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private Player Pj(string nombre, byte x) => new()
    {
        CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
        Pos = new WorldPos(1, x, 50), Heading = 3, Hp = 100, MaxHp = 100, Level = 1, Gold = 10_000,
    };

    private (Player, ClientSession, Transporte) Conectado(string nombre, byte x)
    {
        var p = Pj(nombre, x);
        _db.SaveCharacter(p);
        var t = new Transporte();
        var sesion = new ClientSession(t, _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, t);
    }

    private async Task Mandar(ClientSession s, byte[] paquete)
    {
        _dispatcher.HandleOne(s, new ByteQueueReader(paquete));
        var atendido = new TaskCompletionSource();
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private Task Enviar(ClientSession s, string dest, short ranura, short cantidad) => Mandar(s,
        new ByteQueueWriter().WriteByte((byte)ClientPacketId.EnviarCorreo).WriteAsciiString(dest)
            .WriteAsciiString("hola").WriteInteger(ranura).WriteInteger(cantidad).ToArray());

    private Task Buzon(ClientSession s, byte accion, byte carta) => Mandar(s,
        new ByteQueueWriter().WriteByte((byte)ClientPacketId.Packets_Correo).WriteByte(accion).WriteByte(carta).ToArray());

    private static async Task Asentar(params Transporte[] ts)
    {
        int antes = -1;
        for (int i = 0; i < 50; i++)
        {
            int ahora = ts.Sum(t => t.Recibidos.Count);
            if (ahora == antes) break;
            antes = ahora;
            await Task.Delay(150);
        }
    }

    [Fact]
    public async Task El_adjunto_sale_de_la_ranura_que_manda_el_cliente()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        Conectado("Bruma", 60);
        p.Inventory[4] = new InventorySlot { ObjIndex = PocionRoja, Amount = 5 };

        await Enviar(s, "Bruma", 4, 2);
        await Asentar(t);

        Assert.Equal(3, p.Inventory[4].Amount);
        var carta = Assert.Single(_world.PeekMail("Bruma"));
        Assert.Equal((PocionRoja, (short)2), (carta.ObjIndex, carta.Amount));
        Assert.Contains("El mensaje fue enviado correctamente.", t.Textos());
    }

    [Fact]
    public async Task Al_conectado_le_llega_el_indicador_y_el_aviso_del_original()
    {
        var (_, s, t) = Conectado("Aldo", 50);
        var (_, _, otro) = Conectado("Bruma", 60);

        await Enviar(s, "Bruma", 0, 0);
        await Asentar(t, otro);

        Assert.True(otro.Recibio(ServerMessages.MensajeSigno(true)));
        Assert.Contains("Has recibido un nuevo mensaje de [Aldo], ve a un correo local para leerlo.", otro.Textos());
        Assert.DoesNotContain(t.Textos(), m => m.StartsWith("Enviaste"));
    }

    [Fact]
    public async Task Sin_esa_cantidad_en_la_ranura_no_sale_ni_se_cobra()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        Conectado("Bruma", 60);
        p.Inventory[1] = new InventorySlot { ObjIndex = PocionRoja, Amount = 5 };
        p.Inventory[2] = new InventorySlot { ObjIndex = PocionRoja, Amount = 5 };

        await Enviar(s, "Bruma", 1, 8);     // 8 entre las dos ranuras, pero no en la 1
        await Enviar(s, "Bruma", 3, 1);     // ranura vacía
        await Asentar(t);

        Assert.Equal(2, t.Textos().Count(m => m == "No tienes esa cantidad."));
        Assert.Equal(((short)5, (short)5), (p.Inventory[1].Amount, p.Inventory[2].Amount));
        Assert.Equal(10_000, p.Gold);
        Assert.Empty(_world.PeekMail("Bruma"));
    }

    [Fact]
    public async Task Los_rechazos_con_los_textos_del_original()
    {
        var (_, s, t) = Conectado("Aldo", 50);
        Conectado("Bruma", 60);
        for (int i = 0; i < GameLimits.MaxCorreos; i++) _world.DeliverMail("Bruma", new MailEntry("x", "y", 0, 0));

        await Enviar(s, "Nadie", 0, 0);
        await Enviar(s, "Bruma", 0, 0);
        await Asentar(t);

        Assert.Contains("No existe el personaje [Nadie]", t.Textos());
        Assert.Contains("Bruma no tiene más espacio en su casilla de mensajes.", t.Textos());
    }

    [Fact]
    public async Task Uno_se_puede_mandar_una_carta_a_si_mismo()
    {
        var (_, s, t) = Conectado("Aldo", 50);

        await Enviar(s, "Aldo", 0, 0);
        await Asentar(t);

        Assert.Single(_world.PeekMail("Aldo"));
        Assert.Contains("El mensaje fue enviado correctamente.", t.Textos());
    }

    [Fact]
    public async Task Retirar_y_borrar_no_vuelven_a_mandar_la_bandeja()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        _world.DeliverMail("Aldo", new MailEntry("Bruma", "tomá", PocionRoja, 2));

        await Buzon(s, 3, 1);
        await Buzon(s, 1, 1);
        await Asentar(t);

        Assert.Equal(PocionRoja, p.Inventory[1].ObjIndex);
        Assert.Empty(_world.PeekMail("Aldo"));
        Assert.False(t.RecibioId(ServerPacketId.CorreoList));
    }

    [Fact]
    public async Task Retirar_con_el_inventario_lleno_avisa_y_lo_tira_al_piso()
    {
        var (p, s, t) = Conectado("Aldo", 50);
        for (byte i = 1; i <= GameLimits.MaxInventorySlots; i++) p.Inventory[i] = new InventorySlot { ObjIndex = 1, Amount = 1 };
        _world.DeliverMail("Aldo", new MailEntry("Bruma", "tomá", PocionRoja, 2));

        await Buzon(s, 3, 1);
        await Asentar(t);

        Assert.True(t.Recibio(ServerMessages.LocaleMsg(25)));
        Assert.DoesNotContain(t.Textos(), m => m.Contains("piso"));
    }

    [Fact]
    public async Task La_respuesta_del_gm_por_correo_la_firma_el_staff()
    {
        var (gm, s, t) = Conectado("Rugura", 50);
        gm.Privileges = 8;
        _db.SaveCharacter(Pj("Bruma", 60));                // offline

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(87)
            .WriteAsciiString("Bruma").WriteAsciiString("ya te ayudo").WriteAsciiString("Usuario").ToArray());
        await Asentar(t);

        var carta = Assert.Single(_world.PeekMail("Bruma"));
        Assert.Equal(("CoverAO Staff", "Rugura:ya te ayudo"), (carta.Sender, carta.Message));
        Assert.Contains("El mensaje fue enviado correctamente.", t.Textos());
        Assert.Contains("Se le ha enviado por correo la respuesta enviada.", t.Textos());
    }

    [Fact]
    public async Task La_respuesta_del_gm_con_la_casilla_llena_no_se_manda()
    {
        var (gm, s, t) = Conectado("Rugura", 50);
        gm.Privileges = 8;
        _db.SaveCharacter(Pj("Bruma", 60));
        for (int i = 0; i < GameLimits.MaxCorreos; i++) _world.DeliverMail("Bruma", new MailEntry("x", "y", 0, 0));

        await Mandar(s, new ByteQueueWriter().WriteByte((byte)ClientPacketId.GMCommands).WriteByte(87)
            .WriteAsciiString("Bruma").WriteAsciiString("ya te ayudo").WriteAsciiString("Usuario").ToArray());
        await Asentar(t);

        Assert.Equal(GameLimits.MaxCorreos, _world.PeekMail("Bruma").Count);
        Assert.Contains("El usuario tiene su casilla de correos llena, por favor comunicate con el usuario a la brevedad.", t.Textos());
        Assert.DoesNotContain(t.Textos(), m => m.StartsWith("Se le ha enviado"));
    }
}
