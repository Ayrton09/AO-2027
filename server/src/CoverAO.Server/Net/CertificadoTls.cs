using System.Security.Cryptography.X509Certificates;

namespace CoverAO.Server.Net;

/// <summary>
/// EL CERTIFICADO DE <c>--tls &lt;cert.pfx&gt; [clave]</c> (SEC-4), que envuelve el transporte TCP en SslStream. Si se pidió TLS y
/// el certificado no carga, el servidor NO arranca: antes avisaba "sigo en plaintext" y abría el puerto en claro, y el que
/// hostea creía estar cifrando las contraseñas (auditoría externa del 23/10). Sin <c>--tls</c>, en claro, como siempre.
/// </summary>
public static class CertificadoTls
{
    /// <summary>El certificado pedido, o null si no se pidió TLS. Si se pidió y no se pudo cargar, null y
    /// <paramref name="error"/> dice por qué.</summary>
    public static X509Certificate2? Cargar(string[] args, out string? error)
    {
        error = null;
        int i = Array.FindIndex(args, a => string.Equals(a, "--tls", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "--tls necesita la ruta del certificado (.pfx).";
            return null;
        }
        string pfx = args[i + 1];
        string? clave = i + 2 < args.Length && !args[i + 2].StartsWith("--", StringComparison.Ordinal) ? args[i + 2] : null;
        if (!File.Exists(pfx))
        {
            error = $"no existe el certificado '{pfx}'.";
            return null;
        }
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, clave);
            if (!cert.HasPrivateKey)
            {
                cert.Dispose();
                error = $"el certificado '{pfx}' no trae la clave privada.";
                return null;
            }
            return cert;
        }
        catch (Exception ex)
        {
            error = $"no se pudo cargar el certificado '{pfx}': {ex.Message}";
            return null;
        }
    }
}
