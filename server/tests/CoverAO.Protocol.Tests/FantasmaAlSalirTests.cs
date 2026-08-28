using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// QUIÉN DEJA FANTASMA AL IRSE DE LA PANTALLA.
///
/// <para><c>CharacterRemove</c> lleva un booleano <c>Desvanecido</c> y el cliente lo obedece:
/// <c>HandleCharacterRemove</c> (Cliente/Codigo/Protocol.bas:1822) sólo llama a <c>CrearFantasma</c> si viene
/// en <c>True</c>. En el original vale <c>True</c> en todas las emisiones vivas menos una — al desconectar,
/// después de guardar (<c>EraseUserChar(..., False)</c>, TCP.bas:1650) — y <c>True</c> al cambiar de mapa
/// (Modulo_UsUaRiOs.bas:1597) y al sacar una criatura (<c>EraseNPCChar</c>, MODULO_NPCs.bas:672).</para>
///
/// <para><b>Lo que este archivo cuida no es el layout, es la DECISIÓN de cada emisión.</b> El parámetro tenía
/// un default en <c>false</c> y las dieciocho emisiones se escribieron sin nombrarlo, así que el mundo entero
/// mandaba lo contrario del original y el cliente encima ignoraba el campo. El oráculo no puede cazarlo:
/// compara QUÉ paquetes vuelven, no sus bytes.</para>
/// </summary>
public class FantasmaAlSalirTests
{
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    /// <summary>El byte viaja, y viaja distinto según el caso. Sin esto el resto del archivo mediría humo.</summary>
    [Fact]
    public void El_booleano_llega_al_paquete()
    {
        var conFantasma = ServerMessages.CharacterRemove(7, faded: true);
        var sinFantasma = ServerMessages.CharacterRemove(7, faded: false);
        Assert.Equal(new byte[] { (byte)ServerPacketId.CharacterRemove, 7, 0, 1 }, conFantasma);
        Assert.Equal(new byte[] { (byte)ServerPacketId.CharacterRemove, 7, 0, 0 }, sinFantasma);
    }

    /// <summary>
    /// LA CAUSA RAÍZ, y por eso se cuida por reflexión y no leyendo el fuente: mientras el parámetro no tenga
    /// default, el compilador obliga a decidir en cada emisión nueva. Volver a ponerle uno reabre el bug
    /// entero de una sola línea y sin que falle nada más.
    /// </summary>
    [Fact]
    public void El_parametro_no_puede_tener_default()
    {
        var m = typeof(ServerMessages).GetMethod(nameof(ServerMessages.CharacterRemove));
        Assert.NotNull(m);
        var faded = m!.GetParameters().Single(p => p.Name == "faded");
        Assert.False(faded.HasDefaultValue,
            "`faded` volvió a tener un valor por defecto: así se escribieron 18 emisiones sin decidirlo.");
    }

    /// <summary>
    /// Y el ÚNICO caso que el original manda en false: la desconexión. Se lee del fuente porque la decisión
    /// vive en el call site, no en el emisor — un test que llamara al emisor con `false` y comprobara que
    /// escribe un 0 no diría nada sobre qué manda el server cuando alguien se va.
    /// </summary>
    [Fact]
    public void Solo_la_desconexion_se_va_sin_fantasma()
    {
        var raiz = Path.GetDirectoryName(BuscarEnElRepo("server/src/CoverAO.Server/Net/ServerMessages.cs"))!;
        var llamadas = Directory
            .EnumerateFiles(Path.GetDirectoryName(raiz)!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f).Select((linea, i) => (archivo: f, i, linea)))
            .Where(t => t.linea.Contains("ServerMessages.CharacterRemove("))
            .ToList();

        Assert.True(llamadas.Count >= 15,
            $"esperaba encontrar las emisiones de CharacterRemove en el fuente y hallé {llamadas.Count}");

        foreach (var (archivo, i, linea) in llamadas)
        {
            Assert.True(Regex.IsMatch(linea, @"CharacterRemove\([^()]*,\s*faded:\s*(true|false)\)"),
                $"{Path.GetFileName(archivo)}:{i + 1} emite CharacterRemove sin nombrar `faded`: {linea.Trim()}");
        }

        // Las que se van SIN fantasma son las dos de desconexión, las dos en el despachador.
        var sinFantasma = llamadas.Where(t => t.linea.Contains("faded: false")).ToList();
        Assert.Equal(2, sinFantasma.Count);
        Assert.All(sinFantasma, t => Assert.EndsWith("PacketDispatcher.cs", t.archivo));
    }
}
