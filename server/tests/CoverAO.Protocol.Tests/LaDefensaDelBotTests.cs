using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public sealed class LaDefensaDelBotTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    [Theory]
    [InlineData(false, 502)]
    [InlineData(true, 0)]
    public async Task El_golpe_llega_despues_del_hp_y_solo_identifica_al_agresor_visible(bool oculto, int esperado)
    {
        var f = Armar();
        var datos = Mundo.Value.Datos;
        var nivel = new Leveling(datos.LevelExp);
        using var almacen = AlmacenSqlite.EnMemoria();
        var dispatcher = new PacketDispatcher(new World(), new GameLoop(), new Persistence(almacen, nivel), datos, nivel);
        var transporte = new TransporteDeBot("Rufo");
        transporte.Recibido += f.Sentidos.Recibir;
        var entrega = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);
        transporte.RecibioAtaqueDeJugador += (quien, cuando) =>
        {
            f.Sentidos.ObservarAgresionDeJugador(quien, cuando);
            entrega.TrySetResult(quien);
        };
        var victima = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_bot", Pos = new WorldPos(18, 50, 50), Hp = 180, MaxHp = 200,
        };
        var atacante = new Player
        {
            CharIndex = 502, Name = "Kael", Account = "kael", Pos = new WorldPos(18, 53, 50), Oculto = oculto,
        };
        var atacanteSesion = new ClientSession(new TransporteDeBot("Kael"), dispatcher) { Player = atacante };
        var victimaSesion = new ClientSession(transporte, dispatcher) { Player = victima };
        victima.Connection = victimaSesion;
        try
        {
            typeof(PacketDispatcher).GetMethod("DanoAUnJugador", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dispatcher, [atacanteSesion, atacante, victima, 20]);
            Assert.Equal((short)esperado, await entrega.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0.8, f.Sentidos.Salud, precision: 2);
            Assert.Equal(oculto ? null : (short?)502, Defiende(f));
            f.Sentidos.Recibir(ServerMessages.UpdateHP(140)); // daño ambiental entre el golpe y la decisión
            Assert.Equal((short)esperado, f.Sentidos.AgresorReciente(2_500));
            Assert.Equal(oculto ? null : (short?)502, Defiende(f));
        }
        finally
        {
            victimaSesion.Player = null;
            atacanteSesion.Player = null;
            victimaSesion.Close("fin");
            atacanteSesion.Close("fin");
        }
    }

    [Fact]
    public void Una_cura_no_identifica_al_sanador_como_agresor()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, "Kael%^5%25", 0, 13));
        Assert.Null(Defiende(f));
    }

    [Fact]
    public void Un_buff_con_fuente_de_combate_tampoco_es_una_agresion()
    {
        var f = Armar();
        var buff = Mundo.Value.Datos.Spells.Values.First(h => h.SubeFuerza == 1 && h.SubeHp != 2);
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, $"Kael%^{buff.Index}%10", 0, 2));
        Assert.Null(Defiende(f));
    }

    [Fact]
    public void Un_buff_posterior_no_borra_la_identidad_del_agresor_real()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 503, Name = "Juno", Account = "juno_test", Pos = new WorldPos(18, 50, 52),
            FactionStatus = Player.StatusCiudadano,
        }));
        var buff = Mundo.Value.Datos.Spells.Values.First(h => h.SubeFuerza == 1 && h.SubeHp != 2);
        f.Sentidos.ObservarAgresionDeJugador(502, Environment.TickCount64);
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, $"Juno%^{buff.Index}%10", 0, 2));
        Assert.Equal((short)502, Defiende(f));
    }

    [Theory]
    [InlineData("ausente")]
    [InlineData("aliado")]
    [InlineData("lejos")]
    public void El_agresor_identificado_debe_seguir_visible_enemigo_y_al_alcance(string cambio)
    {
        var f = Armar();
        f.Sentidos.ObservarAgresionDeJugador(502, Environment.TickCount64);
        switch (cambio)
        {
            case "ausente": f.Sentidos.Recibir(ServerMessages.CharacterRemove(502, false)); break;
            case "aliado": f.Sentidos.Recibir(ServerMessages.CharStatus(502, Player.StatusCiudadano)); break;
            case "lejos": f.Sentidos.Recibir(ServerMessages.CharacterMove(502, 60, 50)); break;
        }
        Assert.Null(Defiende(f));
    }

    [Fact]
    public void Un_agresor_viejo_no_se_atribuye_a_una_nueva_baja_de_vida()
    {
        var f = Armar();
        f.Sentidos.ObservarAgresionDeJugador(502, Environment.TickCount64 - 3_000);
        f.Sentidos.Recibir(ServerMessages.UpdateHP(170));
        Assert.Null(Defiende(f));
    }

    [Fact]
    public void Un_segundo_atacante_oculto_reemplaza_al_atacante_visible_anterior()
    {
        var f = Armar();
        f.Sentidos.ObservarAgresionDeJugador(502, Environment.TickCount64);
        f.Sentidos.Recibir(ServerMessages.UpdateHP(170));
        f.Sentidos.ObservarAgresionDeJugador(0, Environment.TickCount64);

        Assert.Equal((short)0, f.Sentidos.AgresorReciente(2_500));
        Assert.Null(Defiende(f));
    }

    [Fact]
    public void Sin_identidad_explicita_no_acusa_al_enemigo_mas_cercano()
    {
        var f = Armar();
        Assert.Null(Defiende(f));
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos);

    private static Fixture Armar()
    {
        var (datos, mapas) = Mundo.Value;
        Assert.True(datos.IsPkMap(18), "La defensa PvP requiere un mapa inseguro.");
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(18, 50, 50), Level = 24,
            FactionStatus = Player.StatusCiudadano, MaxHp = 200, Hp = 200, MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(18));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Bruno", Account = "bruno_test", Pos = new WorldPos(18, 51, 50),
            FactionStatus = Player.StatusCriminal,
        }));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 502, Name = "Kael", Account = "kael_test", Pos = new WorldPos(18, 53, 50),
            FactionStatus = Player.StatusCriminal,
        }));
        s.Recibir(ServerMessages.UpdateHP(180));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
        return new Fixture(bot, s);
    }

    private static short? Defiende(Fixture f) =>
        (short?)typeof(Bot).GetMethod("AQuienSeDefiende", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Bot, new object[] { f.Sentidos });
}
