using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL QUE MIRA EL ARCHIVO LE HACE PERDER EL GUARDADO AL JUGADOR.
///
/// <para>La escritura de <c>FileStore</c> es atómica porque termina en un rename, y en Windows el rename es
/// justo lo único que un tercero puede bloquear: <c>File.Move</c> con <c>overwrite</c> se cae con
/// <c>UnauthorizedAccessException</c> si el destino está ABIERTO por alguien, y no hay <c>FileShare</c> que lo
/// evite. Ese "alguien" no es raro: el antivirus escaneando al vuelo, el agente de backup del hosting, o
/// OneDrive sincronizando la carpeta del usuario — y el <c>data/</c> vive al lado del ejecutable.</para>
///
/// <para>Está MEDIDO contra el server andando, no razonado: con un solo proceso pidiéndole el tamaño a los
/// diez <c>.chr</c> del fixture —ni siquiera leyéndolos— se perdieron <b>181 de 10.620</b> escrituras (1,7%);
/// con ese proceso apagado, 0 de 12.430. Con el reintento puesto, 0 de 12.450 con el mirón encendido.</para>
///
/// <para>Y lo que se pierde es silencioso: el destino se queda con lo viejo, el jugador siguió jugando, y el
/// que hostea lo ve como alguien a quien "se le borró lo de la última media hora", cada tanto y sin patrón.</para>
/// </summary>
public class GuardadoConLectorAjenoTests
{
    private static string RaizNueva() =>
        Path.Combine(Path.GetTempPath(), "coverao-lector-" + Guid.NewGuid().ToString("N"));

    /// <summary>Abre el <c>.chr</c> con el share que BLOQUEA el rename y devuelve las dos señales: una que
    /// avisa que ya está abierto (antes de eso, escribir no probaría nada) y otra para soltarlo.</summary>
    private static (ManualResetEventSlim abierto, ManualResetEventSlim soltar) Aferrar(string ruta)
    {
        var abierto = new ManualResetEventSlim(false);
        var soltar = new ManualResetEventSlim(false);
        var hilo = new Thread(() =>
        {
            using var h = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
            abierto.Set();
            soltar.Wait(TimeSpan.FromSeconds(30));
        }) { IsBackground = true };
        hilo.Start();
        Assert.True(abierto.Wait(TimeSpan.FromSeconds(10)), "el hilo que aferra el archivo no llegó a abrirlo");
        return (abierto, soltar);
    }

    /// <summary>QUE REINTENTE, medido por el tiempo que tarda en rendirse.
    ///
    /// <para>La primera versión de este test era la obvia: aferrar el archivo unos milisegundos, soltarlo con un
    /// <c>Timer</c> y exigir que el guardado hubiera aterrizado. Pasaba sola y **fallaba en la suite** — el
    /// <c>Timer</c> corre en el pool de hilos, y con 709 tests en paralelo el pool no le da turno adentro de los
    /// 31 ms que dura el presupuesto del reintento. Es exactamente la trampa de <see cref="EsperaDelEscritor"/>:
    /// un plazo elegido a mano compitiendo contra una constante del producto que no se miró.</para>
    ///
    /// <para>Así que no se mide "aterrizó" sino "reintentó", y con una afirmación de UN SOLO LADO: si el destino
    /// no se suelta nunca, rendirse tiene que costar por lo menos el presupuesto. Una máquina cargada sólo puede
    /// hacer ese número MÁS grande, nunca más chico, así que no hay carrera que perder. Sin reintento, el
    /// <c>File.Move</c> pelado tira en el primer intento y el número se va a cero.</para></summary>
    [Fact]
    public void El_guardado_reintenta_antes_de_darse_por_vencido()
    {
        if (!OperatingSystem.IsWindows()) return;   // en Linux el rename sobre un abierto anda: no hay qué reintentar

        var raiz = RaizNueva();
        ManualResetEventSlim? soltar = null;
        try
        {
            var store = new FileStore(raiz);
            store.WriteCharacter("rugura", "[STATS]\nGLD=1\n");
            (_, soltar) = Aferrar(Path.Combine(raiz, "Charfile", "RUGURA.chr"));

            var reloj = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<IOException>(() => store.WriteCharacter("rugura", "[STATS]\nGLD=2\n"));
            reloj.Stop();

            // 1+2+4+8+16 = 31 ms de presupuesto. Se exige el 80% para no atarse al valor exacto.
            Assert.True(reloj.ElapsedMilliseconds >= 25,
                $"se rindió en {reloj.ElapsedMilliseconds} ms: no reintentó. Un handle de antivirus se suelta en " +
                "milisegundos, así que sin reintento el guardado del jugador se pierde y nadie se entera.");
        }
        finally
        {
            soltar?.Set();
            Thread.Sleep(50);
            if (Directory.Exists(raiz)) { try { Directory.Delete(raiz, recursive: true); } catch (IOException) { } }
        }
    }

