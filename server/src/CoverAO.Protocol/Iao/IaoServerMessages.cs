namespace CoverAO.Protocol.Iao;

/// <summary>
/// IAO server-packet emitters — the emission layer of the IAO pivot. Each method reproduces the exact
/// wire bytes of IMPAOSV.exe's <c>Write*</c> subs (Protocol.bas), using the plaintext clsByteQueue
/// codec (same as CoverAO). Verified byte-for-byte against transcripts captured from the LIVE IAO
/// server — see <c>IaoServerMessagesTests</c> and <c>fase0/parity/iao_golden/</c>. This is the first
/// emission code of the pivot; it grows packet-by-packet as more of IMPAOSV.exe is captured.
/// </summary>
public static class IaoServerMessages
{
    private static ByteQueueWriter Begin(IaoServerPacketId id) => new ByteQueueWriter().WriteByte((byte)id);

    /// <summary>WriteShowMessageBox: a modal message (ASCIIString).</summary>
    public static byte[] ShowMessageBox(string message) =>
        Begin(IaoServerPacketId.ShowMessageBox).WriteAsciiString(message).ToArray();

    /// <summary>WriteErrorMsg: an error message that also closes the socket (ASCIIString).</summary>
    public static byte[] ErrorMsg(string message) =>
        Begin(IaoServerPacketId.ErrorMsg).WriteAsciiString(message).ToArray();

    /// <summary>WriteConsoleMsg: chat/console line (ASCIIString + Byte fontIndex).</summary>
    public static byte[] ConsoleMsg(string chat, byte fontIndex) =>
        Begin(IaoServerPacketId.ConsoleMsg).WriteAsciiString(chat).WriteByte(fontIndex).ToArray();

    /// <summary>WriteShowAccount: switch the client to the account/char-selection screen (id only).</summary>
    public static byte[] ShowAccount() =>
        Begin(IaoServerPacketId.ShowAccount).ToArray();

    /// <summary>WriteChangeMap: load a map — NOTE IAO sends TWO integers (map + version), unlike CoverAO's one.</summary>
    public static byte[] ChangeMap(short map, short version) =>
        Begin(IaoServerPacketId.ChangeMap).WriteInteger(map).WriteInteger(version).ToArray();

    /// <summary>WriteUserIndexInServer: the player's server-side user index (Integer).</summary>
    public static byte[] UserIndexInServer(short userIndex) =>
        Begin(IaoServerPacketId.UserIndexInServer).WriteInteger(userIndex).ToArray();

    /// <summary>WriteAddPj: one entry of the account's character list (shown after ShowAccount).</summary>
    public static byte[] AddPj(byte index, string name, short head, short body, byte casco,
        byte weapon, byte shield, byte nivel, short pos, byte clase, byte color) =>
        Begin(IaoServerPacketId.AddPJ)
            .WriteByte(index).WriteAsciiString(name).WriteInteger(head).WriteInteger(body)
            .WriteByte(casco).WriteByte(weapon).WriteByte(shield).WriteByte(nivel)
            .WriteInteger(pos).WriteByte(clase).WriteByte(color).ToArray();
}
