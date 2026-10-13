using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace CoverAO.Server.Data;

/// <summary>
/// LA ACTUALIZACIÓN DEL SERVER, LO QUE NO TOCA EL DISCO (el autor, 12/10: cada host actualiza cuando quiere, sin que se le toque
/// lo suyo). Cada paquete del server lleva <c>archivos.json</c>: la fecha de la compilación, la plataforma y el SHA-256 de cada
/// archivo OFICIAL (el ejecutable, la data del juego, el cliente, la documentación y los scripts; de <c>data/</c>, sólo las
/// novedades), firmado con la clave del autor, la misma que firma el cliente (desktop/firma.js). Ed25519 no viene en .NET: se
/// verifica con BouncyCastle.
///
/// <para>Con el manifiesto de la versión instalada y el de la nueva se decide archivo por archivo (<see cref="Plan"/>): lo oficial
/// que el host no tocó se reemplaza; lo que modificó (un mapa propio) queda como está y el nuevo va aparte. El cliente y el
/// ejecutable se reemplazan siempre: tienen que ser los firmados. Lo que no figura en ningún manifiesto (sus personajes, su
/// base, su Server.ini, sus logs) no se toca nunca.</para>
/// </summary>
public static class Actualizacion
{
    /// <summary>La clave pública del autor (la de <c>desktop/clave-publica.pem</c>), adentro del código y no en un archivo del
    /// paquete: un archivo lo puede cambiar cualquiera que reparta un paquete.</summary>
    public const string ClavePublicaDelAutor = "MCowBQYDK2VwAyEAOYEO9fGkAlDIxsQ8zpoeyWwdoqQmlrFufPD3QnKRIgg=";

    /// <summary>Donde se publican los paquetes: las releases del repo del juego (privado hasta que el autor lo haga público).</summary>
    public const string Descargas = "https://github.com/Ayrton09/CoverAO/releases/latest/download/";

    public const string Manifiesto = "archivos.json";
    public const string Compilacion = "compilacion.json";
    /// <summary>El aviso de versión nueva que mira el server cada tanto: sólo la fecha, firmada.</summary>
    public const string Aviso = "servidor.json";

    public static string Plataforma => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    public static string Ejecutable => OperatingSystem.IsWindows() ? "CoverAO.Server.exe" : "CoverAO.Server";
    public static string NombreDelPaquete(string rid) => rid.StartsWith("win", StringComparison.Ordinal)
        ? $"CoverAO-servidor-{rid}.zip" : $"CoverAO-servidor-{rid}.tar.gz";

    public sealed record ManifiestoDelPaquete(string Id, string Rid, IReadOnlyDictionary<string, string> Archivos);

