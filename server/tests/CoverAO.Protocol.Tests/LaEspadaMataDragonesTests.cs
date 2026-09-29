using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA ESPADA MATA DRAGONES (reporte del tester, 2026-09-27: "está testeando matar al Rey Dragón y no le saca vida"). En el
/// original, contra un dragón el golpe máximo de la espada cuenta cincuenta veces; contra otra criatura pega 1, y contra una
/// persona tira entre 22 y 35 (VB6 CalcularDaño, SistemaCombate.bas:339-348 y :383-387). Faltaba: la espada pegaba como un
/// arma común y la defensa 650 del Rey Dragón se comía el golpe entero.
/// </summary>
public class LaEspadaMataDragonesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_matadragones_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* el temp queda */ }
    }

    [Fact]
    public void La_tirada_de_la_espada_segun_contra_quien_pega()
    {
        Assert.Equal((27L, 1500L), Combat.TiradaDeLaMataDragones(27, 30, contraNpc: true, esDragon: true, (a, b) => a));
        Assert.Equal((1L, 1L), Combat.TiradaDeLaMataDragones(27, 30, contraNpc: true, esDragon: false, (a, b) => a));
        Assert.Equal((22L, 35L), Combat.TiradaDeLaMataDragones(27, 30, contraNpc: false, esDragon: false, (a, b) => a));
        Assert.Equal((35L, 35L), Combat.TiradaDeLaMataDragones(27, 30, contraNpc: false, esDragon: true, (a, b) => b));
    }

    /// <summary>
    /// EL CASO DEL TESTER, CON LA DATA: la espada del install contra la defensa del Rey Dragón del install. Sin la regla, ni el
    /// mejor golpe de un Guerrero con 35 de fuerza pasa de cero; con ella, le saca miles.
    /// </summary>
    [Fact]
    public void Con_la_data_la_espada_le_saca_vida_al_Rey_Dragon()
    {
        var datos = GameData.Load();
        var espada = datos.Obj(Combat.EspadaMataDragones);
        var rey = datos.Npc(590);
        Assert.True(espada is { Name: "Espada Mata Dragones" }, "premisa: el objeto 402 es la Espada Mata Dragones");
        Assert.True(rey is { Name: "Rey Dragón", NpcType: Combat.NpcDragon }, "premisa: la ficha 590 es el Rey Dragón, de tipo dragón");
        const byte Guerrero = 3;
        const int Fuerza = 35, GolpePropio = 150;
        double mod = datos.WeaponMod.TryGetValue(Guerrero, out var m) ? m : 1.0;

        long sinLaRegla = Combat.CalcularDano(espada!.MaxHit, espada.MaxHit, GolpePropio, Fuerza, mod) - rey!.Def;
        var (tirada, maximo) = Combat.TiradaDeLaMataDragones(espada.MaxHit, espada.MaxHit, contraNpc: true, esDragon: true, (a, b) => b);
        long conLaRegla = Combat.CalcularDano(tirada, maximo, GolpePropio, Fuerza, mod) - rey.Def;

        Assert.True(sinLaRegla <= 0, $"premisa: sin la regla la espada le saca {sinLaRegla}");
        Assert.True(conLaRegla > 1000, $"con la regla le saca {conLaRegla}");
    }

    /// <summary>
    /// Y POR EL SERVIDOR: un Guerrero con la espada puesta le pega a un dragón y a una criatura igual que no es dragón. Al dragón
    /// le saca miles por golpe; a la otra, con la misma defensa, nada.
    /// </summary>
    [Fact]
    public async Task Pegando_por_el_servidor_la_espada_lastima_al_dragon_y_no_a_otra_criatura()
    {
        Assert.True(await VidaQueLeSaca(esDragon: true) > 1000, "al dragón no le sacó vida");
        Assert.True(await VidaQueLeSaca(esDragon: false) <= 0, "a la criatura que no es dragón le sacó vida");
    }

    private sealed class Transporte : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    /// <summary>Cuánta vida le saca el primer golpe que acierta, a una criatura quieta de defensa 650.</summary>
    private async Task<int> VidaQueLeSaca(bool esDragon)
    {
        const short Mapa = 14;   // Campos Abiertos: inseguro y despejado alrededor de (40,40)
        var datos = GameData.Load();
        var world = new World();
        var loop = new GameLoop();
        var npcs = new NpcSystem(world, datos);
        var leveling = new Leveling(datos.LevelExp);
        string carpeta = Path.Combine(_root, esDragon ? "dragon" : "otra");
        Directory.CreateDirectory(carpeta);
        var dispatcher = new PacketDispatcher(world, loop, new Persistence(AlmacenSqlite.EnMemoria(), leveling), datos, leveling,
            admin: new AdminConfig(), npcs: npcs);
        using var cts = new CancellationTokenSource();
        var corriendo = loop.RunAsync(cts.Token);
        try
        {
            var p = new Player
            {
                CharIndex = world.AllocateCharIndex(), Name = "Aldo", Account = "aldo", Class = 3, Level = 40,
                Pos = new WorldPos(Mapa, 40, 40), Heading = 2, Hp = 500, MaxHp = 500, MinHit = 1, MaxHit = 2,
                Elu = long.MaxValue, CombatMode = true, Stamina = 100_000, MaxStamina = 100_000,
            };
            p.Attributes[1] = 35;   // fuerza
            for (int i = 0; i < p.Skills.Length; i++) p.Skills[i] = 100;
            p.Inventory[1] = new InventorySlot { ObjIndex = Combat.EspadaMataDragones, Amount = 1, Equipped = true };
            var sesion = new ClientSession(new Transporte(), dispatcher);
            sesion.Player = p;
            p.Connection = sesion;
            world.Add(p);

            var ficha = new NpcInfo(Index: 900, Name: "Blanco", Body: 1, Head: 1, Heading: 3, MaxHp: 1_000_000, MinHit: 0,
                MaxHit: 0, Def: 650, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0, Hostile: true, Attackable: true,
                Movement: 1, NpcType: esDragon ? Combat.NpcDragon : 0);
            npcs.AddSpawn(ficha, new WorldPos(Mapa, 41, 40), hostile: true, movement: 1);
            var blanco = world.Npcs.Single(n => n.Template.Name == "Blanco");

            // La tirada de acierto puede fallar: se pega hasta que la vida cambia, o un rato.
            for (int i = 0; i < 200 && blanco.Hp == blanco.Template.MaxHp; i++)
            {
                dispatcher.HandleOne(sesion, new ByteQueueReader(new[] { (byte)ClientPacketId.attack }));
                await Task.Delay(20);
            }
            await Task.Delay(100);
            return blanco.Template.MaxHp - blanco.Hp;
        }
        finally
        {
            cts.Cancel();
            try { corriendo.Wait(TimeSpan.FromSeconds(5)); } catch { /* cancelado */ }
        }
    }
}
