using System.Buffers.Binary;

namespace CoverAO.Protocol;

/// <summary>Raised when a Read would run past the end of the available buffer.</summary>
public sealed class NotEnoughDataException : Exception
{
    public NotEnoughDataException(int need, int have)
        : base($"need {need} bytes, have {have}") { }
}

/// <summary>Se levanta cuando una cadena del cliente pasa el tope de <see cref="ByteQueueReader"/>.
/// Es DISTINTA de <see cref="NotEnoughDataException"/> a propósito: aquélla significa "todavía no llegaron
/// todos los bytes, esperá", y ésta "ningún cliente manda algo así", que termina en cierre de conexión.</summary>
public sealed class StringTooLongException : Exception
{
    public StringTooLongException(int length, int max)
        : base($"cadena de {length} caracteres; el tope es {max}") { }
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
    private readonly int _maxString;

    /// <param name="maxStringLength">Tope de caracteres de una cadena leída de este buffer; 0 = sin tope.
    ///
    /// <para>Va acá y no en cada handler porque el dispatcher lee CIENTO OCHO cadenas del cliente y el tope
    /// tiene que valer para las que todavía no existen: la auditoría de textos de clan puso techo a las
    /// cuatro del panel del líder y se le pasaron las del acto de FUNDAR, que son las mismas cuatro por otro
    /// paquete. El largo de una cadena lo elige entero el que manda: con el buffer de entrada en 64 KB, un
    /// nombre de clan de treinta mil caracteres viaja pegado al nombre del personaje en cada
    /// <c>CharacterCreate</c>, o sea multiplicado por cada uno que lo tenga en pantalla.</para>
    ///
    /// <para>La dirección SERVIDOR→CLIENTE no lleva tope: ahí sí hay cadenas legítimamente largas (la lista
    /// de conectados y la de clanes viajan como un solo string separado por NUL).</para></param>
    public ByteQueueReader(ReadOnlyMemory<byte> data, int maxStringLength = 0)
    {
        _buf = data;
        _maxString = maxStringLength;
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
        // El tope se mira ANTES de pedir los bytes: pedirlos primero devolvería "faltan datos" —que significa
        // esperar más— en vez de cortar al que mandó el largo imposible.
        if (_maxString > 0 && length > _maxString) throw new StringTooLongException(length, _maxString);
        return Codec.Ansi.GetString(Take(length));
    }

    public string ReadUnicodeString()
    {
        short charCount = BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        if (charCount < 0) throw new InvalidOperationException($"negative string length: {charCount}");
        if (_maxString > 0 && charCount > _maxString) throw new StringTooLongException(charCount, _maxString);
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
