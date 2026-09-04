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
/// <para>Ya pasó tres veces. El <c>SND_NIVEL = 6</c> se portó un día de julio y su archivo no existía; el
/// <c>PlayWave(78)</c> de la incineración se agregó el 2026-09-04 y el fuego ardía en silencio; y ese mismo
/// día este barrido —recién escrito— dejó pasar CUATRO más, por el motivo de abajo.</para>
///
/// <para><b>LO QUE NO SE PUEDE RESOLVER SE DENUNCIA, NO SE SALTEA.</b> La primera versión resolvía sólo
/// literales y constantes, y lo demás lo daba por "sale de la data" sin mirarlo. No era cierto: el id también
/// viaja en una VARIABLE LOCAL —<c>snd = esManzana ? 82 : 7</c> al comer, <c>craftSound</c> con su default al
/// fabricar— y el de MORIR no lo emitía nadie. Resultado: el juego salía mudo al comer, al morder una manzana
/// y al fabricar, con este test en verde. Ahora los nombres se resuelven contra sus asignaciones, y todo
/// argumento que quede sin clasificar hace fallar el test por su nombre.</para>
///
/// <para>La otra mitad son los ids que salen de la DATA (el <c>Snd2</c> de una criatura, el <c>WAV</c> de un
/// hechizo): esos se reconocen porque el argumento es un acceso a miembro (<c>obj.Snd1</c>), los deriva de los
/// .json el extractor <c>fase0/transcode/decode_sounds.py</c>, y si falta uno el síntoma es el mismo pero la
/// causa es no haber vuelto a correr el extractor.</para>
/// </summary>
public class SonidosQueElServerPuedeMandarTests
{
    /// <summary>Parámetros que sólo REENVÍAN un id que ya se contó en su llamador (los helpers de emisión).
    /// Es la única excusa aceptada para no resolver un nombre, y va explícita para que agregar otra sea una
    /// decisión y no un descuido.</summary>
    private static readonly HashSet<string> PasaCalles = new() { "wave" };

