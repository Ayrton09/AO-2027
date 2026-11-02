using System.Collections.Concurrent;
using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class LaPercepcionDelBotEmpujadoTests
{
    [Theory]
    [InlineData(1, 72, 50)]
    [InlineData(2, 73, 51)]
    [InlineData(3, 72, 52)]
    [InlineData(4, 71, 51)]
    public void El_movimiento_forzado_del_servidor_actualiza_la_posicion_propia(byte rumbo, byte x, byte y)
    {
        var s = Sentidos(72, 51);
        s.Recibir(ServerMessages.ForceCharMove(rumbo));
        Assert.Equal((x, y), s.Posicion);
    }

    [Fact]
    public void La_paralisis_no_descarta_un_movimiento_que_el_servidor_ya_aplico()
    {
        var s = Sentidos(72, 51);
        s.Recibir(ServerMessages.ParalizeOK());
        s.Recibir(ServerMessages.ForceCharMove(4));
        Assert.Equal(((byte)71, (byte)51), s.Posicion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Un_rumbo_forzado_invalido_no_inventa_un_paso_al_oeste(byte rumbo)
    {
        var s = Sentidos(72, 51);
        s.Recibir(ServerMessages.ForceCharMove(rumbo));
        Assert.Equal(((byte)72, (byte)51), s.Posicion);
    }

    [Fact]
    public void Un_forzado_no_inventa_posicion_antes_de_ubicarse()
    {
        var s = new SentidosDelBot { MiNombre = "Aldo" };
        s.Recibir(ServerMessages.ForceCharMove(2));
        Assert.False(s.Ubicado);
        Assert.Equal(((byte)0, (byte)0), s.Posicion);
    }

    [Fact]
    public void Un_empujon_autoritativo_puede_salir_del_borde_caminable_del_jugador()
    {
        var s = Sentidos(7, 51);
        s.Recibir(ServerMessages.ForceCharMove(4));
        Assert.Equal(((byte)6, (byte)51), s.Posicion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_npc_que_empuja_un_cadaver_actualiza_su_area_y_la_percepcion_del_sacerdote(bool entra)
    {
        await using var e = new Escena(entra, conLobo: true);
        await e.Inicializar();
        var anterior = e.Cadaver.Pos;
        Assert.Equal(!entra, e.Sentidos.MasCercano(e.EsSacerdote) is not null);
        var mover = typeof(NpcSystem).GetMethod("MoveNpc", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.True((bool)mover.Invoke(e.Npcs, new object[] { e.Lobo!, (int)anterior.X, (int)anterior.Y, entra ? (byte)2 : (byte)4 })!);
        await e.Transporte.Sincronizar(e.Sesion);

        Assert.Equal(e.PosicionDelEmpujon, e.Cadaver.Pos);
        var paquetes = e.Transporte.Paquetes.ToArray();
        Assert.Contains(paquetes, p => p.SequenceEqual(ServerMessages.ForceCharMove(entra ? (byte)4 : (byte)2)));
        // El actor NO recibe su propio CharacterMove: el ForceCharMove es el único aviso de su desplazamiento.
        Assert.DoesNotContain(paquetes, p => p[0] == (byte)ServerPacketId.CharacterMove && BitConverter.ToInt16(p, 1) == e.Cadaver.CharIndex);
        if (entra)
            Assert.Contains(paquetes, p => p.SequenceEqual(ServerMessages.CharacterCreate(e.Sacerdote)));
        else
            Assert.Contains(paquetes, p => p.SequenceEqual(ServerMessages.CharacterRemove(e.Sacerdote.CharIndex, faded: true)));
        Assert.Equal(entra, e.Sentidos.MasCercano(e.EsSacerdote) is not null);
        Assert.Equal((e.Cadaver.Pos.X, e.Cadaver.Pos.Y), e.Sentidos.Posicion);
    }

    [Fact]
    public async Task El_empujon_de_un_jugador_ya_envia_posicion_vieja_force_y_al_sacerdote_que_entra_en_vista()
    {
        await using var e = new Escena(entra: true, conLobo: false);
        await e.Inicializar();
        var anterior = e.Cadaver.Pos;
        var empujar = typeof(PacketDispatcher).GetMethod("CorrerAlCasper", BindingFlags.Instance | BindingFlags.NonPublic)!;
        empujar.Invoke(e.Dispatcher, new object[] { e.Cadaver, (byte)2, e.PosicionDelEmpujon });
        await e.Transporte.Sincronizar(e.Sesion);

        var paquetes = e.Transporte.Paquetes.Where(p => p[0] != (byte)ServerPacketId.Pong).ToArray();
        Assert.Equal(ServerMessages.PosUpdate(anterior.X, anterior.Y), paquetes[0]);
        Assert.Equal(ServerMessages.ForceCharMove(4), paquetes[1]);
        Assert.Contains(paquetes, p => p.SequenceEqual(ServerMessages.CharacterCreate(e.Sacerdote)));
        Assert.DoesNotContain(paquetes, p => p[0] == (byte)ServerPacketId.CharacterMove && BitConverter.ToInt16(p, 1) == e.Cadaver.CharIndex);
        Assert.NotNull(e.Sentidos.MasCercano(e.EsSacerdote));
    }

    private static SentidosDelBot Sentidos(byte x, byte y)
    {
        var s = new SentidosDelBot { MiNombre = "Aldo" };
        s.Recibir(ServerMessages.UserIndexInServer(500)); s.Recibir(ServerMessages.ChangeMap(208));
        s.Recibir(ServerMessages.CharacterCreate(new Player { CharIndex = 500, Name = "Aldo", Account = "aldo_qa", Pos = new WorldPos(208, x, y) }));
        return s;
    }

    private sealed class Espia(SentidosDelBot sentidos) : ITransport
    {
        public ConcurrentQueue<byte[]> Paquetes { get; } = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _barreras = new();
        private int _siguiente;
        public string RemoteEndpoint => "qa:percepcion:0";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Close(string motivo) { }
        public void Send(byte[] p)
        {
            sentidos.Recibir(p); Paquetes.Enqueue(p);
            if (p[0] == (byte)ServerPacketId.Pong && _barreras.TryRemove(BitConverter.ToInt32(p, 1), out var b)) b.TrySetResult();
        }
        public async Task Sincronizar(ClientSession sesion)
        {
            int id = Interlocked.Increment(ref _siguiente);
            var b = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _barreras[id] = b; sesion.Send(ServerMessages.Pong(id));
            await b.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        public void Vaciar() { while (Paquetes.TryDequeue(out _)) { } }
    }

    private sealed class Escena : IAsyncDisposable
    {
        private readonly AlmacenSqlite _almacen;
        private readonly Persistence _db;
        private readonly GameData _datos;
        private readonly World _world = new();
        public NpcSystem Npcs { get; }
        public PacketDispatcher Dispatcher { get; }
        public Player Cadaver { get; }
        public Npc Sacerdote { get; }
        public Npc? Lobo { get; }
        public SentidosDelBot Sentidos { get; } = new() { MiNombre = "Aldo" };
        public Espia Transporte { get; }
        public ClientSession Sesion { get; }
        public WorldPos PosicionDelEmpujon { get; }
        public bool EsSacerdote(short ficha) => _datos.Npc(ficha)?.NpcType == 1;

        public Escena(bool entra, bool conLobo)
        {
            _datos = GameData.Load();
            _almacen = AlmacenSqlite.EnMemoria(); _db = new Persistence(_almacen, new Leveling(_datos.LevelExp));
            Npcs = new NpcSystem(_world, _datos);
            Dispatcher = new PacketDispatcher(_world, new GameLoop(), _db, _datos, new Leveling(_datos.LevelExp));
            Cadaver = new Player { CharIndex = 500, Name = "Aldo", Account = "aldo_qa", Hp = 0, MaxHp = 55, Pos = new WorldPos(208, entra ? (byte)72 : (byte)71, 52) };
            PosicionDelEmpujon = new WorldPos(208, entra ? (byte)71 : (byte)72, 52);
            foreach (var x in new[] { 71, 72 })
                Assert.False(_datos.IsBlockedTile(208, x, 52) || _datos.IsWater(208, x, 52), "Premisa: el borde de area debe ser tierra caminable real");
            Sacerdote = new Npc { CharIndex = 600, Template = _datos.Npc(5)!, Pos = new WorldPos(208, 55, 51), Movement = 1, IsReviver = true, Hp = _datos.Npc(5)!.MaxHp };
            Assert.Equal(1, Sacerdote.Template.NpcType); Assert.Equal(1, Sacerdote.Template.Movement);
            Assert.Equal(!entra, World.InView(Cadaver.Pos, Sacerdote.Pos));
            Assert.Equal(entra, World.InView(PosicionDelEmpujon, Sacerdote.Pos));
            _world.AddNpc(Sacerdote);
            if (conLobo)
            {
                Lobo = new Npc { CharIndex = 601, Template = _datos.Npc(501)!, Pos = PosicionDelEmpujon, Hp = 100, Movement = 1 };
                _world.AddNpc(Lobo);
            }
            Transporte = new Espia(Sentidos); Sesion = new ClientSession(Transporte, Dispatcher);
            Cadaver.Connection = Sesion; Sesion.Player = Cadaver; _world.Add(Cadaver);
        }

        public async Task Inicializar()
        {
            Sesion.Send(ServerMessages.UserIndexInServer(Cadaver.CharIndex)); Sesion.Send(ServerMessages.ChangeMap(Cadaver.Pos.Map));
            Sesion.Send(ServerMessages.CharacterCreate(Cadaver)); Sesion.Send(ServerMessages.UpdateUserStats(Cadaver));
            PacketDispatcher.RefreshAreaVisibility(_world, _datos, Cadaver, default, Cadaver.Pos);
            await Transporte.Sincronizar(Sesion); Transporte.Vaciar();
        }
        public ValueTask DisposeAsync()
        {
            Sesion.Close("fin QA"); _db.Dispose(); _almacen.Dispose(); return ValueTask.CompletedTask;
        }
    }
}
