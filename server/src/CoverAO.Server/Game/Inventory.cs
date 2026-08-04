namespace CoverAO.Server.Game;

/// <summary>One inventory slot: item id, quantity, and whether it's equipped.</summary>
public struct InventorySlot
{
    public short ObjIndex;
    public short Amount;
    public bool Equipped;

    public readonly bool IsEmpty => ObjIndex == 0;
}
