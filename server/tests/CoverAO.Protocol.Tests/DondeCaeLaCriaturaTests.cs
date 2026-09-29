using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DÓNDE APARECE UNA CRIATURA QUE LLEGA JUNTO A ALGUIEN. El original la ubica siempre con <c>SpawnNpc</c>
/// (MODULO_NPCs.bas:803): la baldosa LEGAL más cercana —sin bloqueo, sin nadie parado y con el agua que la
/// criatura aguante— recorriendo cuadrados fila por fila. Cinco caminos se lo salteaban: la mascota que viaja
/// con el amo, el familiar y la invocación miraban sólo que no hubiera nadie parado, y el entrenador y el
/// centinela creaban la criatura ENCIMA de quien la pedía.
///
/// <para>Los escenarios con agua salen de la data real, y cada uno afirma primero su premisa: si el mapa
/// cambia y ahí deja de haber agua, el rojo lo dice en vez de pasar sin mirar nada.</para>
/// </summary>
public class DondeCaeLaCriaturaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_dondecae_" + Guid.NewGuid().ToString("N"));
    private readonly GameData _datos = GameData.Load();
    private readonly World _world = new();
    private readonly GameLoop _loop = new();
    private readonly PacketDispatcher _dispatcher;

    private const short Campo = 14;     // Campos Abiertos: inseguro y despejado alrededor de (40,40)
    private const short Orilla = 17;    // inseguro; las columnas 68 y 69 son agua y la 70 ya es tierra
    private const short Lobo = 501;     // el domable de los flows de mascotas
    private const short LlamadoALaNaturaleza = 17;   // invoca tres lobos

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public DondeCaeLaCriaturaTests()
    {
        Directory.CreateDirectory(_root);
        var leveling = new Leveling(_datos.LevelExp);
        _dispatcher = new PacketDispatcher(_world, _loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), _datos, leveling);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    private Player Amo(short mapa, byte x, byte y)
    {
        var p = new Player
        {
            CharIndex = _world.AllocateCharIndex(), Name = "Aldo", Account = "aldo",
            Pos = new WorldPos(mapa, x, y), Hp = 100, MaxHp = 100,
        };
        _world.Add(p);
        return p;
    }

    private List<WorldPos> DelAmo(Player amo) =>
        _world.Npcs.Where(n => n.OwnerCharIndex == amo.CharIndex).Select(n => n.Pos).ToList();

    private void PremisaDeLaOrilla()
    {
        Assert.True(_datos.IsPkMap(Orilla), "premisa: el mapa 17 dejó de ser inseguro");
        Assert.True(_datos.IsWater(Orilla, 69, 11) && _datos.IsWater(Orilla, 69, 12) && _datos.IsWater(Orilla, 69, 13),
            "premisa: la columna 69 del mapa 17 ya no es agua");
        Assert.False(_datos.IsWater(Orilla, 70, 11) || _datos.IsBlockedTile(Orilla, 70, 11),
            "premisa: (70,11) del mapa 17 ya no es tierra libre");
    }

    /// <summary>LA MASCOTA QUE VIAJA no aparece adentro de una pared. Con la diagonal de arriba a la izquierda
    /// del amo bloqueada, el barrido de antes la ponía justo ahí —era la primera baldosa que miraba—; el
    /// original sigue por la misma fila.</summary>
    [Fact]
    public void La_mascota_que_viaja_no_aparece_adentro_de_una_pared()
    {
        Assert.NotNull(_datos.Npc(Lobo));
        var amo = Amo(Campo, 50, 50);
        amo.MascotasType[1] = Lobo;   // esperando afuera: el lugar ocupado, sin criatura en el mundo
        amo.NroMascotas = 1;
        _datos.AddBlocked(Campo, 39, 39);

        _dispatcher.WarpOtroJugador(amo, new WorldPos(Campo, 40, 40));

        var mascota = Assert.Single(DelAmo(amo));
        Assert.Equal(new WorldPos(Campo, 40, 39), mascota);
    }

    /// <summary>Y NO CAE AL AGUA: el lobo llega a la orilla y queda en la tierra de al lado, no en el mar.</summary>
    [Fact]
    public void La_mascota_que_viaja_no_cae_al_agua()
    {
        PremisaDeLaOrilla();
        Assert.False(_datos.Npc(Lobo)!.AguaValida, "premisa: el lobo aprendió a nadar");
        var amo = Amo(Campo, 50, 50);
        amo.MascotasType[1] = Lobo;
        amo.NroMascotas = 1;

        _dispatcher.WarpOtroJugador(amo, new WorldPos(Orilla, 70, 12));

        var mascota = Assert.Single(DelAmo(amo));
        Assert.Equal(new WorldPos(Orilla, 70, 11), mascota);
    }

    /// <summary>EL FAMILIAR viaja con la misma regla que la mascota, así que tampoco cae al agua.</summary>
    [Fact]
    public void El_familiar_que_viaja_no_cae_al_agua()
    {
        PremisaDeLaOrilla();
        var amo = Amo(Campo, 50, 50);
        amo.Familiar.Tipo = Familiar.Ely;
        Assert.False(_datos.Npc(Familiar.FichaDe(Familiar.Ely))!.AguaValida, "premisa: el Ely aprendió a nadar");
        // Afuera con su dueño: sin un índice puesto el familiar no viaja. No hace falta que la criatura exista,
        // retirarlo con el índice suelto sólo lo limpia.
        amo.Familiar.CharIndexEnElMundo = short.MaxValue;

        _dispatcher.WarpOtroJugador(amo, new WorldPos(Orilla, 70, 12));

        var familiar = Assert.Single(DelAmo(amo));
        Assert.Equal(new WorldPos(Orilla, 70, 11), familiar);
    }

    private const short Sirena = 595;   // una de las dos criaturas de la data que SÓLO andan por agua

    /// <summary>UNA CRIATURA DE AGUA LLEGA AL AGUA: el primer intento de <c>SpawnNpc</c> es con su propio terreno,
    /// así que con el amo en tierra firme no queda en la baldosa de al lado sino en la primera agua, un anillo
    /// más afuera.</summary>
    [Fact]
    public void Una_criatura_de_agua_llega_al_agua_y_no_a_la_orilla()
    {
        PremisaDeLaOrilla();
        Assert.True(_datos.Npc(Sirena) is { AguaValida: true, TierraInvalida: true }, "premisa: la sirena ya no es sólo de agua");
        Assert.True(_datos.IsWater(Orilla, 69, 10), "premisa: (69,10) del mapa 17 ya no es agua");
        for (int x = 70; x <= 72; x++)
            for (int y = 11; y <= 13; y++)
                Assert.False(_datos.IsWater(Orilla, x, y), $"premisa: ({x},{y}) del mapa 17 es agua y el anillo de al lado tenía que ser tierra");
        var amo = Amo(Campo, 50, 50);
        amo.MascotasType[1] = Sirena;
        amo.NroMascotas = 1;

        _dispatcher.WarpOtroJugador(amo, new WorldPos(Orilla, 71, 12));

        var sirena = Assert.Single(DelAmo(amo));
        Assert.Equal(new WorldPos(Orilla, 69, 10), sirena);
    }

    /// <summary>Y SI NO HAY AGUA CERCA, CAE EN TIERRA: el segundo intento de <c>SpawnNpc</c> admite tierra. Sin
    /// ese segundo intento la criatura no llegaba nunca. La pared en la diagonal está para que la respuesta
    /// correcta no coincida con la primera baldosa que miraba el barrido viejo.</summary>
    [Fact]
    public void Sin_agua_cerca_la_criatura_de_agua_cae_en_tierra()
    {
        for (int x = 28; x <= 52; x++)
            for (int y = 28; y <= 52; y++)
                Assert.False(_datos.IsWater(Campo, x, y), $"premisa: hay agua en ({x},{y}) del mapa 14, a menos de 12 de (40,40)");
        var amo = Amo(Campo, 50, 50);
        amo.MascotasType[1] = Sirena;
        amo.NroMascotas = 1;
        _datos.AddBlocked(Campo, 39, 39);

        _dispatcher.WarpOtroJugador(amo, new WorldPos(Campo, 40, 40));

        var sirena = Assert.Single(DelAmo(amo));
        Assert.Equal(new WorldPos(Campo, 40, 39), sirena);
    }

    /// <summary>LA INVOCACIÓN, lanzada de verdad —el hechizo elegido y el clic— al lado del agua: los tres lobos
    /// quedan en tierra, en el orden del original, y ninguno en la columna del mar.</summary>
    [Fact]
    public async Task La_invocacion_no_cae_al_agua()
    {
        PremisaDeLaOrilla();
        var hechizo = _datos.Spell(LlamadoALaNaturaleza);
        Assert.True(hechizo is { IsInvoke: true, InvokeCount: 3 }, "premisa: el hechizo 17 ya no invoca tres criaturas");
        Assert.False(_datos.Npc(hechizo!.InvokeNpc)!.AguaValida, "premisa: lo invocado aprendió a nadar");

        using var cts = new CancellationTokenSource();
        var loopTask = _loop.RunAsync(cts.Token);
        try
        {
            var amo = Amo(Orilla, 70, 12);
            amo.Privileges = 8;          // GM: se saltea skill, maná y anillo, que no son lo que se mide
            amo.CombatMode = true;       // un hechizo sobre el terreno lo pide
            amo.Spells[1] = LlamadoALaNaturaleza;
            var sesion = new ClientSession(new Transporte(), _dispatcher);
            sesion.Player = amo;

            _dispatcher.HandleOne(sesion, Paquete(ClientPacketId.CastSpell, 1));
            _dispatcher.HandleOne(sesion, Paquete(ClientPacketId.WorkLeftClick, 70, 12, GameLimits.SkillMagia));
            for (int i = 0; i < 250 && DelAmo(amo).Count < 3; i++) await Task.Delay(20);

            var lobos = DelAmo(amo);
            Assert.Equal(3, lobos.Count);
            Assert.Equal(
                new[] { new WorldPos(Orilla, 70, 11), new WorldPos(Orilla, 71, 11), new WorldPos(Orilla, 71, 12) },
                lobos.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray());
            sesion.Close("fin del test");
        }
        finally
        {
            cts.Cancel();
            try { await loopTask; } catch (OperationCanceledException) { }
        }
    }

    private static NpcInfo Ficha(short indice, string nombre) =>
        new(Index: indice, Name: nombre, Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Movement: 1);

    /// <summary>EL ENTRENADOR pide la criatura sobre SU baldosa: tiene que quedar al lado, no encima de él.</summary>
    [Fact]
    public void La_criatura_del_entrenador_no_queda_encima_del_entrenador()
    {
        var world = new World();
        var sys = new NpcSystem(world, new GameData());
        var suBaldosa = new WorldPos(1, 40, 40);
        sys.AddSpawn(Ficha(1, "Entrenador"), suBaldosa);

        int ci = sys.GmSpawn(Ficha(2, "Lobo"), suBaldosa);

        Assert.Equal(new WorldPos(1, 39, 39), world.NpcByIndex((short)ci)?.Pos);
        Assert.Single(world.Npcs, n => n.Pos == suBaldosa);
    }

    /// <summary>EL CENTINELA se pide sobre la baldosa del revisado: aparece a su lado, no parado encima.</summary>
    [Fact]
    public void El_centinela_aparece_al_lado_del_revisado()
    {
        var world = new World();
        var revisado = new Player { CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(1, 40, 40), Hp = 1 };
        world.Add(revisado);
        var sys = new NpcSystem(world, new GameData());

        int ci = sys.GmSpawn(Ficha(3, "Centinela"), revisado.Pos);

        Assert.Equal(new WorldPos(1, 39, 39), world.NpcByIndex((short)ci)?.Pos);
    }

    /// <summary>UN /ACC CONTRA UNA PARED no mete la criatura adentro: cae en la legal más cercana.</summary>
    [Fact]
    public void Un_acc_contra_una_pared_no_la_mete_adentro()
    {
        var world = new World();
        var datos = new GameData();
        datos.AddBlocked(1, 40, 40);
        var sys = new NpcSystem(world, datos);

        int ci = sys.GmSpawn(Ficha(4, "Lobo"), new WorldPos(1, 40, 40));

        Assert.Equal(new WorldPos(1, 39, 39), world.NpcByIndex((short)ci)?.Pos);
    }

    private static ByteQueueReader Paquete(ClientPacketId id, params byte[] datos)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id);
        foreach (byte dato in datos) w.WriteByte(dato);
        return new ByteQueueReader(w.ToArray());
    }
}
