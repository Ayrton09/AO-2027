using System.Text;

namespace CoverAO.Protocol;

/// <summary>
/// Shared codec constants for the CoverAO wire protocol.
/// Verified against src/CoverAO-2021/Servidor/Codigo/clsByteQueue.cls (x86 little-endian).
/// See fase0/spec/PROTOCOLO.md and the reference implementation fase0/codec/bytequeue.py.
/// </summary>
public static class Codec
{
    /// <summary>Windows-1252 — the codepage of the Spanish AO build (ANSI strings).</summary>
    public static readonly Encoding Ansi;

    static Codec()
    {
        // Code-page encodings are not registered by default on modern .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252);
    }
}
