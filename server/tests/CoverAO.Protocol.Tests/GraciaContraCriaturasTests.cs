using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA GRACIA CONTRA LAS CRIATURAS, con sus dos mitades como el original: el reloj (<c>TimerPuedeSerAtacado</c>) y el
/// flag (<c>flags.NoPuedeSerAtacado</c>). El login y el teletransporte prenden los dos; CUALQUIER paquete del jugador
/// apaga el flag (Protocol.bas:160-162), y la IA protege sólo con los dos prendidos. Eso vale también para el golpe
/// de la criatura pegada (HostilMalvadoAI, AI_NPC.bas:160). Acá la gracia era sólo el reloj y sólo en la búsqueda.
/// </summary>
public class GraciaContraCriaturasTests
{
    private const int TicksPorVuelta = NpcSystem.AttackEveryTicks;
    private const int Vueltas = 30 * 1000 / (TicksPorVuelta * GameLoop.TickMs);   // 30 segundos

    private static (Player Jugador, NpcSystem Sistema) Escena()
    {
        var world = new World();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        jugador.Pos = new WorldPos(1, 10, 11);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        world.Add(jugador);
        var ficha = new NpcInfo(Index: 1, Name: "Lobo", Body: 1, Head: 1, Heading: 3, MaxHp: 1000, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);
        world.AddNpc(new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000,
            Pos = new WorldPos(1, 10, 10), Movement = 1, Hostile = true, OriginallyHostile = true,
        });
        return (jugador, new NpcSystem(world, new GameData()));
    }

    private static int GolpesEn30Segundos(NpcSystem sys, Player jugador)
    {
        for (int i = 1; i <= Vueltas; i++) sys.Update(TicksPorVuelta * i);
        return jugador.MaxHp - jugador.Hp;
    }

    [Fact]
    public void Con_la_gracia_la_criatura_pegada_no_le_pega()
    {
        var (jugador, sys) = Escena();
        jugador.NoPuedeSerAtacado = true;
        jugador.NoAtacablePorNpcHastaMs = Environment.TickCount64 + 600_000;
        Assert.Equal(0, GolpesEn30Segundos(sys, jugador));
    }

    /// <summary>El reloj solo no alcanza: si el flag ya se apagó (el jugador hizo algo), la criatura pega.</summary>
    [Fact]
    public void Sin_el_flag_el_reloj_solo_no_protege()
    {
        var (jugador, sys) = Escena();
        jugador.NoPuedeSerAtacado = false;
        jugador.NoAtacablePorNpcHastaMs = Environment.TickCount64 + 600_000;
        Assert.True(GolpesEn30Segundos(sys, jugador) > 0, "sin el flag, la criatura tenía que pegarle");
    }

    /// <summary>Cualquier paquete del jugador apaga el flag.</summary>
    [Fact]
    public async Task Cualquier_paquete_apaga_la_gracia()
    {
        var datos = GameData.Load();
        var world = new World();
        var loop = new GameLoop();
        var root = Path.Combine(Path.GetTempPath(), "coverao_gracia_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cts = new CancellationTokenSource();
        var leveling = new Leveling(datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), datos, leveling);
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            var jugador = new Player
            {
                CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Pos = new WorldPos(1, 50, 50),
                Hp = 100, MaxHp = 100, NoPuedeSerAtacado = true, NoAtacablePorNpcHastaMs = Environment.TickCount64 + 600_000,
            };
            var sesion = new ClientSession(new TransporteMudo(), dispatcher);
            sesion.Player = jugador;
            jugador.Connection = sesion;
            world.Add(jugador);
            dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.RequestPositionUpdate }));
            await Task.Delay(300);
            Assert.False(jugador.NoPuedeSerAtacado);
        }
        finally
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
            try { Directory.Delete(root, recursive: true); } catch { /* el temp queda */ }
        }
    }

    private sealed class TransporteMudo : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }
}
