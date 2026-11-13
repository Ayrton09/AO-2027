using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SALTO CON LA RUNA PARA CAZAR (<c>Bot.ValeSaltarConLaRuna</c>): del lado de Orac no se sale a pie, y la elección del coto sólo
/// miraba los mapas a los que se llega caminando. El caso es el de la ronda 68: un republicano de nivel 25 y 185 de vida, como Bruma,
/// en la Avanzada republicana (815, mapa seguro), con la runa, pociones y antídoto. Con el hogar en Rinkel (el 18 suma 4920 a tres
/// mapas) salta; con el hogar en Illiandor (lo mejor, el 195, suma 1495) se queda a cazar en la región.
/// </summary>
public class ElSaltoConLaRunaTests
{
    private const short Runa = 1600, PocionRoja = 38, PocionVioleta = 166, MiIndice = 500;
    private const int Avanzada = 815;

    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    private static readonly MethodInfo BuscarDondeCazarAsync =
        typeof(Bot).GetMethod("BuscarDondeCazarAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Theory]
    [InlineData(2604, 4920, true)]    // del 823 al 18, desde Rinkel
    [InlineData(2708, 6460, true)]    // del 746 al 262, desde Suramei
    [InlineData(2604, 3415, false)]   // al 191 desde Illiandor: 1,3 veces
    [InlineData(2604, 1495, false)]   // al 195 desde Illiandor
    [InlineData(0, 4920, false)]      // sin nada a pie: lo resuelve el encerrado
    public void Salta_si_desde_la_casa_suma_una_vez_y_media(int aPie, int desdeLaCasa, bool salta) =>
        Assert.Equal(salta, Bot.ValeSaltarConLaRuna(aPie, desdeLaCasa));

    /// <summary>Al caer con la runa en un pueblo que le vende, el descanso de la compra que no encontró tienda se corta; el de una
    /// compra hecha, no (<c>Bot.ComprarAlCaerConLaRuna</c>).</summary>
    [Theory]
    [InlineData(true, true, true)]     // probó en el 824, sin tienda; cae en Rinkel
    [InlineData(false, true, false)]   // ya compró: el descanso se respeta
    [InlineData(true, false, false)]   // cae donde no le venden
    [InlineData(false, false, false)]
    public void Compra_al_caer_si_venia_de_no_encontrar_tienda(bool porFracaso, bool cayoDondeLeVenden, bool compra) =>
        Assert.Equal(compra, Bot.ComprarAlCaerConLaRuna(porFracaso, cayoDondeLeVenden));

    [Fact]
    public async Task Con_el_hogar_en_Rinkel_usa_la_runa_antes_de_ir_a_cazar_en_la_region()
        => Assert.Equal<ClientPacketId?>(ClientPacketId.UseItem, await PrimeraDecision(Cities.Rinkel));

    /// <summary>Desde Illiandor, cruzando, el 191 suma 3415 contra los 2708 del 746: antes se quedaba (pedía una vez y media); desde el
    /// 12/11 el encerrado salta con una vez y un quinto (decisión del autor).</summary>
    [Fact]
    public async Task Con_el_hogar_en_Illiandor_encerrada_en_la_region_tambien_usa_la_runa()
        => Assert.Equal<ClientPacketId?>(ClientPacketId.UseItem, await PrimeraDecision(Cities.Illiandor));

    /// <summary>Bruma en la ronda 104: cazando en el 824 con presa que entrena a la vista, el ciclo sólo la llamaba para entrenar, y esa
    /// llamada salía antes de mirar la runa. Las siete corridas terminaron en el 824. El encerrado la mira también ahí; el que vuelve a
    /// pie a su casa, no.</summary>
    [Fact]
    public async Task Cazando_en_el_824_el_encerrado_mira_la_runa_aunque_solo_busque_donde_entrenar()
    {
        Assert.Equal<ClientPacketId?>(ClientPacketId.Walk, await PrimeraDecision(Cities.Illiandor, new WorldPos(824, 75, 30), soloEntrenamiento: true));
        Assert.Null(await PrimeraDecision(Cities.Illiandor, new WorldPos(195, Bot.DesdeDondeSeCaza(Mundo.Value.Datos, 195)[0].X,
                                                                         Bot.DesdeDondeSeCaza(Mundo.Value.Datos, 195)[0].Y), soloEntrenamiento: true));
    }

    /// <summary>Corre la búsqueda de coto con el hogar <paramref name="hogar"/> y devuelve el primer paquete de usar o caminar (null si
    /// no mandó ninguno en diez segundos).</summary>
    private static async Task<ClientPacketId?> PrimeraDecision(byte hogar, WorldPos? desde = null, bool soloEntrenamiento = false)
    {
        var d = Mundo.Value.Datos;
        Assert.True(!d.IsPkMap(Avanzada) && d.IsPkMap(823) && d.IsPkMap(824), "premisa: la Avanzada es segura y el 823 y el 824 no (MapInfo.json)");
        Assert.True(d.Obj(Runa) is { ObjType: 38 }, "premisa: la 1600 es la runa (obj.json)");
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = MiIndice, Name = "Bruma", Account = "bruma_test", Pos = desde ?? new WorldPos(Avanzada, 50, 50), Level = 25,
            MaxHp = 185, Hp = 185, MaxMana = 0, Mana = 0, MaxStamina = 300, Stamina = 300, FactionStatus = Player.StatusRepublicano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.ChangeMap(p.Pos.Map));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Runa, 1, false, 0f, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, PocionRoja, 200, false, 0f, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(3, PocionVioleta, 20, false, 0f, 1));
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, null!, 0, 0, d, Mundo.Value.Mapas, null!, hogarDe: _ => hogar), 1);
        var t = new TransporteDeBot("Bruma");

        var fin = new TaskCompletionSource();
        var busqueda = (Task)BuscarDondeCazarAsync.Invoke(bot, new object[] { t, s, 185, 25, soloEntrenamiento, fin.Task, CancellationToken.None, soloEntrenamiento })!;
        ClientPacketId? primera = null;
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (primera is null && DateTime.UtcNow < limite && !busqueda.IsCompleted)
            if (await Proximo(t, 200) is { } paquete && paquete[0] is (byte)ClientPacketId.UseItem or (byte)ClientPacketId.Walk)
                primera = (ClientPacketId)paquete[0];
        fin.TrySetResult();
        try { await busqueda.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
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
