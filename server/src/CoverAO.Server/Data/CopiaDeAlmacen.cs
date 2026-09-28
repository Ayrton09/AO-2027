using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>Lo que dejó una copia: cuánto se copió, lo que no cierra entre los datos y la verificación.</summary>
public sealed class InformeDeCopia
{
    public int Cuentas { get; internal set; }
    public int Personajes { get; internal set; }
    public int Clanes { get; internal set; }
    public int Cartas { get; internal set; }
    /// <summary>Lo que no cierra en los datos de origen: se copió igual —o se dice por qué no— y queda anotado.</summary>
    public List<string> Avisos { get; } = new();
    /// <summary>Personajes que se volvieron a leer del destino iguales a como se leyeron del origen.</summary>
    public int Equivalentes { get; internal set; }
    /// <summary>Los que no: el nombre y el primer campo distinto. Una copia con alguno de éstos no vale.</summary>
    public List<string> Distintos { get; } = new();

    public bool SalioBien => Distintos.Count == 0;

    public string Texto()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Copiados: {Cuentas} cuentas, {Personajes} personajes, {Clanes} clanes, {Cartas} cartas.");
        sb.AppendLine($"Verificación: {Equivalentes} de {Personajes} personajes se leen iguales en el destino.");
        foreach (var d in Distintos) sb.AppendLine("  DISTINTO: " + d);
        if (Avisos.Count > 0)
        {
            sb.AppendLine($"Avisos ({Avisos.Count}):");
            foreach (var a in Avisos) sb.AppendLine("  " + a);
        }
        return sb.ToString();
    }
}

/// <summary>
/// LA COPIA DE UN ALMACÉN A OTRO (fase5/postgres/DISENO.md, sección 6). Importar —de los archivos o del SQLite viejo a la
/// base—, exportar —de la base a archivos— y pasar a Postgres son la misma operación con los almacenes cambiados.
///
/// <para><b>Nada se descarta en silencio.</b> Lo que no cierra en el origen —un personaje que no está en ninguna cuenta, una
/// cuenta que lista a uno que no existe, el clan del personaje contra la lista del clan, amigos, parejas y destinatarios que
/// no existen, nombres fuera de las reglas— va al informe.</para>
///
/// <para><b>Se verifica la EQUIVALENCIA</b>: cada personaje se vuelve a leer del destino y se compara campo por campo con lo
/// que se leyó del origen. No es comparar archivos —un <c>.chr</c> y una fila no se parecen—, es comparar lo que el juego
/// cargaría.</para>
/// </summary>
public static class CopiaDeAlmacen
{
    /// <param name="correoAparte">El correo que el origen no tiene en su casilla —el del original, que va adentro de cada
    /// <c>.chr</c>—: se suma a las casillas del mismo destinatario.</param>
    public static InformeDeCopia Copiar(IAlmacen origen, IAlmacen destino,
        IReadOnlyDictionary<string, IReadOnlyList<MailEntry>>? correoAparte = null)
    {
        var informe = new InformeDeCopia();
        var fichas = new Dictionary<string, FichaDePersonaje>(StringComparer.OrdinalIgnoreCase);

        // ── Las cuentas y sus personajes, en el orden de su lista ───────────────────────────────────────────────
        foreach (var nombreDeCuenta in origen.NombresDeCuentas())
        {
            var cuenta = origen.LeerCuenta(nombreDeCuenta);
            if (cuenta is null) { informe.Avisos.Add($"la cuenta {nombreDeCuenta} no se pudo leer"); continue; }
            if (!Naming.IsValidName(cuenta.Name)) informe.Avisos.Add($"la cuenta {cuenta.Name} tiene un nombre fuera de las reglas");
            destino.GuardarCuenta(cuenta);
            informe.Cuentas++;
            foreach (var nombre in cuenta.Characters)
            {
                if (fichas.ContainsKey(nombre))
                {
                    informe.Avisos.Add($"{nombre} figura en más de una cuenta: queda en la primera, no en {cuenta.Name}");
                    continue;
                }
                var ficha = origen.LeerPersonaje(nombre);
                if (ficha is null) { informe.Avisos.Add($"la cuenta {cuenta.Name} lista a {nombre}, que no existe"); continue; }
                if (!Naming.IsValidName(ficha.Nombre)) informe.Avisos.Add($"{ficha.Nombre} tiene un nombre fuera de las reglas");
                if (!destino.CrearPersonaje(cuenta, ficha))
                {
                    informe.Avisos.Add($"{ficha.Nombre} no entró: la cuenta {cuenta.Name} ya tenía {AccountFile.MaxCharacters}");
                    continue;
                }
                fichas[nombre] = ficha;
                informe.Personajes++;
            }
        }
        foreach (var nombre in origen.NombresDePersonajes())
            if (!fichas.ContainsKey(nombre))
                informe.Avisos.Add($"{nombre} no está en ninguna cuenta: no se copia (un personaje necesita su cuenta)");

        // ── Lo que guarda el mundo ──────────────────────────────────────────────────────────────────────────────
        var clanes = origen.LeerClanes();
        destino.GuardarClanes(clanes);
        informe.Clanes = clanes.Count;
        destino.GuardarRechazosDeClan(origen.LeerRechazosDeClan());
        var correo = origen.LeerCorreo().ToDictionary(c => c.Key, c => c.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (destinatario, cartas) in correoAparte ?? new Dictionary<string, IReadOnlyList<MailEntry>>())
        {
            if (!correo.TryGetValue(destinatario, out var casilla)) correo[destinatario] = casilla = new List<MailEntry>();
            casilla.AddRange(cartas);
        }
        destino.GuardarCasillas(correo.ToDictionary(c => c.Key, c => (IReadOnlyList<MailEntry>)c.Value, StringComparer.OrdinalIgnoreCase));
        informe.Cartas = correo.Values.Sum(c => c.Count);
        destino.GuardarSuelo(origen.LeerSuelo());
        destino.GuardarLlavesVendidas(origen.LeerLlavesVendidas());
        destino.GuardarRecord(origen.LeerRecord());

        Cruzar(informe, fichas, clanes, correo);

        // ── La verificación: cada personaje, releído del destino ───────────────────────────────────────────────
        foreach (var (nombre, ficha) in fichas)
        {
            var leida = destino.LeerPersonaje(nombre);
            string? diferencia = leida is null ? "no está en el destino" : PrimeraDiferencia(ficha, leida);
            if (diferencia is null) informe.Equivalentes++;
            else informe.Distintos.Add($"{nombre}: {diferencia}");
        }
        return informe;
    }

