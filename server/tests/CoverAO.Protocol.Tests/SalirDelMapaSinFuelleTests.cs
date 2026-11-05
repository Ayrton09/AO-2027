using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA SALIDA DEL MAPA SIN FUELLE, con el ciclo del bot de verdad (<c>ComportarseAsync</c>): lo que decide irse del mapa es la serie de
/// la amenaza de enfrente (<c>Bot.ContadoresDeHuida.SerieDe</c>), y sin energía para pelear, en un mapa inseguro, sale como el rescate
/// (<c>Bot.SinFuelleVaAlSeguro</c>) y no al mapa vecino. El caso es Gonzo en el estado de las rondas 54, 56 y 57
/// (`fase5/herramientas/bots/estado_fuelle`): nivel 17, republicano, 183 de vida, la energía en 0 de 280, sin maná ni pociones, en
/// (51,11) del Arroyo de Nix (27, pk), a tres baldosas de la Serpiente que nace en (50,13).
/// </summary>
public class SalirDelMapaSinFuelleTests
{
    private const short Serpiente = 504, LaDeEnfrente = 600, OtraCriatura = 601, MiIndice = 500;
    private const int Mapa = 27, Rinkel = 20;

    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    private static readonly MethodInfo ComportarseAsync = typeof(Bot).GetMethod("ComportarseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Huidas = typeof(Bot).GetField("_huidas", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo RescateHacia = typeof(Bot).GetField("_rescateHacia", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Con la serie en tres de OTRA criatura, la de enfrente no lo saca del mapa: huye de ella, su serie arranca en uno, y no sale el
    /// rescate. Con la serie de la última criatura (`fuelle7`), Gonzo se iba de cada mapa sin huir de lo que tenía enfrente.
    /// </summary>
    [Fact]
    public async Task Con_la_serie_de_otra_criatura_huye_de_la_de_enfrente_y_no_se_va_del_mapa()
    {
        var (bot, s, t) = Gonzo();
        var huidas = SerieEnTres(bot, OtraCriatura);
        Assert.Equal<ClientPacketId?>(ClientPacketId.Walk, await PrimeraDecision(bot, s, t));
        Assert.Equal(1, huidas.SerieDe(LaDeEnfrente));
        Assert.Null(RescateHacia.GetValue(bot));
    }

    /// <summary>
    /// Con la serie en tres de la de enfrente se va, y como el rescate: queda un rescate en curso (<c>_rescateHacia</c>) a un mapa que
    /// no es pk. La salida al vecino (<c>SalirDelMapaAsync</c>) no lo anota.
    /// </summary>
    [Fact]
    public async Task Con_la_serie_de_la_de_enfrente_sale_como_el_rescate_y_no_al_vecino()
    {
        var (bot, s, t) = Gonzo();
        SerieEnTres(bot, LaDeEnfrente);
        Assert.Equal<ClientPacketId?>(ClientPacketId.Walk, await PrimeraDecision(bot, s, t));
        var destino = ((int Mapa, byte X, byte Y, int Saltos)?)RescateHacia.GetValue(bot);
        Assert.NotNull(destino);
        Assert.False(Mundo.Value.Datos.IsPkMap(destino!.Value.Mapa));
    }

    /// <summary>Gonzo en (51,11) del 27 y la Serpiente en (50,13), con la vida y la energía que le mandaría el servidor.</summary>
    private static (Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte) Gonzo()
    {
        var d = Mundo.Value.Datos;
        Assert.True(d.Npc(Serpiente) is { Hostile: true, MaxHit: > 0, Veneno: > 0 }, "premisa: la Serpiente es hostil, pega y envenena (NPCs.json)");
        Assert.True(d.IsPkMap(Mapa) && !d.IsPkMap(Rinkel), "premisa: el 27 es pk y Rinkel no (MapInfo.json)");
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = MiIndice, Name = "Gonzo", Account = "gonzo_test", Pos = new WorldPos(Mapa, 51, 11), Level = 17, Class = 10, Race = 1, Gender = 1,
            MaxHp = 183, Hp = 183, MaxMana = 0, Mana = 0, MaxStamina = 280, Stamina = 0, FactionStatus = Player.StatusRepublicano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.ChangeMap(Mapa));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        s.Recibir(ServerMessages.MiniStats(p));
        s.Recibir(ServerMessages.CharacterCreate(new Npc { CharIndex = LaDeEnfrente, Template = d.Npc(Serpiente)!, Pos = new WorldPos(Mapa, 50, 13) }));
        var bot = new Bot("Gonzo", null, new EntornoDeBots(null!, null!, 0, 0, d, Mundo.Value.Mapas, null!), 1);
        return (bot, s, new TransporteDeBot("Gonzo"));
    }

    /// <summary>Tres huidas seguidas de <paramref name="deQuien"/>: la serie en tres (ver <c>Bot.NoSeLoSacaDeEncima</c>).</summary>
    private static Bot.ContadoresDeHuida SerieEnTres(Bot bot, short deQuien)
    {
        var huidas = (Bot.ContadoresDeHuida)Huidas.GetValue(bot)!;
        for (int i = 0; i < Bot.HuidasParaAcorralado; i++) huidas.Huyo(deQuien, Environment.TickCount64, 60_000);
        Assert.Equal(Bot.HuidasParaAcorralado, huidas.SerieDe(deQuien));
        return huidas;
    }

    /// <summary>Corre el ciclo del bot hasta su primera decisión de caminar, girar o pegar, y devuelve el id de ese paquete (null si no
    /// decidió nada en quince segundos).</summary>
    private static async Task<ClientPacketId?> PrimeraDecision(Bot bot, SentidosDelBot s, TransporteDeBot t)
    {
        var fin = new TaskCompletionSource();
        var ciclo = (Task)ComportarseAsync.Invoke(bot, new object[] { t, s, fin.Task, CancellationToken.None })!;
        ClientPacketId? primera = null;
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (primera is null && DateTime.UtcNow < limite && !ciclo.IsCompleted)
        {
            if (await Proximo(t, 200) is { } p && p[0] is (byte)ClientPacketId.Walk or (byte)ClientPacketId.ChangeHeading or (byte)ClientPacketId.attack)
                primera = (ClientPacketId)p[0];
        }
        fin.TrySetResult();
        await ciclo.WaitAsync(TimeSpan.FromSeconds(10));
        return primera;
    }

    /// <summary>El próximo paquete que mandó el bot, o null si no mandó ninguno en <paramref name="ms"/> milisegundos.</summary>
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
