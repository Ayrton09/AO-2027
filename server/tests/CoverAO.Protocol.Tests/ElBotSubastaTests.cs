using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT VENDE POR EL CENTRO DE SUBASTAS (decisión del autor, 22/10; ver <c>Bots.Subastas.cs</c>): lee la lista, en un pueblo
/// con Subastador guarda lo que vale en vez de vendérselo al mercader, y lo publica con compra directa por debajo del mostrador.
/// </summary>
public class ElBotSubastaTests
{
    private static readonly GameData Datos = GameData.Load();

    /// <summary>Algo que se subasta y no es equipo, comida ni poción: es botín sin depender de lo que tiene puesto.</summary>
    private static readonly ObjInfo Joya = Datos.Objects.Values.First(o => Bot.VaALaSubasta(o) && !o.IsWeapon && !o.IsArmor
        && !o.IsShield && !o.IsHelmet && !o.IsFood && !o.IsDrink && !o.IsPotion && !o.IsGold && o.IsPickable);

    [Fact]
    public void Lee_la_lista_del_centro()
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        s.Recibir(ServerMessages.SubastasLista(false, new[]
        {
            new ServerMessages.FilaDeSubasta(7, 120, 1, 450, 600, "Bruma", 3600, ""),
            new ServerMessages.FilaDeSubasta(9, 33, 5, 80, 0, "Juan Perez", 60, "Ana"),
        }));
        Assert.Equal(1, s.VecesQueVioLasSubastas);
        Assert.Equal(2, s.Subastas.Count);
        Assert.Equal(new ServerMessages.FilaDeSubasta(9, 33, 5, 80, 0, "Juan Perez", 60, "Ana"), s.Subastas[1]);
    }

    [Theory]
    [InlineData("El Centro de Subastas dejó en tu bóveda 600 monedas de oro.", 600)]
    [InlineData("El Centro de Subastas dejó en tu bóveda: 1 Espada Larga.", null)]
    [InlineData("[Ana] El Centro de Subastas dejó en tu bóveda 600 monedas de oro.", null)]
    public void El_oro_de_una_venta_se_lee_del_aviso(string linea, int? oro)
    {
        Assert.Equal(oro, SentidosDelBot.OroQueDejoLaSubasta(linea));
    }

    [Fact]
    public void El_oro_de_una_venta_suma_a_la_boveda_que_sabe()
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        s.Recibir(ServerMessages.BankInit(true, 1_000));
        s.Recibir(ServerMessages.ConsoleMsg("El Centro de Subastas dejó en tu bóveda 600 monedas de oro.", PacketDispatcher.FuenteInfo));
        Assert.Equal(1_600, s.Banco);
    }

    /// <summary>Aun vendida en el piso y descontado el depósito, le queda más que lo que paga el mercader (un tercio).</summary>
    [Theory]
    [InlineData(Bot.ValorParaSubastar)]
    [InlineData(3_000)]
    [InlineData(40_000)]
    public void El_precio_queda_entre_lo_que_paga_y_lo_que_cobra_el_mercader(int valor)
    {
        var (inicial, final) = Bot.PrecioDeSubasta(valor);
        long neto = inicial - CentroDeSubastas.Deposito(Bot.HorasDeSubasta, inicial, final);
        Assert.True(valor / 3 < neto && inicial < final && final < valor, $"{inicial} / {final}, neto {neto}");
        Assert.False(Bot.VaALaSubasta(Datos.Objects.Values.First(o => o.Value is > 0 and < Bot.ValorParaSubastar)));
    }

    [Theory]
    [InlineData("El Centro de Subastas dejó en tu bóveda: 1 Espada Larga.", "Espada Larga")]
    [InlineData("El Centro de Subastas dejó en tu bóveda 600 monedas de oro.", null)]
    [InlineData("[Ana] El Centro de Subastas dejó en tu bóveda: 1 Espada Larga.", null)]
    public void Lo_que_deja_en_la_boveda_se_lee_del_aviso(string linea, string? nombre)
    {
        Assert.Equal(nombre, SentidosDelBot.ObjetoQueDejoLaSubasta(linea));
    }

    private static readonly MethodInfo GuardaM = typeof(Bot).GetMethod("GuardaParaLaSubasta", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublicarM = typeof(Bot).GetMethod("PasarPorLaSubastaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ListaVistaEnF = typeof(Bot).GetField("_listaVistaEn", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo LeVendeAM = typeof(Bot).GetMethod("LeVendeA", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo AnotarM = typeof(Bot).GetMethod("AnotarLoQueDejoLaSubasta", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Se_guarda_solo_donde_hay_subastador_y_hasta_el_tope()
    {
        var (bot, s, _) = Armar(Rinkel);
        Assert.True((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
        // Con las suyas ya publicadas, se lo vende al mercader.
        s.Recibir(ServerMessages.SubastasLista(false, Enumerable.Range(1, Bot.SubastasPropias)
            .Select(i => new ServerMessages.FilaDeSubasta(i, Joya.Index, 1, 1, 2, "bruma", 60, "")).ToList()));
        ListaVistaEnF.SetValue(bot, Environment.TickCount64);
        Assert.False((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
        // Pero la lista vieja no cuenta: pueden haber vencido o haberse vendido, y vuelve a mirar.
        ListaVistaEnF.SetValue(bot, Environment.TickCount64 - Bot.MirarLaSubastaMs - 1);
        Assert.True((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);

        var (bot2, s2, _) = Armar(NuevaEsperanza);
        Assert.False((bool)GuardaM.Invoke(bot2, new object[] { s2, Joya.Index })!);
    }

    /// <summary>
    /// EL MERCADER SIGUE SIRVIENDO PARA LO QUE SE GUARDA: la guarda se mira en el mostrador, no al elegir adónde ir. Decidido desde el
    /// coto, al llegar a un pueblo con Subastador no quedaba mercader para eso y el bot iba y volvía sin vender, una y otra vez.
    /// </summary>
    [Fact]
    public void Lo_que_se_guarda_no_le_saca_el_mercader()
    {
        var (bot, s, _) = Armar(Rinkel);
        Assert.True((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
        Assert.True((bool)LeVendeAM.Invoke(bot, new object[] { s, 1000, Joya.Index })!);
    }

    /// <summary>LO SUYO QUE VENCIÓ SIN OFERTAS: el aviso del Centro lo pone para retirar, y no se vuelve a publicar.</summary>
    [Fact]
    public void Lo_que_vencio_se_retira_y_no_se_vuelve_a_publicar()
    {
        var (bot, s, _) = Armar(Rinkel);
        s.Recibir(ServerMessages.ConsoleMsg($"El Centro de Subastas dejó en tu bóveda: 1 {Joya.Name}.", PacketDispatcher.FuenteInfo));
        AnotarM.Invoke(bot, new object[] { s });
        Assert.Contains(Joya.Index, (HashSet<short>)PorRetirarF.GetValue(bot)!);
        Assert.False((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
    }

    [Fact]
    public async Task Al_lado_del_subastador_lo_clickea_pide_la_lista_y_publica()
    {
        var (bot, s, t) = Armar(Rinkel);
        s.Recibir(ServerMessages.ChangeInventorySlot(3, Joya.Index, 1, false, 0, 1));
        var publicar = (Task<bool>)PublicarM.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, CancellationToken.None })!;

        var clic = await Siguiente(t, ClientPacketId.LeftClick);
        Assert.Equal(new byte[] { SubastadorX, SubastadorY }, clic[1..3]);
        await Siguiente(t, ClientPacketId.SubastasPedir);
        s.Recibir(ServerMessages.SubastasLista(false, Array.Empty<ServerMessages.FilaDeSubasta>()));

        var pub = await Siguiente(t, ClientPacketId.SubastaPublicar);
        var (inicial, final) = Bot.PrecioDeSubasta(Joya.Value);
        Assert.Equal(3, pub[1]);
        Assert.Equal(1, BitConverter.ToInt16(pub, 2));
        Assert.Equal(inicial, BitConverter.ToInt32(pub, 4));
        Assert.Equal(final, BitConverter.ToInt32(pub, 8));
        Assert.Equal(Bot.HorasDeSubasta, pub[12]);
        // El server se la toma: sale de la mochila y vuelve la lista con la suya.
        s.Recibir(ServerMessages.ChangeInventorySlot(3, 0, 0, false, 0, 0));
        s.Recibir(ServerMessages.SubastasLista(false, new[] { new ServerMessages.FilaDeSubasta(1, Joya.Index, 1, inicial, final, "Bruma", 43_200, "") }));
        Assert.True(await publicar.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Si_no_lo_atiende_por_un_rato_vende_como_siempre()
    {
        var (bot, s, t) = Armar(Rinkel);
        s.Recibir(ServerMessages.ChangeInventorySlot(3, Joya.Index, 1, false, 0, 1));
        var publicar = (Task<bool>)PublicarM.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, CancellationToken.None })!;
        await Siguiente(t, ClientPacketId.SubastasPedir);   // y la lista no llega nunca
        Assert.False(await publicar.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
    }

    [Theory]
    [InlineData("Juan Perez", 600, 1, 5_000, true)]
    [InlineData("Juan Perez", 801, 1, 5_000, false)]      // más del 80 % del valor: el mercader no la cobra mucho más
    [InlineData("Juan Perez", 0, 1, 5_000, false)]        // sin compra directa: no oferta
    [InlineData("Juan Perez", 600, 2, 5_000, false)]      // el precio es del lote
    [InlineData("Juan Perez", 600, 1, 500, false)]        // no le alcanza lo de encima
    [InlineData("bruma", 600, 1, 5_000, false)]           // la suya
    public void Compra_barato_y_de_una_persona(string vendedor, int final, short cantidad, int plata, bool compra)
    {
        var f = new ServerMessages.FilaDeSubasta(1, 1, cantidad, final, final, vendedor, 60, "");
        Assert.Equal(compra, Bot.LeConvieneLaSubasta(f, 1_000, "Bruma", plata));
    }

    [Fact]
    public void De_otro_bot_no_compra()
    {
        var f = new ServerMessages.FilaDeSubasta(1, 1, 1, 100, 100, PobladoDeBots.Nombres[0], 60, "");
        Assert.False(Bot.LeConvieneLaSubasta(f, 1_000, "Bruma", 5_000));
    }

    private static readonly MethodInfo MejoraM = typeof(Bot).GetMethod("MejoraLoQueTiene", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RetirarM = typeof(Bot).GetMethod("RetirarLoDeLaSubastaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PorRetirarF = typeof(Bot).GetField("_porRetirar", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>En Lindos, que tiene Subastador y no banquero: compra lo que lo mejora y no va a ningún lado a retirarlo.</summary>
    [Fact]
    public async Task Compra_directo_lo_que_lo_mejora_y_lo_anota_para_retirar()
    {
        var (bot, s, t) = Armar(Lindos, LindosX, LindosY, oro: 50_000);
        short mejora = Datos.Objects.Values.Where(o => o.Value is >= 1_000 and <= 20_000 && (bool)MejoraM.Invoke(bot, new object[] { s, o.Index })!)
                                           .Select(o => o.Index).First();
        int precio = (int)Matematicas.Porcentaje(Datos.Obj(mejora)!.Value, 50);
        var publicar = (Task<bool>)PublicarM.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, CancellationToken.None })!;
        await Siguiente(t, ClientPacketId.SubastasPedir);
        s.Recibir(ServerMessages.SubastasLista(false, new[]
        {
            new ServerMessages.FilaDeSubasta(4, mejora, 1, precio, precio * 3, "Juan Perez", 600, ""),   // cara
            new ServerMessages.FilaDeSubasta(5, mejora, 1, precio, precio, "Juan Perez", 600, ""),
        }));
        var compra = await Siguiente(t, ClientPacketId.SubastaComprar);
        Assert.Equal(5, BitConverter.ToInt32(compra, 1));
        s.Recibir(ServerMessages.UpdateGold(50_000 - precio));
        Assert.True(await publicar.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(mejora, (HashSet<short>)PorRetirarF.GetValue(bot)!);
    }

    [Fact]
    public async Task Lo_comprado_se_retira_de_la_boveda()
    {
        var banquero = Datos.MapSpawns[Rinkel].First(n => Datos.Npc(n.NpcIndex)?.NpcType == 4);
        var (bot, s, t) = Armar(Rinkel, (byte)banquero.X, (byte)banquero.Y, oro: 1_000, ficha: (short)banquero.NpcIndex);
        ((HashSet<short>)PorRetirarF.GetValue(bot)!).Add(Joya.Index);
        var retirar = (Task)RetirarM.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, CancellationToken.None })!;
        await Siguiente(t, ClientPacketId.DoubleClick);
        s.Recibir(ServerMessages.BankInit(true, 0));
        await Siguiente(t, ClientPacketId.BankStart);
        s.Recibir(ServerMessages.ChangeBankSlot(1, 33, 4, 1));
        s.Recibir(ServerMessages.ChangeBankSlot(5, Joya.Index, 1, Joya.Value));
        s.Recibir(ServerMessages.BankInit(false));
        var retira = await Siguiente(t, ClientPacketId.BankExtractItem);
        Assert.Equal(5, retira[1]);
        Assert.Equal(1, BitConverter.ToInt16(retira, 2));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, Joya.Index, 1, false, 0, 1));
        await Siguiente(t, ClientPacketId.BankEnd);
        await retirar.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((HashSet<short>)PorRetirarF.GetValue(bot)!);
    }

    private const short Yo = 500, ElSubastador = 600;
    private const int Rinkel = 20, NuevaEsperanza = 112, Lindos = 62;
    private const byte SubastadorX = 20, SubastadorY = 81, LindosX = 21, LindosY = 68;

    private static (Bot, SentidosDelBot, TransporteDeBot) Armar(int mapa) => Armar(mapa, SubastadorX, SubastadorY);

    /// <summary>El bot a dos baldosas de un NPC que está a la vista: por omisión, el Subastador de Rinkel (20,81).</summary>
    private static (Bot, SentidosDelBot, TransporteDeBot) Armar(int mapa, byte npcX, byte npcY, int oro = 5_000, short ficha = 164)
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        var yo = new Player
        {
            CharIndex = Yo, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos((short)mapa, npcX, (byte)(npcY + 2)), Level = 30,
            MaxHp = 300, Hp = 300, MaxStamina = 300, Stamina = 300, Gold = oro, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(Yo));
        s.Recibir(ServerMessages.ChangeMap((short)mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = ElSubastador, Template = Datos.Npc(ficha)!, Pos = new WorldPos((short)mapa, npcX, npcY),
        }));
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, Datos, new Navegante(Datos), null!), 1);
        return (bot, s, new TransporteDeBot("Bruma"));
    }

    /// <summary>El próximo paquete de ese tipo que manda el bot (los demás se saltean).</summary>
    private static async Task<byte[]> Siguiente(TransporteDeBot t, ClientPacketId id)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);
        var buffer = new byte[4096];
        while (DateTime.UtcNow < limite)
        {
            using var cts = new CancellationTokenSource(500);
            int n;
            try { n = await t.ReceiveAsync(buffer, cts.Token); } catch (OperationCanceledException) { continue; }
            if (n == 0) continue;
            var paquete = new byte[n];
            for (int i = 0; i < n; i++) paquete[i] = (byte)(buffer[i] ^ t.Clave);
            if (paquete[0] == (byte)id) return paquete;
        }
        throw new Xunit.Sdk.XunitException($"el bot no mandó {id}");
    }
}
