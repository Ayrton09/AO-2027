using System.Buffers.Binary;

namespace CoverAO.Protocol;

/// <summary>Raised when a Read would run past the end of the available buffer.</summary>
public sealed class NotEnoughDataException : Exception
{
    public NotEnoughDataException(int need, int have)
        : base($"need {need} bytes, have {have}") { }
}

/// <summary>
/// Consumes an inbound packet buffer. Faithful port of the VB6 clsByteQueue Read* methods.
/// Advances an internal cursor; <see cref="Consumed"/> reports how many bytes were read so a
/// stream decoder can remove exactly one message from the front of a rolling buffer.
/// </summary>
public sealed class ByteQueueReader
{
    private readonly ReadOnlyMemory<byte> _buf;
    private int _pos;

    public ByteQueueReader(ReadOnlyMemory<byte> data)
    {
        _buf = data;
    }

    public int Consumed => _pos;
    public bool AtEnd => _pos >= _buf.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _buf.Length)
            throw new NotEnoughDataException(n, _buf.Length - _pos);
        var span = _buf.Span.Slice(_pos, n);
        _pos += n;
        return span;
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => Take(1)[0] == 1;

    public short ReadInteger() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public int ReadLong() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public string ReadAsciiString()
    {
        short length = BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        if (length < 0) throw new InvalidOperationException($"negative string length: {length}");
        return Codec.Ansi.GetString(Take(length));
    }

    public string ReadUnicodeString()
    {
        short charCount = BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        if (charCount < 0) throw new InvalidOperationException($"negative string length: {charCount}");
        return System.Text.Encoding.Unicode.GetString(Take(charCount * 2));
    }

    public byte[] ReadBytes(int n) => Take(n).ToArray();

    /// <summary>Peek the next byte (the packet id) without consuming it.</summary>
    public byte PeekByte()
    {
        if (_pos >= _buf.Length) throw new NotEnoughDataException(1, 0);
        return _buf.Span[_pos];
    }
}
