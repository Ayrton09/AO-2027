using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// La escolta entre bots (ronda 139, 23/11): el servidor no pasa los susurros de un bot a otro y Quim no tenía a nadie a la vista, así
/// que el Minero le pide escolta a un bot aliado libre, donde esté, y el otro va hasta él y lo acompaña.
/// </summary>
public sealed class EscoltaEntreBotsTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });

    private const byte Minero = 14;

    [Theory]
    [InlineData(GameLimits.ClaseGuerrero, Player.StatusCiudadano, 20, true, true, true)]
    [InlineData(GameLimits.ClaseGuerrero, Player.StatusCiudadano, 17, true, true, false)]   // tres niveles menos
    [InlineData(GameLimits.ClaseGuerrero, Player.StatusCriminal, 20, true, true, false)]    // del otro bando
    [InlineData(GameLimits.ClaseGuerrero, Player.StatusCiudadano, 20, false, true, false)]  // ya acompaña a alguien
    [InlineData(GameLimits.ClaseGuerrero, Player.StatusCiudadano, 20, true, false, false)]  // muerto
    [InlineData(13, Player.StatusCiudadano, 20, true, true, false)]                         // un Leñador, de oficio
    public void Quien_le_sirve_de_escolta(byte clase, byte bando, int nivel, bool libre, bool vivo, bool sirve) =>
        Assert.Equal(sirve, Bot.EsEscoltaPosible(new Bot.FichaPublica(clase, bando, nivel, vivo, libre, 13),
                                                 Player.StatusCiudadano, 20, agresivo: false, atacarAliadoVuelveCriminal: false));

    [Fact]
    public async Task Le_pide_escolta_a_un_bot_de_otro_mapa_y_el_otro_lo_acompana_al_verlo()
    {
        var (datos, mapas) = Mundo.Value;
        var entorno = new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!);
        var (quim, sQuim) = Armar(entorno, "Quim", Minero, 500, new WorldPos(13, 50, 50));
        var (ciro, sCiro) = Armar(entorno, "Ciro", GameLimits.ClaseGuerrero, 501, new WorldPos(20, 60, 60));
        Invocar(ciro, "PublicarFicha", sCiro);
        entorno.EnElMundo["Quim"] = quim;
        entorno.EnElMundo["Ciro"] = ciro;

        Assert.True((bool)Invocar(quim, "PedirEscolta", new TransporteDeBot("Quim"), sQuim, 33)!);
        Assert.True((bool)Invocar(quim, "EsperaLaEscolta", sQuim)!);

        // Ciro llega a donde lo espera y lo ve: lo acompaña.
        sCiro.Recibir(ServerMessages.ChangeMap(13));
        sCiro.Recibir(ServerMessages.CharacterCreate(new Player { CharIndex = 501, Name = "Ciro", Account = "c", Pos = new WorldPos(13, 52, 50) }));
        sCiro.Recibir(ServerMessages.CharacterCreate(new Player { CharIndex = 500, Name = "Quim", Account = "q", Pos = new WorldPos(13, 50, 50),
                                                                  FactionStatus = Player.StatusCiudadano }));
        var atender = (Task<bool>)Invocar(ciro, "AtenderPedidoDeEscoltaAsync", new TransporteDeBot("Ciro"), sCiro,
                                          new TaskCompletionSource().Task, CancellationToken.None)!;
        Assert.True(await atender.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Quim", ciro.AcompanaA);

        // Y ya acompañando, no le sirve a otro.
        Invocar(ciro, "PublicarFicha", sCiro);
        Assert.Null(Invocar(quim, "BotParaLaEscolta", sQuim));
    }

    private static (Bot, SentidosDelBot) Armar(EntornoDeBots entorno, string nombre, byte clase, short charIndex, WorldPos pos)
    {
        var s = new SentidosDelBot { MiNombre = nombre };
        var yo = new Player { CharIndex = charIndex, Name = nombre, Account = nombre + "_test", Pos = pos, Level = 20, MaxHp = 250, Hp = 250,
                              MaxStamina = 300, Stamina = 300, Class = clase, FactionStatus = Player.StatusCiudadano };
        s.Recibir(ServerMessages.UserIndexInServer(charIndex));
        s.Recibir(ServerMessages.ChangeMap(pos.Map));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        var bot = new Bot(nombre, null, entorno, 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, clase);
        return (bot, s);
    }

    private static object? Invocar(Bot bot, string metodo, params object[] args) =>
        typeof(Bot).GetMethod(metodo, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bot, args);
}
