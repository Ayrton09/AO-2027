using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CoverAO.Server.Data;

/// <summary>
/// EL SERVER.INI DEL HOST AL DÍA, SIN TOCARLE NADA (el autor, 12/10: "la idea es no joder a ningun host con sus propias
/// configs"). Al actualizar, el Server.ini se rearma sobre el <c>Server.ini.ejemplo</c> de la versión nueva: el mismo orden,
/// las mismas explicaciones y todas las claves que existen hoy, con los valores del host donde los tenga.
///
/// <para>LO QUE NO PUEDE PASAR es que cambie con qué corre el server. Por eso:</para>
/// <list type="bullet">
/// <item>Cada clave que el host NO tiene se prueba sola: si escribirla cambia lo que lee el server (una sección cuya ausencia
/// apaga una regla, como <c>[NIVELMINIMO]</c>), va COMENTADA; si no, va con el valor del ejemplo, que es el que el server ya
/// usaba por defecto.</item>
/// <item>Lo del host que el ejemplo no trae se conserva: los nombres del staff, los mapas de su <c>[NIVELMINIMO]</c>, sus
/// secciones propias. Una clave de <c>[INIT]</c> que el server no conoce (mal escrita, o de una versión vieja) también: va al
/// final, bajo un título que le dice que se ignora.</item>
/// <item>Al final se lee el resultado con el MISMO lector del server y se compara opción por opción con lo que leía antes, y
/// clave por clave con lo que el host tenía escrito. Si una sola cosa difiere, no hay rearmado: <see cref="Resultado.Texto"/>
/// es null y <see cref="Resultado.Problema"/> dice qué habría cambiado. El que llama deja el archivo del host como estaba.</item>
/// </list>
/// Lo único que se pierde son los comentarios que el host escribió a mano; el que llama guarda el archivo viejo al lado.
/// </summary>
public static class RearmadoDelIni
{
    public sealed record Resultado(
        string? Texto,
        IReadOnlyList<string> Agregadas,
        IReadOnlyList<string> Comentadas,
        IReadOnlyList<string> Desconocidas,
        IReadOnlyList<string> Renombradas,
        string? Problema);

    /// <summary>Encabezado del bloque de las claves de <c>[INIT]</c> que el server no reconoce. Sin tildes, como el resto del
    /// Server.ini: se escribe en Windows-1252 y así se lee igual en cualquier editor.</summary>
    public const string TituloDesconocidas =
        "; ---- Claves que el server NO reconoce: se ignoran. Revisa como estan escritas (o borralas) ----";

    private sealed record Linea(string Texto, string Seccion, string? Clave, string? Valor);

    public static Resultado Rearmar(string textoDelHost, string textoDelEjemplo,
        IReadOnlyDictionary<(string Seccion, string Vieja), string>? renombres = null)
    {
        renombres ??= ServerIni.ClavesRenombradas;
        var host = IniFile.Parse(textoDelHost);
        var renombradas = new List<string>();
        var renombradasViejas = new HashSet<(string, string)>(renombres.Keys, ClaveComparer.Instancia);
        foreach (var ((seccion, vieja), nueva) in renombres)
        {
            if (host.HasKey(seccion, vieja) && !host.HasKey(seccion, nueva))
            {
                host.Set(seccion, nueva, host.GetString(seccion, vieja));
                renombradas.Add($"{vieja} → {nueva}");
            }
        }
        string antes = Huella(ServerIni.Desde(host));

        var lineas = LeerLineas(textoDelEjemplo);
        var delEjemplo = new HashSet<(string, string)>(lineas.Where(l => l.Clave is not null).Select(l => (l.Seccion, l.Clave!)),
            ClaveComparer.Instancia);
        var agregadas = new List<string>();
        var comentadas = new List<string>();
        var desconocidas = new List<(string Clave, string Valor)>();

        var salida = new StringBuilder();
        string seccionActual = "";
        var seccionesVistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };

        // Lo del host que el ejemplo no trae en esta sección, al cerrarla. Las claves renombradas no: ya van con su nombre nuevo.
        void CerrarSeccion(string seccion)
        {
            foreach (var clave in host.Claves(seccion))
            {
                if (delEjemplo.Contains((seccion, clave)) || renombradasViejas.Contains((seccion, clave))) continue;
                if (seccion.Equals("INIT", StringComparison.OrdinalIgnoreCase) && !ServerIni.EsClaveConocida(clave))
                    desconocidas.Add((clave, host.GetString(seccion, clave)));
                else
                    salida.Append(clave).Append('=').Append(host.GetString(seccion, clave)).Append('\n');
            }
        }

        foreach (var l in lineas)
        {
            if (l.Clave is null && l.Texto.TrimStart().StartsWith('[') && l.Seccion != seccionActual)
            {
                CerrarSeccion(seccionActual);
                seccionActual = l.Seccion;
                seccionesVistas.Add(seccionActual);
                salida.Append(l.Texto).Append('\n');
                continue;
            }
            if (l.Clave is null) { salida.Append(l.Texto).Append('\n'); continue; }
            if (host.HasKey(l.Seccion, l.Clave))
            {
                salida.Append(l.Clave).Append('=').Append(host.GetString(l.Seccion, l.Clave)).Append('\n');
                continue;
            }
            // UNA CLAVE QUE EL HOST NO TIENE: con el valor del ejemplo sólo si eso no cambia lo que lee el server.
            var prueba = IniFile.Parse(host.ToText());
            prueba.Set(l.Seccion, l.Clave, l.Valor!);
            if (Huella(ServerIni.Desde(prueba)) == antes)
            {
                salida.Append(l.Texto.Trim()).Append('\n');
                agregadas.Add(Nombre(l.Seccion, l.Clave));
            }
            else
            {
                salida.Append(';').Append(l.Texto.Trim()).Append('\n');
                comentadas.Add(Nombre(l.Seccion, l.Clave));
            }
        }
        CerrarSeccion(seccionActual);

