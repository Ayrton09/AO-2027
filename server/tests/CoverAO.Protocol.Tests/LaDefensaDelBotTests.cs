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
    [InlineData(34)]
    [InlineData(498)]
    public void Se_defiende_del_agresor_identificado_por_el_servidor_antes_que_de_otro_enemigo_mas_cercano(short mensaje)
    {
        var f = Armar();
        f.Sentidos.Recibir(DanoDeKael(mensaje));
        Assert.Equal((short)502, Defiende(f));
    }

    [Fact]
    public void El_nombre_del_agresor_se_reconoce_sin_importar_mayusculas()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, "kAeL%^6%12", 0, 2));
        Assert.Equal((short)502, Defiende(f));
    }

    [Fact]
    public void Una_cura_no_identifica_al_sanador_como_agresor()
    {
        var f = Armar();
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, "Kael%^5%25", 0, 13));
        Assert.Equal((short)501, Defiende(f));
    }

    [Fact]
    public void Un_buff_con_fuente_de_combate_tampoco_es_una_agresion()
    {
        var f = Armar();
        var buff = Mundo.Value.Datos.Spells.Values.First(h => h.SubeFuerza == 1 && h.SubeHp != 2);
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, $"Kael%^{buff.Index}%10", 0, 2));
        Assert.Equal((short)501, Defiende(f));
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
        f.Sentidos.Recibir(DanoDeKael(498));
        f.Sentidos.Recibir(ServerMessages.LocaleMsg(498, $"Juno%^{buff.Index}%10", 0, 2));
        Assert.Equal((short)502, Defiende(f));
    }

    [Fact]
    public void El_historial_reciente_se_acota_y_una_cura_no_desplaza_los_efectos_de_combate()
    {
        var sentidos = new SentidosDelBot();
        for (int i = 0; i < 100; i++) sentidos.Recibir(ServerMessages.LocaleMsg(34, $"Persona{i}%12"));
        sentidos.Recibir(ServerMessages.LocaleMsg(498, "Sanador%^5%25", 0, 13));
        var recientes = sentidos.EfectosRecibidos(2_500);
        Assert.Equal(8, recientes.Count);
        Assert.Equal("Persona99", recientes[0].Quien);
        Assert.Equal("Persona92", recientes[^1].Quien);
    }

    [Theory]
    [InlineData("ausente")]
    [InlineData("aliado")]
    [InlineData("lejos")]
    public void El_agresor_identificado_debe_seguir_visible_enemigo_y_al_alcance(string cambio)
    {
        var f = Armar();
        f.Sentidos.Recibir(DanoDeKael(498));
        switch (cambio)
        {
            case "ausente": f.Sentidos.Recibir(ServerMessages.CharacterRemove(502, false)); break;
            case "aliado": f.Sentidos.Recibir(ServerMessages.CharStatus(502, Player.StatusCiudadano)); break;
            case "lejos": f.Sentidos.Recibir(ServerMessages.CharacterMove(502, 60, 50)); break;
        }
        Assert.Equal((short)501, Defiende(f));
    }

    [Fact]
    public async Task Un_agresor_viejo_no_se_atribuye_a_una_nueva_baja_de_vida()
    {
        var f = Armar();
        f.Sentidos.Recibir(DanoDeKael(498));
        await Task.Delay(3_000); // La defensa considera sólo los últimos 2,5 s.
        f.Sentidos.Recibir(ServerMessages.UpdateHP(160));
        Assert.Equal((short)501, Defiende(f));
    }

    [Fact]
    public void Sin_identidad_explicita_conserva_la_defensa_por_proximidad()
    {
        var f = Armar();
        Assert.Equal((short)501, Defiende(f));
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

    private static byte[] DanoDeKael(short mensaje) => mensaje == 34
        ? ServerMessages.LocaleMsg(34, "Kael%12")
        : ServerMessages.LocaleMsg(498, "Kael%^6%12", 0, 2);

    private static short? Defiende(Fixture f) =>
        (short?)typeof(Bot).GetMethod("AQuienSeDefiende", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Bot, new object[] { f.Sentidos });
}
