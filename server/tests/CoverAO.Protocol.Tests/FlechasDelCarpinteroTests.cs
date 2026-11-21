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
}
