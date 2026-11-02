using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Decisiones observables por el protocolo: blanco válido, recuperación segura y rechazo de una cura.</summary>
public sealed class DecisionesDeMagiaDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task Elige_el_hechizo_que_afecta_al_blanco_aunque_haya_otro_mas_fuerte(bool esNpc, byte ranura)
    {
        var f = Armar();
        Assert.Equal(2, f.Datos.Spell(2)!.Target); // Proyectil: daño 15, sólo criaturas.
        Assert.Equal(3, f.Datos.Spell(6)!.Target); // Saeta: daño 14, ambos.
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 2));
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(2, 6));
        var blanco = new SentidosDelBot.Visible(501, 50, 47, esNpc ? (short)1 : (short)0);

        Assert.True(Invocar<bool>(f, "LanzarAlgo", f.Transporte, f.Sentidos, blanco, null!));
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, ranura }, await Proximo(f));
    }

    [Fact]
    public void Sin_hechizos_compatibles_no_pide_un_casteo_que_el_servidor_rechazara()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 2));
        Assert.False(Invocar<bool>(f, "LanzarAlgo", f.Transporte, f.Sentidos,
            new SentidosDelBot.Visible(501, 50, 47, 0), null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Los_hechizos_de_terreno_siguen_disponibles_con_ambos_tipos_de_blanco(bool esNpc)
    {
        var f = Armar();
        var area = f.Datos.Spells.Values.First(h => h.Target == 4 && h.SubeHp == 2 && h.ManaCost <= 1_000 && h.StaRequerido <= 300);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, area.Index));
        Assert.True(Invocar<bool>(f, "LanzarAlgo", f.Transporte, f.Sentidos,
            new SentidosDelBot.Visible(501, 50, 47, esNpc ? (short)1 : (short)0), null!));
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
    }

    [Fact]
    public async Task Para_quietar_no_elige_un_hechizo_exclusivo_de_criaturas_contra_una_persona()
    {
        // La compatibilidad también se aplica a los hechizos de estado, no sólo al daño.
        var datos = new GameData();
        datos.AddSpellForTest(new SpellInfo(120, "Sólo criaturas", 0, 0, 0, 10, 2, Paraliza: 1));
        datos.AddSpellForTest(new SpellInfo(121, "Ambos", 0, 0, 0, 20, 3, Paraliza: 1));
        var f = Armar(datos: datos);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 120));
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(2, 121));
        Assert.True(Invocar<bool>(f, "QuietarA", f.Transporte, f.Sentidos, new SentidosDelBot.Visible(501, 50, 47, 0)));
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(f));
    }

    [Fact]
    public async Task Para_curarse_elige_una_cura_que_afecta_a_jugadores()
    {
        var datos = new GameData();
        datos.AddSpellForTest(new SpellInfo(120, "Curar criatura", 1, 90, 100, 10, 2));
        datos.AddSpellForTest(new SpellInfo(121, "Curar jugador", 1, 20, 25, 10, 1));
        var f = Armar(hp: 60, datos: datos);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 120));
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(2, 121));
        Assert.True(Invocar<bool>(f, "CurarseConMagia", f.Transporte, f.Sentidos));
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(f));
    }

    [Theory]
    [InlineData("sacerdote", false)]
    [InlineData("sacerdote", true)]
    [InlineData("banquero", false)]
    [InlineData("banquero", true)]
    [InlineData("mercader", false)]
    [InlineData("mercader", true)]
    [InlineData("guardia propio", false)]
    [InlineData("guardia propio", true)]
    public async Task Recupera_mana_junto_a_un_servicio_seguro_o_guardia_propio(string vecino, bool conAzules)
    {
        var f = Armar(mana: 100);
        AgregarVecino(f, Vecino(f.Datos, vecino));
        if (conAzules) f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 37, 10, false, 0, 1));
        using var corte = new CancellationTokenSource();
        var recuperar = Invocar<Task<bool>>(f, "MeditarAsync", f.Transporte, f.Sentidos, corte.Token);
        try
        {
            if (recuperar.IsCompletedSuccessfully) Assert.True(await recuperar);
            Assert.Equal(conAzules ? ClientPacketId.UseItem : ClientPacketId.Meditate, (ClientPacketId)(await Proximo(f))[0]);
        }
        finally { await Cancelar(recuperar, corte); }
    }

    [Theory]
    [InlineData("hostil", false)]
    [InlineData("hostil", true)]
    [InlineData("guardia enemigo", false)]
    [InlineData("guardia enemigo", true)]
    [InlineData("no hostil con golpe", false)]
    [InlineData("no hostil con golpe", true)]
    public async Task No_recupera_mana_junto_a_una_criatura_que_puede_atacarlo(string vecino, bool conAzules)
    {
        var f = Armar(mana: 100);
        AgregarVecino(f, Vecino(f.Datos, vecino));
        if (conAzules) f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 37, 10, false, 0, 1));
        Assert.False(await Invocar<Task<bool>>(f, "MeditarAsync", f.Transporte, f.Sentidos, CancellationToken.None));
        Assert.False(await Invocar<Task<bool>>(f, "TomarPocionesDeManaAsync", f.Transporte, f.Sentidos, CancellationToken.None));
    }

    [Fact]
    public async Task Deja_de_meditar_si_una_amenaza_se_le_acerca_aunque_ya_este_junto_al_sacerdote()
    {
        var f = Armar(mana: 100);
        AgregarVecino(f, Vecino(f.Datos, "sacerdote"));
        using var corte = new CancellationTokenSource();
        var recuperar = Invocar<Task<bool>>(f, "MeditarAsync", f.Transporte, f.Sentidos, corte.Token);
        try
        {
            if (recuperar.IsCompletedSuccessfully) Assert.True(await recuperar);
            Assert.Equal((byte)ClientPacketId.Meditate, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.MeditateToggle());
            AgregarVecino(f, Vecino(f.Datos, "hostil"), 503, 51, 50);
            Assert.Equal((byte)ClientPacketId.Meditate, (await Proximo(f))[0]); // Se levanta.
            Assert.True(await recuperar.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { await Cancelar(recuperar, corte); }
    }

    [Fact]
    public async Task Una_cura_que_funciono_no_se_veta_por_el_dano_recibido_despues()
    {
        var f = PrepararCura();
        var cuidarse = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, await Proximo(f));
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.UpdateMana((short)(1_000 - f.Datos.Spell(5)!.ManaCost)));
        f.Sentidos.Recibir(ServerMessages.UpdateHP(85)); // La cura sí entró: 60 -> 85.
        f.Sentidos.Recibir(ServerMessages.UpdateHP(40)); // El golpe posterior deja un saldo negativo.
        Assert.True(await cuidarse.WaitAsync(TimeSpan.FromSeconds(4)));

        await AssertSiguienteCuidado(f, ClientPacketId.CastSpell);
    }

    [Fact]
    public async Task El_rechazo_explicito_por_magia_conserva_la_pocion_como_alternativa()
    {
        var f = PrepararCura();
        var cuidarse = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(221));
        Assert.True(await cuidarse.WaitAsync(TimeSpan.FromSeconds(4)));

        await AssertSiguienteCuidado(f, ClientPacketId.UseItem);
    }

    [Fact]
    public async Task Sin_energia_para_la_cura_toma_una_roja_y_no_veta_el_hechizo_para_cuando_se_recupere()
    {
        var f = PrepararCura();
        f.Sentidos.Recibir(ServerMessages.UpdateSta(0));
        await AssertSiguienteCuidado(f, ClientPacketId.UseItem);
        f.Sentidos.Recibir(ServerMessages.UpdateSta(300));
        await AssertSiguienteCuidado(f, ClientPacketId.CastSpell);
    }

    [Fact]
    public async Task Una_cura_sin_confirmacion_deja_probar_una_roja_y_luego_se_puede_volver_a_lanzar()
    {
        var f = PrepararCura();
        var cuidarse = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        // El servidor no confirma gasto de maná ni manda 221: puede ser una cadencia o un rechazo temporal.
        Assert.True(await cuidarse.WaitAsync(TimeSpan.FromSeconds(4)));

        var pocion = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
        Assert.True(await pocion.WaitAsync(TimeSpan.FromSeconds(4)));
        await AssertSiguienteCuidado(f, ClientPacketId.CastSpell);
    }

    [Fact]
    public async Task Un_rechazo_de_magia_tardio_durante_la_roja_alternativa_tambien_veta_la_cura()
    {
        var f = PrepararCura();
        var cura = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal((byte)ClientPacketId.CastSpell, (await Proximo(f))[0]);
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, (await Proximo(f))[0]);
        Assert.True(await cura.WaitAsync(TimeSpan.FromSeconds(4)));

        var alternativa = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
        Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(221)); // Llegó tarde, antes de pedir otro hechizo.
        Assert.True(await alternativa.WaitAsync(TimeSpan.FromSeconds(4)));

        for (int intento = 0; intento < 3; intento++)
        {
            var cuidado = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, CancellationToken.None);
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            Assert.True(await cuidado.WaitAsync(TimeSpan.FromSeconds(4)));
        }
    }

    private static async Task AssertSiguienteCuidado(Fixture f, ClientPacketId esperado)
    {
        using var corte = new CancellationTokenSource();
        var siguiente = Invocar<Task<bool>>(f, "CuidarseAsync", f.Transporte, f.Sentidos, corte.Token);
        try { Assert.Equal((byte)esperado, (await Proximo(f))[0]); }
        finally { await Cancelar(siguiente, corte); }
    }

    private static Fixture PrepararCura()
    {
        var f = Armar(hp: 60);
        f.Sentidos.Recibir(ServerMessages.ChangeSpellSlot(1, 5));
        f.Sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 38, 10, false, 0, 1));
        return f;
    }

    private static NpcInfo Vecino(GameData datos, string cual) => cual switch
    {
        "sacerdote" => datos.Npcs.Values.First(n => n.NpcType == 1 && !n.Hostile && !n.Attackable),
        "banquero" => datos.Npcs.Values.First(n => n.NpcType == 4 && !n.Hostile && n.MaxHit == 0 && (n.Spells?.Count ?? 0) == 0),
        "mercader" => datos.Npcs.Values.First(n => n.StockItems is { Count: > 0 } && !n.Hostile && n.MaxHit == 0 && (n.Spells?.Count ?? 0) == 0),
        "guardia propio" => datos.Npcs.Values.First(n => n.NpcType == 2 && !n.Hostile && n.Status == ServerIni.GuardiaImperial && n.MaxHit > 0),
        "guardia enemigo" => datos.Npcs.Values.First(n => n.NpcType == 2 && !n.Hostile && n.Status == ServerIni.GuardiaRepublicano && n.MaxHit > 0),
        "hostil" => datos.Npcs.Values.First(n => n.Hostile && n.MaxHit > 0),
        "no hostil con golpe" => datos.Npcs.Values.First(n => !n.Hostile && n.NpcType != 2 && n.MaxHit > 0 && n.StockItems is null),
        _ => throw new ArgumentOutOfRangeException(nameof(cual)),
    };

    private static void AgregarVecino(Fixture f, NpcInfo ficha, short indice = 502, byte x = 50, byte y = 49) =>
        f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = indice, Template = ficha, Hp = ficha.MaxHp, Pos = new WorldPos(18, x, y),
        }));

    private sealed record Fixture(GameData Datos, Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte);

    private static Fixture Armar(int mana = 1_000, int hp = 200, GameData? datos = null)
    {
        datos ??= Mundo.Value.Datos;
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, 50, 50), Level = 24,
            FactionStatus = Player.StatusCiudadano, MaxHp = 200, Hp = hp, MaxMana = 2_000, Mana = mana,
            MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(18));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, Mundo.Value.Mapas, null!), 1);
        return new Fixture(datos, bot, s, new TransporteDeBot("Rufo"));
    }

    private static T Invocar<T>(Fixture f, string metodo, params object[] args) =>
        (T)typeof(Bot).GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Bot, args)!;

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private static async Task Cancelar(Task tarea, CancellationTokenSource corte)
    {
        corte.Cancel();
        try { await tarea.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (corte.IsCancellationRequested) { }
    }
}
