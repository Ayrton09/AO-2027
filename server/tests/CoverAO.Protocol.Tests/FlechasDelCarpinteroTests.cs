using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Las flechas del Cazador, del Carpintero (parte D del pedido del tester, 20/11; el autor la eligió el 22/11): el Carpintero las
/// fabrica y las subasta, y el Cazador las compra; el arco, en el Carpintero NPC.
/// </summary>
public sealed class FlechasDelCarpinteroTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private const short Flecha = 480, FlechaMas3 = 553, ArcoCompuesto = 479, Carpintero = 44, FlechaSimple = 169;

    [Fact]
    public void Las_buenas_las_hace_el_carpintero()
    {
        var d = Datos.Value;
        Assert.True(Bot.EsFlechaDeCarpintero(d, Flecha));
        Assert.True(Bot.EsFlechaDeCarpintero(d, FlechaMas3));
        Assert.False(Bot.EsFlechaDeCarpintero(d, ArcoCompuesto));
        // La de la tienda, la Flecha Simple, pega menos que la peor del Carpintero.
        Assert.True(d.Obj(FlechaSimple) is { IsAmmo: true, MaxHit: var simple } && simple < d.Obj(Flecha)!.MinHit);
        Assert.Contains(d.Npc(Carpintero)!.StockItems!, s => s.ObjIndex == FlechaSimple);
    }

    /// <summary>Valen 0 en el obj.dat: el precio sale de la leña (10 cada una), más la mitad.</summary>
    [Fact]
    public void El_valor_sale_de_la_lena()
    {
        Assert.Equal(15, Bot.ValorDeLaFlecha(Datos.Value, Flecha));
        Assert.Equal(60, Bot.ValorDeLaFlecha(Datos.Value, FlechaMas3));
        Assert.Equal(15, Bot.ValorDeMercado(Datos.Value, Flecha));
    }

    [Fact]
    public void Al_cazador_le_conviene_el_precio_del_carpintero()
    {
        int valor = Bot.ValorDeLaFlecha(Datos.Value, Flecha);
        var (_, final) = Bot.PrecioDeSubasta(valor * Bot.FlechasPorLote);
        var lote = new ServerMessages.FilaDeSubasta(1, Flecha, (short)Bot.FlechasPorLote, 0, final, "Osmar", 3_600, "");
        Assert.True(Bot.LeConvienenLasFlechas(lote, valor, "Gonzo", 100_000, tiene: 0));
        Assert.False(Bot.LeConvienenLasFlechas(lote, valor, "Gonzo", 100_000, tiene: Bot.FlechasQueQuiereElCazador));   // tiene
        Assert.False(Bot.LeConvienenLasFlechas(lote, valor, "Osmar", 100_000, tiene: 0));                               // las suyas
        Assert.False(Bot.LeConvienenLasFlechas(lote, valor, "Gonzo", final - 1, tiene: 0));                             // sin plata
        var cara = lote with { PrecioFinal = valor * Bot.FlechasPorLote };
        Assert.False(Bot.LeConvienenLasFlechas(cara, valor, "Gonzo", 100_000, tiene: 0));                               // al valor entero, no
    }

    [Fact]
    public void El_carpintero_npc_vende_un_arco_para_el_cazador()
    {
        var d = Datos.Value;
        Assert.Contains(d.Npc(Carpintero)!.StockItems!, s => s.ObjIndex == ArcoCompuesto);
        Assert.True(d.Obj(ArcoCompuesto) is { IsBow: true, Value: > 0 } arco && !arco.ClassForbidden(GameLimits.ClaseCazador));
    }

    /// <summary>Ronda 134: el Arco Compuesto es de dos manos y el servidor no lo deja poner con escudo (263). Primero se saca el
    /// escudo, después se pone el arco.</summary>
    [Fact]
    public async Task Con_el_arco_de_dos_manos_primero_se_saca_el_escudo()
    {
        var d = Datos.Value;
        const short Escudo = 404;   // Escudo de Tortuga
        Assert.True(d.Obj(ArcoCompuesto) is { TwoHanded: 1 } && d.Obj(Escudo) is { IsShield: true });
        var s = new SentidosDelBot { MiNombre = "Gonzo" };
        var yo = new Player { CharIndex = 500, Name = "Gonzo", Account = "gonzo_test", Pos = new WorldPos(1, 50, 50), Level = 23,
                              MaxHp = 220, Hp = 220, MaxStamina = 300, Stamina = 300, Class = GameLimits.ClaseCazador };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(1));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, Escudo, 1, true, 0, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(2, ArcoCompuesto, 1, false, 0, 1));
        s.Recibir(ServerMessages.ChangeInventorySlot(3, FlechaSimple, 54, false, 0, 1));
        var bot = new Bot("Gonzo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, new Navegante(d), null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, GameLimits.ClaseCazador);
        var t = new TransporteDeBot("Gonzo");
        var equiparse = typeof(Bot).GetMethod("Equiparse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)equiparse.Invoke(bot, new object[] { t, s })!);
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[64];
        int n = await t.ReceiveAsync(buffer, corte.Token);
        Assert.Equal(new byte[] { (byte)ClientPacketId.EquipItem, 1 }, buffer.Take(n).Select(b => (byte)(b ^ t.Clave)).ToArray());
    }

    /// <summary>Ronda 135: Gonzo, con el arco y una flecha o ninguna, no pasaba por la subasta, donde estaba el lote de Osmar.</summary>
    [Theory]
    [InlineData(GameLimits.ClaseCazador, true, 1, true)]
    [InlineData(GameLimits.ClaseCazador, true, 300, false)]    // ya tiene las que quiere
    [InlineData(GameLimits.ClaseCazador, false, 0, false)]     // sin arco, primero el arco en la tienda
    [InlineData(GameLimits.ClaseGuerrero, true, 0, false)]
    public void El_cazador_va_a_la_subasta_por_flechas(byte clase, bool conArco, int flechas, bool va)
    {
        var d = Datos.Value;
        var s = new SentidosDelBot { MiNombre = "Gonzo" };
        var yo = new Player { CharIndex = 500, Name = "Gonzo", Account = "gonzo_test", Pos = new WorldPos(1, 50, 50), Level = 23,
                              MaxHp = 220, Hp = 220, MaxStamina = 300, Stamina = 300, Class = clase };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(1));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        if (conArco) s.Recibir(ServerMessages.ChangeInventorySlot(1, ArcoCompuesto, 1, true, 0, 1));
        if (flechas > 0) s.Recibir(ServerMessages.ChangeInventorySlot(2, FlechaSimple, (short)flechas, true, 0, 1));
        var bot = new Bot("Gonzo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, new Navegante(d), null!), 1);
        typeof(Bot).GetField("_clase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, clase);
        var busca = typeof(Bot).GetMethod("CazadorBuscaFlechas", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(va, (bool)busca.Invoke(bot, new object[] { s })!);
    }
}
