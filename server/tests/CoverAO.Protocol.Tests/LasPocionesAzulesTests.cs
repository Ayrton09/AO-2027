using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS POCIONES AZULES EN VEZ DE MEDITAR (<c>Bot.TomarPocionesDeManaAsync</c>): los que pelean con hechizos se sentaban a meditar de 50
/// a 75 segundos por cada Zombi (rondas 68 y 69). Con el maná bajo y azules en la mochila, el bot toma una en vez de sentarse; sin
/// azules, medita como siempre.
/// </summary>
public class LasPocionesAzulesTests
{
    private const short PocionAzul = 37, MiIndice = 500;

    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    private static readonly MethodInfo MeditarAsync = typeof(Bot).GetMethod("MeditarAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public async Task Con_azules_toma_una_en_vez_de_sentarse() =>
        Assert.Equal<ClientPacketId?>(ClientPacketId.UseItem, await PrimerPaquete(conAzules: true));

    [Fact]
    public async Task Sin_azules_se_sienta_a_meditar() =>
        Assert.Equal<ClientPacketId?>(ClientPacketId.Meditate, await PrimerPaquete(conAzules: false));

    private static async Task<ClientPacketId?> PrimerPaquete(bool conAzules)
    {
        var d = Mundo.Value.Datos;
        Assert.True(d.Obj(PocionAzul) is { IsPotion: true, SubType: 4 }, "premisa: la 37 es la poción azul, de maná (obj.json)");
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = MiIndice, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(24, 50, 50), Level = 24,
            MaxHp = 185, Hp = 185, MaxMana = 800, Mana = 100, MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.ChangeMap(24));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        if (conAzules) s.Recibir(ServerMessages.ChangeInventorySlot(1, PocionAzul, 50, false, 0f, 1));
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, null!, 0, 0, d, Mundo.Value.Mapas, null!), 1);
        var t = new TransporteDeBot("Bruma");
        using var corte = new CancellationTokenSource();
        var meditar = (Task)MeditarAsync.Invoke(bot, new object[] { t, s, corte.Token })!;
        ClientPacketId? primero = null;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (primero is null && DateTime.UtcNow < limite)
            if (await Proximo(t, 200) is { } paquete) primero = (ClientPacketId)paquete[0];
        corte.Cancel();
        try { await meditar.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        return primero;
    }

    private static async Task<byte[]?> Proximo(TransporteDeBot t, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        var buffer = new byte[4096];
        try
        {
            int n = await t.ReceiveAsync(buffer, cts.Token);
            if (n == 0) return null;
            var paquete = new byte[n];
            for (int i = 0; i < n; i++) paquete[i] = (byte)(buffer[i] ^ t.Clave);
            return paquete;
        }
        catch (OperationCanceledException) { return null; }
    }
}