    private static readonly Regex Fecha = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{3})?Z$", RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static string TextoDelManifiesto(string id, string rid, IReadOnlyDictionary<string, string> archivos) =>
        $"servidor 1\nid {id}\nrid {rid}\n" +
        string.Concat(archivos.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key} {a.Value}\n"));

    public static string TextoDelAviso(string id) => $"servidor-aviso 1\nid {id}\n";

    /// <summary>¿La firma (base64) es de la clave (SPKI en base64) sobre ese texto?</summary>
    public static bool FirmaValida(string texto, string? firma, string clave = ClavePublicaDelAutor)
    {
        if (string.IsNullOrEmpty(firma)) return false;
        try
        {
            var publica = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(Convert.FromBase64String(clave));
            var verificador = new Ed25519Signer();
            verificador.Init(false, publica);
            byte[] bytes = Encoding.UTF8.GetBytes(texto);
            verificador.BlockUpdate(bytes, 0, bytes.Length);
            return verificador.VerifySignature(Convert.FromBase64String(firma));
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException) { return false; }
    }

    /// <summary>Una ruta de archivo oficial: relativa, con '/', sin '..', y de <c>data/</c> sólo las novedades.</summary>
    public static bool RutaOficialValida(string rel) =>
        rel.Length is > 0 and < 300 && !rel.StartsWith('/') && !rel.Contains('\\') && !rel.Contains(':')
        && !rel.Split('/').Any(p => p is "" or "." or "..")
        && (!rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || rel.Equals("data/updates.txt", StringComparison.OrdinalIgnoreCase))
        && !rel.Equals("Server.ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>El manifiesto si está bien armado (y firmado por el autor, si se pide). Si no, null.</summary>
    public static ManifiestoDelPaquete? LeerManifiesto(string json, bool exigirFirma, string clave = ClavePublicaDelAutor)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) return null;
            string? id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
            string? rid = r.TryGetProperty("rid", out var p) ? p.GetString() : null;
            if (id is null || !Fecha.IsMatch(id) || rid is not ("win-x64" or "linux-x64")) return null;
            if (!r.TryGetProperty("archivos", out var a) || a.ValueKind != JsonValueKind.Object) return null;
            var archivos = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var x in a.EnumerateObject())
            {
                string? h = x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : null;
                if (h is null || !Hash.IsMatch(h) || !RutaOficialValida(x.Name)) return null;
                archivos[x.Name] = h;
            }
            if (archivos.Count == 0) return null;
            string? firma = r.TryGetProperty("firma", out var f) ? f.GetString() : null;
            if (exigirFirma && !FirmaValida(TextoDelManifiesto(id, rid, archivos), firma, clave)) return null;
            return new ManifiestoDelPaquete(id, rid, archivos);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>La fecha del aviso de versión nueva si está firmado por el autor; si no, null.</summary>
    public static string? IdDelAviso(string json, string clave = ClavePublicaDelAutor)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
            string? firma = r.TryGetProperty("firma", out var f) ? f.GetString() : null;
            return id is not null && Fecha.IsMatch(id) && FirmaValida(TextoDelAviso(id), firma, clave) ? id : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>¿<paramref name="id"/> es posterior a <paramref name="local"/>? Sin fecha local (una instalación de antes de las
    /// actualizaciones), cualquiera lo es.</summary>
    public static bool EsMasNueva(string id, string? local) =>
        string.IsNullOrEmpty(local) || !DateTimeOffset.TryParse(local, out var l)
        || (DateTimeOffset.TryParse(id, out var n) && n > l);

    public enum Accion { Agregar, Reemplazar, Borrar, Conservar }

    public sealed record Paso(string Ruta, Accion Accion);

    /// <summary>El cliente y el ejecutable se reemplazan aunque estén cambiados: tienen que ser los del paquete (el cliente lo
    /// verifica la app contra su firma).</summary>
    public static bool SiempreSeReemplaza(string rel) =>
        rel.StartsWith("webclient/", StringComparison.Ordinal) || rel.StartsWith("wwwroot/", StringComparison.Ordinal)
        || rel is "CoverAO.Server.exe" or "CoverAO.Server";

    /// <summary>
    /// Qué hacer con cada archivo. <paramref name="viejos"/>: el manifiesto de la versión instalada (null en una instalación de
    /// antes de las actualizaciones: ahí no se sabe qué tocó el host, y todo lo oficial se reemplaza, guardando lo anterior).
    /// <paramref name="hashActual"/>: el SHA-256 del archivo como está hoy, o null si no existe. Un archivo igual al nuevo no
    /// aparece: no hay nada que hacer.
    /// </summary>
    public static List<Paso> Plan(IReadOnlyDictionary<string, string>? viejos, IReadOnlyDictionary<string, string> nuevos,
        Func<string, string?> hashActual)
    {
        var pasos = new List<Paso>();
        foreach (var (rel, nuevo) in nuevos.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            string? actual = hashActual(rel);
            if (actual is null) pasos.Add(new Paso(rel, Accion.Agregar));
            else if (actual == nuevo) continue;
            else if (SiempreSeReemplaza(rel) || viejos is null || (viejos.TryGetValue(rel, out var oficial) && oficial == actual))
                pasos.Add(new Paso(rel, Accion.Reemplazar));
            else pasos.Add(new Paso(rel, Accion.Conservar));
        }
        if (viejos is not null)
            foreach (var (rel, oficial) in viejos.OrderBy(v => v.Key, StringComparer.Ordinal))
                if (!nuevos.ContainsKey(rel) && hashActual(rel) == oficial) pasos.Add(new Paso(rel, Accion.Borrar));
        return pasos;
    }

    public static string Sha256DeArchivo(string ruta)
    {
        using var f = File.OpenRead(ruta);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}
