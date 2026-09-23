using CoverAO.Server.Data;

namespace CoverAO.Server.Game;

/// <summary>
/// EL MOSTRADOR DE UN MERCADER, como lo lleva el original (Modulo_InventANDobj.bas:103-263 y Comercio.bas:207-240).
/// Son 25 ranuras (<c>MAX_INVENTORY_SLOTS</c>) y un contador <c>NroItems</c> que arranca en el <c>NROITEMS</c> de la
/// ficha: una línea que falta en el .dat cuenta igual, así que hay mercaderes cuyo contador no llega nunca a cero.
/// <list type="bullet">
/// <item><b>Al vaciarse una ranura</b>, un objeto <c>Crucial</c> se repone solo con la cantidad de su línea del .dat
/// (<c>EncontrarCant</c>), si no queda otro igual en el mostrador. No es stock infinito: comprarle dos pociones al
/// alquimista le deja 98.</item>
/// <item><b>Cuando el contador llega a cero</b>, se recarga todo del .dat (<c>CargarInvent</c>), salvo que la ficha
/// traiga <c>InvReSpawn=1</c>: los diez mercaderes de Propiedades, que venden cada llave una sola vez.</item>
/// </list>
/// Las líneas del .dat llegan de afuera (<see cref="LineasDe"/>) porque el original las reescribe: la llave que se
/// vende queda en cero en el archivo (Comercio.bas:106-109).
/// </summary>
public static class InventarioDelMercader
{
    /// <summary>El mostrador recién cargado (<c>OpenNPC</c>, MODULO_NPCs.bas:200-210, y <c>CargarInvent</c>): las
    /// líneas 1..<paramref name="nroItems"/> en su ranura, cada una con lo que diga su línea aunque sea nada.</summary>
    public static void Cargar(Npc npc, IReadOnlyList<(int ObjIndex, int Amount)> lineas, int nroItems)
    {
        int n = Math.Clamp(nroItems, 0, GameLimits.MaxInventorySlots);
        var ranuras = new List<MerchantSlot>(GameLimits.MaxInventorySlots);
        for (int i = 0; i < GameLimits.MaxInventorySlots; i++)
            ranuras.Add(i < n && i < lineas.Count
                ? new MerchantSlot { ObjIndex = (short)lineas[i].ObjIndex, Amount = (short)lineas[i].Amount }
                : default);
        npc.Stock = ranuras;
        npc.NroItems = n;
    }

    /// <summary>La ranura <paramref name="ranura"/> (1..25), o vacía si el mostrador no llega hasta ahí.</summary>
    public static MerchantSlot Ranura(Npc npc, int ranura) =>
        npc.Stock is { } st && ranura >= 1 && ranura <= st.Count ? st[ranura - 1] : default;

    /// <summary><c>QuitarNpcInvItem</c> (Modulo_InventANDobj.bas:202-263): lo que se lleva el que compra. Las dos
    /// ramas del <c>If Crucial = 0</c> descuentan; lo que cambia es qué pasa al vaciarse la ranura.</summary>
    public static void Quitar(Npc npc, int ranura, int cantidad, bool crucial,
                              IReadOnlyList<(int ObjIndex, int Amount)> lineas, int nroItemsDelDat)
    {
        if (npc.Stock is not { } st || ranura < 1 || ranura > st.Count) return;
        var slot = st[ranura - 1];
        short obj = slot.ObjIndex;
        slot.Amount = (short)(slot.Amount - cantidad);
        if (slot.Amount > 0) { st[ranura - 1] = slot; return; }

        npc.NroItems--;
        st[ranura - 1] = default;
        if (crucial && !QuedanItems(npc, obj) && EncontrarCant(lineas, obj) is int cant and not 0)
        {
            st[ranura - 1] = new MerchantSlot { ObjIndex = obj, Amount = (short)cant };
            npc.NroItems++;
        }
        if (npc.NroItems == 0 && !npc.InvReSpawn) Cargar(npc, lineas, nroItemsDelDat);
    }

    /// <summary><c>SlotEnNPCInv</c> (Comercio.bas:207-240) y lo que <c>Comercio</c> hace con esa ranura (:174-186): lo
    /// vendido se apila en una ranura del mismo objeto si entra, si no va a la primera vacía y suma al contador.
    /// Devuelve la ranura (1..25), o 0 si no hay lugar — y entonces lo vendido no queda en ningún lado.</summary>
    public static byte Meter(Npc npc, short obj, int cantidad)
    {
        if (npc.Stock is not { } st) return 0;
        int i = st.FindIndex(s => s.ObjIndex == obj && s.Amount + cantidad <= GameLimits.MaxInventoryObjs);
        if (i < 0)
        {
            i = st.FindIndex(s => s.ObjIndex == 0);
            if (i < 0) return 0;
            npc.NroItems++;
        }
        var slot = st[i];
        slot.ObjIndex = obj;
        slot.Amount = (short)Math.Min(GameLimits.MaxInventoryObjs, slot.Amount + cantidad);
        st[i] = slot;
        return (byte)(i + 1);
    }

    /// <summary><c>ResetNpcInv</c> (Modulo_InventANDobj.bas:170-193), el /RESETINV: vacío, el contador en cero y
    /// <c>InvReSpawn</c> apagado, así que la próxima vez que se vacíe se recarga del .dat.</summary>
    public static void Vaciar(Npc npc)
    {
        if (npc.Stock is { } st)
            for (int i = 0; i < st.Count; i++) st[i] = default;
        npc.NroItems = 0;
        npc.InvReSpawn = false;
    }

    /// <summary>Las 25 líneas del .dat de esta ficha, con las llaves ya vendidas en cero.</summary>
    public static IReadOnlyList<(int ObjIndex, int Amount)> LineasDe(NpcInfo ficha, IReadOnlyDictionary<int, short>? vendidas)
    {
        var lineas = ficha.LineasDelDat ?? Array.Empty<(int, int)>();
        if (vendidas is not { Count: > 0 }) return lineas;
        var copia = lineas.ToArray();
        foreach (var (ranura, obj) in vendidas)
            if (ranura >= 1 && ranura <= copia.Length) copia[ranura - 1] = (obj, 0);
        return copia;
    }

    /// <summary><c>QuedanItems</c> (Modulo_InventANDobj.bas:103): mira el contador antes que las ranuras.</summary>
    private static bool QuedanItems(Npc npc, short obj) => npc.NroItems > 0 && npc.Stock!.Any(s => s.ObjIndex == obj);

    /// <summary><c>EncontrarCant</c> (Modulo_InventANDobj.bas:139): la cantidad de la PRIMERA de las 25 líneas con ese
    /// objeto, aunque quede más allá de <c>NROITEMS</c>; 0 si no está.</summary>
    public static int EncontrarCant(IReadOnlyList<(int ObjIndex, int Amount)> lineas, short obj)
    {
        foreach (var (o, cant) in lineas)
            if (o == obj) return cant;
        return 0;
    }
}