    /// <summary>Lo que no cierra entre los datos: se anota, no se arregla. Arreglarlo sería decidir por el que hostea.</summary>
    private static void Cruzar(InformeDeCopia informe, Dictionary<string, FichaDePersonaje> fichas, IReadOnlyList<ClanGuardado> clanes,
        Dictionary<string, List<MailEntry>> correo)
    {
        var porNombre = clanes.ToDictionary(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
        foreach (var (nombre, ficha) in fichas)
        {
            if (ficha.Clan.Length > 0)
            {
                if (!porNombre.TryGetValue(ficha.Clan, out var clan))
                    informe.Avisos.Add($"{nombre} dice ser del clan {ficha.Clan}, que no existe");
                else if (!clan.Miembros.Contains(nombre, StringComparer.OrdinalIgnoreCase))
                    informe.Avisos.Add($"{nombre} dice ser del clan {ficha.Clan}, y la lista del clan no lo tiene");
            }
            foreach (var amigo in ficha.Amigos)
                if (!fichas.ContainsKey(amigo)) informe.Avisos.Add($"{nombre} tiene de amigo a {amigo}, que no existe");
            if (ficha.Pareja.Length > 0 && !fichas.ContainsKey(ficha.Pareja))
                informe.Avisos.Add($"{nombre} está casado con {ficha.Pareja}, que no existe");
        }
        foreach (var clan in clanes)
            foreach (var miembro in clan.Miembros)
            {
                if (!fichas.TryGetValue(miembro, out var ficha))
                    informe.Avisos.Add($"el clan {clan.Nombre} lista a {miembro}, que no existe");
                else if (!string.Equals(ficha.Clan, clan.Nombre, StringComparison.OrdinalIgnoreCase))
                    informe.Avisos.Add($"el clan {clan.Nombre} lista a {miembro}, que dice ser de '{ficha.Clan}'");
            }
        foreach (var (destinatario, cartas) in correo)
            if (cartas.Count > 0 && !fichas.ContainsKey(destinatario))
                informe.Avisos.Add($"hay {cartas.Count} carta(s) para {destinatario}, que no existe");
    }

    /// <summary>
    /// El primer campo en que difieren dos fichas, o null si son iguales. Recorre TODOS los campos de la ficha, así un campo
    /// nuevo entra solo en la verificación. El nombre se compara sin mayúsculas: los archivos lo devuelven como se lo pidió.
    /// </summary>
    public static string? PrimeraDiferencia(FichaDePersonaje a, FichaDePersonaje b)
    {
        foreach (var prop in typeof(FichaDePersonaje).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? x = prop.GetValue(a), y = prop.GetValue(b);
            bool iguales = prop.Name == nameof(FichaDePersonaje.Nombre)
                ? string.Equals((string?)x, (string?)y, StringComparison.OrdinalIgnoreCase)
                : Iguales(x, y);
            if (!iguales) return $"{prop.Name}: {Mostrar(x)} en el origen, {Mostrar(y)} en el destino";
        }
        return null;
    }

    private static bool Iguales(object? x, object? y)
    {
        if (x is FichaDeFamiliar f && y is FichaDeFamiliar g)
            return f with { Habilidades = default } == g with { Habilidades = default } && f.Habilidades.SequenceEqual(g.Habilidades);
        // Los ImmutableArray se comparan por referencia con su Equals: acá, como secuencias.
        if (x is IEnumerable ex && y is IEnumerable ey && x is not string)
            return ex.Cast<object?>().SequenceEqual(ey.Cast<object?>());
        return Equals(x, y);
    }

    private static string Mostrar(object? v) => v switch
    {
        null => "(nada)",
        string s => $"'{s}'",
        IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Take(8)) + "…]",
        _ => v.ToString() ?? "",
    };
}
