using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CoverAO.Protocol;

namespace CoverAO.Server.Data;

/// <summary>
/// <c>CoverAO.Server actualizar [--desde &lt;paquete&gt;] [--forzar]</c>: instala una versión nueva del server en esta carpeta, con
/// el server APAGADO (el autor, 12/10: cada host actualiza cuando quiere). Sin <c>--desde</c> baja el paquete de las releases
/// del juego; con <c>--desde</c> usa el .zip o .tar.gz que le pasaron (así se actualiza mientras el repo es privado).
///
/// <para>EN ORDEN, y cualquier problema antes del paso 4 deja todo como estaba:</para>
/// <list type="number">
/// <item>Abre el paquete aparte (<c>actualizacion/paquete</c>) y verifica que esté firmado por el autor, que sea para esta
/// plataforma, que sea más nuevo que lo instalado y que cada archivo sea el firmado.</item>
/// <item>Backup completo de la base y de <c>data/</c>, como el de siempre. Si no sale, o le falta un archivo, no actualiza.</item>
/// <item>Decide archivo por archivo (<see cref="Actualizacion.Plan"/>): lo oficial que el host no tocó se reemplaza, lo que
/// modificó queda y el nuevo va a <c>actualizacion/sin-aplicar</c>. Lo reemplazado queda en <c>actualizacion/anterior</c>.</item>
/// <item>Aplica moviendo archivos (rápido, en el mismo disco); si un movimiento falla, deshace los anteriores.</item>
/// <item>Rearma el Server.ini con los valores del host (<see cref="RearmadoDelIni"/>); si cambiaría algo, no lo toca.</item>
/// <item>Anota la versión instalada: <c>archivos.json</c> y, LO ÚLTIMO, <c>compilacion.json</c>, con la fecha del manifiesto
/// firmado (el del paquete no va firmado). Si algo falla antes, la versión anotada sigue siendo la de antes y volver a correr
/// <c>actualizar</c> rehace todo.</item>
/// </list>
/// </summary>
public static class Actualizador
{
    public static bool EsElComando(string arg) => arg.Equals("actualizar", StringComparison.OrdinalIgnoreCase);

    private sealed class Problema(string mensaje) : Exception(mensaje);

