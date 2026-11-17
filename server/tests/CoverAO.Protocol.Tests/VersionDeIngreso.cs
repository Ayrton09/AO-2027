using CoverAO.Protocol;
using CoverAO.Server;

namespace CoverAO.Protocol.Tests;

internal static class VersionDeIngreso
{
    // Las pruebas de login usan el cliente de esta compilación, sin fijar el número de una release vieja.
    public static ByteQueueWriter WriteVersionDelJuego(this ByteQueueWriter writer)
    {
        var version = System.Version.Parse(Build.Version);
        return writer.WriteByte(checked((byte)version.Major)).WriteByte(checked((byte)version.Minor))
            .WriteByte(checked((byte)version.Build));
    }
}
