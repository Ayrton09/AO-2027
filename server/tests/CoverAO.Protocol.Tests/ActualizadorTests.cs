using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA ACTUALIZACIÓN DEL SERVER (ver <see cref="Actualizacion"/> y <see cref="Actualizador"/>): sólo instala un paquete firmado por
/// el autor, más nuevo y entero; reemplaza lo oficial que el host no tocó, conserva lo que modificó, no toca sus datos, hace backup
/// antes y deja su Server.ini al día con sus valores.
/// </summary>
public class ActualizadorTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_act_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    // ── La firma ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>LO QUE FIRMA NODE LO VERIFICA EL SERVER: la firma del autor sale de desktop/firma.js (Node) y se verifica con
    /// BouncyCastle. Este par lo generó Node con una clave de prueba.</summary>
    [Fact]
    public void Lo_que_firma_node_lo_verifica_el_server()
    {
        const string clave = "MCowBQYDK2VwAyEALlRkbuu0t/ysdB8Su/V7CM9+UFjggGzchesFC474H+8=";
        const string firma = "sWhbkq9rSE5ZXnu/aFUM1a43lDVHDmnUZzj6eWhduFum1vzDFRZa/iqFsgiLkjh2MBzEKgiozlb1vUr7FAj9DQ==";
        var archivos = new Dictionary<string, string> { ["gamedata/mapa1.json"] = new string('b', 64), ["CoverAO.Server.exe"] = new string('a', 64) };
        string texto = Actualizacion.TextoDelManifiesto("2026-10-13T00:00:00.000Z", "win-x64", archivos);
        Assert.True(Actualizacion.FirmaValida(texto, firma, clave));
        Assert.False(Actualizacion.FirmaValida(texto + "x", firma, clave));
        Assert.False(Actualizacion.FirmaValida(texto, firma));                     // con la clave del autor no: es otra
    }

    [Fact]
    public void La_clave_del_server_es_la_de_la_app()
    {
        string pem = File.ReadAllText(BuscarEnElRepo(Path.Combine("desktop", "clave-publica.pem")));
        string cuerpo = string.Concat(pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----")));
        Assert.Equal(Actualizacion.ClavePublicaDelAutor, cuerpo);
    }

    private sealed class Firmante
    {
        private readonly Ed25519PrivateKeyParameters _privada;
        public string Clave { get; }
        public Firmante()
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var par = gen.GenerateKeyPair();
            _privada = (Ed25519PrivateKeyParameters)par.Private;
            Clave = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(par.Public).GetEncoded());
        }
        public string Firmar(string texto)
        {
            var s = new Ed25519Signer();
            s.Init(true, _privada);
            byte[] b = Encoding.UTF8.GetBytes(texto);
            s.BlockUpdate(b, 0, b.Length);
            return Convert.ToBase64String(s.GenerateSignature());
        }
        public string Manifiesto(string id, string rid, IReadOnlyDictionary<string, string> archivos) => JsonSerializer.Serialize(new
        {
            version = 1, id, rid, archivos, firma = Firmar(Actualizacion.TextoDelManifiesto(id, rid, archivos)),
        });
    }

    [Fact]
    public void El_manifiesto_se_lee_solo_si_esta_firmado_y_bien_armado()
    {
        var f = new Firmante();
        var archivos = new Dictionary<string, string> { ["gamedata/a.json"] = new string('c', 64) };
        string bueno = f.Manifiesto("2026-10-13T00:00:00.000Z", "linux-x64", archivos);
        Assert.NotNull(Actualizacion.LeerManifiesto(bueno, exigirFirma: true, f.Clave));
        Assert.Null(Actualizacion.LeerManifiesto(bueno, exigirFirma: true, new Firmante().Clave));        // otra clave
        Assert.Null(Actualizacion.LeerManifiesto(bueno.Replace(new string('c', 64), new string('d', 64)), exigirFirma: true, f.Clave));
        foreach (var mala in new[] { "../fuera.txt", "data/coverao.db", "Server.ini", "C:/x.txt", "/etc/passwd", "a//b", "gamedata\\x" })
        {
            var a = new Dictionary<string, string> { [mala] = new string('c', 64) };
            Assert.Null(Actualizacion.LeerManifiesto(f.Manifiesto("2026-10-13T00:00:00.000Z", "linux-x64", a), exigirFirma: true, f.Clave));
        }
        Assert.NotNull(Actualizacion.LeerManifiesto(f.Manifiesto("2026-10-13T00:00:00.000Z", "linux-x64",
            new Dictionary<string, string> { ["data/updates.txt"] = new string('c', 64) }), exigirFirma: true, f.Clave));
        Assert.Null(Actualizacion.LeerManifiesto(f.Manifiesto("ayer", "linux-x64", archivos), exigirFirma: true, f.Clave));
        Assert.Null(Actualizacion.LeerManifiesto(f.Manifiesto("2026-10-13T00:00:00.000Z", "mac", archivos), exigirFirma: true, f.Clave));
    }

    [Fact]
    public void El_plan_reemplaza_lo_oficial_y_conserva_lo_del_host()
    {
        var viejos = new Dictionary<string, string> { ["g/igual"] = "1", ["g/tocado"] = "1", ["g/sin-tocar"] = "1", ["g/se-va"] = "1",
            ["g/se-va-tocado"] = "1", ["webclient/i.html"] = "1" };
        var nuevos = new Dictionary<string, string> { ["g/igual"] = "1", ["g/tocado"] = "2", ["g/sin-tocar"] = "2", ["g/nuevo"] = "2",
            ["webclient/i.html"] = "2", ["g/propio"] = "2" };
        var hoy = new Dictionary<string, string> { ["g/igual"] = "1", ["g/tocado"] = "X", ["g/sin-tocar"] = "1", ["g/se-va"] = "1",
            ["g/se-va-tocado"] = "X", ["webclient/i.html"] = "X", ["g/propio"] = "X" };
        var plan = Actualizacion.Plan(viejos, nuevos, r => hoy.GetValueOrDefault(r)).ToDictionary(p => p.Ruta, p => p.Accion);
        Assert.False(plan.ContainsKey("g/igual"));
        Assert.Equal(Actualizacion.Accion.Conservar, plan["g/tocado"]);
        Assert.Equal(Actualizacion.Accion.Reemplazar, plan["g/sin-tocar"]);
        Assert.Equal(Actualizacion.Accion.Agregar, plan["g/nuevo"]);
        Assert.Equal(Actualizacion.Accion.Borrar, plan["g/se-va"]);
        Assert.False(plan.ContainsKey("g/se-va-tocado"));                                  // el host lo cambió: se queda
        Assert.Equal(Actualizacion.Accion.Reemplazar, plan["webclient/i.html"]);           // el cliente siempre es el firmado
        Assert.Equal(Actualizacion.Accion.Conservar, plan["g/propio"]);                    // uno suyo con el mismo nombre
    }

    // ── De punta a punta ──────────────────────────────────────────────────────────────────────────────

    private static string Sha(string texto) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();

    private string Base => Path.Combine(_raiz, "server");
    private string Data => Path.Combine(Base, "data");

    private void Escribir(string carpeta, string rel, string texto)
    {
        string f = Path.Combine(carpeta, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, texto);
    }

    private string Leer(string rel) => File.ReadAllText(Path.Combine(Base, rel));

    /// <summary>Una instalación de la versión "vieja": oficiales, uno modificado por el host, sus datos y su Server.ini.</summary>
    private void ArmarInstalacion()
    {
        var oficiales = new Dictionary<string, string>
        {
            ["gamedata/mapa1.json"] = "mapa1 v1", ["gamedata/mapa2.json"] = "mapa2 v1", ["gamedata/viejo.json"] = "viejo v1",
            ["webclient/index.html"] = "cliente v1", ["Server.ini.ejemplo"] = "[INIT]\nStartPort=7666\nExp=1\n",
        };
        foreach (var (rel, t) in oficiales) Escribir(Base, rel, t);
        Escribir(Base, "gamedata/mapa2.json", "mapa2 DEL HOST");                         // modificado por el host
        Escribir(Base, "webclient/index.html", "cliente tocado");                         // el cliente no se respeta
        Escribir(Base, "data/personaje.txt", "mis cosas");
        Escribir(Base, "Server.ini", "[INIT]\nStartPort=7700\nExp=5\nDioses=1\n\n[Dioses]\nDios1=Rugura\n");
        File.WriteAllText(Path.Combine(Base, Actualizacion.Manifiesto), JsonSerializer.Serialize(new
        {
            version = 1, id = "2026-10-01T00:00:00.000Z", rid = Actualizacion.Plataforma,
            archivos = oficiales.ToDictionary(o => o.Key, o => Sha(o.Value)), firma = "",
        }));
        File.WriteAllText(Path.Combine(Base, Actualizacion.Compilacion), "{\"id\":\"2026-10-01T00:00:00.000Z\"}");
    }

    /// <summary>El paquete de la versión nueva, firmado, como lo arma publish.ps1 (con la carpeta adentro del zip).</summary>
    private string ArmarPaquete(Firmante f, string id, Action<string>? despuesDeFirmar = null)
    {
        string carpeta = Path.Combine(_raiz, "armado-" + Guid.NewGuid().ToString("N"), "CoverAO-" + Actualizacion.Plataforma);
        var oficiales = new Dictionary<string, string>
        {
            ["gamedata/mapa1.json"] = "mapa1 v2", ["gamedata/mapa2.json"] = "mapa2 v2", ["gamedata/nuevo.json"] = "nuevo v2",
            ["webclient/index.html"] = "cliente v2", ["data/updates.txt"] = "novedades v2",
            ["Server.ini.ejemplo"] = "[INIT]\n; El puerto\nStartPort=7666\n; La experiencia\nExp=1\n; Una nueva\nClaveMinima=6\n",
        };
        foreach (var (rel, t) in oficiales) Escribir(carpeta, rel, t);
        Escribir(carpeta, "Server.ini", "[INIT]\nStartPort=1\n");                       // el que trae el paquete: no se instala
        var huellas = oficiales.ToDictionary(o => o.Key, o => Sha(o.Value));
        File.WriteAllText(Path.Combine(carpeta, Actualizacion.Manifiesto), f.Manifiesto(id, Actualizacion.Plataforma, huellas));
        File.WriteAllText(Path.Combine(carpeta, Actualizacion.Compilacion), JsonSerializer.Serialize(new { id }));
        despuesDeFirmar?.Invoke(carpeta);
        string zip = Path.Combine(_raiz, $"paquete-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(carpeta, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    /// <summary>Sin otros servers a la vista: los procesos de la máquina donde corren los tests no son asunto de estos tests.</summary>
    private static readonly Func<string?>[] NingunOtro = Array.Empty<Func<string?>>();

    private (int Codigo, string Salida) Actualizar(string paquete, string clave, params string[] mas)
    {
        var salida = new StringWriter();
        int codigo = Actualizador.Correr(new[] { "actualizar", "--desde", paquete }.Concat(mas).ToArray(), Base, Data, new ServerIni(), clave,
            salida, NingunOtro);
        return (codigo, salida.ToString());
    }

    [Fact]
    public void Actualiza_lo_oficial_y_no_toca_lo_del_host()
    {
        ArmarInstalacion();
        var f = new Firmante();
        var (codigo, salida) = Actualizar(ArmarPaquete(f, "2026-10-13T00:00:00.000Z"), f.Clave);
        Assert.True(codigo == 0, salida);

        Assert.Equal("mapa1 v2", Leer("gamedata/mapa1.json"));                          // oficial sin tocar: se reemplaza
        Assert.Equal("mapa2 DEL HOST", Leer("gamedata/mapa2.json"));                    // el del host queda...
        Assert.Equal("mapa2 v2", File.ReadAllText(Path.Combine(Base, "actualizacion", "sin-aplicar", "gamedata", "mapa2.json")));
        Assert.Equal("nuevo v2", Leer("gamedata/nuevo.json"));
        Assert.False(File.Exists(Path.Combine(Base, "gamedata", "viejo.json")));        // el que ya no va, a "anterior"
        Assert.Equal("viejo v1", File.ReadAllText(Path.Combine(Base, "actualizacion", "anterior", "gamedata", "viejo.json")));
        Assert.Equal("cliente v2", Leer("webclient/index.html"));
        Assert.Equal("novedades v2", Leer("data/updates.txt"));
        Assert.Equal("mis cosas", Leer("data/personaje.txt"));                          // sus datos, intactos

        var ini = IniFile.Parse(Leer("Server.ini"));                                     // su ini: sus valores y la clave nueva
        Assert.Equal("7700", ini.GetString("INIT", "StartPort"));
        Assert.Equal("5", ini.GetString("INIT", "Exp"));
        Assert.Equal("Rugura", ini.GetString("Dioses", "Dios1"));
        Assert.True(ini.HasKey("INIT", "ClaveMinima"));
        Assert.Contains("; Una nueva", Leer("Server.ini"));
        Assert.Contains("StartPort=7700", File.ReadAllText(Path.Combine(Base, "actualizacion", "anterior", "Server.ini")));

        Assert.Equal("2026-10-13T00:00:00.000Z", Actualizador.IdInstalado(Base));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(Data, "backups")));   // el backup, antes
        Assert.False(Directory.Exists(Path.Combine(Base, "actualizacion", "paquete")));

        // OTRA VEZ EL MISMO: no hay nada que hacer, y no toca nada.
        Escribir(Base, "gamedata/mapa1.json", "mapa1 tocado despues");
        var (otra, salidaOtra) = Actualizar(ArmarPaquete(f, "2026-10-13T00:00:00.000Z"), f.Clave);
        Assert.Equal(0, otra);
        Assert.Contains("Ya tenés esta versión", salidaOtra);
        Assert.Equal("mapa1 tocado despues", Leer("gamedata/mapa1.json"));
    }

    [Fact]
    public void Un_paquete_que_no_es_del_autor_no_se_instala()
    {
        ArmarInstalacion();
        var (codigo, salida) = Actualizar(ArmarPaquete(new Firmante(), "2026-10-13T00:00:00.000Z"), new Firmante().Clave);
        Assert.Equal(1, codigo);
        Assert.Contains("NO está firmado por el autor", salida);
        Assert.Equal("mapa1 v1", Leer("gamedata/mapa1.json"));
        Assert.Equal("2026-10-01T00:00:00.000Z", Actualizador.IdInstalado(Base));
    }

    [Fact]
    public void Un_paquete_con_un_archivo_cambiado_despues_de_firmar_no_se_instala()
    {
        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-10-13T00:00:00.000Z",
            carpeta => File.WriteAllText(Path.Combine(carpeta, "webclient", "index.html"), "cliente con trampa"));
        var (codigo, salida) = Actualizar(zip, f.Clave);
        Assert.Equal(1, codigo);
        Assert.Contains("dañado", salida);
        Assert.Equal("cliente tocado", Leer("webclient/index.html"));
        Assert.False(Directory.Exists(Path.Combine(Base, "data", "backups")));            // ni siquiera llegó al backup
    }

    [Fact]
    public void Una_version_mas_vieja_no_se_instala_salvo_que_se_fuerce()
    {
        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-09-01T00:00:00.000Z");
        var (codigo, salida) = Actualizar(zip, f.Clave);
        Assert.Equal(0, codigo);
        Assert.Contains("Ya tenés esta versión o una más nueva", salida);
        Assert.Equal("mapa1 v1", Leer("gamedata/mapa1.json"));

        var forzada = new StringWriter();
        Assert.Equal(0, Actualizador.Correr(new[] { "actualizar", "--desde", zip, "--forzar" }, Base, Data, new ServerIni(), f.Clave, forzada));
        Assert.Equal("mapa1 v2", Leer("gamedata/mapa1.json"));
    }

    /// <summary>UN SERVER QUE NO SE DEJA VER CUENTA COMO PRENDIDO: desde una consola sin Administrador, Windows no da la ruta de
    /// un proceso de otra cuenta (el arranque automático corre como LOCAL SERVICE). Actualizar no sigue, salvo con --forzar.</summary>
    [Fact]
    public void Un_server_que_no_se_deja_ver_cuenta_como_prendido()
    {
        string yo = Path.Combine(Base, Actualizacion.Ejecutable);
        Func<string?> sinRuta = () => null, sinPermiso = () => throw new System.ComponentModel.Win32Exception(5),
            termino = () => throw new InvalidOperationException(), otro = () => Path.Combine(_raiz, "otro", Actualizacion.Ejecutable);
        Assert.Equal(Actualizador.OtroServer.SinSaberDonde, Actualizador.OtroServerCorriendo(yo, new[] { sinRuta }));
        Assert.Equal(Actualizador.OtroServer.SinSaberDonde, Actualizador.OtroServerCorriendo(yo, new[] { otro, sinPermiso }));
        Assert.Equal(Actualizador.OtroServer.Ninguno, Actualizador.OtroServerCorriendo(yo, new[] { termino, otro }));
        Assert.Equal(Actualizador.OtroServer.EnEstaCarpeta, Actualizador.OtroServerCorriendo(yo, new Func<string?>[] { sinRuta, () => yo }));

        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-10-13T00:00:00.000Z");
        var salida = new StringWriter();
        Assert.Equal(1, Actualizador.Correr(new[] { "actualizar", "--desde", zip }, Base, Data, new ServerIni(), f.Clave, salida, new[] { sinRuta }));
        Assert.Contains("no se puede ver de qué carpeta es", salida.ToString());
        Assert.Equal("mapa1 v1", Leer("gamedata/mapa1.json"));
        Assert.False(Directory.Exists(Path.Combine(Data, "backups")));

        var forzada = new StringWriter();
        Assert.Equal(0, Actualizador.Correr(new[] { "actualizar", "--desde", zip, "--forzar" }, Base, Data, new ServerIni(), f.Clave, forzada, new[] { sinRuta }));
        Assert.Equal("mapa1 v2", Leer("gamedata/mapa1.json"));
    }

    /// <summary>UN FALLO DESPUÉS DE REEMPLAZAR ARCHIVOS DICE QUÉ QUEDÓ HECHO, y como la versión se anota lo último, volver a correr
    /// termina el trabajo: el Server.ini se rearma y lo que el host modificó sigue siendo suyo.</summary>
    [Fact]
    public void Un_fallo_a_medias_lo_dice_y_otra_corrida_lo_termina()
    {
        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-10-13T00:00:00.000Z");
        string trabado = Path.Combine(Base, "Server.ini.tmp");             // el Server.ini no se puede escribir
        Directory.CreateDirectory(trabado);

        var (codigo, salida) = Actualizar(zip, f.Clave);
        Assert.Equal(1, codigo);
        Assert.Contains("QUEDÓ A MEDIAS: ya están puestos los archivos de la versión del 2026-10-13T00:00:00.000Z", salida);
        Assert.Contains("falta rearmar el Server.ini", salida);
        Assert.Equal("mapa1 v2", Leer("gamedata/mapa1.json"));
        Assert.DoesNotContain("ClaveMinima", Leer("Server.ini"));
        Assert.Equal("2026-10-01T00:00:00.000Z", Actualizador.IdInstalado(Base));     // la versión anotada, la de antes

        Directory.Delete(trabado);
        var (otra, salidaOtra) = Actualizar(zip, f.Clave);
        Assert.True(otra == 0, salidaOtra);
        Assert.Contains("ClaveMinima", Leer("Server.ini"));
        Assert.Contains("StartPort=7700", Leer("Server.ini"));
        Assert.Equal("mapa2 DEL HOST", Leer("gamedata/mapa2.json"));
        Assert.Equal("mapa2 v2", File.ReadAllText(Path.Combine(Base, "actualizacion", "sin-aplicar", "gamedata", "mapa2.json")));
        Assert.Equal("2026-10-13T00:00:00.000Z", Actualizador.IdInstalado(Base));
    }

    /// <summary>LA VERSIÓN INSTALADA ES LA DEL MANIFIESTO FIRMADO: el compilacion.json del paquete no va firmado, y uno con una
    /// fecha futura dejaba todas las versiones siguientes como "ya tenés una más nueva".</summary>
    [Fact]
    public void La_version_instalada_sale_del_manifiesto_firmado()
    {
        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-10-13T00:00:00.000Z",
            carpeta => File.WriteAllText(Path.Combine(carpeta, Actualizacion.Compilacion), "{\"id\":\"2099-01-01T00:00:00.000Z\"}"));
        var (codigo, salida) = Actualizar(zip, f.Clave);
        Assert.True(codigo == 0, salida);
        Assert.Equal("2026-10-13T00:00:00.000Z", Actualizador.IdInstalado(Base));
    }

    /// <summary>CON EL BACKUP INCOMPLETO NO SE ACTUALIZA: un archivo de data/ que otro programa tiene abierto (un antivirus) no
    /// entra en la copia; actualizar lo dice y no sigue, salvo con --forzar.</summary>
    [Fact]
    public void Con_el_backup_incompleto_no_se_actualiza_salvo_que_se_fuerce()
    {
        if (!OperatingSystem.IsWindows()) return;   // en Linux un archivo abierto se copia igual
        ArmarInstalacion();
        var f = new Firmante();
        string zip = ArmarPaquete(f, "2026-10-13T00:00:00.000Z");
        using (new FileStream(Path.Combine(Data, "personaje.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (codigo, salida) = Actualizar(zip, f.Clave);
            Assert.Equal(1, codigo);
            Assert.Contains("El backup salió incompleto: no se pudo copiar personaje.txt", salida);
            Assert.Equal("mapa1 v1", Leer("gamedata/mapa1.json"));

            var (forzada, salidaForzada) = Actualizar(zip, f.Clave, "--forzar");
            Assert.True(forzada == 0, salidaForzada);
            Assert.Contains("el backup salió sin personaje.txt", salidaForzada);
        }
        Assert.Equal("mapa1 v2", Leer("gamedata/mapa1.json"));
    }

    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }
}
