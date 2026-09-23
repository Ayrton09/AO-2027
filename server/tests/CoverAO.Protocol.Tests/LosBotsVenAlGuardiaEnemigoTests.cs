using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS BOTS VEN AL GUARDIA DEL OTRO BANDO (decisión del autor del 2026-09-23). El guardia no es hostil, así que la regla
/// de las criaturas no lo veía, y con la regla de la casa <c>GuardiasAtacan</c> prendida un bot que entraba a una ciudad
/// enemiga moría. La regla del guardia sale de la ficha (<see cref="NpcSystem.GuardiaVaPor"/>) y el bot sabe su bando
/// por su propio <c>CharacterCreate</c>.
/// </summary>
public class LosBotsVenAlGuardiaEnemigoTests : IDisposable
{
    private readonly GameData _datos = GameData.Load();
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_bots_guardias_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose()
    {
        try { File.Delete(_ini); } catch { /* el temp queda */ }
    }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ini);
    }

    /// <summary>Un guardia imperial de la data que pelea a golpes.</summary>
    private NpcInfo GuardiaImperial() =>
        _datos.Npcs.Values.First(n => n.NpcType == 2 && n.Status == ServerIni.GuardiaImperial && n.MaxHit > 0);

    [Theory]
    [InlineData(Player.StatusRepublicano, true)]
    [InlineData(Player.StatusMilicia, true)]
    [InlineData(Player.StatusCaos, true)]
    [InlineData(Player.StatusCriminal, true)]
    [InlineData(Player.StatusCiudadano, false)]
    [InlineData(Player.StatusArmada, false)]
    public void El_guardia_va_por_los_bandos_enemigos_y_no_por_los_suyos(byte bando, bool vaPorEl) =>
        Assert.Equal(vaPorEl, NpcSystem.GuardiaVaPor(GuardiaImperial(), bando, nivel: 30, _datos, new ServerIni()));

    [Fact]
    public void Al_novato_no_salvo_que_el_ini_lo_diga()
    {
        Assert.False(NpcSystem.GuardiaVaPor(GuardiaImperial(), Player.StatusRepublicano, GameLimits.NewbieMaxLevel, _datos, new ServerIni()));
        Assert.True(NpcSystem.GuardiaVaPor(GuardiaImperial(), Player.StatusRepublicano, GameLimits.NewbieMaxLevel, _datos,
                                           Cargar("GuardiasAtacanNewbies=1\r\n")));
    }

    [Fact]
    public void Con_la_regla_apagada_ningun_guardia_va_por_nadie()
    {
        Assert.False(NpcSystem.GuardiaVaPor(GuardiaImperial(), Player.StatusRepublicano, 30, _datos, Cargar("GuardiasAtacan=0\r\n")));
        Assert.False(NpcSystem.GuardiaVaPor(GuardiaImperial(), Player.StatusRepublicano, 30, _datos, ini: null));
    }

    [Fact]
    public void Una_criatura_que_no_es_guardia_no_cuenta()
    {
        var lobo = _datos.Npcs.Values.First(n => n.NpcType != 2 && n.Hostile && n.MaxHit > 0);

        Assert.False(NpcSystem.GuardiaVaPor(lobo, Player.StatusRepublicano, 30, _datos, new ServerIni()));
    }

    // ── LO QUE DECIDE EL BOT ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void El_guardia_enemigo_es_peligro_y_el_propio_no()
    {
        var guardia = GuardiaImperial();

        Assert.True(Bot.EsPeligrosoParaElBot(guardia, 300, 30, conPociones: true, curaVeneno: true, Player.StatusRepublicano,
                                             _datos, new ServerIni()));
        Assert.False(Bot.EsPeligrosoParaElBot(guardia, 300, 30, conPociones: true, curaVeneno: true, Player.StatusCiudadano,
                                              _datos, new ServerIni()));
    }

    /// <summary>La ciudad del otro bando no sirve de refugio, de mercado ni de templo: Ullathorpe para el republicano,
    /// Illiandor para el ciudadano. Al novato los guardias no lo tocan, y con la regla apagada tampoco a nadie.</summary>
    [Fact]
    public void La_ciudad_del_otro_bando_no_es_segura()
    {
        short Ullathorpe = 1, Illiandor = 194;
        var ini = new ServerIni();

        Assert.False(Bot.MapaSeguroPara(Ullathorpe, Player.StatusRepublicano, 30, _datos, ini));
        Assert.True(Bot.MapaSeguroPara(Ullathorpe, Player.StatusCiudadano, 30, _datos, ini));
        Assert.False(Bot.MapaSeguroPara(Illiandor, Player.StatusCiudadano, 30, _datos, ini));
        Assert.True(Bot.MapaSeguroPara(Ullathorpe, Player.StatusRepublicano, GameLimits.NewbieMaxLevel, _datos, ini));
        Assert.True(Bot.MapaSeguroPara(Ullathorpe, Player.StatusRepublicano, 30, _datos, Cargar("GuardiasAtacan=0\r\n")));
    }

    // ── EL BANDO DEL BOT ─────────────────────────────────────────────────────────────────────────────────

    private static Player Yo(short ci, byte bando) => new()
    {
        CharIndex = ci, Name = "Aldo", Account = "aldo", Pos = new WorldPos(1, 50, 50), FactionStatus = bando,
    };

    [Fact]
    public void Sabe_su_bando_por_su_character_create_en_cualquier_orden()
    {
        var antes = new SentidosDelBot();
        antes.Recibir(ServerMessages.UserCharIndexInServer(500));
        antes.Recibir(ServerMessages.CharacterCreate(Yo(500, Player.StatusRepublicano)));
        Assert.Equal(Player.StatusRepublicano, antes.MiEstado);

        var despues = new SentidosDelBot();
        despues.Recibir(ServerMessages.CharacterCreate(Yo(500, Player.StatusRepublicano)));
        despues.Recibir(ServerMessages.UserCharIndexInServer(500));
        Assert.Equal(Player.StatusRepublicano, despues.MiEstado);
    }

    [Fact]
    public void Si_cambia_de_bando_se_entera()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserCharIndexInServer(500));
        s.Recibir(ServerMessages.CharacterCreate(Yo(500, Player.StatusCiudadano)));

        s.Recibir(ServerMessages.UpdateTagAndStatus(Yo(500, Player.StatusCriminal)));

        Assert.Equal(Player.StatusCriminal, s.MiEstado);
    }

    [Fact]
    public void El_bando_de_otro_no_es_el_suyo()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserCharIndexInServer(500));
        s.Recibir(ServerMessages.CharacterCreate(Yo(500, Player.StatusCiudadano)));

        s.Recibir(ServerMessages.CharacterCreate(Yo(501, Player.StatusCaos)));
        s.Recibir(ServerMessages.UpdateTagAndStatus(Yo(501, Player.StatusCriminal)));

        Assert.Equal(Player.StatusCiudadano, s.MiEstado);
    }
}
