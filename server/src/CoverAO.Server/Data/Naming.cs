
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
    /// <summary>Largo máximo de un nombre de CUENTA. Veinte, como el original (VB6
    /// <c>CheckDataNewAccount</c>, Cuentas.bas:568: <c>If Len(Cuenta) &gt; 20</c> → aviso 63). Acá decía 30, o
    /// sea que aceptábamos diez caracteres que el original rechaza. El nombre de PERSONAJE tiene el suyo, más
    /// corto todavía (<see cref="MaxCharNameLength"/>).</summary>
    public const int MaxNameLength = 20;

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

    public static bool IsValidName(string? name) => FormaValida(name) && !IsForbiddenName(name);

    /// <summary>Las reglas de <see cref="IsValidName"/> sin la lista de nombres prohibidos.</summary>
    private static bool FormaValida(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        // Sin espacios en los bordes ni dobles: 'Rugura ' / ' Rugura' / 'Rug  ura' rendean idéntico a 'Rugura'
        // pero serían archivos/claves DISTINTOS → suplantación de identidad (incl. del char demo GM). Al exigir
        // el nombre ya normalizado, la clave del store queda canónica y el backstop de dispositivos ('CON ') cae acá.
        if (name != name.Trim() || name.Contains("  ")) return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '_')) return false;
        return !ReservedNames.Contains(name);
    }

    public const int MaxCharNameLength = 18;   // VB6 ValidarNombre: `If Len(Nombre) < 1 Or Len(Nombre) > 18`

    /// <summary>Nombre de PERSONAJE: letras, números y espacio, EMPEZANDO CON UNA LETRA, hasta 18; sin guión bajo, que
    /// <see cref="IsValidName"/> sí acepta para las cuentas. El original (VB6 <c>ValidarNombre</c>, TCP.bas:181) no deja
    /// números; el autor los pidió (05/10: "debería dejarme poner números", con "Ayrton09").
    ///
    /// Lo de empezar con una letra no es cosmético: el <c>CharacterCreate</c> de un NPC manda su NÚMERO de ficha en el
    /// campo del nombre, y el cliente distingue NPC de usuario preguntando si ese nombre es NUMÉRICO (VB6 <c>IsNumeric</c>,
    /// Protocol.bas:1733; el web, <c>Number(nombre)</c>). Un personaje llamado "500" se renderizaría como la criatura 500:
    /// nombre ajeno, color ajeno y la etiqueta escondida bajo la regla de los NPCs. Un nombre que empieza con una letra
    /// nunca es numérico, así que el truco sigue siendo seguro.</summary>
    public static bool IsValidCharName(string? name) => IsValidCharNameShape(name) && !IsForbiddenName(name);

    /// <summary>Las reglas de <see cref="IsValidCharName"/> sin la lista de prohibidos. Separa los dos avisos del
    /// original al crear un personaje: el nombre mal formado ("Nombre inválido.", 4) del que está en la lista
    /// ("No está permitido usar este nombre en el juego.", 74).</summary>
    public static bool IsValidCharNameShape(string? name)
    {
        if (!FormaValida(name) || name!.Length > MaxCharNameLength) return false;
        static bool Letra(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        if (!Letra(name[0])) return false;
        foreach (char c in name)
            if (!(Letra(c) || (c >= '0' && c <= '9') || c == ' ')) return false;
        return true;
    }

    /// <summary>Largo máximo de un nombre de CLAN. NO sale del original: <c>GuildNameValido</c> mira los
    /// caracteres y no el largo, y el formulario del cliente VB6 tampoco pone tope. Hace falta igual porque el
    /// nombre del clan viaja PEGADO al del personaje en cada <c>CharacterCreate</c>
    /// (<c>Modulo_UsUaRiOs.bas:215</c>: <c>Nombre &lt;Clan&gt;</c>), o sea que lo recibe cada uno de los que lo
    /// tienen en pantalla, cada vez que entra en su área. Con el charset de abajo el nombre no puede hacer
    /// daño; con el largo suelto, sí. Treinta es el mismo tope que el de una cuenta.</summary>
    public const int MaxGuildNameLength = 30;

    /// <summary>Nombre de CLAN (VB6 <c>GuildNameValido</c>, modGuilds.bas:890): pasa el nombre a minúsculas y
    /// exige que cada carácter sea una letra a-z, un espacio, o el 255 del codepage. Ni dígitos, ni acentos, ni
    /// puntuación, ni NUL, ni controles.
    ///
    /// <para>No es cosmético. El nombre se difunde dentro del nombre del personaje, se guarda en el
    /// <c>.chr</c> y se usa como clave del registro de clanes; y las listas de clanes y de miembros viajan como
    /// UNA cadena separada por NUL, así que un nombre con un NUL adentro se lee como dos clanes. El original
    /// cierra todo eso con esta única regla, y acá el llamador no la estaba aplicando.</para></summary>
    public static bool IsValidGuildName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxGuildNameLength) return false;
        foreach (char c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == ' ' || c == 'ÿ')) return false;
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
