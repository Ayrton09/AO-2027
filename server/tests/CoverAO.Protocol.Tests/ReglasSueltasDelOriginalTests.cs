using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// REGLAS SUELTAS DEL ORIGINAL que el port no tenía, cada una con su control positivo:
/// <list type="bullet">
/// <item>el nivel mínimo para USAR un objeto (InvUsuario.bas:1342), no sólo para equiparlo;</item>
/// <item>el Consejero sin RoleMaster no levanta, no tira ni destruye (Protocol.bas:1757, :2018, :2109);</item>
/// <item>los contadores del bucle disparan una vuelta después de llegar (General.bas);</item>
/// <item>en la arena no se roba (Trabajo.bas:1490);</item>
/// <item>la bóveda de objetos pide que el objetivo sea el banquero, y matar deja sin objetivo (Protocol.bas:3480,
/// :3585; MODULO_NPCs.bas:107);</item>
/// <item>parado en una baldosa con trigger 1 no se pesca (Acciones.bas:35);</item>
/// <item>el esfuerzo de la faena se cobra aunque no alcance (Trabajo.bas:1827, 2478).</item>
/// </list>
/// </summary>
public class ReglasSueltasDelOriginalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_reglas_sueltas_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _corriendo;
    private readonly PacketDispatcher _dispatcher;

    private const short Barca = 474, Cana = 881, Oro = 12, Pocion = 38;
    private const byte PrivUser = 1, PrivConsejero = 2;

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ReglasSueltasDelOriginalTests()
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

    private (Player Jugador, ClientSession Sesion) Jugador(short mapa, byte x, byte y, string nombre = "Aldo")
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = nombre, Account = nombre.ToLowerInvariant(),
            Pos = new WorldPos(mapa, x, y), Heading = 2, Hp = 100, MaxHp = 100, Level = 1,
            Stamina = 100, MaxStamina = 100, Mana = 5000, MaxMana = 5000,
        };
        var sesion = new ClientSession(new Transporte(), _dispatcher);
        sesion.Player = p;
        p.Connection = sesion;
        _world.Add(p);
        return (p, sesion);
    }

    private async Task Mandar(ClientSession sesion, params byte[] paquete)
    {
        _dispatcher.HandleOne(sesion, new ByteQueueReader(paquete));
        await Task.Delay(250);
    }

    // ---- EL NIVEL MÍNIMO PARA USAR -------------------------------------------------------------------------

    /// <summary>Una baldosa de tierra pegada al agua: ahí la Barca se puede usar.</summary>
    private (short Mapa, byte X, byte Y) Orilla()
    {
        foreach (short mapa in new short[] { 1, 15, 23, 34, 47 })
            for (byte y = 10; y < 90; y++)
                for (byte x = 10; x < 90; x++)
                    if (!_datos.IsWater(mapa, x, y) && _datos.WaterAdjacent(mapa, x, y)) return (mapa, x, y);
        throw new InvalidOperationException("no hay orilla en los mapas mirados");
    }

    private async Task<Player> UsarLaBarca(int nivel)
    {
        Assert.Equal(25, _datos.Obj(Barca)!.MinLevel);                       // premisa: la Barca pide nivel 25
        var (mapa, x, y) = Orilla();
        var (p, sesion) = Jugador(mapa, x, y);
        p.Level = (byte)nivel;
        p.Skills[GameLimits.SkillNavegacion] = 100;
        p.Inventory[1] = new InventorySlot { ObjIndex = Barca, Amount = 1 };
        await Mandar(sesion, (byte)ClientPacketId.UseItem, 1);
        return p;
    }

    [Fact]
    public async Task Sin_el_nivel_la_barca_no_se_usa()
    {
        var p = await UsarLaBarca(20);
        Assert.False(p.Navigating);
    }

    [Fact]
    public async Task Con_el_nivel_la_barca_se_usa()
    {
        var p = await UsarLaBarca(25);
        Assert.True(p.Navigating, "con nivel 25 tenía que embarcar: si no, el test de arriba no prueba nada");
    }

    // ---- EL CONSEJERO NO LEVANTA NI TIRA --------------------------------------------------------------------

    [Theory]
    [InlineData(PrivConsejero, false)]
    [InlineData(PrivUser, true)]
    public async Task El_consejero_no_levanta_oro(byte privilegios, bool levanta)
    {
        var (p, sesion) = Jugador(15, 50, 50);
        p.Privileges = privilegios;
        _world.SetGround(p.Pos, Oro, 500);
        await Mandar(sesion, (byte)ClientPacketId.PickUp);
        Assert.Equal(levanta ? 500 : 0, p.Gold);
    }

    [Theory]
    [InlineData(PrivConsejero, 1000)]
    [InlineData(PrivUser, 900)]
    public async Task El_consejero_no_tira_oro(byte privilegios, int queda)
    {
        var (p, sesion) = Jugador(15, 50, 50);
        p.Privileges = privilegios;
        p.Gold = 1000;
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.Drop)
            .WriteByte((byte)(GameLimits.MaxInventorySlots + 1)).WriteLong(100);
        await Mandar(sesion, w.ToArray());
        Assert.Equal(queda, p.Gold);
    }

    [Theory]
    [InlineData(PrivConsejero, 5)]
    [InlineData(PrivUser, 4)]
    public async Task El_consejero_no_destruye(byte privilegios, int quedan)
    {
        var (p, sesion) = Jugador(15, 50, 50);
        p.Privileges = privilegios;
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.DropDestroy).WriteByte(1).WriteLong(1);
        await Mandar(sesion, w.ToArray());
        Assert.Equal(quedan, p.Inventory[1].Amount);
    }

    // ---- LOS CONTADORES DEL BUCLE ----------------------------------------------------------------------------

    /// <summary>Con intervalo 5 el efecto sale en la SEXTA vuelta y después cada seis: <c>If cnt &lt; 5 Then
    /// cnt += 1 Else (efecto): cnt = 0</c>.</summary>
    [Fact]
    public void El_contador_dispara_una_vuelta_despues_de_llegar()
    {
        int contador = 0;
        var disparos = new List<int>();
        for (int vuelta = 1; vuelta <= 18; vuelta++)
            if (Survival.Cumplio(ref contador, 5)) disparos.Add(vuelta);
        Assert.Equal(new[] { 6, 12, 18 }, disparos);
    }

    // ---- EN LA ARENA NO SE ROBA ------------------------------------------------------------------------------

    private async Task<Player> Robar(short mapa, byte x, byte y)
    {
        var (ladron, sesion) = Jugador(mapa, x, y, "Ladron");
        ladron.FactionStatus = Player.StatusCriminal;
        var (victima, _) = Jugador(mapa, (byte)(x + 1), y, "Victima");
        victima.Gold = 10_000;
        await Mandar(sesion, (byte)ClientPacketId.WorkLeftClick, (byte)(x + 1), y, (byte)GameLimits.SkillRobar);
        return ladron;
    }

    [Fact]
    public async Task En_la_arena_no_se_roba_ni_se_cobra_la_energia()
    {
        const short IslaBeleta = 47;
        Assert.True(_datos.IsPkMap(IslaBeleta));                                   // premisas de la data
        Assert.True(_datos.IsArena(IslaBeleta, 80, 30) && _datos.IsArena(IslaBeleta, 81, 30));
        var ladron = await Robar(IslaBeleta, 80, 30);
        Assert.Equal(100, ladron.Stamina);
    }

    [Fact]
    public async Task Fuera_de_la_arena_el_robo_cobra_la_energia()
    {
        Assert.False(_datos.IsArena(15, 50, 50));
        var ladron = await Robar(15, 50, 50);
        Assert.Equal(85, ladron.Stamina);                                          // el control: QuitarSta(15)
    }

    // ---- LA BÓVEDA DE OBJETOS PIDE AL BANQUERO ---------------------------------------------------------------

    private Npc Banquero(WorldPos pos)
    {
        var ficha = new NpcInfo(Index: 901, Name: "Banquero de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 0,
            MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, NpcType: 4);
        var npc = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 100, Pos = pos, IsBanker = true };
        _world.AddNpc(npc);
        return npc;
    }

    private async Task<Player> Depositar(bool conElBanquero, bool vivo = true)
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var banquero = Banquero(new WorldPos(1, 51, 50));
        p.BankOpen = true;
        p.TargetNpc = conElBanquero ? (short)banquero.CharIndex : (short)0;
        if (!vivo) p.Hp = 0;
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        var w = new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankDeposit).WriteByte(1).WriteInteger(5);
        await Mandar(sesion, w.ToArray());
        return p;
    }

    [Fact]
    public async Task Con_el_banquero_de_objetivo_se_deposita()
    {
        var p = await Depositar(conElBanquero: true);
        Assert.True(p.Inventory[1].IsEmpty, "con el banquero de objetivo tenía que depositar");
    }

    [Fact]
    public async Task Sin_el_banquero_de_objetivo_no_se_deposita()
    {
        var p = await Depositar(conElBanquero: false);
        Assert.Equal(5, p.Inventory[1].Amount);
    }

    [Fact]
    public async Task Muerto_no_se_deposita()
    {
        var p = await Depositar(conElBanquero: true, vivo: false);
        Assert.Equal(5, p.Inventory[1].Amount);
    }

    /// <summary>Matar una criatura deja al que la mata sin objetivo: con un hechizo que siempre pega, sobre una
    /// criatura de una vida. El casteo la deja de objetivo (LookatTile) y la muerte la tiene que borrar.</summary>
    [Fact]
    public async Task Matar_una_criatura_deja_sin_objetivo()
    {
        const short ProyectilMagico = 2;
        var (p, sesion) = Jugador(15, 50, 50);
        p.CombatMode = true; p.Level = 30;
        p.Skills[GameLimits.SkillMagia] = 100;
        p.Spells[1] = ProyectilMagico;
        var ficha = new NpcInfo(Index: 902, Name: "Rata de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 1, MinHit: 0,
            MaxHit: 0, Def: 0, GiveExp: 1, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true, Movement: 1);
        var rata = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 1, Pos = new WorldPos(15, 52, 50) };
        _world.AddNpc(rata);
        await Mandar(sesion, (byte)ClientPacketId.CastSpell, 1);
        await Mandar(sesion, (byte)ClientPacketId.WorkLeftClick, 52, 50, (byte)GameLimits.SkillMagia);
        Assert.False(rata.IsAlive, "premisa: el hechizo tenía que matarla");
        Assert.Equal(0, p.TargetNpc);
    }

    // ---- PESCAR BAJO TECHO -----------------------------------------------------------------------------------

    private async Task<Player> Pescar(short mapa, byte x, byte y, byte aguaX, byte aguaY)
    {
        var (p, sesion) = Jugador(mapa, x, y);
        p.Skills[GameLimits.SkillPesca] = 100;
        p.Inventory[1] = new InventorySlot { ObjIndex = Cana, Amount = 1, Equipped = true };
        await Mandar(sesion, (byte)ClientPacketId.WorkLeftClick, aguaX, aguaY, (byte)GameLimits.SkillPesca);
        return p;
    }

    [Fact]
    public async Task Parado_bajo_techo_no_se_pesca()
    {
        Assert.Equal(1, _datos.TileTrigger(23, 78, 46));                           // premisas de la data
        Assert.True(_datos.IsWater(23, 70, 43));
        var p = await Pescar(23, 78, 46, 70, 43);
        Assert.False(p.Laborando);
    }

    [Fact]
    public async Task A_la_intemperie_se_pesca()
    {
        var (mapa, x, y) = Orilla();
        Assert.NotEqual(1, _datos.TileTrigger(mapa, x, y));
        (byte ax, byte ay) = _datos.IsWater(mapa, x + 1, y) ? ((byte)(x + 1), y) : _datos.IsWater(mapa, x - 1, y) ? ((byte)(x - 1), y)
                           : _datos.IsWater(mapa, x, y + 1) ? (x, (byte)(y + 1)) : (x, (byte)(y - 1));
        var p = await Pescar(mapa, x, y, ax, ay);
        Assert.True(p.Laborando, "a la intemperie y con agua al lado tenía que arrancar a pescar");
    }

    // ---- EL CANSANCIO DE LA FAENA ----------------------------------------------------------------------------

    /// <summary>Con 2 de energía y un esfuerzo de 3 (pescar sin ser Pescador) el original hace la vuelta igual y
    /// la energía queda en cero; recién en la vuelta siguiente la faena se corta por "poca energía".</summary>
    [Fact]
    public void El_esfuerzo_se_cobra_aunque_no_alcance()
    {
        var (mapa, x, y) = Orilla();
        var (p, sesion) = Jugador(mapa, x, y);
        p.Class = 1;                                                               // no es Pescador
        p.Skills[GameLimits.SkillPesca] = 100;
        p.Inventory[1] = new InventorySlot { ObjIndex = Cana, Amount = 1, Equipped = true };
        p.Laborando = true;
        p.Stamina = 2;
        long ahora = 1_000_000;
        _dispatcher.TickTrabajo(sesion, p, ahora);
        Assert.Equal(0, p.Stamina);
        Assert.True(p.Laborando, "la vuelta con 2 de energía se hace: la faena sigue prendida");
        _dispatcher.TickTrabajo(sesion, p, ahora + _dispatcher.IntervaloTrabajoMs);
        Assert.False(p.Laborando, "con menos de 2, la vuelta siguiente corta la faena");
    }
}