        // Las secciones del host que el ejemplo no tiene, enteras.
        foreach (var seccion in host.Secciones)
        {
            if (seccionesVistas.Contains(seccion) || host.Claves(seccion).Count == 0) continue;
            salida.Append('\n').Append('[').Append(seccion).Append("]\n");
            seccionActual = seccion;
            CerrarSeccion(seccion);
        }
        if (desconocidas.Count > 0)
        {
            salida.Append('\n').Append(TituloDesconocidas).Append("\n[INIT]\n");
            foreach (var (clave, valor) in desconocidas) salida.Append(clave).Append('=').Append(valor).Append('\n');
        }

        string texto = salida.ToString();
        string? problema = Verificar(texto, host, antes, renombradasViejas);
        return new Resultado(problema is null ? texto : null, agregadas, comentadas, desconocidas.Select(d => d.Clave).ToList(),
            renombradas, problema);
    }

    /// <summary>Null si el texto rearmado lee igual que el del host y conserva cada clave que el host escribió.</summary>
    internal static string? Verificar(string texto, IniFile host, string antes, HashSet<(string, string)> renombradasViejas)
    {
        var nuevo = IniFile.Parse(texto);
        string despues = Huella(ServerIni.Desde(nuevo));
        if (despues != antes)
        {
            var a = antes.Split('\n');
            var d = despues.Split('\n');
            string? distinta = a.Zip(d).FirstOrDefault(p => p.First != p.Second).First;
            return $"cambiaba lo que lee el server: {distinta ?? "(una opción)"}";
        }
        foreach (var seccion in host.Secciones)
            foreach (var clave in host.Claves(seccion))
                if (!nuevo.HasKey(seccion, clave) && !renombradasViejas.Contains((seccion, clave)))
                    return $"se perdía [{seccion}] {clave}";
                else if (nuevo.HasKey(seccion, clave) && nuevo.GetString(seccion, clave) != host.GetString(seccion, clave))
                    return $"cambiaba [{seccion}] {clave}";
        return null;
    }

    /// <summary>
    /// Todo lo que el server saca del ini, en texto: cada propiedad pública de <see cref="ServerIni"/> (el staff incluido), con las
    /// colecciones ordenadas. Dos ini que dan la misma huella hacen correr al server igual. No entran la ruta del archivo ni las
    /// listas de qué claves están escritas y cuáles faltan, que cambian a propósito al agregar las nuevas.
    /// </summary>
    public static string Huella(ServerIni ini)
    {
        var sb = new StringBuilder();
        foreach (var p in typeof(ServerIni).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is nameof(ServerIni.Ruta) or nameof(ServerIni.ClavesAusentes)
                or nameof(ServerIni.InitEscritas) or nameof(ServerIni.ClavesDesconocidas)) continue;
            sb.Append(p.Name).Append('=').Append(Valor(p.GetValue(ini))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Valor(object? o) => o switch
    {
        null => "null",
        string s => s,
        IDictionary d => "{" + string.Join(",", d.Keys.Cast<object>().Select(k => Valor(k) + ":" + Valor(d[k])).Order(StringComparer.Ordinal)) + "}",
        IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(Valor).Order(StringComparer.Ordinal)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "",
    };

    private static string Nombre(string seccion, string clave) =>
        seccion.Equals("INIT", StringComparison.OrdinalIgnoreCase) ? clave : $"[{seccion}] {clave}";

    /// <summary>Las líneas del ejemplo con su sección y, si es una clave activa, su clave y valor (las mismas reglas que IniFile).</summary>
    private static List<Linea> LeerLineas(string texto)
    {
        if (texto.Length > 0 && texto[0] == '﻿') texto = texto[1..];
        var lineas = new List<Linea>();
        string seccion = "";
        foreach (var raw in texto.Split('\n'))
        {
            string original = raw.TrimEnd('\r');
            string l = original.Trim();
            if (l.Length > 0 && l[0] == '[')
            {
                int fin = l.IndexOf(']', 1);
                if (fin >= 0) seccion = l[1..fin].Trim();
                lineas.Add(new Linea(original, seccion, null, null));
                continue;
            }
            bool comentario = l.Length == 0 || l[0] == ';' || l[0] == '\'' || l[0] == '#' || l.StartsWith("//", StringComparison.Ordinal);
            int eq = l.IndexOf('=');
            if (comentario || eq < 0) { lineas.Add(new Linea(original, seccion, null, null)); continue; }
            lineas.Add(new Linea(original, seccion, l[..eq].Trim(), l[(eq + 1)..].Trim()));
        }
        // Un archivo que termina en salto de línea deja una línea vacía de más al final: no se repite.
        if (lineas.Count > 0 && lineas[^1].Texto.Length == 0) lineas.RemoveAt(lineas.Count - 1);
        return lineas;
    }

    private sealed class ClaveComparer : IEqualityComparer<(string, string)>
    {
        public static readonly ClaveComparer Instancia = new();
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) c) =>
            HashCode.Combine(c.Item1.ToUpperInvariant(), c.Item2.ToUpperInvariant());
    }
}