    private static List<string> FuentesDelServer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server", "src", "CoverAO.Server")))
            dir = dir.Parent;
        Assert.True(dir is not null, "no encontré server/src/CoverAO.Server desde " + AppContext.BaseDirectory);
        var raiz = Path.Combine(dir!.FullName, "server", "src", "CoverAO.Server");
        return Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    private static string CarpetaDeSonidos()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "client-web", "assets", "sounds")))
            dir = dir.Parent;
        Assert.True(dir is not null, "no encontré client-web/assets/sounds");
        return Path.Combine(dir!.FullName, "client-web", "assets", "sounds");
    }

    [Fact]
    public void Todo_sonido_que_el_server_puede_mandar_tiene_su_archivo()
    {
        var archivos = FuentesDelServer();
        string sonidos = CarpetaDeSonidos();

        // 1) las CONSTANTES de sonido, en las dos formas que usa el código.
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

        // De un ternario cuentan las RAMAS, no la condición: los números de `obj == 1 || obj == 467` son
        // ObjIndex, y colarlos haría exigir un `467.wav` que no existe. Un id inventado es tan malo como uno
        // perdido.
        HashSet<int> Valores(string expr)
        {
            int q = expr.IndexOf('?');
            if (q >= 0) expr = expr[(q + 1)..];
            var v = new HashSet<int>();
            foreach (Match m in Regex.Matches(expr, @"\b(\d{1,4})\b")) v.Add(int.Parse(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(expr, @"\b([A-Za-z_]\w*)\b"))
                if (constantes.TryGetValue(m.Groups[1].Value, out int c)) v.Add(c);
            return v;
        }

        // 2) los NOMBRES que llevan un id: inicializador, reasignación, default de parámetro y argumento con
        //    nombre del call site.
        var bind = new Dictionary<string, HashSet<int>>();
        foreach (var f in archivos)
        {
            string texto = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(texto, @"(?:short|int|byte)\s+(\w+)\s*(?:=([^,;)]*))?"))
            {
                if (!bind.TryGetValue(m.Groups[1].Value, out var b)) bind[m.Groups[1].Value] = b = new HashSet<int>();
                if (m.Groups[2].Success) b.UnionWith(Valores(m.Groups[2].Value));
            }
            foreach (Match m in Regex.Matches(texto, @"^\s*(\w+)\s*=([^;=][^;]*);", RegexOptions.Multiline))
                if (bind.TryGetValue(m.Groups[1].Value, out var b)) b.UnionWith(Valores(m.Groups[2].Value));
            foreach (Match m in Regex.Matches(texto, @"(?<=[(,])\s*(\w+)\s*:\s*([A-Za-z_]\w*|\d+)\s*(?=[,)])"))
                if (bind.TryGetValue(m.Groups[1].Value, out var b)) b.UnionWith(Valores(m.Groups[2].Value));
        }

        // 3) el barrido de los call sites. El argumento se toma ENTERO hasta la coma para poder clasificarlo.
        var llamada = new Regex(@"PlayWave\(\s*([^,]+),");
        var faltan = new List<string>();
        var sinClasificar = new List<string>();
        var vistos = new HashSet<int>();
        // Un mismo id puede salir de varios lados (una variable con el mismo nombre vive en dos archivos),
        // asi que el mensaje nombra TODOS los sitios en vez de inventar uno solo.
        var donde_de = new Dictionary<int, List<string>>();
        foreach (var f in archivos)
        {
            var lineas = File.ReadAllLines(f);
            for (int i = 0; i < lineas.Length; i++)
            {
                string s = lineas[i].Trim();
                if (s.StartsWith("//") || !s.Contains("PlayWave(")) continue;
                foreach (Match m in llamada.Matches(s))
                {
                    string arg = m.Groups[1].Value.Trim();
                    string donde = $"{Path.GetFileName(f)}:{i + 1}";
                    string desnudo = Regex.Replace(arg, @"^\(short\)\s*", "").Trim();
                    // La FIRMA del propio emisor (`public static byte[] PlayWave(short wave, ...)`) también
                    // contiene "PlayWave(": se reconoce porque el argumento trae el TIPO delante.
                    if (Regex.IsMatch(desnudo, @"^(short|int|byte)\s+\w+$")) continue;

                    var ids = new HashSet<int>();
                    if (Regex.IsMatch(desnudo, @"^\d+$")) ids.Add(int.Parse(desnudo));
                    else if (constantes.TryGetValue(desnudo, out int c)) ids.Add(c);
                    else if (desnudo.Contains('.')) continue;               // sale de la DATA: lo cubre el extractor
                    else if (PasaCalles.Contains(desnudo)) continue;        // reenvío de un id ya contado
                    else if (bind.TryGetValue(desnudo, out var b) && b.Count > 0) ids.UnionWith(b);
                    else { sinClasificar.Add($"{arg}  en {donde}"); continue; }

                    foreach (int id in ids)
                    {
                        if (!donde_de.TryGetValue(id, out var sitios)) donde_de[id] = sitios = new List<string>();
                        if (!sitios.Contains(donde)) sitios.Add(donde);
                    }
                }
            }
        }

        foreach (var (id, sitios) in donde_de)
        {
            vistos.Add(id);
            if (!File.Exists(Path.Combine(sonidos, $"{id}.wav")))
                faltan.Add($"{id}.wav  lo puede mandar {string.Join(", ", sitios)}");
        }

        // La PREMISA: si el barrido no encontró casi nada, el que está roto es el barrido y no el juego.
        Assert.True(vistos.Count >= 10, $"el barrido resolvió sólo {vistos.Count} ids: revisá el patrón, no el juego");

        // LO NO CLASIFICADO ES UN FALLO, no un salteo. Es exactamente lo que dejó pasar los cuatro sonidos
        // mudos: el id estaba en una variable y el barrido siguió de largo sin decir nada.
        Assert.True(sinClasificar.Count == 0,
            "hay PlayWave con un argumento que este barrido no sabe resolver. NO se puede saltear en silencio: "
            + "o se le enseña a resolverlo, o se lo agrega a PasaCalles con su motivo." + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", sinClasificar));

        Assert.True(faltan.Count == 0,
            "el servidor puede mandar estos sonidos y el cliente no los tiene (se extraen con "
            + "fase0/transcode/decode_sounds.py):" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", faltan));
    }
}
