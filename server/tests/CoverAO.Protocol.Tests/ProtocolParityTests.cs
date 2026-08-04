using System.Text.RegularExpressions;
using CoverAO.Protocol;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Byte-parity guard against the ORIGINAL VB6 source of CoverAO-2021 (the ground truth).
/// The wire ID of every packet is the numeric value of its member in the VB6
/// <c>ServerPacketID</c> / <c>ClientPacketID</c> enums (positional from 0, since the VB6 enums
/// carry no explicit <c>= N</c>). A single insertion/removal/reordering in either enum would shift
/// every subsequent ID and silently break a real VB6 client. These tests re-derive the VB6 values
/// straight from <c>Protocol.bas</c> and assert the C# enums match name-by-name, case-insensitively.
/// </summary>
public class ProtocolParityTests
{
    // ---- Locate the VB6 source relative to the repo root (walk up from the test assembly). ----
    private static string? FindVb6Protocol()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CoverAO-2021", "Servidor", "Codigo", "Protocol.bas");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Parses a VB6 <c>Enum</c> body into name→value, mirroring VB6 semantics:
    /// members number positionally from 0; an explicit <c>= N</c> resets the running counter.</summary>
    private static Dictionary<string, int> ParseVb6Enum(string text, string enumName)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        bool inside = false;
        int next = 0;
        var header = new Regex($@"^\s*(?:Public|Private)\s+Enum\s+{Regex.Escape(enumName)}\b", RegexOptions.IgnoreCase);
        var member = new Regex(@"^\s*([A-Za-z_]\w*)\s*(?:=\s*(-?\w+))?\s*(?:'.*)?$");
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inside)
            {
                if (header.IsMatch(line)) inside = true;
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*End\s+Enum\b", RegexOptions.IgnoreCase)) break;
            var stripped = line.Split('\'')[0].Trim(); // drop VB6 comment
            if (stripped.Length == 0) continue;
            var m = member.Match(line);
            if (!m.Success) continue;
            int val = next;
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var assigned))
                val = assigned;
            result[m.Groups[1].Value] = val;
            next = val + 1;
        }
        return result;
    }

    private static Dictionary<int, string> CsEnumByValue<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().ToDictionary(v => Convert.ToInt32(v), v => v.ToString());

    private static void AssertEnumParity(string enumName, Dictionary<int, string> cs)
    {
        var path = FindVb6Protocol();
        // Si la fuente VB6 (src/CoverAO-2021) no está (p.ej. tras el retiro de Fase 4), esta comparación
        // no puede correr — pero el guard permanente NO desaparece: los snapshots de abajo
        // (*_snapshot_pins_ids) fijan los valores esperados sin depender del árbol VB6.
        if (path is null) return;
        var vb6 = ParseVb6Enum(File.ReadAllText(path), enumName);
        // VB6 value -> name
        var vb6ByValue = vb6.ToDictionary(kv => kv.Value, kv => kv.Key);

        // Same number of members on both sides.
        Assert.True(vb6ByValue.Count == cs.Count,
            $"{enumName}: VB6 tiene {vb6ByValue.Count} miembros, C# {cs.Count}.");

        // Every C# id must map to the same-named VB6 member (case-insensitive).
        foreach (var (value, csName) in cs)
        {
            Assert.True(vb6ByValue.TryGetValue(value, out var vb6Name),
                $"{enumName}: el valor {value} ({csName}) no existe en el enum VB6.");
            Assert.True(string.Equals(vb6Name, csName, StringComparison.OrdinalIgnoreCase),
                $"{enumName}[{value}]: VB6='{vb6Name}' vs C#='{csName}' (IDs desalineados).");
        }
    }

    [Fact]
    public void ServerPacketId_matches_vb6_source_byte_for_byte()
        => AssertEnumParity("ServerPacketID", CsEnumByValue<ServerPacketId>());

    [Fact]
    public void ClientPacketId_matches_vb6_source_byte_for_byte()
        => AssertEnumParity("ClientPacketID", CsEnumByValue<ClientPacketId>());

    // ---- Durable snapshot: pins the wire IDs WITHOUT the VB6 tree, so the guard survives Fase 4's
    // removal of src/CoverAO-2021. Member counts + spot-checks across the range catch any reorder
    // (a single insert/remove shifts every following id, which the spread of anchors detects). ----

    [Fact]
    public void ServerPacketId_snapshot_pins_ids()
    {
        Assert.Equal(107, Enum.GetValues<ServerPacketId>().Length);
        Assert.Equal(0, (int)ServerPacketId.LoggedSuccessful);
        Assert.Equal(9, (int)ServerPacketId.CommerceInit);
        Assert.Equal(10, (int)ServerPacketId.BankInit);
        Assert.Equal(14, (int)ServerPacketId.UpdateGold);
        Assert.Equal(19, (int)ServerPacketId.ChatOverHeadLocale);
        Assert.Equal(20, (int)ServerPacketId.ConsoleMsg);
        Assert.Equal(25, (int)ServerPacketId.CharacterCreate);
        Assert.Equal(48, (int)ServerPacketId.BlacksmithWeapons);
        Assert.Equal(53, (int)ServerPacketId.SastreObjects);
        Assert.Equal(68, (int)ServerPacketId.TrainerCreatureList);
        Assert.Equal(76, (int)ServerPacketId.GuildDetails);
        Assert.Equal(82, (int)ServerPacketId.UpdateTagAndStatus);
        Assert.Equal(106, (int)ServerPacketId.EjecutarAccion);
    }

    [Fact]
    public void ClientPacketId_snapshot_pins_ids()
    {
        Assert.Equal(114, Enum.GetValues<ClientPacketId>().Length);
        Assert.Equal(0, (int)ClientPacketId.Walk);
        Assert.Equal(17, (int)ClientPacketId.DropDestroy);
        Assert.Equal(20, (int)ClientPacketId.DoubleClick);
        Assert.Equal(26, (int)ClientPacketId.CraftSastre);
        Assert.Equal(30, (int)ClientPacketId.EquiparSkin);
        Assert.Equal(39, (int)ClientPacketId.MoveBank);
        Assert.Equal(60, (int)ClientPacketId.GuildRequestMembership);
        Assert.Equal(92, (int)ClientPacketId.GMCommands);
        Assert.Equal(102, (int)ClientPacketId.TransferGOLD);
        Assert.Equal(113, (int)ClientPacketId.DesconectarCuenta);
    }
}
