using System;
using System.IO;
using System.Linq;

namespace CoverAO.Server;

/// <summary>Qué encontró el sello al arrancar. Ver <see cref="Build.SellarMundo"/>.</summary>
public enum SelloDelMundo
{
    /// <summary>No había <c>data/</c>, o estaba vacío: mundo recién creado por esta versión.</summary>
    Nuevo,
    /// <summary>El mundo ya venía sellado con esta misma versión.</summary>
    Igual,
    /// <summary>El mundo lo creó OTRA versión. <c>previa</c> dice cuál.</summary>
    Distinta,
    /// <summary>Hay mundo pero sin sello: lo creó una versión anterior a que esto existiera.</summary>
    SinSello,
}

/// <summary>
/// La versión del servidor, en UN solo lugar, y el sello que dice con qué versión se creó un mundo.
///
/// <para>La versión estaba escrita como literal en <c>Program.cs</c> (el banner y el panel) y otra vez en el
/// armador (<c>hosting/publish.ps1</c>, que la usa para el nombre del zip). Con dos copias, el paquete y el
/// servidor que trae adentro se pueden contradecir sin que nada falle. Un test cuida que sigan iguales.</para>
/// </summary>
public static class Build
{
    public const string Version = "1.0.7";

    /// <summary>El archivo del sello, adentro de <c>data/</c> para que viaje con el mundo y no con el programa.</summary>
    public const string ArchivoDelSello = "version.txt";

    /// <summary>
    /// Deja escrito con qué versión corre este mundo, y dice qué había antes.
    ///
    /// <para><b>Para qué sirve.</b> Actualizar es descomprimir la versión nueva encima conservando
    /// <c>data/</c>, así que un mundo puede venir de cualquier versión anterior. Hoy eso es inofensivo —los
    /// guardados son texto y las claves que no existían toman su default—, pero el día que un campo cambie de
    /// SIGNIFICADO en vez de agregarse, lo primero que hace falta es saber de dónde viene el mundo. Sin sello
    /// no hay forma de averiguarlo después: la información no está en ningún lado.</para>
    ///
    /// <para>También caza el caso al revés, que es el peligroso: abrir con una versión VIEJA un mundo que
    /// guardó una nueva. Ahí el servidor viejo ignora en silencio lo que no entiende.</para>
    ///
    /// <para>No falla nunca por el sello: si el disco es de sólo lectura devuelve lo que pudo averiguar y
    /// sigue. Un servidor no se cae por no poder escribir una línea de metadatos.</para>
    /// </summary>
    public static SelloDelMundo SellarMundo(string dataRoot, out string previa)
    {
        previa = "";
        string archivo = Path.Combine(dataRoot, ArchivoDelSello);
        SelloDelMundo resultado;
        try
        {
            if (File.Exists(archivo))
            {
                previa = File.ReadAllText(archivo).Trim();
                resultado = previa == Version ? SelloDelMundo.Igual : SelloDelMundo.Distinta;
            }
            else
            {
                // "Vacío" es lo que decide si el mundo es NUEVO o viene de antes del sello. Se mira si hay
                // algo adentro, no si la carpeta existe: el server la crea al arrancar, así que su mera
                // existencia no dice nada.
                bool hayMundo = Directory.Exists(dataRoot)
                                && Directory.EnumerateFileSystemEntries(dataRoot).Any();
                resultado = hayMundo ? SelloDelMundo.SinSello : SelloDelMundo.Nuevo;
            }

            if (resultado != SelloDelMundo.Igual)
            {
                Directory.CreateDirectory(dataRoot);
                // Escritura ATÓMICA, aunque sea una línea: `File.WriteAllText` trunca antes de escribir y un
                // corte ahí deja un sello de cero bytes, que se leería como "otra versión" en cada arranque.
                // Lo caza `GuardadoConLectorAjenoTests` — que fue justamente quien lo encontró acá.
                Data.EscrituraAtomica.Escribir(archivo, Version + Environment.NewLine, System.Text.Encoding.ASCII);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return SelloDelMundo.Igual;   // no se pudo leer ni escribir: no es motivo para molestar a nadie
        }
        return resultado;
    }
}