    /// <summary>El otro lado de la regla, y el que importa de verdad: si el archivo NO se suelta, el guardado
    /// se pierde —eso es inevitable— pero el viejo tiene que quedar ENTERO y el error tiene que decir QUÉ se
    /// perdió. Antes esto salía como un <c>[loop] action error: Access to the path is denied.</c> pelado, que
    /// no nombra ni el archivo ni al jugador.</summary>
    [Fact]
    public void Si_no_se_suelta_nunca_el_viejo_queda_entero_y_el_error_nombra_el_archivo()
    {
        // En Linux el rename sobre un archivo abierto ANDA (el inode viejo sigue vivo para el que lo tiene
        // abierto), así que no hay nada que probar: la regla es de Windows.
        if (!OperatingSystem.IsWindows()) return;

        var raiz = RaizNueva();
        ManualResetEventSlim? soltar = null;
        try
        {
            var store = new FileStore(raiz);
            const string viejo = "[STATS]\nGLD=1\n";
            store.WriteCharacter("rugura", viejo);
            string ruta = Path.Combine(raiz, "Charfile", "RUGURA.chr");

            (_, soltar) = Aferrar(ruta);

            var ex = Assert.Throws<IOException>(() => store.WriteCharacter("rugura", "[STATS]\nGLD=2\n"));
            Assert.Contains("RUGURA.chr", ex.Message);
            Assert.Equal(viejo, store.ReadCharacter("rugura"));   // el guardado se perdió; el personaje NO
        }
        finally
        {
            soltar?.Set();
            Thread.Sleep(50);
            if (Directory.Exists(raiz)) { try { Directory.Delete(raiz, recursive: true); } catch (IOException) { } }
        }
    }

    /// <summary>Los BYTES tienen que ser los mismos que escribía <c>File.WriteAllText</c>.
    ///
    /// <para>La escritura pasó a un <c>StreamWriter</c> sobre un <c>FileStream</c> para poder bajar el archivo
    /// al disco antes de renombrar. Es un cambio de plomería con una trampa adentro: cuando se le pasa una
    /// codificación, <c>WriteAllText</c> escribe el PREÁMBULO (el BOM de UTF-8), y armar los bytes con
    /// <c>enc.GetBytes</c> no lo escribiría. Un <c>.cnt</c> o un <c>Misc/*.json</c> que de golpe pierde o gana
    /// tres bytes al principio es exactamente el tipo de cambio que nadie ve hasta que algo no parsea.</para>
    ///
    /// <para>Por eso se comparan las dos formas sobre la misma data real, en vez de mirar una captura: es la
    /// regla de siempre para un refactor que no debe cambiar nada.</para></summary>
    [Theory]
    [InlineData("[STATS]\r\nGLD=1234\r\n")]
    [InlineData("[INIT]\r\nNombre=Ñandú áéíóú\r\n")]
    [InlineData("")]
    public void La_escritura_atomica_deja_los_mismos_bytes_que_WriteAllText(string texto)
    {
        var raiz = RaizNueva();
        try
        {
            Directory.CreateDirectory(raiz);
            foreach (var enc in new[] { Codec.Ansi, System.Text.Encoding.UTF8 })
            {
                string viejo = Path.Combine(raiz, "viejo.txt"), nuevo = Path.Combine(raiz, "nuevo.txt");
                File.WriteAllText(viejo, texto, enc);
                EscrituraAtomicaDePrueba(nuevo, texto, enc);
                Assert.Equal(File.ReadAllBytes(viejo), File.ReadAllBytes(nuevo));
            }
        }
        finally { if (Directory.Exists(raiz)) Directory.Delete(raiz, recursive: true); }
    }

