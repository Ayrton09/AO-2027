namespace CoverAO.Protocol;

/// <summary>
/// Wire "encryption": a symmetric single-byte XOR over the whole packet buffer
/// (clsSecurity.NAC_E_Byte / NAC_D_Byte). Applied ONLY client->server; server->client
/// is plaintext. XOR is symmetric, so encrypt == decrypt.
///
/// The key starts at <see cref="DefaultKey"/> and becomes a random per-session byte that
/// the server announces in the <c>Logged</c> packet (the <c>redundance</c> field). The
/// transform itself is applied inline where the bytes arrive (ClientSession), so this
/// class only carries the bootstrap key.
/// </summary>
public static class Crypto
{
    public const byte DefaultKey = 13;
}
