namespace CoverAO.Protocol;

/// <summary>
/// Wire "encryption": a symmetric single-byte XOR over the whole packet buffer
/// (clsSecurity.NAC_E_Byte / NAC_D_Byte). Applied ONLY client->server; server->client
/// is plaintext. XOR is symmetric, so encrypt == decrypt.
///
/// The key starts at <see cref="DefaultKey"/> and becomes a random per-session byte that
/// the server announces in the <c>Logged</c> packet (the <c>redundance</c> field).
///
/// NOTE: the exact bootstrap (whether the very first login packet is already XOR'd with 13)
/// must be confirmed against a real client capture via the parity harness before relying on
/// it in production. The transform itself is verified.
/// </summary>
public static class Crypto
{
    public const byte DefaultKey = 13;

    public static void XorInPlace(Span<byte> data, byte key)
    {
        for (int i = 0; i < data.Length; i++)
            data[i] ^= key;
    }

    public static byte[] Xor(ReadOnlySpan<byte> data, byte key)
    {
        var outBuf = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            outBuf[i] = (byte)(data[i] ^ key);
        return outBuf;
    }
}
