using System.Buffers.Binary;

namespace CoverAO.Protocol;

/// <summary>
/// Builds an outbound packet buffer. Faithful port of the VB6 clsByteQueue Write* methods.
/// All multi-byte values are little-endian; Integer/Long are signed (VB6 Integer=16-bit, Long=32-bit).
/// Fluent: each Write* returns the writer.
/// </summary>
public sealed class ByteQueueWriter
{
    private readonly List<byte> _buf = new(64);

    public int Length => _buf.Count;

    public ByteQueueWriter WriteByte(byte value)
    {
        _buf.Add(value);
        return this;
    }

    /// <summary>Write a server packet id (the leading byte of an outbound message).</summary>
    public ByteQueueWriter WritePacketId(ServerPacketId id) => WriteByte((byte)id);

    public ByteQueueWriter WriteBoolean(bool value)
    {
        _buf.Add(value ? (byte)1 : (byte)0);
        return this;
    }

    public ByteQueueWriter WriteInteger(short value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(tmp, value);
        _buf.AddRange(tmp);
        return this;
    }

    public ByteQueueWriter WriteLong(int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        _buf.AddRange(tmp);
        return this;
    }

    public ByteQueueWriter WriteSingle(float value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(tmp, value);
        _buf.AddRange(tmp);
        return this;
    }

    public ByteQueueWriter WriteDouble(double value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(tmp, value);
        _buf.AddRange(tmp);
        return this;
    }

    /// <summary>Variable ASCII string: Int16 char-count prefix (LE) + ANSI (cp1252) bytes.</summary>
    public ByteQueueWriter WriteAsciiString(string value)
    {
        // RECORTE AL MÁXIMO DEL PREFIJO. El largo va en un Int16, así que un string de más de 32767 hacía
        // que el cast se envolviera —prefijo negativo o truncado— mientras el payload se escribía ENTERO: el
        // receptor quedaba desincronizado de forma permanente, porque el protocolo no tiene largo de trama.
        // Y no era teórico: `HandleWhisper` arma "<nombre> te susurra: <chat>" con el texto del atacante, así
        // que con un susurro de 32 KB se rompía el stream DE LA VÍCTIMA. Igual en /DENUNCIAR (contra todos los
        // GMs) y en el mensaje a un amigo. Recortar degrada el mensaje; envolver el prefijo rompe la conexión.
        if (value.Length > short.MaxValue) value = value[..short.MaxValue];
        // VB6 prefix is CInt(Len(Value)) = CHARACTER count. For ANSI, chars == bytes.
        WriteInteger((short)value.Length);
        if (value.Length > 0)
            _buf.AddRange(Codec.Ansi.GetBytes(value));
        return this;
    }

    /// <summary>Fixed ASCII string: raw bytes, no length prefix.</summary>
    public ByteQueueWriter WriteAsciiStringFixed(string value)
    {
        _buf.AddRange(Codec.Ansi.GetBytes(value));
        return this;
    }

    /// <summary>Variable Unicode string: Int16 char-count prefix (LE) + UTF-16LE bytes (count*2).</summary>
    public ByteQueueWriter WriteUnicodeString(string value)
    {
        WriteInteger((short)value.Length);
        if (value.Length > 0)
            _buf.AddRange(System.Text.Encoding.Unicode.GetBytes(value));
        return this;
    }

    public ByteQueueWriter WriteBytes(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data);
        return this;
    }

    public byte[] ToArray() => _buf.ToArray();
}
