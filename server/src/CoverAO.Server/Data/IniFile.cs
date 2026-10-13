using System.Text;
using CoverAO.Protocol;

namespace CoverAO.Server.Data;

/// <summary>
/// INI reader/writer matching the VB6 clsIniManager / GetVar-WriteVar semantics used for the
/// .chr (character) and .cnt (account) files. Sections in [brackets], key=value lines, encoded in
/// Windows-1252 (the same codepage as the wire strings). Section and key order is preserved so a
/// load→save round-trip is byte-stable.
/// </summary>
public sealed class IniFile
{
    // section -> (key -> value), both insertion-ordered
    private readonly List<string> _sectionOrder = new();
    private readonly Dictionary<string, List<string>> _keyOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _data = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path) => Parse(File.ReadAllText(path, Codec.Ansi));

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        string current = "";
        // BOM: los .dat y .chr se leen en Windows-1252, así que un BOM UTF-8 llega como los tres caracteres
        // "ï»¿" pegados al principio. Si la primera línea es un header —y en el Server.ini lo es— deja de
        // empezar con '[', no abre sección, y se pierde la sección ENTERA: todos los ajustes y todos los
        // contadores de staff. El host ve el banner diciendo que leyó el archivo y "STAFF: ninguno", sin
        // ninguna pista. Cualquier editor que guarde en UTF-8 con firma lo dispara.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        else if (text.StartsWith("ï»¿", StringComparison.Ordinal)) text = text[3..];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            // Marcas de comentario: `;` y `'` son las del formato original (INI y VB6). Se aceptan además
            // `//` y `#` porque es lo que la gente escribe por costumbre, y hasta ahora una linea asi NO se
            // trataba como comentario: sin `=` se ignoraba de casualidad, pero con `=` adentro
            // —`// StartPort=7666 es el puerto base`— entraba como una CLAVE llamada "// StartPort".
            // No pisa ningun ajuste real, pero ensucia el ini parseado y el que lo escribio no se entera.
            if (line.Length == 0 || line[0] == ';' || line[0] == '\'' || line[0] == '#'
                || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line[0] == '[')
            {
                // Un header puede traer un comentario VB6 pegado: `[NPC2] 'INVOCADO`. El VB6
                // (clsIniManager.Initialize) toma lo que va entre el '[' y el PRIMER ']' e ignora el resto; y si
                // no hay ']', la linea no es nada — nunca cae al parseo de valores. Exigir que TERMINE en ']'
                // hacia que el header no abriera seccion Y que sus claves se cargaran en la seccion ANTERIOR
                // (el mismo bug le costo 84 NPCs / 123 objetos / 24 hechizos al transcoder). Aca los .chr/.cnt
                // los escribe este server y no traen comentarios, pero el importador de .chr del VB6 si los lee.
                int end = line.IndexOf(']', 1);
                if (end >= 0)
                {
                    current = line[1..end].Trim();
                    ini.EnsureSection(current);
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            ini.Set(current, line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return ini;
    }

    private void EnsureSection(string section)
    {
        if (_data.ContainsKey(section)) return;
        _data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _keyOrder[section] = new List<string>();
        _sectionOrder.Add(section);
    }

    public void Set(string section, string key, string value)
    {
        // Defensa sistémica contra inyección INI: un valor con CR/LF partiría la línea al serializar
        // (ToText) y crearía secciones/keys nuevas que en el próximo Parse pisan a las legítimas (p.ej.
        // Privilegios=32 via un campo de texto como la descripción). Ningún valor de una sola clave puede
        // contener saltos de línea, así que los colapsamos a espacio.
        if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            value = value.Replace('\r', ' ').Replace('\n', ' ');
        EnsureSection(section);
        if (!_data[section].ContainsKey(key)) _keyOrder[section].Add(key);
        _data[section][key] = value;
    }

    public void Set(string section, string key, int value) => Set(section, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Las claves que TRAE una sección, en el orden del archivo. Lo necesita el aviso de arranque
    /// que le dice al host qué opciones nuevas no tiene todavía en su <c>Server.ini</c>.</summary>
    public IReadOnlyList<string> Claves(string section)
        => _keyOrder.TryGetValue(section, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();

    public string GetString(string section, string key, string fallback = "")
        => _data.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string section, string key, int fallback = 0)
        => int.TryParse(GetString(section, key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public short GetShort(string section, string key, short fallback = 0) => (short)GetInt(section, key, fallback);
    public byte GetByte(string section, string key, byte fallback = 0) => (byte)GetInt(section, key, fallback);
    public long GetLong(string section, string key, long fallback = 0)
        => long.TryParse(GetString(section, key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public bool GetBool(string section, string key, bool fallback = false)
    {
        var s = GetString(section, key);
        return s.Length == 0 ? fallback : (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    public bool HasSection(string section) => _data.ContainsKey(section);

    /// <summary>Las secciones, en el orden del archivo (las vacías también).</summary>
    public IReadOnlyList<string> Secciones => _sectionOrder;

    /// <summary>¿La sección trae esta clave? Distingue "no está" de "está vacía", que <see cref="GetString"/> no puede.</summary>
    public bool HasKey(string section, string key) => _data.TryGetValue(section, out var s) && s.ContainsKey(key);

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var section in _sectionOrder)
        {
            sb.Append('[').Append(section).Append("]\r\n");
            foreach (var key in _keyOrder[section])
                sb.Append(key).Append('=').Append(_data[section][key]).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>El candado de las escrituras del ini. Al <c>Server.ini</c> lo reescriben DOS hilos: el del
    /// game loop (<c>/SETINIVAR</c>) y el HTTP del panel (cambiarle el rango a alguien), y los dos hacen
    /// leer-modificar-guardar sobre el mismo archivo. Sin esto, dos cambios a la vez se pisan. Por eso
    /// <c>ServerIni.GuardarStaff</c> lo toma alrededor de las tres cosas, no sólo en <see cref="Save"/>, que lo vuelve a
    /// tomar adentro.</summary>
    internal static readonly object PuertaDeEscritura = new();

    /// <summary>Guarda pasando por un temporal: este archivo decide QUIEN ES STAFF, los puertos y las reglas
    /// de la casa, y con <c>File.WriteAllText</c> derecho quedaba en CERO bytes mientras se reescribia — un
    /// corte ahi devolvia el servidor sin un solo GM. Esta medido en <see cref="EscrituraAtomica"/>.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        lock (PuertaDeEscritura) EscrituraAtomica.Escribir(path, ToText(), Codec.Ansi);
    }
}
