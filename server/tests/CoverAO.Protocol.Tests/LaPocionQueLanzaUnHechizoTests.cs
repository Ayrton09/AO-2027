using System.Collections.Concurrent;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA POCIÓN QUE LANZA UN HECHIZO, COMO EL ORIGINAL (InvUsuario.bas:1635-1641): pasa por el MISMO HechizoEstadoUsuario
/// que el hechizo sobre uno mismo, así que tiene sus cortes (el 451, el 448, nada si no estás quieto) y su aviso (las
/// palabras, el 0 o el 499), no cobra maná, y se gasta sólo si quedó lanzado. Y no suena a trago: el SND_BEBER es de
/// los cuatro Case que se beben. Acá contaba efectos con una copia vieja de los bloques, se gastaba sin estar
/// paralizado, sonaba siempre y avisaba con textos nuestros.
/// </summary>
public class LaPocionQueLanzaUnHechizoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_pocion_hechizo_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Desierto = 15;
    private const short PocionVioleta = 166, RemoverParalisis = 893, Invisibilidad = 1096, Sanar = 1597, PocionRoja = 38;
    private const short SndBeber = 135;

    /// <summary>Anota cada paquete que el server le manda (llegan de a uno y sin cifrar) y no recibe nada.</summary>
    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
    }

    public LaPocionQueLanzaUnHechizoTests()
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

    private (Player Jugador, ClientSession Sesion, Transporte Transporte) Conectado(string nombre, byte x)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(Desierto, x, 50), Heading = 2, Hp = 50, MaxHp = 1000, Level = 30,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, Class = 2,
        };
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, transporte);
    }

    /// <summary>Con la poción en la ranura 1, la toma.</summary>
    private async Task Tomar(Player p, ClientSession sesion, short pocion, int cuantas = 5)
    {
        p.Inventory[1] = new InventorySlot { ObjIndex = pocion, Amount = (short)cuantas };
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.UseItem, (byte)1 }));
        await Task.Delay(300);
    }

    private static bool EsTrago(byte[] x) =>
        x.Length >= 3 && x[0] == (byte)ServerPacketId.PlayWave && BitConverter.ToInt16(x, 1) == SndBeber;

    /// <summary>Los LocaleMsg que llegaron, como (id, texto, fuente).</summary>
    private static List<(short Id, string Texto, byte Fuente)> Avisos(Transporte t) => t.Recibidos
        .Where(x => x.Length >= 7 && x[0] == (byte)ServerPacketId.LocaleMsg)
        .Select(x =>
        {
            int largo = BitConverter.ToInt16(x, 3);
            return (BitConverter.ToInt16(x, 1), Encoding.Latin1.GetString(x, 5, largo), x[5 + largo + 1]);
        }).ToList();

    /// <summary>¿Dijo las palabras de ese hechizo? (ChatOverHeadLocale en modo 1, con el índice del hechizo.)</summary>
    private static bool DijoLasPalabras(Transporte t, Player p, short hechizo) => t.Recibidos.Any(x =>
        x.Length >= 8 && x[0] == (byte)ServerPacketId.ChatOverHeadLocale && BitConverter.ToInt16(x, 1) == p.CharIndex
        && BitConverter.ToInt32(x, 3) == hechizo && x[7] == 1);

    [Fact]
    public async Task La_de_remover_paralisis_sin_estar_quieto_no_se_gasta_ni_dice_nada()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        await Tomar(p, sesion, RemoverParalisis);
        Assert.Equal(5, p.Inventory[1].Amount);
        Assert.Empty(suyo.Recibidos);
    }

    [Fact]
    public async Task La_de_remover_paralisis_libera_y_avisa_como_el_hechizo_sin_sonar_a_trago()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        p.ParalyzedUntilTick = long.MaxValue;
        await Tomar(p, sesion, RemoverParalisis);
        Assert.Equal(0, p.ParalyzedUntilTick);
        Assert.Equal(4, p.Inventory[1].Amount);
        Assert.True(DijoLasPalabras(suyo, p, 10), "el InfoHechizo dice las palabras del hechizo");
        Assert.Contains(((short)0, "=10", (byte)2), Avisos(suyo));
        Assert.DoesNotContain(suyo.Recibidos, EsTrago);
        Assert.DoesNotContain(suyo.Recibidos, x => x.Length > 0 && x[0] == (byte)ServerPacketId.ConsoleMsg);
    }

    [Fact]
    public async Task La_violeta_estando_sano_avisa_el_451_y_no_se_gasta()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        await Tomar(p, sesion, PocionVioleta);
        Assert.Equal(5, p.Inventory[1].Amount);
        Assert.Equal(new short[] { 451 }, Avisos(suyo).Select(a => a.Id));
        Assert.DoesNotContain(suyo.Recibidos, EsTrago);
    }

    [Fact]
    public async Task La_de_invisibilidad_donde_no_hay_invisibilidad_avisa_el_448_y_no_se_gasta()
    {
        _datos.SetMapNoInvi(Desierto, true);
        try
        {
            var (p, sesion, suyo) = Conectado("Aldo", 50);
            await Tomar(p, sesion, Invisibilidad);
            Assert.False(p.Invisible);
            Assert.Equal(5, p.Inventory[1].Amount);
            Assert.Equal(new short[] { 448 }, Avisos(suyo).Select(a => a.Id));
        }
        finally { _datos.SetMapNoInvi(Desierto, false); }
    }

    [Fact]
    public async Task La_de_invisibilidad_se_gasta_aunque_ya_estes_invisible()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        await Tomar(p, sesion, Invisibilidad);
        Assert.True(p.Invisible, "premisa: la primera lo hace invisible");
        Assert.Contains(suyo.Recibidos, x => x.Length >= 4 && x[0] == (byte)ServerPacketId.SetInvisible
                                             && BitConverter.ToInt16(x, 1) == p.CharIndex && x[3] != 0);
        await Tomar(p, sesion, Invisibilidad, cuantas: 4);
        Assert.Equal(3, p.Inventory[1].Amount);
    }

    [Fact]
    public async Task La_de_sanar_cura_con_el_499_verde_y_no_cobra_mana()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        p.PoisonedUntilTick = long.MaxValue;
        await Tomar(p, sesion, Sanar);
        Assert.True(p.Hp > 50, "tenía que curar");
        Assert.False(p.IsPoisoned(_loop.Tick), "la sanación saca el veneno");
        Assert.Equal(5000, p.Mana);
        Assert.Equal(4, p.Inventory[1].Amount);
        var aviso = Assert.Single(Avisos(suyo));
        Assert.Equal((short)499, aviso.Id);
        Assert.Equal($"^32%{p.Hp - 50}", aviso.Texto);
        Assert.Equal(13, aviso.Fuente);
        Assert.True(DijoLasPalabras(suyo, p, 32));
        Assert.DoesNotContain(suyo.Recibidos, EsTrago);
    }

    [Fact]
    public async Task La_roja_manda_la_vida_y_despues_el_trago()
    {
        var (p, sesion, suyo) = Conectado("Aldo", 50);
        await Tomar(p, sesion, PocionRoja);
        var lista = suyo.Recibidos.ToList();
        int vida = lista.FindIndex(x => x.Length > 0 && x[0] == (byte)ServerPacketId.UpdateHP);
        int trago = lista.FindIndex(EsTrago);
        Assert.True(vida >= 0 && vida < trago, "el UpdateHP va antes del SND_BEBER");
    }

    [Fact]
    public async Task Al_invisible_por_hechizo_que_toma_lo_escucha_el_area_y_al_GM_invisible_no()
    {
        var (p, sesion, _) = Conectado("Aldo", 50);
        var (_, _, delOtro) = Conectado("Bruno", 52);
        p.Invisible = true;
        p.InvisibleUntilTick = long.MaxValue;                                     // la del hechizo tiene reloj
        await Tomar(p, sesion, PocionRoja);
        Assert.Contains(delOtro.Recibidos, EsTrago);

        while (delOtro.Recibidos.TryDequeue(out _)) { }
        p.InvisibleUntilTick = 0;
        p.AdminInvisible = true;                                                  // la del /INVISIBLE, no
        await Tomar(p, sesion, PocionRoja);
        Assert.DoesNotContain(delOtro.Recibidos, EsTrago);
    }

    /// <summary>La misma compuerta en la bebida y en el instrumento (InvUsuario.bas:1709 y :1853): es la del GM.</summary>
    [Fact]
    public async Task Al_invisible_por_hechizo_que_bebe_o_toca_un_instrumento_lo_escucha_el_area()
    {
        var bebida = _datos.Objects.Values.First(o => o.IsDrink && o.MinSed > 0);
        var instrumento = _datos.Objects.Values.First(o => o.IsInstrument && o.Snd1 > 0);
        var (p, sesion, _) = Conectado("Aldo", 50);
        var (_, _, delOtro) = Conectado("Bruno", 52);
        p.Invisible = true;
        p.InvisibleUntilTick = long.MaxValue;
        p.Thirst = 0;
        await Tomar(p, sesion, (short)bebida.Index);
        Assert.Contains(delOtro.Recibidos, EsTrago);
        await Tomar(p, sesion, (short)instrumento.Index);
        Assert.Contains(delOtro.Recibidos, x => x.Length >= 3 && x[0] == (byte)ServerPacketId.PlayWave
                                                && BitConverter.ToInt16(x, 1) == instrumento.Snd1);
    }
}
