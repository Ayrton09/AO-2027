using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

public class EntrenamientoDeBotsTests
{
    private static readonly Lazy<GameData> Mundo = new(() => GameData.Load());

    [Theory]
    [InlineData(3, 19, false)] // guerrero novato
    [InlineData(3, 30, true)]
    [InlineData(7, 30, true)] // druida, con combate y alquimia
    [InlineData(11, 30, false)] // pescador
    [InlineData(12, 30, false)] // herrero
    [InlineData(13, 30, false)] // leñador
    [InlineData(14, 30, false)] // minero
    [InlineData(15, 30, false)] // carpintero
    [InlineData(16, 30, false)] // sastre
    public void Las_expediciones_son_para_clases_de_combate_con_nivel_suficiente(byte clase, int nivel, bool sale) =>
        Assert.Equal(sale, Bot.SaleAEntrenar(clase, nivel));

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    public void Los_lobos_no_marcan_el_destino_de_entrenamiento_si_hay_orcos(int nivel)
    {
        var datos = Mundo.Value;
        Assert.Equal(0, Bot.RindeParaEntrenar(datos.Npc(501), nivel));
        Assert.Equal(505, Bot.RindeParaEntrenar(datos.Npc(523), nivel));
    }

    [Fact]
    public void La_armadura_cuenta_solo_si_tiene_pociones_y_no_reduce_el_peligro_de_la_magia()
    {
        var datos = Mundo.Value;
        var arana = datos.Npc(510);
        Assert.True(Bot.EsPeligrosoPara(arana, 320, 30, true, true));
        Assert.False(Bot.EsPeligrosoPara(arana, 320, 30, true, true, defensaMinima: 20));
        Assert.True(Bot.EsPeligrosoPara(arana, 320, 30, false, true, defensaMinima: 20));
        Assert.True(Bot.EsPeligrosoPara(datos.Npc(550), 320, 30, true, true, defensaMinima: 20));
    }

    [Fact]
    public void Desde_Nix_busca_los_orcos_y_una_vez_en_Catacumbas_se_queda()
    {
        var datos = Mundo.Value;
        var mapas = new Navegante(datos);
        var ini = new ServerIni();
        const int nivel = 30, vida = 320, defensa = 20;
        const byte bando = Player.StatusCiudadano;
        var cruza = Bot.SeCruzaBuscando(datos, ini, bando, nivel, vida, true, true, defensa);
        var vuelta = Bot.FiltrosDeLaVuelta(datos, ini, bando, nivel, vida);
        bool Sirve(int mapa) => cruza(mapa) && Bot.TieneComoVolver(mapas, mapa, Bot.DesdeDondeSeCaza(datos, mapa),
            vuelta.Pasa, vuelta.MapaSeguro, vuelta.TieneTienda, conRuna: true);
        int Rinde(int ficha) => Bot.EsPresaPara(datos.Npc(ficha), vida, nivel, true, true, defensa)
            ? Bot.RindeParaEntrenar(datos.Npc(ficha), nivel) : 0;

        var elegido = Bot.CotoDeEntrenamiento(datos, mapas, 34, (12, 86), Rinde, cruza, Sirve, explorar: true);
        Assert.NotNull(elegido);
        Assert.Equal(45, elegido.Value.Mapa);
        Assert.Equal(1, elegido.Value.Saltos);

        var queda = Bot.CotoDeEntrenamiento(datos, mapas, 45, (elegido.Value.X, elegido.Value.Y), Rinde, cruza, Sirve, explorar: false);
        Assert.Equal(45, queda?.Mapa);
        Assert.Equal(0, queda?.Saltos);

        Assert.Null(Bot.CotoDeEntrenamiento(datos, mapas, 45, (elegido.Value.X, elegido.Value.Y), Rinde, cruza,
                                          m => m != 45 && Sirve(m), explorar: false));
    }

    [Fact]
    public async Task Una_agresion_antes_del_primer_paso_devuelve_el_control_al_ciclo()
    {
        var datos = Mundo.Value;
        var bot = new Bot("Ciro", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
        typeof(Bot).GetField("_miBando", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, Player.StatusCiudadano);
        var p = new Player
        {
            CharIndex = 500, Name = "Ciro", Account = "ciro_bot", Pos = new WorldPos(34, 12, 86), Level = 30,
            Hp = 390, MaxHp = 390, Stamina = 300, MaxStamina = 300
        };
        var sentidos = new SentidosDelBot();
        sentidos.Recibir(ServerMessages.UserIndexInServer(p.CharIndex));
        sentidos.Recibir(ServerMessages.ChangeMap(p.Pos.Map));
        sentidos.Recibir(ServerMessages.CharacterCreate(p));
        sentidos.Recibir(ServerMessages.UpdateUserStats(p));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(1, 1600, 1, false, 0f, 1));
        sentidos.Recibir(ServerMessages.ChangeInventorySlot(2, 38, 20, false, 0f, 1));
        sentidos.ObservarAgresionDeJugador(501, Environment.TickCount64);

        var busca = typeof(Bot).GetMethod("BuscarDondeCazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tarea = (Task<bool>)busca.Invoke(bot, new object[]
        {
            new TransporteDeBot("Ciro"), sentidos, (int)p.MaxHp, (int)p.Level, false,
            new TaskCompletionSource().Task, limite.Token, true
        })!;
        Assert.True(await tarea.WaitAsync(limite.Token));
        Assert.Equal(34, sentidos.Mapa);
        Assert.Equal(((byte)12, (byte)86), sentidos.Posicion);
    }
}
