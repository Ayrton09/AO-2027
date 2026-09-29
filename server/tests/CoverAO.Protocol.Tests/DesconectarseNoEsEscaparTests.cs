using System.Collections.Concurrent;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DESCONECTARSE NO ES ESCAPAR. Cuando se corta la conexión el original no saca al personaje: llama a
/// <c>Cerrar_Usuario</c>, la misma salida del /SALIR (wskapiAO.bas:314-324, "no hay q llamar a CloseSocket()
/// directamente, ya q pueden abusar de algun error para desconectarse sin los 10segs"). En un mapa inseguro, vivo y
/// sin ser GM, el personaje queda en el mundo con la cuenta de salida y lo pueden matar; que lo ataquen le reinicia
/// la cuenta en vez de cancelársela (CancelExit sin conexión). Acá cerrar el cliente lo sacaba en el acto.
/// </summary>
public class DesconectarseNoEsEscaparTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_desconectar_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short MapaPk = 15, Ciudad = 1;          // Desierto Rinkel; Ullathorpe
    private const short Paralizar = 9;

    /// <summary>Anota cada paquete que el server le manda (llegan de a uno y sin cifrar) y no recibe nada.</summary>
    private sealed class Transporte : ITransport
    {
        public readonly ConcurrentQueue<byte[]> Recibidos = new();
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) => Recibidos.Enqueue(data);
        public void Close(string reason) { }
        public bool Recibio(ServerPacketId id) => Recibidos.Any(p => p.Length > 0 && p[0] == (byte)id);
    }

    public DesconectarseNoEsEscaparTests()
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

    private (Player Jugador, ClientSession Sesion, Transporte Transporte) Conectado(string nombre, short mapa, byte x = 50,
        byte y = 50, byte faccion = Player.StatusCriminal)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Heading = 2, Hp = 100, MaxHp = 100, Level = 30, FactionStatus = faccion,
            Mana = 5000, MaxMana = 5000, Stamina = 5000, MaxStamina = 5000, CombatMode = true, Class = 2,
        };
        var transporte = new Transporte();
        var sesion = new ClientSession(transporte, _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion, transporte);
    }

    private bool EnElMundo(Player p) => _world.Players.Any(x => ReferenceEquals(x, p));

    /// <summary>El tiempo de antes Y que el loop haya atendido todo lo encolado hasta acá. El corte y el cierre encolan
    /// su trabajo en el loop en el acto (OnDisconnect, SacarAlQueQuedoEsperando), así que una marca encolada después
    /// corre después. Con los 300 ms solos, la suite entera en paralelo a veces llegaba antes que el loop y el test
    /// fallaba por eso, no por el producto.</summary>
    private async Task Esperar()
    {
        await Task.Delay(300);
        var atendido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Enqueue(() => atendido.SetResult());
        await atendido.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cortarse_en_un_mapa_inseguro_deja_al_personaje_con_la_cuenta_de_salida()
    {
        Assert.True(_datos.IsPkMap(MapaPk));                                        // premisa
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.True(EnElMundo(p), "se desconectó en un mapa inseguro: tenía que quedar en el mundo");
        Assert.True(p.Saliendo);
        Assert.True(p.SinConexion);
        Assert.Equal(5, p.SalirSegundos);
    }

    /// <summary>El corte llega por el bucle de recepción: el cliente cerró el socket.</summary>
    [Fact]
    public async Task El_cliente_que_cierra_el_socket_pasa_por_la_cuenta_de_salida()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        await sesion.ReceiveLoopAsync(CancellationToken.None);                      // ReceiveAsync devuelve 0
        await Esperar();
        Assert.True(EnElMundo(p));
        Assert.True(p.Saliendo && p.SinConexion);
    }

    [Fact]
    public async Task Cortarse_en_una_ciudad_saca_en_el_acto()
    {
        Assert.False(_datos.IsPkMap(Ciudad));                                       // premisa
        var (p, sesion, _) = Conectado("Aldo", Ciudad);
        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.False(EnElMundo(p));
        Assert.Null(sesion.Player);
    }

    [Fact]
    public async Task Cortarse_muerto_saca_en_el_acto()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        p.Hp = 0;
        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.False(EnElMundo(p));
    }

    [Fact]
    public async Task Un_gm_que_se_corta_sale_en_el_acto()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        p.Privileges = 8;                                                           // Dios
        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.False(EnElMundo(p));
    }

    /// <summary>El cierre DEFINITIVO sobre el que quedó esperando lo saca: es el fin de la cuenta (Program.cs cierra
    /// con "salida solicitada") y también un /ECHAR o un baneo.</summary>
    [Fact]
    public async Task El_fin_de_la_cuenta_saca_al_que_se_desconecto()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.True(EnElMundo(p), "premisa: quedó esperando");

        sesion.Close("salida solicitada");
        await Esperar();
        Assert.False(EnElMundo(p));
        Assert.Null(sesion.Player);
    }

    /// <summary>Un cierre del server —echar, banear— sigue sacando en el acto, aunque el mapa sea inseguro.</summary>
    [Fact]
    public async Task Echar_a_un_conectado_lo_saca_en_el_acto()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        sesion.Close("expulsado por un GM");
        await Esperar();
        Assert.False(EnElMundo(p));
    }

    /// <summary>Si ya estaba saliendo, el corte no cancela la salida: la REINICIA (CancelExit sin conexión).</summary>
    [Fact]
    public async Task Cortarse_mientras_sale_reinicia_la_cuenta()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.Quit }));
        await Esperar();
        Assert.True(p.Saliendo, "premisa: el /SALIR arrancó la cuenta");
        p.SalirSegundos = 2;

        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.True(EnElMundo(p));
        Assert.True(p.Saliendo);
        Assert.Equal(5, p.SalirSegundos);
    }

    /// <summary>Al desconectado que atacan se le reinicia la cuenta: mientras lo sigan atacando no se va.</summary>
    [Fact]
    public async Task Atacar_al_desconectado_le_reinicia_la_cuenta()
    {
        var (victima, sesionVictima, _) = Conectado("Bruno", MapaPk, x: 52);
        sesionVictima.Cortar("receive loop ended");
        await Esperar();
        Assert.True(victima.Saliendo && victima.SinConexion, "premisa: quedó esperando");
        victima.SalirSegundos = 1;

        var (mago, sesionMago, _) = Conectado("Aldo", MapaPk);
        mago.Skills[GameLimits.SkillMagia] = 100;
        mago.Spells[1] = Paralizar;
        await Castear(sesionMago, victima);

        Assert.True(victima.ParalyzedUntilTick > _loop.Tick, "premisa: el ataque entró");
        Assert.True(victima.Saliendo);
        Assert.Equal(5, victima.SalirSegundos);
    }

    /// <summary>El control: al que sigue conectado, que lo ataquen le CANCELA la salida.</summary>
    [Fact]
    public async Task Atacar_al_conectado_que_sale_le_cancela_la_salida()
    {
        var (victima, sesionVictima, _) = Conectado("Bruno", MapaPk, x: 52);
        _dispatcher.HandleOne(sesionVictima, new ByteQueueReader(new[] { (byte)ClientPacketId.Quit }));
        await Esperar();
        Assert.True(victima.Saliendo, "premisa: el /SALIR arrancó la cuenta");

        var (mago, sesionMago, _) = Conectado("Aldo", MapaPk);
        mago.Skills[GameLimits.SkillMagia] = 100;
        mago.Spells[1] = Paralizar;
        await Castear(sesionMago, victima);

        Assert.False(victima.Saliendo);
    }

    /// <summary>No te vas invisible: el corte destapa al invisible ante los demás, como el /SALIR.</summary>
    [Fact]
    public async Task Cortarse_destapa_al_invisible()
    {
        var (p, sesion, _) = Conectado("Aldo", MapaPk);
        p.Invisible = true;
        p.InvisibleUntilTick = long.MaxValue;
        var (_, _, ojos) = Conectado("Bruno", MapaPk, x: 53);

        sesion.Cortar("receive loop ended");
        await Esperar();
        Assert.False(p.Invisible);
        Assert.True(ojos.Recibio(ServerPacketId.SetInvisible), "el de al lado tenía que verlo aparecer");
    }

    /// <summary>El IdleLimit: el aviso 33 y la salida de siempre, con la conexión viva. En un mapa inseguro le corre
    /// la cuenta como a cualquiera.</summary>
    [Fact]
    public async Task El_inactivo_en_un_mapa_inseguro_sale_con_la_cuenta()
    {
        var (p, sesion, transporte) = Conectado("Aldo", MapaPk);
        _loop.Enqueue(() => _dispatcher.EcharPorInactividad(p));
        await Esperar();
        Assert.True(EnElMundo(p));
        Assert.True(p.Saliendo);
        Assert.False(p.SinConexion);
        Assert.True(transporte.Recibio(ServerPacketId.ShowMessageBox));
        Assert.False(sesion.Cerrada);
    }

    [Fact]
    public async Task El_inactivo_en_una_ciudad_sale_en_el_acto()
    {
        var (p, sesion, _) = Conectado("Aldo", Ciudad);
        _loop.Enqueue(() => _dispatcher.EcharPorInactividad(p));
        await Esperar();
        Assert.False(EnElMundo(p));
        Assert.True(sesion.Cerrada);
    }

    private async Task Castear(ClientSession sesion, Player blanco)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.CastSpell, (byte)1 }));
        await Task.Delay(150);
        _dispatcher.HandleOne(sesion, new ByteQueueReader(new[]
            { (byte)ClientPacketId.WorkLeftClick, blanco.Pos.X, (byte)(blanco.Pos.Y - 1), (byte)GameLimits.SkillMagia }));
        await Esperar();
    }
}
