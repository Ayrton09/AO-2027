using System.Collections.Concurrent;
using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL EFECTO DE LA FLECHA QUE ACIERTA, como el original. La flecha trae su FX en la clave <c>FX1</c> del .dat (FileIO.bas:931
/// lo carga en <c>Snd2</c>), no en <c>SND2</c>, que es la de las armas y los instrumentos. Y el server lo manda con cada flecha
/// que entra aunque sea 0 (SistemaCombate.bas:918 contra una criatura, :1201 contra un jugador): un <c>CreateFX</c> con 0 borra
/// el efecto que tuviera el blanco (<c>SetCharacterFx</c>, TileEngine.bas:2622). Ninguna flecha de la data trae <c>FX1</c>, así
/// que lo que se ve es eso: la flecha que entra le corta al blanco el efecto del hechizo que le estaba mostrando.
/// </summary>
public class LaFlechaQueAciertaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_flecha_fx_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly List<(CancellationTokenSource Cts, Task Corriendo)> _loops = new();

    private const short Mapa = 15;                     // mapa inseguro: se pelea
    private const byte X = 50, Y = 50;
    private const short ArcoSimple = 989, Flecha = 480;
    private const short FxSangre = 14;                 // FXSANGRE (Declares.bas:67)
    private const byte Renegado = 1;

    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Enviados = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Enviados.Enqueue(data);
        public void Close(string reason) { }
    }

    private sealed class Conexion : IConnection
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public long UltimaActividadMs => Environment.TickCount64;
        public void Send(byte[] packet) => Recibidos.Enqueue(packet);
        public void Close(string reason) { }
    }

    public void Dispose()
    {
        foreach (var (cts, corriendo) in _loops)
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    [Fact]
    public void La_flecha_lee_su_efecto_de_FX1_y_no_de_SND2()
    {
        Directory.CreateDirectory(_root);
        string archivo = Path.Combine(_root, "obj.json");
        File.WriteAllText(archivo, """
            {"OBJ": [
              {"_index": 1, "Name": "Flecha con FX1", "ObjType": "32", "FX1": "7", "SND2": "9"},
              {"_index": 2, "Name": "Flecha con SND2", "Objtype": "32", "Snd2": "9"},
              {"_index": 3, "Name": "Espada", "Objtype": "2", "Snd2": "86", "FX1": "7"}
            ]}
            """);
        var cargar = typeof(GameData).GetMethod("LoadObjects", BindingFlags.NonPublic | BindingFlags.Static)!;
        var objetos = (Dictionary<int, ObjInfo>)cargar.Invoke(null, new object[] { archivo })!;

        Assert.Equal(7, objetos[1].Snd2);
        Assert.Equal(0, objetos[2].Snd2);    // el original lo pisa con el FX1, que no está
        Assert.Equal(86, objetos[3].Snd2);   // al arma no le cambia nada
    }

    /// <summary>Un arquero renegado en (50,50) del 15 con arco y flechas, sin la cadencia del arco.</summary>
    private (World W, PacketDispatcher D, ClientSession S, Player Yo, Transporte AMi) Arquero()
    {
        string carpeta = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var world = new World();
        var loop = new GameLoop();
        var leveling = new Leveling(_datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(new FileStore(carpeta), leveling), _datos, leveling,
            serverIni: ServerIni.Desde(IniFile.Parse(
                "[INIT]\r\n[INTERVALOS]\r\nIntervaloFlechasCazadores=0\r\nIntervaloUserPuedeAtacar=0\r\n")));
        var cts = new CancellationTokenSource();
        _loops.Add((cts, loop.RunAsync(cts.Token)));
        var yo = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(Mapa, X, Y), Heading = 2,
            Hp = 1000, MaxHp = 1000, Level = 50, Stamina = 50_000, MaxStamina = 50_000, Class = 10,
            FactionStatus = Renegado, CombatMode = true, Hunger = 100, Thirst = 100,
        };
        yo.Skills[GameLimits.SkillProyectiles] = 100;
        yo.Inventory[1] = new InventorySlot { ObjIndex = ArcoSimple, Amount = 1, Equipped = true };
        yo.Inventory[2] = new InventorySlot { ObjIndex = Flecha, Amount = 500, Equipped = true };
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, dispatcher);
        sesion.Player = yo;
        yo.Connection = sesion;
        world.Add(yo);
        return (world, dispatcher, sesion, yo, transporte);
    }

    private static async Task Tirar(PacketDispatcher d, ClientSession s, byte tx, int veces)
    {
        for (int i = 0; i < veces; i++)
        {
            d.HandleOne(s, new ByteQueueReader(new[] { (byte)ClientPacketId.WorkLeftClick, tx, Y, (byte)GameLimits.SkillProyectiles }));
            await Task.Delay(15);
        }
        await Task.Delay(300);
    }

    /// <summary>Los FX que llegaron sobre un personaje, en orden (CreateFX: CharIndex, FX, Loops).</summary>
    private static List<short> EfectosSobre(IEnumerable<byte[]> paquetes, short charIndex) =>
        paquetes.Where((p) => p.Length == 7 && p[0] == (byte)ServerPacketId.CreateFX && BitConverter.ToInt16(p, 1) == charIndex)
            .Select((p) => BitConverter.ToInt16(p, 3)).ToList();

    /// <summary>Contra un jugador: cada flecha que entra le manda a la víctima el 0 —borra lo que tuviera— y después la sangre.</summary>
    [Fact]
    public async Task La_flecha_que_entra_a_un_jugador_le_borra_el_efecto_antes_de_la_sangre()
    {
        var (w, d, s, _, _) = Arquero();
        var el = new Player
        {
            CharIndex = w.AllocateCharIndex(), Name = "Bruno", Account = "bruno", Pos = new WorldPos(Mapa, X + 1, Y), Heading = 4,
            Hp = 30_000, MaxHp = 30_000, Level = 10, Stamina = 500, MaxStamina = 500, Class = 1,
            FactionStatus = Renegado, Hunger = 100, Thirst = 100,
        };
        var alOtro = new Conexion();
        el.Connection = alOtro;
        w.Add(el);

        await Tirar(d, s, X + 1, 20);

        var fx = EfectosSobre(alOtro.Recibidos, el.CharIndex);
        Assert.Contains(FxSangre, fx);                                          // alguna entró
        Assert.Equal(fx.Count((f) => f == FxSangre), fx.Count((f) => f == 0));   // una por flecha que entra
        Assert.Equal(0, fx[0]);                                                 // y el 0 va antes que la sangre
    }

    /// <summary>Contra una criatura: la flecha que entra le manda el 0 al área, y ahí no hay sangre que lo tape.</summary>
    [Fact]
    public async Task La_flecha_que_entra_a_una_criatura_le_borra_el_efecto()
    {
        var (w, d, s, _, aMi) = Arquero();
        var plantilla = new NpcInfo(Index: 900, Name: "Blanco", Body: 1, Head: 1, Heading: 3, MaxHp: 1_000_000, MinHit: 0,
            MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var blanco = new Npc { CharIndex = w.AllocateCharIndex(), Template = plantilla, Hp = 1_000_000, Pos = new WorldPos(Mapa, X + 2, Y) };
        w.AddNpc(blanco);

        for (int i = 0; i < 20 && blanco.Hp == 1_000_000; i++)
            await Tirar(d, s, X + 2, 1);

        Assert.True(blanco.Hp < 1_000_000, "ninguna flecha entró");
        Assert.Contains((short)0, EfectosSobre(aMi.Enviados, blanco.CharIndex));
    }
}
