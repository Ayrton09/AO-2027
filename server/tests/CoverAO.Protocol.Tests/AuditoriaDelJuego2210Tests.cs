using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE ENCONTRÓ LA AUDITORÍA DEL 22/10 EN LA LÓGICA DEL JUEGO, contra el VB6: el barco defiende de las criaturas, morir saca la
/// invisibilidad y prende el seguro de resurrección, y el medio extra del donador redondea como la barra del original.
/// </summary>
public sealed class AuditoriaDelJuego2210Tests
{
    private static readonly GameData Datos = GameData.Load();
    private const short Galeon = 476;   // MINDEF = MAXDEF = 40

    /// <summary>
    /// NAVEGANDO, EL BARCO ABSORBE EL GOLPE DE LA CRIATURA (NpcDaño, SistemaCombate.bas:537-580: <c>absorbido + defbarco + defmontura</c>
    /// fuera del Select de la zona). Una criatura que pega 40 contra el Galeón (defensa 40) deja cada golpe en 1; sin la defensa del
    /// barco cada golpe sacaba 40.
    /// </summary>
    [Fact]
    public void Navegando_el_barco_defiende_del_golpe_de_la_criatura()
    {
        Assert.True(Datos.Obj(Galeon) is { IsBoat: true, MinDef: 40, MaxDef: 40 }, "premisa: el Galeón defiende 40");
        var world = new World();
        var jugador = new Player { CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test" };
        jugador.Pos = new WorldPos(1, 10, 10);
        jugador.MaxHp = jugador.Hp = 1_000_000;
        jugador.Navigating = true;
        jugador.BoatSlot = 1;
        jugador.Inventory[1] = new InventorySlot { ObjIndex = Galeon, Amount = 1 };
        world.Add(jugador);
        var ficha = new NpcInfo(Index: 1, Name: "Tiburón", Body: 1, Head: 1, Heading: 4, MaxHp: 1000, MinHit: 40, MaxHit: 40,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 10_000);
        world.AddNpc(new Npc
        {
            CharIndex = world.AllocateCharIndex(), Template = ficha, Hp = 1000, Pos = new WorldPos(1, 11, 10),
            Movement = 1, OriginalMovement = 1, Hostile = true, OriginallyHostile = true, Heading = 4,
        });
        var sys = new NpcSystem(world, Datos);
        for (int i = 1; i <= 750; i++) sys.Update(i);
        int perdio = jugador.MaxHp - jugador.Hp;
        Assert.True(perdio > 0, "premisa: la criatura le tenía que pegar");
        Assert.True(perdio < 40, $"perdió {perdio}: cada golpe tenía que quedar en 1");
    }

    [Fact]
    public void Morir_saca_la_invisibilidad_y_prende_el_seguro_de_resurreccion()
    {
        var world = new World();
        var p = new Player
        {
            CharIndex = world.AllocateCharIndex(), Name = "Rugura", Account = "cuenta_test", Pos = new WorldPos(1, 50, 50),
            MaxHp = 100, Hp = 100, Invisible = true, InvisibleUntilTick = 9_999, Oculto = true, OcultoUntilTick = 9_999,
            ResurrectionSafe = false,
        };
        world.Add(p);
        PacketDispatcher.KillPlayer(world, Datos, p);
        Assert.False(p.Invisible || p.Oculto);
        Assert.Equal(0, p.InvisibleUntilTick + p.OcultoUntilTick);
        Assert.True(p.ResurrectionSafe);
    }

    private static readonly MethodInfo DonadorM =
        typeof(PacketDispatcher).GetMethod("ConEventoYDonador", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary><c>ExpaDar = ExpaDar + (ExpaDar / 2)</c> con la barra de punto flotante y un <c>Long</c>: redondea al par.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(5, 8)]
    [InlineData(7, 10)]
    [InlineData(10, 15)]
    public void El_medio_extra_del_donador_redondea_como_el_original(long exp, long esperado)
    {
        var world = new World();
        var leveling = new Leveling(Datos.LevelExp);
        var dispatcher = new PacketDispatcher(world, new GameLoop(), new Persistence(AlmacenSqlite.EnMemoria(), leveling), Datos, leveling);
        var p = new Player { CharIndex = 1, Name = "Rugura", Account = "cuenta_test", Donador = true };
        Assert.Equal(esperado, (long)DonadorM.Invoke(dispatcher, new object[] { p, exp })!);
    }
}
