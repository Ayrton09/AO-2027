using System.Linq;

namespace CoverAO.Server.Data;

/// <summary>Validación de nombres de cuenta/personaje (inspirada en el VB6 AsciiValidos TCP.bas:210 +
/// NombrePermitido TCP.bas:264). Objetivo de seguridad: descartar de raíz los separadores de path
/// (<c>\ / :</c>), los <c>..</c> del path traversal y los metacaracteres de INI (<c>[ ] =</c> CR LF), para
/// que el nombre no pueda escapar del directorio de cuentas/chars ni inyectar secciones en el .cnt/.chr.
/// Se permite letras + dígitos + espacio + guión bajo (más laxo que el AsciiValidos estricto del VB6 —que
/// sólo acepta a-z+espacio— pero suficiente para cerrar el vector, y compatible con nombres como
/// "cuenta_test"). Longitud 1..30.</summary>
public static class Naming
{
    public const int MaxNameLength = 30;

    // Nombres de dispositivo reservados de Windows: pasarían el filtro de charset (son puras letras) pero
    // File.WriteAllText("NUL.chr") escribe al dispositivo nulo (el .chr nunca persiste) y en un host con
    // COM/LPT real podría bloquear el hilo del game loop. Se rechazan explícitamente.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Blocklist de nombres (VB6 NombrePermitido, TCP.bas:264, sobre NombresInvalidos.txt): 60 entradas de
    /// marcas, insultos y nicks de gente del proyecto original. El chequeo es por SUBCADENA, así que
    /// "argentum" veta también "argentumonline" y "xxargentumxx".
    ///
    /// DESVÍO DELIBERADO DEL ORIGINAL: el VB6 usa <c>InStr</c> sin <c>Option Compare Text</c>, o sea que
    /// compara con MAYÚSCULAS Y MINÚSCULAS. Como el archivo trae entradas del estilo "LoBoNeGrO", el chequeo
    /// original sólo bloquea esa grafía exacta y deja pasar "lobonegro" — una blocklist que no bloquea. Acá
    /// se compara sin distinguir mayúsculas, que es lo que el dato evidentemente quiere decir. Es el único
    /// lugar donde este port se aparta a propósito de la semántica del original.
    /// </summary>
    private static string[] _forbidden = Array.Empty<string>();

    public static void LoadForbiddenNames(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            var list = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            if (list is not null)
                _forbidden = list.Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Select(n => n.Trim().ToLowerInvariant()).ToArray();
        }
        catch (System.Text.Json.JsonException) { /* sin blocklist se sigue validando el charset */ }
    }

    /// <summary>¿El nombre contiene alguna entrada de la blocklist? (por subcadena, sin distinguir caso).</summary>
    public static bool IsForbiddenName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || _forbidden.Length == 0) return false;
        string lower = name.ToLowerInvariant();
        foreach (var bad in _forbidden)
            if (lower.Contains(bad, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        // Sin espacios en los bordes ni dobles: 'Rugura ' / ' Rugura' / 'Rug  ura' rendean idéntico a 'Rugura'
        // pero serían archivos/claves DISTINTOS → suplantación de identidad (incl. del char demo GM). Al exigir
        // el nombre ya normalizado, la clave del store queda canónica y el backstop de dispositivos ('CON ') cae acá.
        if (name != name.Trim() || name.Contains("  ")) return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '_')) return false;
        if (ReservedNames.Contains(name)) return false;
        return !IsForbiddenName(name);
    }

    public const int MaxCharNameLength = 18;   // VB6 ValidarNombre: `If Len(Nombre) < 1 Or Len(Nombre) > 18`

    /// <summary>Nombre de PERSONAJE (VB6 <c>ValidarNombre</c>, TCP.bas:181): sólo letras y espacio, hasta 18 —
    /// ni dígitos ni guión bajo, que <see cref="IsValidName"/> sí acepta para las cuentas.
    ///
    /// La diferencia no es cosmética: el <c>CharacterCreate</c> de un NPC manda su NÚMERO de ficha en el campo
    /// del nombre, y el cliente distingue NPC de usuario preguntando si ese nombre es NUMÉRICO (VB6
    /// <c>IsNumeric</c>, Protocol.bas:1733). Un personaje llamado "500" se renderizaría como la criatura 500:
    /// nombre ajeno, color ajeno y la etiqueta escondida bajo la regla de los NPCs. Que el original prohíba los
    /// dígitos es JUSTAMENTE lo que hace que ese truco sea seguro.</summary>
    public static bool IsValidCharName(string? name)
    {
        if (!IsValidName(name) || name!.Length > MaxCharNameLength) return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == ' ')) return false;
        return true;
    }

    /// <summary>Backstop de path traversal para la capa de persistencia: normaliza a mayúsculas y verifica
    /// que el nombre no contenga separadores ni <c>..</c> (un nombre ya validado por <see cref="IsValidName"/>
    /// nunca los tiene, pero esto protege cualquier ruta que llegue sin validar).</summary>
    public static string SafeFileStem(string name)
    {
        string stem = name.ToUpperInvariant();
        if (stem != System.IO.Path.GetFileName(stem) || stem.Contains("..") || ReservedNames.Contains(stem))
            throw new System.ArgumentException($"nombre de archivo inseguro: '{name}'");
        return stem;
    }
}
