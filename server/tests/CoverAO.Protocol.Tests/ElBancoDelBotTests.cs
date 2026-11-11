using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BANCO DEL BOT (Bots.Banco.cs). Con <c>OroQueSeCaeAlMorir</c> el mid perdía todo lo que llevaba encima al morir en un lugar
/// inseguro: deja en la bóveda lo que no es de las compras y retira cuando una compra lo necesita.
/// </summary>
public class ElBancoDelBotTests
{
    /// <summary>EL PANEL DEL ORO DICE LO QUE HAY EN LA BÓVEDA; la bóveda de ítems lo manda en cero y no se lee. Mientras no lo
    /// vio, el banco es -1 y la plata es sólo la de encima.</summary>
    [Fact]
    public void El_panel_del_oro_dice_lo_que_hay_en_la_boveda()
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UpdateGold(300));
        Assert.Equal(-1, s.Banco);
        Assert.Equal(300, s.Plata);

        s.Recibir(ServerMessages.BankInit(goliath: false));
        Assert.Equal(-1, s.Banco);
        Assert.Equal(0, s.VecesQueVioElBanco);

        s.Recibir(ServerMessages.BankInit(goliath: true, 5000, 2));
        Assert.Equal(5000, s.Banco);
        Assert.Equal(5300, s.Plata);
        Assert.Equal(1, s.VecesQueVioElBanco);

        s.MovioAlBanco(-2000);
        Assert.Equal(3000, s.Banco);
    }

    /// <summary>Sin saber lo que hay, un movimiento no inventa un saldo.</summary>
    [Fact]
    public void Sin_haber_visto_el_panel_un_movimiento_no_inventa_el_saldo()
    {
        var s = new SentidosDelBot();
        s.MovioAlBanco(1000);
        Assert.Equal(-1, s.Banco);
    }

    /// <summary>DEPOSITA PASADO EL NOVATO lo que sobra del bolsillo, SÓLO si eso se caería al morir: con la regla apagada o con más
    /// que el tope encima no se cae nada. Y sin saber lo que guardó, pasa a mirar.</summary>
    [Theory]
    [InlineData(15, 50_000, 0, 8_000, 100_000, false)]   // el novato no
    [InlineData(16, 50_000, 0, 8_000, 100_000, true)]
    [InlineData(16, 9_999, 0, 8_000, 100_000, false)]    // sobran 1999
    [InlineData(16, 10_000, 0, 8_000, 100_000, true)]    // sobran 2000
    [InlineData(16, 100_000, 0, 8_000, 100_000, true)]   // con el tope justo se cae todo
    [InlineData(16, 162_370, 0, 8_878, 100_000, false)]  // Milo en la ronda 42: a salvo con más que el tope
    [InlineData(16, 50_000, 0, 8_000, 0, false)]         // la regla apagada
    [InlineData(16, 100, -1, 8_000, 100_000, true)]      // no sabe lo que guardó
    [InlineData(16, 100, -1, 8_000, 0, true)]
    public void Deposita_pasado_el_novato_lo_que_se_caeria(int nivel, int oro, int banco, int bolsillo, int tope, bool deposita)
    {
        Assert.Equal(deposita, Bot.ValeDepositar(nivel, oro, banco, bolsillo, tope));
    }

    /// <summary>LA COMPRA NECESITA LO GUARDADO si hay algo guardado y lo de encima no alcanza para las compras, o si es por gustos.</summary>
    [Theory]
    [InlineData(0, 100, 8_000, true, false)]       // nada guardado
    [InlineData(-1, 100, 8_000, true, false)]      // no sabe
    [InlineData(50_000, 100, 8_000, false, true)]  // le robaron el bolsillo (murió)
    [InlineData(50_000, 9_000, 8_000, false, false)]
    [InlineData(50_000, 9_000, 8_000, true, true)] // el equipo se eligió con lo guardado
    [InlineData(2_928, 34_656, 8_878, true, false)] // Elmo en la ronda 42: lo de encima paga
    public void La_compra_necesita_lo_guardado(int banco, int oro, int bolsillo, bool soloGustos, bool necesita)
    {
        Assert.Equal(necesita, Bot.NecesitaElBanco(banco, oro, bolsillo, soloGustos));
    }

    /// <summary>EL QUE REVIVE SIN NADA MIRA LA BÓVEDA (<see cref="Bot.MiraLaBoveda"/>): Osmar en `confm1`, nivel 23, en Rinkel con 0
    /// encima y 40.328 guardados que no sabía.</summary>
    [Theory]
    [InlineData(23, 0, -1, true, true)]        // Osmar
    [InlineData(23, 0, 40_328, true, false)]   // ya lo sabe: la compra retira
    [InlineData(23, 0, 0, true, false)]        // ya sabe que no hay
    [InlineData(23, 500, -1, true, false)]     // tiene con qué comprar
    [InlineData(23, 0, -1, false, false)]      // sin banquero en el mapa
    [InlineData(10, 0, -1, true, false)]       // novato
    public void Sin_plata_encima_mira_lo_que_guardo(int nivel, int oro, int banco, bool hayBanquero, bool mira)
    {
        Assert.Equal(mira, Bot.MiraLaBoveda(nivel, oro, banco, hayBanquero));
    }

    /// <summary>LA RESERVA PARA VESTIRSE (<see cref="Bot.LoQueRetira"/>): retira lo que pasa de la reserva; desnudo, todo si lo que pasa
    /// no le alcanza para vestirse. Elmo en `medio1` retiró sus 12.728 desnudo, los gastó, y la muerte siguiente lo dejó sin nada.</summary>
    [Theory]
    [InlineData(12_728, false, 300, 12_428)]
    [InlineData(12_728, true, 300, 12_428)]    // desnudo y rico: la reserva se queda
    [InlineData(500, true, 300, 500)]          // desnudo y con poco más: todo, que con 200 no se viste
    [InlineData(300, true, 300, 300)]          // desnudo con la reserva sola: para eso está
    [InlineData(200, false, 300, 0)]           // vestido, menos que la reserva: no la toca
    [InlineData(0, true, 300, 0)]
    [InlineData(-1, false, 300, 0)]            // no sabe
    public void Retira_lo_que_pasa_de_la_reserva(int banco, bool desnudo, int reserva, int retira)
    {
        Assert.Equal(retira, Bot.LoQueRetira(banco, desnudo, reserva));
    }

    /// <summary>
    /// Y LA RESERVA ALCANZA PARA LA ROPA con la regla del desnudo: lo que le sobra después de la comida y la bebida desde cero, la
    /// herramienta y las monedas de apuro (<see cref="Bot.PlataQueSobra"/>) paga la ropa más barata. Con la ropa sola (25, la de Elmo) no:
    /// así salió el 19/10 y el verificador lo cazó.
    /// </summary>
    [Theory]
    [InlineData(25, 70, 0)]
    [InlineData(25, 70, 200)]
    [InlineData(400, 0, 0)]
    public void La_reserva_alcanza_para_la_ropa_con_la_regla_del_desnudo(int ropa, int panza, int herramienta)
    {
        int reserva = Bot.LoQueAlcanzaParaVestirse(ropa, panza, herramienta);
        Assert.True(Bot.LoPuedePagar(ropa, Bot.PlataQueSobra(reserva, panza, herramienta, Bot.LoQueAlcanzaParaVestirse(0, 0, 0))));
        Assert.False(Bot.LoPuedePagar(ropa, Bot.PlataQueSobra(ropa, panza, herramienta, Bot.LoQueAlcanzaParaVestirse(0, 0, 0))) && panza > 0);
    }

    /// <summary>
    /// CON LA DATA DEL JUEGO: un nivel 24 desnudo y con la mochila vacía, con su reserva encima, tiene para la ropa más barata según la
    /// misma cuenta que usa la compra del desnudo (`PlataParaLaRopa`).
    /// </summary>
    [Fact]
    public void Con_su_reserva_el_desnudo_paga_la_ropa_mas_barata()
    {
        var d = GameData.Load();
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (d.MapExits.Count == 0 || !File.Exists(ruta)) return;
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = 500, Name = "Elmo", Account = "elmo_test", Pos = new WorldPos(20, 75, 55), Level = 24,
            MaxHp = 200, Hp = 200, MaxStamina = 300, Stamina = 0, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        var bot = new Bot("Elmo", null, new EntornoDeBots(null!, ServerIni.Cargar(ruta), 0, 0, d, new Navegante(d), null!,
                                                          hogarDe: _ => Cities.Rinkel), 1);
        int Llamar(string metodo) => (int)typeof(Bot).GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)!
                                                     .Invoke(bot, new object[] { s })!;
        int ropa = Llamar("LaRopaMasBarata"), reserva = Llamar("ReservaParaVestirse");
        Assert.True(ropa is > 0 and < int.MaxValue, "premisa: hay ropa a la venta para un nivel 24 (NPCs.json)");
        Assert.True(reserva > ropa, $"la reserva ({reserva}) tiene que pasar de la ropa ({ropa}): la compra aparta la comida y la bebida");
        s.Recibir(ServerMessages.UpdateGold(reserva));
        Assert.True(Bot.LoPuedePagar(ropa, Llamar("PlataParaLaRopa")), $"con {reserva} no paga una ropa de {ropa}");
    }

    /// <summary>NO VUELVE AL MOSTRADOR CON LA MISMA PLATA CON LA QUE NO LE ALCANZÓ (<see cref="Bot.VuelveAlMostrador"/>): Elmo en la ronda
    /// 88, con 22, iba al Alquimista cada minuto y medio sin llevarse nada.</summary>
    [Theory]
    [InlineData(22, 22, false)]
    [InlineData(20, 22, false)]
    [InlineData(30, 22, true)]    // juntó algo
    [InlineData(22, -1, true)]    // la última vez compró
    public void No_vuelve_al_mostrador_con_la_plata_que_no_alcanzo(int plata, int laQueNoAlcanzo, bool vuelve) =>
        Assert.Equal(vuelve, Bot.VuelveAlMostrador(plata, laQueNoAlcanzo));

    [Fact]
    public void Solo_cuenta_la_reposicion_que_el_mostrador_tiene_disponible()
    {
        const short espada = 2, pocionDeVida = 38, antidoto = 166;
        var datos = GameData.Load();
        Assert.NotNull(datos.Obj(espada));
        Assert.True(datos.Obj(pocionDeVida) is { IsPotion: true, SubType: 3 });
        var violeta = Assert.IsType<ObjInfo>(datos.Obj(antidoto));
        Assert.True(Bot.CuraElVeneno(violeta, datos.Spell((short)violeta.LanzaHechizo)));

        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, new Navegante(datos), null!), 1);
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(1, 10, 100, espada));
        s.Recibir(ServerMessages.CommerceInit());
        Assert.False(bot.MostradorOfreceReposicion(s, vaPorPociones: true, vaPorAntidoto: true));

        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 0, 20, pocionDeVida));
        Assert.False(bot.MostradorOfreceReposicion(s, vaPorPociones: true, vaPorAntidoto: false));
        s.Recibir(ServerMessages.ChangeNpcInventorySlot(2, 10, 20, pocionDeVida));
        Assert.True(bot.MostradorOfreceReposicion(s, vaPorPociones: true, vaPorAntidoto: false));
        Assert.False(bot.MostradorOfreceReposicion(s, vaPorPociones: false, vaPorAntidoto: true));

        s.Recibir(ServerMessages.ChangeNpcInventorySlot(3, 10, 10, antidoto));
        Assert.True(bot.MostradorOfreceReposicion(s, vaPorPociones: false, vaPorAntidoto: true));
        Assert.False(bot.MostradorOfreceReposicion(s, vaPorPociones: false, vaPorAntidoto: false));
    }

    /// <summary>EL DESNUDO VA PRIMERO AL QUE VENDE ROPA (<see cref="Bot.ElDesnudoVaPorLaRopa"/>): Ivo en la ronda 89 iba al Alquimista y no se
    /// vestía. Sin quien le venda, el viaje elige como siempre.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void El_desnudo_va_primero_por_la_ropa(bool desnudo, bool hayRopa, bool vaPorLaRopa) =>
        Assert.Equal(vaPorLaRopa, Bot.ElDesnudoVaPorLaRopa(desnudo, hayRopa));

    /// <summary>
    /// Y LO HACE: Osmar, nivel 23, revive en Rinkel (75,55) sin oro, sin pociones y sin saber lo que guardó, y la compra lo manda
    /// caminando a los banqueros (73,35 y 79,35). Un novato igual de pobre no sale: no tiene a qué.
    /// </summary>
    [Fact]
    public async Task El_que_revive_sin_nada_en_Rinkel_camina_al_banquero()
    {
        var d = GameData.Load();
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (d.MapExits.Count == 0 || !File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        Assert.Equal(ClientPacketId.Walk, await PrimerPasoDeLaCompra(d, ini, 23));
        Assert.Null(await PrimerPasoDeLaCompra(d, ini, 10));
    }

    private static readonly MethodInfo IrDeComprasAsync =
        typeof(Bot).GetMethod("IrDeComprasAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Corre la compra de un bot de ese nivel en Rinkel con cero encima y devuelve el primer paquete de caminar o de doble
    /// clic (null si no mandó ninguno en cinco segundos).</summary>
    private static async Task<ClientPacketId?> PrimerPasoDeLaCompra(GameData d, ServerIni ini, int nivel)
    {
        const short MiIndice = 500;
        const int Rinkel = 20;
        var s = new SentidosDelBot();
        var p = new Player
        {
            CharIndex = MiIndice, Name = "Osmar", Account = "osmar_test", Pos = new WorldPos(Rinkel, 75, 55), Level = (byte)nivel,
            MaxHp = 200, Hp = 200, MaxMana = 0, Mana = 0, MaxStamina = 300, Stamina = 300, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(MiIndice));
        s.Recibir(ServerMessages.ChangeMap(Rinkel));
        s.Recibir(ServerMessages.CharacterCreate(p));
        s.Recibir(ServerMessages.UpdateUserStats(p));
        s.Recibir(ServerMessages.UpdateGold(0));
        Assert.Equal(-1, s.Banco);
        var bot = new Bot("Osmar", null, new EntornoDeBots(null!, ini, 0, 0, d, new Navegante(d), null!,
                                                           hogarDe: _ => Cities.Rinkel), 1);
        var t = new TransporteDeBot("Osmar");
        var fin = new TaskCompletionSource();
        var compra = (Task)IrDeComprasAsync.Invoke(bot, new object[] { t, s, fin.Task, CancellationToken.None })!;
        ClientPacketId? primero = null;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (primero is null && DateTime.UtcNow < limite && !compra.IsCompleted)
            if (await Proximo(t, 200) is { } paquete && paquete[0] is (byte)ClientPacketId.Walk or (byte)ClientPacketId.DoubleClick)
                primero = (ClientPacketId)paquete[0];
        fin.TrySetResult();
        try { await compra.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        return primero;
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

    /// <summary>EL BOLSILLO, con los precios de la data: lo de dos viajes de compras del mid. Tiene que quedar MUY por debajo de lo que
    /// el mid lleva encima (25 mil de mediana en las corridas del 01/10 al 06/10), si no depositar no le saca nada del riesgo.</summary>
    [Fact]
    public void El_bolsillo_del_mid_es_mucho_menos_que_lo_que_lleva()
    {
        var datos = GameData.Load();
        if (datos.Npcs.Count == 0) return;
        int bolsillo = Bot.LoDeLasCompras(
            Bot.ReservaParaLasPociones(0, Bot.PrecioDeLaPocionEn(datos)),
            Bot.ReservaParaLoQueFalta(Bot.CostoPorPuntoEn(datos, esBebida: false), 0, Bot.ComidaQueSeLleva(20))
            + Bot.ReservaParaLoQueFalta(Bot.CostoPorPuntoEn(datos, esBebida: true), 0, Bot.BebidaQueSeLleva(20)),
            5 * Bot.PrecioDelAntidotoEn(datos), 0);
        Assert.InRange(bolsillo, 1_000, 12_000);
    }

    [Fact]
    public void Lo_de_las_compras_es_el_doble_de_llenar_todo_mas_la_herramienta()
    {
        Assert.Equal(2 * (4000 + 400 + 100) + 200, Bot.LoDeLasCompras(4000, 400, 100, 200) - Bot.LoDeLasCompras(0, 0, 0, 0));
    }

    /// <summary>LOS BANQUEROS DE RINKEL ESTÁN ADENTRO DEL BANCO: desde el herrero (19,16) no hay camino, y abrir una de las puertas
    /// del mapa lo da. En la ronda 42 (06/10) Elmo salía "sin camino" y no depositaba nunca en el pueblo donde compran casi todos.</summary>
    [Fact]
    public void A_los_banqueros_de_Rinkel_se_llega_abriendo_una_puerta()
    {
        var datos = GameData.Load();
        if (datos.MapExits.Count == 0 || !datos.MapObjects.TryGetValue(20, out var objetos)) return;
        var mapas = new Navegante(datos);
        bool EsBanquero(int ficha) => datos.Npc((short)ficha) is { NpcType: 4 };
        var puertas = Bot.PuertasQueElMapaTiene(objetos, (19, 16), obj => Bot.PuertaQueSeAbre(datos.Obj((short)obj)));
        Assert.NotEmpty(puertas);
        Assert.Contains(puertas, p => mapas.AbrirlaDaCamino(20, (19, 16), p.Donde, EsBanquero));
    }
}
