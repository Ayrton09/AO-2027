using System.Text;
using CoverAO.Protocol;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Byte-exact tests for the C# codec. These assert the ACTUAL bytes on the wire, mirroring
/// fase0/codec/test_bytequeue.py — both codecs must agree byte-for-byte with the VB6 peer.
/// </summary>
public class ByteQueueTests
{
    private static byte[] W(Action<ByteQueueWriter> build)
    {
        var w = new ByteQueueWriter();
        build(w);
        return w.ToArray();
    }

    [Fact]
    public void PrimitivesAreLittleEndian()
    {
        Assert.Equal(new byte[] { 0xC8 }, W(w => w.WriteByte(200)));
        Assert.Equal(new byte[] { 0x02, 0x01 }, W(w => w.WriteInteger(258)));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, W(w => w.WriteInteger(-1)));
        Assert.Equal(new byte[] { 0x70, 0x11, 0x01, 0x00 }, W(w => w.WriteLong(70000)));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, W(w => w.WriteLong(-1)));
        Assert.Equal(BitConverter.GetBytes(1.0f), W(w => w.WriteSingle(1.0f)));
        Assert.Equal(BitConverter.GetBytes(2.5d), W(w => w.WriteDouble(2.5d)));
        Assert.Equal(new byte[] { 0x01 }, W(w => w.WriteBoolean(true)));
        Assert.Equal(new byte[] { 0x00 }, W(w => w.WriteBoolean(false)));
    }

    [Fact]
    public void AsciiStringIsCharCountPrefixPlusCp1252()
    {
        Assert.Equal(new byte[] { 0x02, 0x00, (byte)'A', (byte)'B' }, W(w => w.WriteAsciiString("AB")));
        Assert.Equal(new byte[] { 0x00, 0x00 }, W(w => w.WriteAsciiString("")));
        // cp1252: 'á' is 0xE1
        Assert.Equal(new byte[] { 0x01, 0x00, 0xE1 }, W(w => w.WriteAsciiString("á")));
        Assert.Equal(Encoding.ASCII.GetBytes("XYZ"), W(w => w.WriteAsciiStringFixed("XYZ")));
    }

    [Fact]
    public void UnicodeStringIsCharCountPrefixPlusUtf16Le()
    {
        var expected = new byte[] { 0x02, 0x00 }.Concat(Encoding.Unicode.GetBytes("AB")).ToArray();
        Assert.Equal(expected, W(w => w.WriteUnicodeString("AB")));
    }

    [Fact]
    public void CompositeFrameMatchesExpectedBytes()
    {
        // e.g. a Talk-like frame: [id=3][ascii "Hola AO"]
        var frame = W(w => w.WriteByte(3).WriteAsciiString("Hola AO"));
        var expected = new byte[] { 0x03, 0x07, 0x00 }.Concat(Encoding.ASCII.GetBytes("Hola AO")).ToArray();
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void ReaderRoundTripsEveryType()
    {
        var w = new ByteQueueWriter()
            .WriteByte(255).WriteInteger(-30000).WriteLong(123456789)
            .WriteSingle(3.5f).WriteDouble(-9.75d).WriteBoolean(true)
            .WriteAsciiString("Rugura").WriteUnicodeString("Ñandú");

        var r = new ByteQueueReader(w.ToArray());
        Assert.Equal(255, r.ReadByte());
        Assert.Equal((short)-30000, r.ReadInteger());
        Assert.Equal(123456789, r.ReadLong());
        Assert.Equal(3.5f, r.ReadSingle());
        Assert.Equal(-9.75d, r.ReadDouble());
        Assert.True(r.ReadBoolean());
        Assert.Equal("Rugura", r.ReadAsciiString());
        Assert.Equal("Ñandú", r.ReadUnicodeString());
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void ReaderTracksConsumedForStreamFraming()
    {
        var frame = W(w => w.WriteByte((byte)ClientPacketId.Talk).WriteAsciiString("hi").WriteByte(1));
        var r = new ByteQueueReader(frame);
        Assert.Equal((byte)ClientPacketId.Talk, r.PeekByte());
        Assert.Equal((byte)ClientPacketId.Talk, r.ReadByte());
        Assert.Equal("hi", r.ReadAsciiString());
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(frame.Length, r.Consumed);
    }

    [Fact]
    public void ReaderUnderflowThrows()
    {
        Assert.Throws<NotEnoughDataException>(() => new ByteQueueReader(new byte[] { 1 }).ReadLong());
    }

    [Fact]
    public void PacketIdEnumsUseDeclarationOrder()
    {
        // The crux fixed in Fase 0: Walk = 0 (declaration order), NOT 5 (legacy comment).
        Assert.Equal(0, (byte)ClientPacketId.Walk);
        Assert.Equal(1, (byte)ClientPacketId.LoginExistingChar);
        Assert.Equal(0, (byte)ServerPacketId.LoggedSuccessful);
        Assert.Equal(1, (byte)ServerPacketId.Logged);
    }
}
