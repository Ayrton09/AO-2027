using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// UN `PlayWave(N)` SIN SU `N.wav` NO FALLA, NO LOGUEA Y NO ROMPE NADA: simplemente no se escucha.
///
/// <para>Ya pasó dos veces. El <c>SND_NIVEL = 6</c> se portó un día de julio y su archivo no existía; el
/// <c>PlayWave(78)</c> de la incineración se agregó el 2026-09-04 y el fuego ardía en silencio. Las dos veces
/// el emisor estaba bien escrito y el hueco era el ASSET, que ningún test miraba — y las pruebas a mano se
/// hacen con <c>?mute</c> justamente para no molestar, así que el único camino que quedaba tampoco pasaba
/// por ahí.</para>
///
/// <para>Este barrido lee los <c>PlayWave(...)</c> del propio servidor —literales y constantes— y exige el
/// archivo. Es la mitad del problema que vive en el CÓDIGO. La otra mitad son los ids que salen de la DATA
/// (el <c>Snd2</c> de una criatura, el <c>WAV</c> de un hechizo): esos los deriva de los .json el extractor
/// <c>fase0/transcode/decode_sounds.py</c>, que es el que trae los archivos. Si alguna vez falta uno de esos,
/// el síntoma es el mismo pero la causa es no haber vuelto a correr el extractor.</para>
/// </summary>
public class SonidosQueElServerPuedeMandarTests
{
    [Fact]
    public void Todo_sonido_que_el_server_puede_mandar_tiene_su_archivo()
    {
        string fuentes = SubirHasta("server/src/CoverAO.Server");
        string sonidos = SubirHasta("client-web/assets/sounds");

        var archivos = new List<string>();
        foreach (var f in Directory.EnumerateFiles(fuentes, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            archivos.Add(f);
        }

        // Las constantes de sonido del servidor. Se aceptan las dos formas que usa el código: la declaración
        // suelta (`private const short SndPuerta = 5;`) y la de a varias en una línea (`SndCasamiento = 161,
        // SndSanar = 55;`), que sólo se toma si el nombre parece de sonido — si no, entraría cualquier número.
        var constantes = new Dictionary<string, int>();
        foreach (var f in archivos)
        {
            string texto = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(texto, @"const\s+short\s+(\w+)\s*=\s*(\d+)"))
                constantes[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(texto, @"(\w+)\s*=\s*(\d+)\s*[,;]"))
            {
                string nombre = m.Groups[1].Value;
                if ((nombre.StartsWith("Snd", StringComparison.Ordinal) || nombre.EndsWith("Wave", StringComparison.Ordinal))
                    && !constantes.ContainsKey(nombre))
                    constantes[nombre] = int.Parse(m.Groups[2].Value);
            }
        }

        var llamada = new Regex(@"PlayWave\(\s*(?:\(short\)\s*)?([A-Za-z_]\w*|\d+)");
        var faltan = new List<string>();
        var vistos = new HashSet<int>();
        foreach (var f in archivos)
        {
            var lineas = File.ReadAllLines(f);
            for (int i = 0; i < lineas.Length; i++)
            {
                string s = lineas[i].Trim();
                if (s.StartsWith("//") || !s.Contains("PlayWave(")) continue;
                foreach (Match m in llamada.Matches(s))
                {
                    string tok = m.Groups[1].Value;
                    int id;
                    if (int.TryParse(tok, out id)) { }
                    else if (constantes.TryGetValue(tok, out id)) { }
                    else continue;   // sale de la data (npc.Snd2, spell.Wav…): lo cubre el extractor
                    if (!vistos.Add(id)) continue;
                    if (!File.Exists(Path.Combine(sonidos, $"{id}.wav")))
                        faltan.Add($"{id}.wav  lo manda {Path.GetFileName(f)}:{i + 1}");
                }
            }
        }

        // La PREMISA: si el barrido no encontró NINGÚN id, el que está roto es el barrido y no el juego. Sin
        // esto, cambiar el nombre del paquete dejaría este test en verde para siempre sin mirar nada.
        Assert.True(vistos.Count >= 10, $"el barrido resolvió sólo {vistos.Count} ids: revisá el patrón, no el juego");

        Assert.True(faltan.Count == 0,
            "el servidor puede mandar estos sonidos y el cliente no los tiene (se extraen con "
            + "fase0/transcode/decode_sounds.py):" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", faltan));
    }

    /// <summary>Sube desde el binario del test hasta encontrar la carpeta. Si no está FALLA: saltear en
    /// silencio sería el mismo defecto que este barrido existe para cazar.</summary>
    private static string SubirHasta(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string rel = relativo.Replace('/', Path.DirectorySeparatorChar);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, rel);
            if (Directory.Exists(candidato)) return candidato;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }
}
