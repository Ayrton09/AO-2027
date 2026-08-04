using CoverAO.Protocol;
using CoverAO.Protocol.Iao;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Pins the IAO wire packet IDs — generated from IMPAOSV.exe's Protocol.bas by
/// fase0/parity/iao_gen_enums.py — to the values VERIFIED byte-for-byte against the LIVE IAO server
/// (fase0/parity/iao_capture.py captured them from IMPAOSV.exe on :7666). First step of the IAO pivot:
/// the protocol enums are live, compiled code checked against real captured bytes, not just source.
/// </summary>
public class IaoProtocolTests
{
    [Fact]
    public void Server_ids_match_bytes_captured_from_the_live_iao_server()
    {
        // create_account golden: the server replied with byte 0x28 = 40 = ShowMessageBox + the message
        // "La cuenta ha sido creada con exito." (see fase0/parity/iao_golden/create_account.json).
        Assert.Equal(40, (int)IaoServerPacketId.ShowMessageBox);
        // connect_account golden: the server replied with byte 0x69 = 105 = ShowAccount.
        Assert.Equal(105, (int)IaoServerPacketId.ShowAccount);
        Assert.Equal(38, (int)IaoServerPacketId.ConsoleMsg);
        Assert.Equal(28, (int)IaoServerPacketId.ChangeMap);
    }

    [Fact]
    public void Client_ids_match_the_iao_login_flow()
    {
        Assert.Equal(0, (int)IaoClientPacketId.ConnectAccount);
        Assert.Equal(2, (int)IaoClientPacketId.LoginExistingChar);   // (CoverAO's is 1)
        Assert.Equal(7, (int)IaoClientPacketId.Walk);                // (CoverAO's is 0)
    }

    [Fact]
    public void Iao_ids_diverge_from_coverao_by_design()
    {
        // The pivot's premise, in a test: IAO and CoverAO share 0% of packet-ID values.
        // ShowMessageBox is 40 in IAO, 22 in CoverAO — the modern server must speak IAO's ids to be
        // verifiable against IMPAOSV.exe.
        Assert.Equal(40, (int)IaoServerPacketId.ShowMessageBox);
        Assert.NotEqual((int)ServerPacketId.ShowMessageBox, (int)IaoServerPacketId.ShowMessageBox);
    }
}
