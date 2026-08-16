using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA BÓVEDA DESTRUÍA LO QUE NO ENTRABA EN LA RANURA.
///
/// <para>El resto del juego apila hasta <c>GameLimits.MaxInventoryObjs</c> (10.000, el
/// <c>MAX_INVENTORY_OBJS</c> del original) y <c>FindSlotFor</c> lo respeta: si el montón no entra, busca una
/// ranura vacía. <c>FindBankSlotFor</c> no miraba NADA — devolvía la primera ranura con el mismo objeto,
/// estuviera como estuviera— y los dos handlers cerraban la suma con
/// <c>Math.Min(short.MaxValue, actual + move)</c>.</para>
///
/// <para>Las dos mitades juntas dan pérdida silenciosa: con 30.000 flechas en la bóveda, depositar 5.000
/// descuenta las 5.000 del inventario y la bóveda queda en 32.767. Las 2.233 del medio no van a ningún
/// lado, y el jugador no recibe ni un aviso. Lo mismo al revés al retirar.</para>
///
/// <para>Estos tests miran la INVARIANTE que importa —que la suma de las dos puntas no cambie— y no la
/// implementación: es lo único que distingue "se movió" de "se perdió".</para>
/// </summary>
public class BovedaTopeDeRanuraTests
{
    private const short Flecha = 480;

    private static Player ConBoveda(int enBoveda, int enInventario)
    {
        var p = new Player { Name = "Probador", Account = "cuenta_test", CharIndex = 1 };
        if (enBoveda > 0) p.BankInventory[1] = new InventorySlot { ObjIndex = Flecha, Amount = (short)enBoveda };
        if (enInventario > 0) p.Inventory[1] = new InventorySlot { ObjIndex = Flecha, Amount = (short)enInventario };
        return p;
    }

    private static int TotalDe(Player p)
    {
        int n = 0;
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
            if (p.BankInventory[i].ObjIndex == Flecha) n += p.BankInventory[i].Amount;
        for (int i = 1; i <= GameLimits.MaxInventorySlots; i++)
            if (p.Inventory[i].ObjIndex == Flecha) n += p.Inventory[i].Amount;
        return n;
    }

    /// <summary>La ranura de la bóveda no puede pasar del tope que respeta el resto del juego.</summary>
    [Fact]
    public void LaRanuraDeLaBovedaNoPasaElTopeDelJuego()
    {
        var p = ConBoveda(enBoveda: GameLimits.MaxInventoryObjs - 100, enInventario: 500);
        int antes = TotalDe(p);

        int slot = PacketDispatcher.FindBankSlotFor(p, Flecha, 500);
        Assert.NotEqual(0, slot);
        Assert.True(p.BankInventory[slot].Amount + 500 <= GameLimits.MaxInventoryObjs,
            $"la ranura {slot} quedaría en {p.BankInventory[slot].Amount + 500}, por encima del tope de {GameLimits.MaxInventoryObjs}");
        Assert.Equal(antes, TotalDe(p));
    }

    /// <summary>Con la ranura llena hay que ir a una VACÍA, no apilar encima.</summary>
    [Fact]
    public void ConLaRanuraLlenaBuscaUnaVacia()
    {
        var p = ConBoveda(enBoveda: GameLimits.MaxInventoryObjs, enInventario: 1);
        int slot = PacketDispatcher.FindBankSlotFor(p, Flecha, 1);
        Assert.NotEqual(0, slot);
        Assert.NotEqual(1, slot);                       // la 1 está llena
        Assert.True(p.BankInventory[slot].IsEmpty);     // así que tiene que ser una vacía
    }

    /// <summary>Y si no queda NINGUNA ranura, se rechaza en vez de destruir.</summary>
    [Fact]
    public void SinRanuraLibreDevuelveCero()
    {
        var p = new Player { Name = "Probador", Account = "cuenta_test", CharIndex = 1 };
        for (int i = 1; i <= GameLimits.MaxBankSlots; i++)
            p.BankInventory[i] = new InventorySlot { ObjIndex = Flecha, Amount = (short)GameLimits.MaxInventoryObjs };
        Assert.Equal(0, PacketDispatcher.FindBankSlotFor(p, Flecha, 1));
    }
}