    /// <summary>La fecha de la compilación instalada (<c>compilacion.json</c>), o null en una instalación de antes de las
    /// actualizaciones o corriendo desde el código.</summary>
    public static string? IdInstalado(string baseDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseDir, Actualizacion.Compilacion)));
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static int Correr(string[] args, string baseDir, string dataRoot, ServerIni ini) =>
        Correr(args, baseDir, dataRoot, ini, Actualizacion.ClavePublicaDelAutor, Console.Out);

    /// <param name="otrosServers">La ruta de cada otro proceso del server (ver <see cref="OtroServerCorriendo(string?, IEnumerable{Func{string?}})"/>);
    /// null = los de esta máquina.</param>
    internal static int Correr(string[] args, string baseDir, string dataRoot, ServerIni ini, string clave, TextWriter salida,
        IEnumerable<Func<string?>>? otrosServers = null)
    {
        string? desde = Opcion(args, "--desde") ?? args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        bool forzar = args.Any(a => a.Equals("--forzar", StringComparison.OrdinalIgnoreCase));
        string dir = Path.Combine(baseDir, "actualizacion");
        string preparado = Path.Combine(dir, "paquete");
        string? aMedias = null;   // qué quedó hecho y qué falta, desde que se reemplazó el primer archivo
        try
        {
            var otro = otrosServers is null ? OtroServerCorriendo() : OtroServerCorriendo(Environment.ProcessPath, otrosServers);
            if (otro == OtroServer.EnEstaCarpeta)
                throw new Problema("El server está prendido en esta carpeta: apagalo primero (coverao.ps1 detener, " +
                                   "Ctrl+C en su ventana o systemctl stop) y volvé a correr esto.");
            if (otro == OtroServer.SinSaberDonde && !forzar)
                throw new Problema("Hay un server de CoverAO prendido con otra cuenta y no se puede ver de qué carpeta es (el arranque " +
                                   "automático de coverao.ps1 corre como LOCAL SERVICE). Si es éste, apagalo primero (coverao.ps1 detener " +
                                   "desde una consola de Administrador, o systemctl stop) y volvé a correr esto. Si es el de otra carpeta, " +
                                   "corré esto como Administrador (o con sudo) para que se vea, o con --forzar.");
            string? idLocal = IdInstalado(baseDir);
            Directory.CreateDirectory(dir);

            // 1. EL PAQUETE, APARTE Y VERIFICADO.
            string paquete = desde ?? Bajar(dir, salida);
            if (!File.Exists(paquete)) throw new Problema($"No existe {paquete}.");
            salida.WriteLine($"Abriendo {Path.GetFileName(paquete)}...");
            BorrarCarpeta(preparado);
            Extraer(paquete, preparado);
            string raiz = BuscarRaiz(preparado) ?? throw new Problema("Eso no es un paquete del server de CoverAO (no trae archivos.json).");
            var nuevo = Actualizacion.LeerManifiesto(File.ReadAllText(Path.Combine(raiz, Actualizacion.Manifiesto)), exigirFirma: true, clave)
                        ?? throw new Problema("El paquete NO está firmado por el autor del juego (o está dañado): no se instala.");
            if (nuevo.Rid != Actualizacion.Plataforma)
                throw new Problema($"Ese paquete es para {nuevo.Rid} y este server corre en {Actualizacion.Plataforma}.");
            if (!forzar && !Actualizacion.EsMasNueva(nuevo.Id, idLocal))
            {
                salida.WriteLine($"Ya tenés esta versión o una más nueva (la instalada es del {idLocal}). Nada que hacer.");
                BorrarCarpeta(preparado);
                return 0;
            }
            foreach (var (rel, hash) in nuevo.Archivos)
            {
                string f = Path.Combine(raiz, rel);
                if (!File.Exists(f) || Actualizacion.Sha256DeArchivo(f) != hash)
                    throw new Problema($"El paquete está dañado: {rel} no es el firmado. No se instala.");
            }
            salida.WriteLine($"Versión del {nuevo.Id}, firmada por el autor ({nuevo.Archivos.Count} archivos).");

            // 2. EL BACKUP, antes de tocar nada.
            salida.WriteLine("Backup de los personajes y la base...");
            string backup;
            var sinCopiar = new List<string>();
            try
            {
                var (almacen, _) = Almacenes.Abrir(ini, dataRoot);
                try { backup = Backups.Crear(almacen, Path.Combine(dataRoot, "backups"), DateTime.Now, dataRoot, sinCopiar); }
                finally { (almacen as IDisposable)?.Dispose(); }
            }
            catch (Exception e) when (e is not Problema)
            {
                throw new Problema($"No se pudo hacer el backup ({e.Message}): no se actualiza sin backup.");
            }
            salida.WriteLine($"  {backup}");
            if (sinCopiar.Count > 0)
            {
                if (!forzar)
                    throw new Problema($"El backup salió incompleto: no se pudo copiar {string.Join(", ", sinCopiar)} de data/ (lo tiene abierto " +
                                       "otro programa, como un antivirus o el server prendido). No se actualiza sin el backup entero: cerralo " +
                                       "y volvé a correr esto, o corré con --forzar.");
                salida.WriteLine($"  OJO: el backup salió sin {string.Join(", ", sinCopiar)} (--forzar).");
            }

            // 3 y 4. QUÉ REEMPLAZAR, Y REEMPLAZARLO.
            var viejos = LeerManifiestoInstalado(baseDir);
            if (viejos is null)
                salida.WriteLine("  Esta instalación no sabe cuáles de sus archivos son los oficiales (es de antes de las actualizaciones): " +
                                 "se reemplaza todo lo del paquete, también los mapas y la data del juego que hayas modificado, y lo de " +
                                 "antes queda en actualizacion/anterior.");
            var plan = Actualizacion.Plan(viejos?.Archivos, nuevo.Archivos, rel =>
            {
                string f = Path.Combine(baseDir, rel);
                return File.Exists(f) ? Actualizacion.Sha256DeArchivo(f) : null;
            });
            string anterior = Path.Combine(dir, "anterior");
            string sinAplicar = Path.Combine(dir, "sin-aplicar");
            if (Directory.Exists(anterior) || Directory.Exists(sinAplicar))
                salida.WriteLine("  Se borra lo que dejó la actualización anterior en actualizacion/anterior y actualizacion/sin-aplicar.");
            BorrarCarpeta(anterior);
            BorrarCarpeta(sinAplicar);
            Aplicar(plan, raiz, baseDir, anterior, sinAplicar);
            aMedias = $"ya están puestos los archivos de la versión del {nuevo.Id} (los de antes, en actualizacion/anterior), y falta " +
                      "rearmar el Server.ini y anotar la versión instalada";
            if (!OperatingSystem.IsWindows())
                foreach (var ejecutable in new[] { Actualizacion.Ejecutable, "servidor.sh" })
                {
                    string f = Path.Combine(baseDir, ejecutable);
                    if (File.Exists(f)) File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }

            var resumen = new StringBuilder();
            resumen.AppendLine($"Actualizado a la versión del {nuevo.Id} ({DateTime.Now:yyyy-MM-dd HH:mm}).");
            resumen.AppendLine($"  Backup: {backup}");
            int Cuantos(Actualizacion.Accion a) => plan.Count(p => p.Accion == a);
            resumen.AppendLine($"  Archivos: {Cuantos(Actualizacion.Accion.Reemplazar)} reemplazados, {Cuantos(Actualizacion.Accion.Agregar)} nuevos, " +
                               $"{Cuantos(Actualizacion.Accion.Borrar)} que ya no van. Lo de antes, en actualizacion/anterior.");
            var conservados = plan.Where(p => p.Accion == Actualizacion.Accion.Conservar).Select(p => p.Ruta).ToList();
            if (conservados.Count > 0)
            {
                resumen.AppendLine($"  {conservados.Count} archivo(s) tuyos modificados se dejaron como estaban; la versión nueva de cada uno " +
                                   "está en actualizacion/sin-aplicar:");
                foreach (var c in conservados.Take(30)) resumen.AppendLine("    " + c);
                if (conservados.Count > 30) resumen.AppendLine($"    … y {conservados.Count - 30} más");
            }

            // 5. EL SERVER.INI.
            RearmarIni(baseDir, anterior, resumen);
            aMedias = $"ya están puestos los archivos de la versión del {nuevo.Id} y el Server.ini, y falta anotar la versión instalada";

            // 6. LA VERSIÓN INSTALADA, LO ÚLTIMO. Hasta acá sigue anotada la de antes: un reintento rehace todo.
            Directory.CreateDirectory(anterior);
            string manifiesto = Path.Combine(baseDir, Actualizacion.Manifiesto);
            if (File.Exists(manifiesto)) File.Copy(manifiesto, Path.Combine(anterior, Actualizacion.Manifiesto), overwrite: true);
            File.Copy(Path.Combine(raiz, Actualizacion.Manifiesto), manifiesto + ".tmp", overwrite: true);
            EscrituraAtomica.Renombrar(manifiesto + ".tmp", manifiesto);
            BorrarCarpeta(preparado);
            if (desde is null) try { File.Delete(paquete); } catch (IOException) { }
            EscrituraAtomica.Escribir(Path.Combine(dir, "ultima.txt"), resumen.ToString(), new UTF8Encoding(false));
            // La fecha, del manifiesto que se verificó: el compilacion.json del paquete no va firmado.
            string compilacion = Path.Combine(baseDir, Actualizacion.Compilacion);
            if (File.Exists(compilacion)) File.Copy(compilacion, Path.Combine(anterior, Actualizacion.Compilacion), overwrite: true);
            EscrituraAtomica.Escribir(compilacion, JsonSerializer.Serialize(new { id = nuevo.Id }), new UTF8Encoding(false));
            aMedias = null;

            salida.Write(resumen.ToString());
            salida.WriteLine("Listo. Ya podés arrancar el server.");
            return 0;
        }
        catch (Exception e) when (e is Problema or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            salida.WriteLine(e is Problema ? e.Message : $"No se pudo actualizar: {e.Message}");
            if (aMedias is not null)
                salida.WriteLine($"OJO, QUEDÓ A MEDIAS: {aMedias}. Volvé a correr actualizar, que rehace lo que falta.");
            try { BorrarCarpeta(preparado); } catch (Exception) { }
            return 1;
        }
    }

    private static string? Opcion(string[] args, string nombre)
    {
        int i = Array.FindIndex(args, a => a.Equals(nombre, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Actualizacion.ManifiestoDelPaquete? LeerManifiestoInstalado(string baseDir)
    {
        try { return Actualizacion.LeerManifiesto(File.ReadAllText(Path.Combine(baseDir, Actualizacion.Manifiesto)), exigirFirma: false); }
        catch (IOException) { return null; }
    }

    internal enum OtroServer { Ninguno, EnEstaCarpeta, SinSaberDonde }

    /// <summary>¿Corre otro server con ESTE ejecutable? Con el server prendido no se actualiza: tiene la base abierta, y sus
    /// archivos en uso.
    ///
    /// <para>UNO CUYA RUTA NO SE PUEDE LEER CUENTA COMO PRENDIDO (auditoría del 13/10). Desde una consola sin Administrador,
    /// Windows no deja ver la ruta de un proceso de otra cuenta, y el arranque automático de coverao.ps1 corre como LOCAL
    /// SERVICE: tomarlo como apagado dejaba actualizar encima del server prendido. <c>--forzar</c> lo saltea.</para></summary>
    /// <param name="rutas">La ruta del ejecutable de cada otro proceso con el nombre del server: null o una excepción si no se
    /// puede leer.</param>
    internal static OtroServer OtroServerCorriendo(string? yo, IEnumerable<Func<string?>> rutas)
    {
        if (yo is null) return OtroServer.Ninguno;
        var r = OtroServer.Ninguno;
        foreach (var ruta in rutas)
        {
            string? f;
            try { f = ruta(); }
            catch (InvalidOperationException) { continue; }   // terminó mientras se lo miraba
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or NotSupportedException)
            {
                f = null;
            }
            if (f is null) r = OtroServer.SinSaberDonde;
            else if (string.Equals(f, yo, StringComparison.OrdinalIgnoreCase)) return OtroServer.EnEstaCarpeta;
        }
        return r;
    }

    private static OtroServer OtroServerCorriendo()
    {
        var procesos = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Actualizacion.Ejecutable));
        try
        {
            return OtroServerCorriendo(Environment.ProcessPath, procesos.Where(p => p.Id != Environment.ProcessId)
                .Select(p => (Func<string?>)(() => p.MainModule?.FileName)));
        }
        finally { foreach (var p in procesos) p.Dispose(); }
    }

    private static string Bajar(string dir, TextWriter salida)
    {
        string nombre = Actualizacion.NombreDelPaquete(Actualizacion.Plataforma);
        string url = Actualizacion.Descargas + nombre;
        salida.WriteLine($"Bajando {url}...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CoverAO-actualizador");
        using var r = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new Problema("No hay una versión publicada a la vista (el repo del juego todavía es privado). Pedile el paquete al " +
                               "autor y corré:  actualizar --desde <el .zip o .tar.gz>");
        if (!r.IsSuccessStatusCode) throw new Problema($"No se pudo bajar la versión nueva ({(int)r.StatusCode}).");
        string destino = Path.Combine(dir, nombre);
        using (var origen = r.Content.ReadAsStream())
        using (var archivo = File.Create(destino)) origen.CopyTo(archivo);
        return destino;
    }

    private static void Extraer(string paquete, string destino)
    {
        Directory.CreateDirectory(destino);
        try
        {
            // Los dos extractores de .NET se niegan a escribir fuera de la carpeta (una entrada con "../"): un paquete armado
            // para pisar otra cosa no pasa de acá.
            if (paquete.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(paquete, destino, overwriteFiles: true);
            else if (paquete.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || paquete.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                using var f = File.OpenRead(paquete);
                using var gz = new GZipStream(f, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, destino, overwriteFiles: true);
            }
            else throw new Problema("El paquete tiene que ser un .zip (Windows) o un .tar.gz (Linux).");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new Problema($"No se pudo abrir el paquete ({e.Message}).");
        }
    }

    /// <summary>La carpeta del paquete que trae <c>archivos.json</c>: la misma, o la única de adentro (el zip trae
    /// <c>CoverAO-win-x64/</c>).</summary>
    private static string? BuscarRaiz(string preparado)
    {
        if (File.Exists(Path.Combine(preparado, Actualizacion.Manifiesto))) return preparado;
        var dentro = Directory.GetDirectories(preparado);
        return dentro.Length == 1 && File.Exists(Path.Combine(dentro[0], Actualizacion.Manifiesto)) ? dentro[0] : null;
    }

    private static void Aplicar(List<Actualizacion.Paso> plan, string raiz, string baseDir, string anterior, string sinAplicar)
    {
        var movidos = new List<(string De, string A)>();
        try
        {
            foreach (var paso in plan)
            {
                string actual = Path.Combine(baseDir, paso.Ruta), nuevo = Path.Combine(raiz, paso.Ruta);
                switch (paso.Accion)
                {
                    // El ejecutable en uso se puede MOVER (no borrar) en Windows: es este mismo proceso, y así se reemplaza.
                    case Actualizacion.Accion.Reemplazar:
                        Mover(actual, Path.Combine(anterior, paso.Ruta), movidos);
                        Mover(nuevo, actual, movidos);
                        break;
                    case Actualizacion.Accion.Agregar: Mover(nuevo, actual, movidos); break;
                    case Actualizacion.Accion.Borrar: Mover(actual, Path.Combine(anterior, paso.Ruta), movidos); break;
                    case Actualizacion.Accion.Conservar: Mover(nuevo, Path.Combine(sinAplicar, paso.Ruta), movidos); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            for (int i = movidos.Count - 1; i >= 0; i--)
                try { File.Move(movidos[i].A, movidos[i].De, overwrite: true); } catch (Exception) { }
            throw new Problema($"No se pudo reemplazar un archivo ({e.Message}): se dejó todo como estaba.");
        }
    }

    private static void Mover(string de, string a, List<(string, string)>? movidos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.Move(de, a, overwrite: true);
        movidos?.Add((de, a));
    }

    private static void RearmarIni(string baseDir, string anterior, StringBuilder resumen)
    {
        string ini = Path.Combine(baseDir, "Server.ini"), ejemplo = Path.Combine(baseDir, "Server.ini.ejemplo");
        if (!File.Exists(ini) || !File.Exists(ejemplo)) return;
        string host = File.ReadAllText(ini, Codec.Ansi);
        var r = RearmadoDelIni.Rearmar(host, File.ReadAllText(ejemplo, Codec.Ansi));
        if (r.Problema is not null)
        {
            resumen.AppendLine($"  Server.ini: NO se rearmó ({r.Problema}). Sigue el tuyo, sin tocar; las claves nuevas están en Server.ini.ejemplo.");
            return;
        }
        string texto = host.Contains("\r\n", StringComparison.Ordinal) ? r.Texto!.Replace("\n", "\r\n") : r.Texto!;
        if (texto == host) { resumen.AppendLine("  Server.ini: ya estaba al día."); return; }
        Directory.CreateDirectory(anterior);
        File.Copy(ini, Path.Combine(anterior, "Server.ini"), overwrite: true);
        EscrituraAtomica.Escribir(ini, texto, Codec.Ansi);
        resumen.AppendLine($"  Server.ini: al día con tus valores (el de antes, en actualizacion/anterior). {r.Agregadas.Count} clave(s) nueva(s)" +
                           (r.Agregadas.Count > 0 ? ": " + string.Join(", ", r.Agregadas.Take(15)) + (r.Agregadas.Count > 15 ? "…" : "") : "") + ".");
        if (r.Comentadas.Count > 0)
            resumen.AppendLine($"  {r.Comentadas.Count} van comentadas porque prenderlas cambiaría cómo corre tu server: " + string.Join(", ", r.Comentadas.Take(15)) +
                               (r.Comentadas.Count > 15 ? "…" : "") + ".");
        if (r.Renombradas.Count > 0) resumen.AppendLine("  Cambiaron de nombre, con tu valor: " + string.Join(", ", r.Renombradas) + ".");
        if (r.Desconocidas.Count > 0)
            resumen.AppendLine("  OJO: estas claves tuyas no existen y se ignoran (quedaron al final, revisá cómo están escritas): " +
                               string.Join(", ", r.Desconocidas) + ".");
    }

    private static void BorrarCarpeta(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// EL AVISO DE VERSIÓN NUEVA, con el server prendido: al minuto de arrancar y cada 6 horas mira el aviso firmado de las
    /// releases y, si hay una más nueva que la instalada, lo dice una vez en la consola. No baja ni instala nada: eso lo decide el
    /// host. Mientras el repo sea privado no encuentra nada y no dice nada.
    /// </summary>
    public static async Task VigilarAsync(string idLocal, Action<string> avisar, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CoverAO-servidor");
        string? avisada = null;
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? id = Actualizacion.IdDelAviso(await http.GetStringAsync(Actualizacion.Descargas + Actualizacion.Aviso, ct));
                if (id is not null && id != avisada && Actualizacion.EsMasNueva(id, idLocal))
                {
                    avisada = id;
                    avisar($"[actualizacion] Hay una versión nueva del server (del {id}). Para instalarla, apagalo y corré " +
                           (OperatingSystem.IsWindows() ? "coverao.ps1 actualizar <nombre>, o Actualizar.bat" : "./servidor.sh actualizar") +
                           ". Tus personajes no se tocan y tu Server.ini queda con tus valores. Si modificaste la data del juego " +
                           "queda la tuya, y la nueva va a actualizacion/sin-aplicar; el cliente web y el ejecutable se reemplazan siempre.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
