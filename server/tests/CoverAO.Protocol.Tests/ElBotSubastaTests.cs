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

    [Fact]
    public void El_precio_queda_entre_lo_que_paga_y_lo_que_cobra_el_mercader()
    {
        var (inicial, final) = Bot.PrecioDeSubasta(3_000);
        Assert.True(3_000 / 3 < inicial && inicial < final && final < 3_000, $"{inicial} / {final}");
        Assert.False(Bot.VaALaSubasta(Datos.Objects.Values.First(o => o.Value is > 0 and < Bot.ValorParaSubastar)));
    }

    private static readonly MethodInfo GuardaM = typeof(Bot).GetMethod("GuardaParaLaSubasta", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PublicarM = typeof(Bot).GetMethod("PublicarEnLaSubastaAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Se_guarda_solo_donde_hay_subastador_y_hasta_el_tope()
    {
        var (bot, s, _) = Armar(Rinkel);
        Assert.True((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);
        // Con las suyas ya publicadas, se lo vende al mercader.
        s.Recibir(ServerMessages.SubastasLista(false, Enumerable.Range(1, Bot.SubastasPropias)
            .Select(i => new ServerMessages.FilaDeSubasta(i, Joya.Index, 1, 1, 2, "bruma", 60, "")).ToList()));
        Assert.False((bool)GuardaM.Invoke(bot, new object[] { s, Joya.Index })!);

        var (bot2, s2, _) = Armar(NuevaEsperanza);
        Assert.False((bool)GuardaM.Invoke(bot2, new object[] { s2, Joya.Index })!);
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

    private const short Yo = 500, ElSubastador = 600;
    private const int Rinkel = 20, NuevaEsperanza = 112;
    private const byte SubastadorX = 20, SubastadorY = 81;

    /// <summary>El bot a dos baldosas del Subastador de Rinkel (20,81), que está a la vista.</summary>
    private static (Bot, SentidosDelBot, TransporteDeBot) Armar(int mapa)
    {
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        var yo = new Player
        {
            CharIndex = Yo, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos((short)mapa, SubastadorX, SubastadorY + 2), Level = 30,
            MaxHp = 300, Hp = 300, MaxStamina = 300, Stamina = 300, Gold = 5_000, FactionStatus = Player.StatusCiudadano,
        };
        s.Recibir(ServerMessages.UserIndexInServer(Yo));
        s.Recibir(ServerMessages.ChangeMap((short)mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.CharacterCreate(new Npc
        {
            CharIndex = ElSubastador, Template = Datos.Npc(164)!, Pos = new WorldPos((short)mapa, SubastadorX, SubastadorY),
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