    /// <summary><see cref="EscrituraAtomica"/> es interno del server; el test lo alcanza por el almacén, que es
    /// su único usuario público. Se escribe un personaje y se leen los bytes del <c>.chr</c> que quedó.</summary>
    private static void EscrituraAtomicaDePrueba(string destino, string texto, System.Text.Encoding enc)
    {
        var raiz = Path.Combine(Path.GetDirectoryName(destino)!, "almacen");
        var store = new FileStore(raiz);
        if (ReferenceEquals(enc, Codec.Ansi))
        {
            store.WriteCharacter("bytes", texto);
            File.Copy(Path.Combine(raiz, "Charfile", "BYTES.chr"), destino, overwrite: true);
        }
        else
        {
            store.WriteMisc("bytes", texto);
            File.Copy(Path.Combine(raiz, "Misc", "BYTES.json"), destino, overwrite: true);
        }
        Directory.Delete(raiz, recursive: true);
    }

    /// <summary>EL BARRIDO, que es el guardia que de verdad hacía falta.
    ///
    /// <para>El defecto no fue que faltara la escritura atómica: <c>FileStore</c> la tenía, con su comentario
    /// explicando por qué. Fue que el <c>Server.ini</c> y la lista de baneados —que también duelen si quedan
    /// cortados— se escribían con <c>File.WriteAllText</c> derecho, sin pasar por ella. El helper existía y el
    /// call site no lo usaba, que es donde viven los bugs de esta clase.</para>
    ///
    /// <para>Un test que mire UN archivo no habría encontrado esos dos ni encuentra el tercero que venga. Éste
    /// mira todos: cualquier escritura directa de un archivo que sobrevive al proceso tiene que salir por
    /// <see cref="EscrituraAtomica"/> o estar acá abajo con su motivo. Los logs no entran —son append y
    /// perderlos no rompe nada— por eso el barrido busca los tres <c>WriteAll*</c> y no todo lo que escribe.</para></summary>
    [Fact]
    public void Ningun_archivo_que_sobrevive_al_proceso_se_escribe_truncando()
    {
        // Con su motivo, como corresponde. La clave es el archivo; el valor, lo que la línea tiene que decir
        // para estar perdonada — así un WriteAllText NUEVO en el mismo archivo no entra de prestado.
        var permitidos = new Dictionary<string, (string EnLaLinea, string Motivo)>
        {
            ["EscrituraAtomica.cs"] = ("tmp", "es el helper: acá vive la escritura buena, y escribe al TEMPORAL"),
            ["PacketDispatcher.cs"] = ("mapa", "el volcado de un mapa a .json de un GM: diagnóstico, no estado del juego"),
        };

        var raiz = SubirHasta("server/src/CoverAO.Server");
        var culpables = new List<string>();
        foreach (var archivo in Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories))
        {
            if (archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            string nombre = Path.GetFileName(archivo);
            var lineas = File.ReadAllLines(archivo);
            for (int i = 0; i < lineas.Length; i++)
            {
                string s = lineas[i].Trim();
                if (s.StartsWith("//") || s.StartsWith("*") || s.StartsWith("///")) continue;
                if (!s.Contains("File.WriteAllText(") && !s.Contains("File.WriteAllLines(")
                    && !s.Contains("File.WriteAllBytes(")) continue;
                if (permitidos.TryGetValue(nombre, out var ok) && s.Contains(ok.EnLaLinea)) continue;
                culpables.Add($"{nombre}:{i + 1}  {s}");
            }
        }

        Assert.True(culpables.Count == 0,
            "esto trunca el archivo antes de escribirlo: si el proceso muere ahí queda en cero bytes. "
            + "Usá EscrituraAtomica.Escribir, o agregalo arriba con su motivo:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", culpables));
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
        throw new DirectoryNotFoundException(
            $"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}: sin el fuente esto no prueba nada.");
    }
}
