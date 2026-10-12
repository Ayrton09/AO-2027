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
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
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

    // ---- LO QUE EL CLIENTE NO MANDA ---------------------------------------------------------------------------

    /// <summary>Un rumbo fuera de 1..4 no se guarda: el cliente web dibuja el cuerpo con <c>walk[heading - 1]</c> y el
    /// personaje quedaba invisible para los demás.</summary>
    [Fact]
    public async Task Caminar_con_un_rumbo_invalido_no_lo_guarda_ni_lo_mueve()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.Walk).WriteByte(5).ToArray());
        Assert.Equal(2, p.Heading);
        Assert.Equal(new WorldPos(1, 50, 50), p.Pos);
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.Walk).WriteByte(0).ToArray());
        Assert.Equal(2, p.Heading);
    }

    [Fact]
    public void La_ficha_con_un_rumbo_invalido_se_carga_mirando_al_sur()
    {
        Assert.Equal(3, new FichaDePersonaje { Nombre = "Rumbo", Rumbo = 9 }.CrearPlayer(1, "rumbo").Heading);
        Assert.Equal(4, new FichaDePersonaje { Nombre = "Rumbo", Rumbo = 4 }.CrearPlayer(1, "rumbo").Heading);
    }

    /// <summary>Cero o menos no mueve nada (el original: <c>If num > 0</c>, <c>Cantidad > 0</c>, <c>Cantidad < 1 Then Exit
    /// Sub</c>); el recorte a 1 movía una unidad, y destruir con un negativo en el original duplica.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Tirar_o_destruir_cero_o_menos_no_hace_nada(int cantidad)
    {
        var (p, sesion) = Jugador(1, 50, 50);
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.Drop).WriteByte(1).WriteLong(cantidad).ToArray());
        Assert.Equal(5, p.Inventory[1].Amount);
        Assert.False(_world.TryGetGround(p.Pos, out _), "no tenía que quedar nada en el piso");
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.DropDestroy).WriteByte(1).WriteLong(cantidad).ToArray());
        Assert.Equal(5, p.Inventory[1].Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Depositar_o_retirar_cero_o_menos_no_hace_nada(short cantidad)
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var banquero = Banquero(new WorldPos(1, 51, 50));
        p.BankOpen = true;
        p.TargetNpc = (short)banquero.CharIndex;
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        p.BankInventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 7 };
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankDeposit).WriteByte(1).WriteInteger(cantidad).ToArray());
        Assert.Equal(5, p.Inventory[1].Amount);
        Assert.Equal(7, p.BankInventory[1].Amount);
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankExtractItem).WriteByte(1).WriteInteger(cantidad).ToArray());
        Assert.Equal(5, p.Inventory[1].Amount);
        Assert.Equal(7, p.BankInventory[1].Amount);
        await Mandar(sesion, new ByteQueueWriter().WriteByte((byte)ClientPacketId.BankDeposit).WriteByte(1).WriteInteger(2).ToArray());
        Assert.Equal(3, p.Inventory[1].Amount);                                    // el control: con 2 deposita
    }

    // ---- LO QUE EL CLIENTE NO MANDA NUNCA, CUENTA COMO TRAMPA -------------------------------------------------

    private static byte[] P(ClientPacketId id, Action<ByteQueueWriter> armar)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id);
        armar(w);
        return w.ToArray();
    }

    private static byte[] Skills(byte cuantos) => P(ClientPacketId.ModifySkills, w =>
    {
        for (int i = 1; i <= GameLimits.NumSkills; i++) w.WriteByte(i == 1 ? cuantos : (byte)0);
    });

    /// <summary>Valores que el cliente oficial no puede producir nunca (PacketDispatcher.Imposible).</summary>
    public static TheoryData<string, byte[]> PaquetesImposibles => new()
    {
        { "caminar con el rumbo 5", P(ClientPacketId.Walk, w => w.WriteByte(5)) },
        { "girar al rumbo 0", P(ClientPacketId.ChangeHeading, w => w.WriteByte(0)) },
        { "el hechizo 0", P(ClientPacketId.CastSpell, w => w.WriteByte(0)) },
        { "el hechizo 36", P(ClientPacketId.CastSpell, w => w.WriteByte(36)) },
        { "usar la ranura 26", P(ClientPacketId.UseItem, w => w.WriteByte(26)) },
        { "equipar la ranura 0", P(ClientPacketId.EquipItem, w => w.WriteByte(0)) },
        { "tirar 0", P(ClientPacketId.Drop, w => w.WriteByte(1).WriteLong(0)) },
        { "tirar de la ranura 27", P(ClientPacketId.Drop, w => w.WriteByte(27).WriteLong(1)) },
        { "tirar 100.001 de oro", P(ClientPacketId.Drop, w => w.WriteByte(26).WriteLong(100_001)) },
        { "destruir -1", P(ClientPacketId.DropDestroy, w => w.WriteByte(1).WriteLong(-1)) },
        { "cambiar con la ranura 30", P(ClientPacketId.SwapObjects, w => w.WriteByte(1).WriteByte(30)) },
        { "mover un hechizo con el sentido 2", P(ClientPacketId.MoveSpell, w => w.WriteByte(2).WriteByte(1)) },
        { "mover la ranura 41 de la bóveda", P(ClientPacketId.MoveBank, w => w.WriteByte(1).WriteByte(41)) },
        { "depositar 10.001", P(ClientPacketId.BankDeposit, w => w.WriteByte(1).WriteInteger(10_001)) },
        { "retirar de la ranura 41", P(ClientPacketId.BankExtractItem, w => w.WriteByte(41).WriteInteger(1)) },
        { "comprar 0", P(ClientPacketId.CommerceBuy, w => w.WriteByte(1).WriteInteger(0)) },
        { "vender -5", P(ClientPacketId.CommerceSell, w => w.WriteByte(1).WriteInteger(-5)) },
        { "forjar 1001", P(ClientPacketId.CraftBlacksmith, w => w.WriteInteger(1).WriteInteger(1001)) },
        { "la receta 0 de carpintería", P(ClientPacketId.CraftCarpenter, w => w.WriteInteger(0).WriteInteger(1)) },
        { "confeccionar 0", P(ClientPacketId.CraftSastre, w => w.WriteInteger(1).WriteInteger(0)) },
        { "preparar 2000", P(ClientPacketId.Craftalquimia, w => w.WriteInteger(1).WriteInteger(2000)) },
        { "entrenar la criatura 3", P(ClientPacketId.Train, w => w.WriteByte(3)) },
        { "101 puntos a un skill", Skills(101) },
        { "el aspecto 2", P(ClientPacketId.EquiparSkin, w => w.WriteByte(2).WriteByte(0)) },
        { "el familiar 10", P(ClientPacketId.ElegirFamiliar, w => w.WriteByte(10).WriteAsciiString("Fido")) },
        { "hablar en el modo 4", P(ClientPacketId.Talk, w => w.WriteAsciiString("hola").WriteByte(4)) },
        { "apuntar con el skill 7", P(ClientPacketId.WorkLeftClick, w => w.WriteByte(50).WriteByte(50).WriteByte(7)) },
        { "trabajar con el skill 2", P(ClientPacketId.Work, w => w.WriteByte(2)) },
        { "el formulario 2", P(ClientPacketId.AbrirForms, w => w.WriteByte(2)) },
        { "el hogar en el modo 2", P(ClientPacketId.SeleccionarHogar, w => w.WriteByte(2)) },
        { "la amistad en el caso 3", P(ClientPacketId.AddAmigos, w => w.WriteAsciiString("Nadie").WriteByte(3)) },
        { "el premio 0", P(ClientPacketId.RPremios, w => w.WriteInteger(0)) },
        { "subastar por 5 horas", P(ClientPacketId.SubastaPublicar, w => w.WriteByte(1).WriteInteger(1).WriteLong(10).WriteLong(20).WriteByte(5)) },
        { "ofertar por la subasta 0", P(ClientPacketId.SubastaOfertar, w => w.WriteLong(0).WriteLong(10)) },
        { "comprar la subasta -1", P(ClientPacketId.SubastaComprar, w => w.WriteLong(-1)) },
        { "cancelar la subasta 0", P(ClientPacketId.SubastaCancelar, w => w.WriteLong(0)) },
        { "un clan con la alineación 3", P(ClientPacketId.GuildFundation, w => w.WriteByte(3)) },
        { "un ping negativo", P(ClientPacketId.Ping, w => w.WriteLong(-1)) },
    };

    [Theory]
    [MemberData(nameof(PaquetesImposibles))]
    public async Task Lo_que_el_cliente_no_manda_nunca_cuenta_una_trampa(string caso, byte[] paquete)
    {
        var (p, sesion) = Jugador(1, 50, 50);
        p.FreeSkillPoints = 200;   // para que el reparto de skills no sea, además, pedir más de lo que tiene
        await Mandar(sesion, paquete);
        Assert.True(p.Trampas == 1, $"{caso}: {p.Trampas} trampas");
    }

    /// <summary>Con el Server.ini por defecto (<c>TrampasParaEchar</c> y <c>TrampasParaBanear</c> en 0) ninguna trampa echa ni
    /// banea: se cuentan y van al log.</summary>
    [Fact]
    public async Task Por_defecto_las_trampas_solo_se_anotan()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        for (int i = 0; i < 7; i++) await Mandar(sesion, P(ClientPacketId.Walk, w => w.WriteByte(9)));
        Assert.Equal(7, p.Trampas);
        Assert.False(p.Banned);
        Assert.False(sesion.Cerrada);
    }

    /// <summary>Los bordes que el cliente (o un bot) SÍ manda: no cuentan.</summary>
    public static TheoryData<string, byte[]> PaquetesEnElBorde => new()
    {
        { "caminar con el rumbo 4", P(ClientPacketId.Walk, w => w.WriteByte(4)) },
        { "el hechizo 35", P(ClientPacketId.CastSpell, w => w.WriteByte(35)) },
        { "usar la ranura 25", P(ClientPacketId.UseItem, w => w.WriteByte(25)) },
        { "tirar 100.000 de oro", P(ClientPacketId.Drop, w => w.WriteByte(26).WriteLong(100_000)) },
        { "tirar 32767, como los bots", P(ClientPacketId.Drop, w => w.WriteByte(1).WriteLong(short.MaxValue)) },
        { "depositar 10.000", P(ClientPacketId.BankDeposit, w => w.WriteByte(25).WriteInteger(10_000)) },
        { "retirar de la ranura 40", P(ClientPacketId.BankExtractItem, w => w.WriteByte(40).WriteInteger(1)) },
        { "fabricar 1000", P(ClientPacketId.CraftBlacksmith, w => w.WriteInteger(1).WriteInteger(1000)) },
        { "100 puntos a un skill", Skills(100) },
        { "hablar en el modo 3", P(ClientPacketId.Talk, w => w.WriteAsciiString(";hola").WriteByte(3)) },
        { "apuntar con el 21, como los druidas", P(ClientPacketId.WorkLeftClick, w => w.WriteByte(50).WriteByte(50).WriteByte(21)) },
        { "trabajar con el 11", P(ClientPacketId.Work, w => w.WriteByte(11)) },
        { "subastar por 36 horas", P(ClientPacketId.SubastaPublicar, w => w.WriteByte(1).WriteInteger(1).WriteLong(10).WriteLong(20).WriteByte(36)) },
        { "un ping en 0", P(ClientPacketId.Ping, w => w.WriteLong(0)) },
    };

    [Theory]
    [MemberData(nameof(PaquetesEnElBorde))]
    public async Task Lo_que_el_cliente_manda_no_cuenta(string caso, byte[] paquete)
    {
        var (p, sesion) = Jugador(1, 50, 50);
        p.FreeSkillPoints = 200;   // para que el reparto de skills no sea, además, pedir más de lo que tiene
        await Mandar(sesion, paquete);
        Assert.True(p.Trampas == 0, $"{caso}: {p.Trampas} trampas");
    }

    /// <summary>Una muerte se cobra una vez: la puñalada que remata la cerraba adentro de DoApunalar y AttackNpc la volvía a
    /// cerrar, con el oro y el botín dos veces (auditoría del 12/10).</summary>
    [Fact]
    public void La_muerte_de_una_criatura_se_cobra_una_sola_vez()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var ficha = new NpcInfo(Index: 902, Name: "Rata de prueba", Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 0,
            MaxHit: 0, Def: 0, GiveExp: 0, GiveGld: 100, PoderEvasion: 0, PoderAtaque: 0, NpcType: 0);
        var rata = new Npc { CharIndex = _world.AllocateCharIndex(), Template = ficha, Hp = 0, Pos = new WorldPos(1, 51, 50) };
        _world.AddNpc(rata);
        p.Gold = 0;
        _dispatcher.NpcKilledByPlayer(sesion, p, rata);
        int cobrado = p.Gold;
        Assert.True(cobrado > 0, "la primera vez se cobra");
        _dispatcher.NpcKilledByPlayer(sesion, p, rata);
        Assert.Equal(cobrado, p.Gold);
    }

    // ---- AUDITORÍA DEL 12/10 ------------------------------------------------------------------------------------

    [Fact]
    public async Task No_se_retira_mas_oro_del_que_se_puede_llevar()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var banquero = Banquero(new WorldPos(1, 51, 50));
        p.TargetNpc = (short)banquero.CharIndex;
        p.Gold = 89_999_990; p.Bank = 1_000;
        await Mandar(sesion, P(ClientPacketId.BankExtractGold, w => w.WriteLong(500)));
        Assert.Equal(89_999_990, p.Gold);                                          // pasaría el tope de 90 millones
        Assert.Equal(1_000, p.Bank);
        await Mandar(sesion, P(ClientPacketId.BankExtractGold, w => w.WriteLong(5)));
        Assert.Equal(89_999_995, p.Gold);                                          // el control: lo que entra, sale
    }

    [Fact]
    public async Task La_boveda_no_opera_lejos_del_banquero()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var banquero = Banquero(new WorldPos(1, 70, 50));                          // a 20 baldosas
        p.BankOpen = true;
        p.TargetNpc = (short)banquero.CharIndex;
        p.Inventory[1] = new InventorySlot { ObjIndex = Pocion, Amount = 5 };
        await Mandar(sesion, P(ClientPacketId.BankDeposit, w => w.WriteByte(1).WriteInteger(5)));
        Assert.Equal(5, p.Inventory[1].Amount);
    }

    [Fact]
    public async Task El_barco_con_el_que_se_navega_no_se_deposita_y_se_lleva_su_ranura()
    {
        var (p, sesion) = Jugador(1, 50, 50);
        var banquero = Banquero(new WorldPos(1, 51, 50));
        p.BankOpen = true;
        p.TargetNpc = (short)banquero.CharIndex;
        p.Inventory[3] = new InventorySlot { ObjIndex = Barca, Amount = 1 };
        p.Navigating = true; p.BoatSlot = 3;
        await Mandar(sesion, P(ClientPacketId.BankDeposit, w => w.WriteByte(3).WriteInteger(1)));
        Assert.Equal(Barca, p.Inventory[3].ObjIndex);
        await Mandar(sesion, P(ClientPacketId.SwapObjects, w => w.WriteByte(3).WriteByte(7)));
        Assert.Equal(Barca, p.Inventory[7].ObjIndex);
        Assert.Equal(7, p.BoatSlot);                                               // la ranura sigue al barco
    }

    [Fact]
    public void Un_ingreso_bien_no_borra_los_fallos_de_la_ip()
    {
        var freno = new LoginThrottle(maxFails: 3, windowMs: 60_000, blockMs: 60_000);
        freno.RecordFailure("1.2.3.4"); freno.RecordFailure("1.2.3.4");
        freno.RecordSuccess("1.2.3.4");                                            // con una cuenta propia
        freno.RecordFailure("1.2.3.4");
        Assert.True(freno.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void Detras_de_un_proxy_de_confianza_la_ip_sale_de_la_cabecera()
    {
        string[] proxy = { "127.0.0.1" };
        Assert.Equal("200.1.2.3:0", WebSocketGameServer.DireccionDelCliente("127.0.0.1:5000", "10.0.0.9, 200.1.2.3", proxy));
        Assert.Equal("[2001:db8::1]:0", WebSocketGameServer.DireccionDelCliente("127.0.0.1:5000", "2001:db8::1", proxy));
        // De otro par, o sin la clave, la cabecera no vale: la escribe el cliente.
        Assert.Equal("8.8.8.8:5000", WebSocketGameServer.DireccionDelCliente("8.8.8.8:5000", "200.1.2.3", proxy));
        Assert.Equal("127.0.0.1:5000", WebSocketGameServer.DireccionDelCliente("127.0.0.1:5000", "200.1.2.3", System.Array.Empty<string>()));
        Assert.Equal("127.0.0.1:5000", WebSocketGameServer.DireccionDelCliente("127.0.0.1:5000", "no es una ip", proxy));
    }
}
