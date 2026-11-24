using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT SE SUBE A LA MONTURA EQUIPÁNDOLA: el servidor monta con el equipar (<c>AlternarMontura</c>, desde HandleEquipItem) y al
/// usarla contesta "No puedes usar ese objeto". Reportado por el tester (23/11): el bot anotaba que se subía y seguía a pie.
/// </summary>
public sealed class SubirseALaMonturaTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    [Fact]
    public async Task Afuera_se_sube_a_la_montura_equipandola()
    {
        var (datos, mapas) = Mundo.Value;
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        var montura = datos.Objects.Values.First(o => o.IsMount && o.MinSkill <= 100);
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where !datos.IsBlockedTile(18, x, y) && !datos.NoSeMontaAca(18, x, y)
                     select (X: (byte)x, Y: (byte)y)).First();
        var sentidos = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, sitio.X, sitio.Y), Level = 30,
            MaxHp = 200, Hp = 200, MaxStamina = 300, Stamina = 300,
        };
        sentidos.Recibir(ServerMessages.UserIndexInServer(500));
        sentidos.Recibir(ServerMessages.ChangeMap(18));
        sentidos.Recibir(ServerMessages.CharacterCreate(yo));
        sentidos.Recibir(ServerMessages.UpdateUserStats(yo));
        sentidos.Recibir(ServerMessages.SendSkills(Enumerable.Repeat((byte)100, GameLimits.NumSkills + 1).ToArray()));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, montura.Index, 1, false, 0, 1));
        var transporte = new TransporteDeBot("Rufo");

        var montarse = typeof(Bot).GetMethod("Montarse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)montarse.Invoke(bot, new object[] { transporte, sentidos })!);

        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[64];
        int n = await transporte.ReceiveAsync(buffer, corte.Token);
        Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 2 }, buffer.Take(n).Select(b => (byte)(b ^ transporte.Clave)).ToArray());
    }
}
