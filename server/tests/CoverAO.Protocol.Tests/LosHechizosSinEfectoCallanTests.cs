using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS HECHIZOS SIN EFECTO CALLAN. En el original la llamada al efecto de Materializar y de Detectar invisibilidad
/// está comentada (HandleHechizoTerreno, modHechizos.bas:715-721): `HechizoCasteado` queda en False, no se cobra y
/// no se avisa nada. Acá salía un "El hechizo X aun no esta implementado." que el original no tiene.
/// </summary>
public class LosHechizosSinEfectoCallanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_sin_efecto_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public LosHechizosSinEfectoCallanTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
        _corriendo = _loop.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    [Theory]
    [InlineData(55)]   // Materializar: Comida (Tipo 7)
    [InlineData(60)]   // Materializar: Bebida (Tipo 7)
    [InlineData(62)]   // Detectar invisibilidad (Tipo 12)
    public async Task Lanzarlo_sobre_el_terreno_no_cobra_ni_avisa(short hechizo)
    {
        Assert.Equal(4, _datos.Spell(hechizo)!.Target);                           // premisa: va al terreno
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Desierto, 50, 50),
            Heading = 2, Hp = 100, MaxHp = 100, Level = 30, Class = 2, CombatMode = true,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000,
        };
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = hechizo;
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);

        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        while (transporte.Recibidos.TryDequeue(out _)) { }                       // el pedido de blanco
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, (byte)53, (byte)50, (byte)GameLimits.SkillMagia }));
        await Task.Delay(300);

        Assert.Equal(5000, p.Mana);
        Assert.Empty(transporte.Recibidos);
    }
}
