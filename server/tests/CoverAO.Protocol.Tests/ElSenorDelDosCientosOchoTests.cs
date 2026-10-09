using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS MUERTES DEL 208 (ver <c>Bot.LaAmenazaDejaPelearParado</c> y el botín en <c>LevantarBotinAsync</c>): acorralado con algo que lo mata
/// de dos golpes a tres baldosas, no se queda peleando con lo que tiene pegado; y no se camina a un botín que lo deja al lado de algo
/// peligroso (<c>Bot.ArrimaAlPeligroAlBotin</c>, con <c>Bot.LoArrimaAlPeligro</c>). Los casos de conducta corren el código del bot de verdad
/// y leen lo que el bot manda por el transporte.
/// </summary>
public class ElSenorDelDosCientosOchoTests
{
    private const short SenorDeLasSombras = 534, Escorpion = 529, Botin = 737, MiIndice = 500;
    private const int Mapa = 208;

    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    private static readonly MethodInfo LevantarBotinAsync = typeof(Bot).GetMethod("LevantarBotinAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ComportarseAsync = typeof(Bot).GetMethod("ComportarseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Huidas = typeof(Bot).GetField("_huidas", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo NadaQueHacerAca = typeof(Bot).GetField("_nadaQueHacerAca", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Con_el_Senor_a_tres_baldosas_no_pelea_parado_con_el_Escorpion()
    {
        var senor = Mundo.Value.Datos.Npc(SenorDeLasSombras);
        Assert.NotNull(senor);
        Assert.True(senor!.MaxHit * 2 >= 77, "el Señor tiene que matar de dos golpes a Elmo con 77 de vida");
        // Elmo en (57,51), el Escorpión pegado en (57,52), y el Señor, que lo mata de dos, a tres baldosas (60,51) o a una (58,51).
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)60, (byte)51) }, (57, 52)));
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)58, (byte)51) }, (57, 52)));
    }

    [Fact]
    public void Lo_pegado_que_tambien_mata_de_dos_no_tapa_al_Senor_a_tres_baldosas()
    {
        Assert.False(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)57, (byte)52), ((byte)60, (byte)51) }, (57, 52)));
    }

    [Fact]
    public void Lejos_o_si_lo_que_mata_de_dos_es_lo_pegado_pelea_como_siempre()
    {
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)61, (byte)51) }, (57, 52)), "el Señor a cuatro baldosas");
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), new[] { ((byte)57, (byte)52) }, (57, 52)), "lo pegado, solo");
        Assert.True(Bot.LaAmenazaDejaPelearParado((57, 51), Array.Empty<(byte, byte)>(), (57, 52)), "nada que mate de dos a tiro");
    }

    /// <summary>
    /// El acorralado que no se puede mover pelea aunque pierda o la amenaza no lo deje (decisión del autor, 09/10); el que se puede
    /// mover, sólo si se gana y la amenaza lo deja.
    /// </summary>
    [Fact]
    public void El_acorralado_que_no_se_puede_mover_pelea_igual()
    {
        int quieto = Bot.HuidasParaAcorralado, seMueve = Bot.HuidasParaAcorralado - 1;
        Assert.True(Bot.PeleaAcorralado(quieto, () => false, () => false));
        Assert.False(Bot.PeleaAcorralado(seMueve, () => true, () => false));
        Assert.False(Bot.PeleaAcorralado(seMueve, () => false, () => true));
        Assert.True(Bot.PeleaAcorralado(seMueve, () => true, () => true));
    }

    [Fact]
    public void El_botin_al_lado_del_peligro_no_se_va_a_buscar()
    {
        var peligro = new[] { ((byte)55, (byte)50) };
        Assert.True(Bot.LoArrimaAlPeligro((60, 51), (57, 51), peligro));    // hacia el peligro
        Assert.False(Bot.LoArrimaAlPeligro((60, 51), (62, 51), peligro));   // alejándose
        Assert.False(Bot.LoArrimaAlPeligro((60, 51), (57, 51), Array.Empty<(byte, byte)>()));
    }

    /// <summary>Acorralado con el Escorpión pegado y el Señor a tres baldosas, que lo mata de dos golpes con 130 de vida: el bot huye y
    /// no pelea parado. La primera decisión del ciclo tiene que ser caminar.</summary>
    [Fact]
    public async Task Acorralado_con_el_Senor_a_tres_baldosas_que_lo_mata_de_dos_huye_y_no_pelea_parado()
    {
        Assert.True(Mundo.Value.Datos.Npc(SenorDeLasSombras)!.MaxHit * 2 >= 130, "el Señor tiene que matar de dos golpes con 130 de vida");
        var s = Elmo(57, 51, vidaMax: 200, vida: 130);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, Escorpion, 57, 52)));                 // pegado
        s.Recibir(ServerMessages.CharacterCreate(Criatura(601, SenorDeLasSombras, 60, 51)));         // a tres baldosas
        var bot = NuevoBot();
        Acorralado(bot);
        Assert.Equal<ClientPacketId?>(ClientPacketId.Walk, await PrimeraDecision(bot, s, new TransporteDeBot("Elmo")));
    }

    /// <summary>El control de la prueba de arriba: la misma posición, con 160 de vida el Señor no lo mata de dos golpes, y el acorralado
    /// pelea parado con el Escorpión (la primera decisión es girar hacia él).</summary>
    [Fact]
    public async Task Acorralado_con_el_Senor_a_tres_baldosas_que_no_lo_mata_de_dos_pelea_parado()
    {
        Assert.True(Mundo.Value.Datos.Npc(SenorDeLasSombras)!.MaxHit * 2 < 160, "el Señor tiene que no matar de dos golpes con 160 de vida");
        var s = Elmo(57, 51, vidaMax: 200, vida: 160);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, Escorpion, 57, 52)));
        s.Recibir(ServerMessages.CharacterCreate(Criatura(601, SenorDeLasSombras, 60, 51)));
        var bot = NuevoBot();
        Acorralado(bot);
        Assert.Equal<ClientPacketId?>(ClientPacketId.ChangeHeading, await PrimeraDecision(bot, s, new TransporteDeBot("Elmo")));
    }

    /// <summary>El control del botín: sin el Señor, el mismo botín a tres pasos se camina y se levanta.</summary>
    [Fact]
    public async Task Sin_el_Senor_el_mismo_botin_se_camina_y_se_levanta()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));
        var t = new TransporteDeBot("Elmo");
        Assert.True(await Levantar(NuevoBot(), s, t));
        Assert.Contains((byte)ClientPacketId.PickUp, (await Mandado(t)).Select(p => p[0]));
    }

    /// <summary>El botín a tres pasos, con el Señor a tres baldosas de él: no se camina hacia el ni se levanta.</summary>
    [Fact]
    public async Task El_botin_al_lado_del_Senor_no_se_camina_ni_se_levanta()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, SenorDeLasSombras, 55, 50)));
        var t = new TransporteDeBot("Elmo");
        Assert.False(await Levantar(NuevoBot(), s, t));
        Assert.Empty(await Mandado(t));
    }

    /// <summary>Parado encima del botín, con el Señor a tres baldosas, se levanta: no hay paso que dar (<c>Bot.ArrimaAlPeligroAlBotin</c>).</summary>
    [Fact]
    public async Task Parado_encima_del_botin_se_levanta_aunque_el_Senor_este_a_tres_baldosas()
    {
        var s = Elmo(57, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, SenorDeLasSombras, 55, 50)));
        var t = new TransporteDeBot("Elmo");
        Assert.True(await Levantar(NuevoBot(), s, t));
        Assert.Contains((byte)ClientPacketId.PickUp, (await Mandado(t)).Select(p => p[0]));
    }

    /// <summary>Con dos botines, el más cercano al lado del Señor no se elige: se camina al que está lejos de él.</summary>
    [Fact]
    public async Task Con_dos_botines_se_va_al_que_no_queda_al_lado_del_peligro()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));    // el más cercano, a tres pasos y al lado del Señor
        s.Recibir(ServerMessages.ObjectCreate(64, 51, Botin, 1));    // a cuatro pasos al este, lejos del Señor
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, SenorDeLasSombras, 55, 50)));
        var t = new TransporteDeBot("Elmo");
        Assert.True(await Levantar(NuevoBot(), s, t));
        var mandado = await Mandado(t);
        Assert.Equal((byte)ClientPacketId.Walk, mandado[0][0]);
        Assert.Equal(SentidosDelBot.RumboHacia(1, 0), mandado[0][1]);
        Assert.Equal((byte)ClientPacketId.PickUp, mandado[^1][0]);
    }

    /// <summary>Con dos botines, el más cercano en una baldosa que ya no sirvió (<c>_nadaQueHacerAca</c>) no se elige: se camina al otro
    /// y se lo levanta.</summary>
    [Fact]
    public async Task Con_dos_botines_se_va_al_que_no_esta_en_una_baldosa_que_no_sirvio()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ChangeMap(Mapa));                   // la baldosa anotada lleva el mapa
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));    // el más cercano, a tres pasos al oeste
        s.Recibir(ServerMessages.ObjectCreate(64, 51, Botin, 1));    // a cuatro pasos al este
        var bot = NuevoBot();
        NoSirvio(bot, 57, 51);
        var t = new TransporteDeBot("Elmo");
        Assert.True(await Levantar(bot, s, t));
        var mandado = await Mandado(t);
        Assert.Equal((byte)ClientPacketId.Walk, mandado[0][0]);
        Assert.Equal(SentidosDelBot.RumboHacia(1, 0), mandado[0][1]);
        Assert.Equal((byte)ClientPacketId.PickUp, mandado[^1][0]);
    }

    /// <summary>El control de la prueba de arriba: sin la baldosa anotada, se camina al más cercano.</summary>
    [Fact]
    public async Task Con_dos_botines_y_ninguna_baldosa_anotada_se_va_al_mas_cercano()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ChangeMap(Mapa));
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));
        s.Recibir(ServerMessages.ObjectCreate(64, 51, Botin, 1));
        var t = new TransporteDeBot("Elmo");
        Assert.True(await Levantar(NuevoBot(), s, t));
        var mandado = await Mandado(t);
        Assert.Equal((byte)ClientPacketId.Walk, mandado[0][0]);
        Assert.Equal(SentidosDelBot.RumboHacia(-1, 0), mandado[0][1]);
        Assert.Equal((byte)ClientPacketId.PickUp, mandado[^1][0]);
    }

    /// <summary>El corte en el camino: el bot ya va hacia el botín cuando aparece el Señor a tres baldosas de él. Deja de caminar y no
    /// lo levanta.</summary>
    [Fact]
    public async Task Si_el_Senor_aparece_a_mitad_del_camino_al_botin_se_corta()
    {
        var s = Elmo(60, 51, vidaMax: 131, vida: 131);
        s.Recibir(ServerMessages.ObjectCreate(57, 51, Botin, 1));
        var t = new TransporteDeBot("Elmo");
        var levantando = Levantar(NuevoBot(), s, t);
        var primero = await Proximo(t, 2_000);
        Assert.NotNull(primero);
        Assert.Equal((byte)ClientPacketId.Walk, primero![0]);
        s.Recibir(ServerMessages.CharacterCreate(Criatura(600, SenorDeLasSombras, 55, 50)));
        Assert.False(await levantando.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await Mandado(t));
    }

    private static Bot NuevoBot() =>
        new("Elmo", null, new EntornoDeBots(null!, null!, 0, 0, Mundo.Value.Datos, Mundo.Value.Mapas, null!), 1);

    /// <summary>Elmo en el 208, con la vida y la energía que le mandaría el servidor (sin magia: el mana en cero).</summary>
    private static SentidosDelBot Elmo(byte x, byte y, int vidaMax, int vida)
    {
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = MiIndice, Name = "Elmo", Account = "elmo_test", Pos = new WorldPos(Mapa, x, y), Level = 12,
            MaxHp = vidaMax, Hp = vida, MaxMana = 0, Mana = 0, MaxStamina = 80, Stamina = 80,
        };
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        return s;
    }

    private static Npc Criatura(short charIndex, short ficha, byte x, byte y) => new()
    {
        CharIndex = charIndex,
        Template = new NpcInfo(ficha, "Criatura", 0, 0, 0, MaxHp: 20, MinHit: 1, MaxHit: 2, Def: 0,
                               GiveExp: 5, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 0),
        Pos = new WorldPos(Mapa, x, y),
    };

    private static Task<bool> Levantar(Bot bot, SentidosDelBot s, TransporteDeBot t) =>
        (Task<bool>)LevantarBotinAsync.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, CancellationToken.None })!;

    /// <summary>Tres huidas que no lo sacaron de encima: acorralado (ver <c>Bot.EstaAcorralado</c>).</summary>
    private static void Acorralado(Bot bot)
    {
        var huidas = Huidas.GetValue(bot)!;
        huidas.GetType().GetField("QueNoLoSacaron")!.SetValue(huidas, 3);
    }

    /// <summary>La baldosa, anotada como una donde ya se agachó y no levantó nada (ver <c>Bot.BaldosaQueNoSirve</c>).</summary>
    private static void NoSirvio(Bot bot, byte x, byte y) =>
        ((HashSet<(short, byte, byte)>)NadaQueHacerAca.GetValue(bot)!).Add(((short)Mapa, x, y));

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

    /// <summary>Todo lo que mandó el bot hasta que se quedó callado <paramref name="quietoMs"/> milisegundos.</summary>
    private static async Task<List<byte[]>> Mandado(TransporteDeBot t, int quietoMs = 150)
    {
        var lista = new List<byte[]>();
        while (await Proximo(t, quietoMs) is { } p) lista.Add(p);
        return lista;
    }
}
