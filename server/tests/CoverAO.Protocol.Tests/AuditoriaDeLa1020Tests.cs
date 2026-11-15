using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los arreglos de la auditoría de la 1.0.20 que se pueden fijar sin armar un server entero: la runa estando muerto, el preso
/// que no sale de la cárcel con ella ni con el regreso al hogar, la cabeza en cero que no se guarda y la vida del familiar
/// en el mundo.
/// </summary>
public class AuditoriaDeLa1020Tests : IDisposable
{
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Mapa = 15;     // Desierto Rinkel: Pk
    private const short Runa = 1600;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public AuditoriaDeLa1020Tests()
    {
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
    }

    private (Player Jugador, ClientSession Sesion) Muerto(int minutosDeCarcel = 0)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(Mapa, 50, 50), Heading = 2, Hp = 0, MaxHp = 100, Level = 20,
            HomeCity = Cities.Ullathorpe, JailMinutesLeft = minutosDeCarcel,
        };
        p.Inventory[1] = new InventorySlot { ObjIndex = Runa, Amount = 1 };
        var sesion = new ClientSession(new Transporte(), _dispatcher) { Player = p };
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    /// <summary>Manda el paquete y espera a que el loop lo atienda: una marca encolada detrás corre después que él.</summary>
    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        var listo = new TaskCompletionSource();
        _loop.Enqueue(() => listo.SetResult());
        await listo.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Muerto_la_runa_lo_lleva_al_hogar_como_fantasma()
    {
        Assert.True(_datos.Obj(Runa) is { ObjType: 38 }, "premisa: la 1600 es la runa");
        var (p, sesion) = Muerto();
        await Mandar(sesion, (byte)ClientPacketId.UseItem, 1);
        Assert.Equal(Cities.DeadTile(Cities.Ullathorpe), p.Pos);
    }

    [Fact]
    public async Task Preso_y_muerto_la_runa_no_lo_saca()
    {
        var (p, sesion) = Muerto(minutosDeCarcel: 10);
        var antes = p.Pos;
        await Mandar(sesion, (byte)ClientPacketId.UseItem, 1);
        Assert.Equal(antes, p.Pos);
    }

    [Fact]
    public async Task Preso_y_muerto_el_regreso_al_hogar_no_lo_saca()
    {
        var (p, sesion) = Muerto(minutosDeCarcel: 10);
        var antes = p.Pos;
        await Mandar(sesion, (byte)ClientPacketId.RegresarHogar);
        Assert.Equal(antes, p.Pos);
        Assert.Equal(10, p.JailMinutesLeft);
    }

    [Fact]
    public void La_cabeza_en_cero_se_guarda_como_la_de_siempre()
    {
        var p = new Player { CharIndex = 1, Account = "aldo", Name = "Aldo", Hp = 10, MaxHp = 10, Head = 0, OrigHead = 7 };   // navegando
        Assert.Equal(7, FichaDePersonaje.Desde(p).Cabeza);
        p.Head = 12;
        Assert.Equal(12, FichaDePersonaje.Desde(p).Cabeza);
    }

    [Fact]
    public void Con_el_familiar_en_el_mundo_la_vida_es_la_de_su_criatura()
    {
        var plantilla = _datos.Npc(Familiar.FichaDe(Familiar.Lobo));
        Assert.NotNull(plantilla);
        var fam = new Familiar { Tipo = Familiar.Lobo, Vida = 80, VidaMaxima = 80 };
        var criatura = new Npc { CharIndex = 9, Template = plantilla!, Hp = 80 };
        fam.PonerEnElMundo(criatura);
        criatura.Hp = 30;                         // le pegaron
        Assert.Equal(30, fam.Vida);
        fam.CharIndexEnElMundo = 0;               // lo retiraron
        criatura.Hp = 5;
        Assert.Equal(80, fam.Vida);
    }
}
