using System;
using System.Linq;
using CoverAO.Protocol.Iao;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Verifies the IAO emitters byte-for-byte against transcripts CAPTURED FROM THE LIVE IAO server
/// (IMPAOSV.exe), stored in fase0/parity/iao_golden/. This is the pivot's capture-verify loop as a
/// test: the modern emission of an IAO packet must equal the real server's bytes on the wire.
/// </summary>
public class IaoServerMessagesTests
{
    private static byte[] Hex(string h) =>
        h.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();

    [Fact]
    public void ShowMessageBox_matches_captured_create_account_response()
    {
        // create_account.json: what IMPAOSV.exe sent back after CreateNewAccount.
        var golden = Hex("28 23 00 4c 61 20 63 75 65 6e 74 61 20 68 61 20 73 69 64 6f 20 63 72 65 61 64 61 " +
                         "20 63 6f 6e 20 65 78 69 74 6f 2e");
        Assert.Equal(golden, IaoServerMessages.ShowMessageBox("La cuenta ha sido creada con exito."));
    }

    [Fact]
    public void ShowAccount_matches_captured_connect_account_response()
    {
        // connect_account.json: a single byte, id 105.
        Assert.Equal(new byte[] { 0x69 }, IaoServerMessages.ShowAccount());
    }

    [Fact]
    public void AddPj_matches_a_captured_char_list_entry()
    {
        // login_barrin.json: the "Nicky jam" entry (2nd AddPj in the ConnectAccount char list).
        var golden = Hex("68 02 09 00 4e 69 63 6b 79 20 6a 61 6d 01 00 01 00 02 02 02 01 01 00 02 02");
        Assert.Equal(golden, IaoServerMessages.AddPj(
            index: 2, name: "Nicky jam", head: 1, body: 1, casco: 2, weapon: 2, shield: 2,
            nivel: 1, pos: 1, clase: 2, color: 2));
    }
}
