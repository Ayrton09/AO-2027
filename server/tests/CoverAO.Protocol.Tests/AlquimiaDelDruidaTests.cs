using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL DRUIDA SE HACE SUS POCIONES (pedido del tester, 25/11, punto 1): la Roja y la Azul, para él, cuando le faltan y sabe la receta.
/// </summary>
public sealed class AlquimiaDelDruidaTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private const short Raiz = 888, Roja = 891, Azul = 894;

    private static (Bot Bot, SentidosDelBot Sentidos) Preparar()
    {
        var p = new Player { CharIndex = 500, Name = "Hilda", Account = "Hilda_qa", Class = GameLimits.ClaseDruida, Gender = 2,
            Pos = new WorldPos(20, 50, 50), Level = 24, Hp = 160, MaxHp = 160, Gold = 5000, Hunger = 100, Thirst = 100 };
        var s = new SentidosDelBot { MiNombre = p.Name };
        s.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
        s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        var b = new Bot(p.Name, null, new EntornoDeBots(null!, new ServerIni(), 0, 0, Datos.Value, new Navegante(Datos.Value), null!), 42);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(b, GameLimits.ClaseDruida);
        return (b, s);
    }

    private static short? QueSeHace(Bot b, SentidosDelBot s, int skill) =>
        (short?)typeof(Bot).GetMethod("PocionQueSeHace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(b,
            [s, Datos.Value.AlchemyItems, Oficios.Alquimia, skill, (Func<int, int>)(obj => s.CuantoTiene(o => o == obj))]);

    [Fact]
    public void Con_57_y_raices_se_hace_la_azul_y_con_60_la_roja_tambien()
    {
        var (b, s) = Preparar();
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Raiz, 100, false, 0, 1));
        Assert.Equal(Azul, QueSeHace(b, s, 57));                 // la Roja pide 60
        s.Recibir(ServerMessages.ChangeInventorySlot(2, Azul, 50, false, 0, 1));
        Assert.Equal(Roja, QueSeHace(b, s, 60));                 // de la que tiene menos
    }

    [Fact]
    public void Con_las_que_quiere_o_sin_raices_no()
    {
        var (b, s) = Preparar();
        Assert.Null(QueSeHace(b, s, 57));                        // sin raíces
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Raiz, 100, false, 0, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, Azul, 200, false, 0, 1));
        Assert.Null(QueSeHace(b, s, 57));                        // ya tiene las 200
    }
}
