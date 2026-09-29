using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// LO QUE GUARDA EL MUNDO COMO TEXTO: el JSON de cada archivo de <c>Misc/</c> —clanes, correo, suelo, llaves vendidas,
/// rechazos de clan, récord—, ida y vuelta. Lo usan el almacén de archivos (<see cref="AlmacenDeTexto"/>) y la base, para
/// pasar a sus tablas lo que una versión anterior guardaba así.
///
/// <para><b>El formato no se toca</b>: son los archivos de los servidores que ya andan. Un campo nuevo entra con default,
/// para que un archivo viejo se siga leyendo.</para>
///
/// <para><b>Un archivo roto, según qué sea.</b> El suelo, las llaves vendidas y los rechazos se pierden con un aviso: perderlos
/// es molesto, y un servidor que no arranca es peor. Los CLANES y el CORREO no: arrancar sin ellos haría que el primer
/// volcado escribiera el mundo vacío encima del archivo roto, y ahí se pierden para siempre. Con el error, el que hostea
/// se entera y restaura un backup.</para>
/// </summary>
public static class JsonDelMundo
{
    // ── Clanes ("guilds") ───────────────────────────────────────────────────────────────────────────────────────

    private sealed record GuildDto(string Name, string Leader, string Desc,
        List<string> Members, List<string> Enemies, List<string> Allies, List<string> Offers,
        string News = "", byte Alignment = 0, List<string>? JoinRequests = null,
        string Url = "", string Codex = "",   // web + códex que fija el líder
        // La solicitud de cada aspirante (nombre → texto). Va en un campo NUEVO y no en JoinRequests
        // para no romper los "guilds" ya persistidos: un blob viejo se sigue leyendo por la lista de
        // nombres, y los aspirantes quedan con la solicitud vacía en vez de perderse.
        Dictionary<string, string>? JoinRequestTexts = null,
        // El texto de cada propuesta de alianza recibida, por el mismo motivo que JoinRequestTexts.
        Dictionary<string, string>? OfferTexts = null,
        // LA PAZ PROPUESTA, LAS ELECCIONES Y LOS VOTOS. El original los guarda —las propuestas en
        // `-propositions.pro` (clsClan.cls:785-787), las elecciones en la ficha del clan y los votos en
        // `-votaciones.vot` (:591-611)—. Campos nuevos con default, como los de arriba: un blob viejo se sigue leyendo.
        Dictionary<string, string>? PeaceOfferTexts = null, bool ElectionsOpen = false,
        Dictionary<string, string>? Votes = null);

    public static string Clanes(IReadOnlyList<ClanGuardado> clanes) => JsonSerializer.Serialize(clanes.Select(c => new GuildDto(
        c.Nombre, c.Lider, c.Descripcion, c.Miembros.ToList(), c.Enemigos.ToList(), c.Aliados.ToList(),
        c.PropuestasDeAlianza.Select(p => p.Key).ToList(), c.Noticias, c.Alineacion, c.Aspirantes.Select(p => p.Key).ToList(),
        c.Url, c.Codex, Diccionario(c.Aspirantes), Diccionario(c.PropuestasDeAlianza), Diccionario(c.PropuestasDePaz),
        c.EleccionesAbiertas, Diccionario(c.Votos))).ToList());

    public static List<ClanGuardado> Clanes(string? json)
    {
        var clanes = new List<ClanGuardado>();
        var dtos = Leer<List<GuildDto>>(json, "guilds", tolerante: false);
        if (dtos is null) return clanes;
        foreach (var d in dtos)
        {
            clanes.Add(new ClanGuardado
            {
                Nombre = d.Name,
                Lider = d.Leader,
                Descripcion = d.Desc,
                Noticias = d.News,
                Alineacion = d.Alignment,
                Url = d.Url,
                Codex = d.Codex,
                EleccionesAbiertas = d.ElectionsOpen,
                Miembros = d.Members.ToImmutableArray(),
                Enemigos = d.Enemies.ToImmutableArray(),
                Aliados = d.Allies.ToImmutableArray(),
                PropuestasDeAlianza = d.Offers.Select(o => Par(o, d.OfferTexts?.GetValueOrDefault(o, "") ?? "")).ToImmutableArray(),
                Aspirantes = (d.JoinRequests ?? new()).Select(j => Par(j, d.JoinRequestTexts?.GetValueOrDefault(j, "") ?? ""))
                    .ToImmutableArray(),
                PropuestasDePaz = (d.PeaceOfferTexts ?? new()).ToImmutableArray(),
                Votos = (d.Votes ?? new()).ToImmutableArray(),
            });
        }
        return clanes;
    }

    // ── Rechazos de clan ("rechazos-de-clan"): personaje → motivo ──────────────────────────────────────────────

    public static string Rechazos(IReadOnlyList<KeyValuePair<string, string>> rechazos) => JsonSerializer.Serialize(Diccionario(rechazos));

    public static List<KeyValuePair<string, string>> Rechazos(string? json)
        => Leer<Dictionary<string, string>>(json, "rechazos-de-clan")?.ToList() ?? new();

    // ── Correo ("mailboxes"): destinatario → cartas ─────────────────────────────────────────────────────────────

    public static string Correo(IReadOnlyDictionary<string, IReadOnlyList<MailEntry>> casillas)
        => JsonSerializer.Serialize(casillas.Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => c.Value.ToList()));

    public static Dictionary<string, IReadOnlyList<MailEntry>> Correo(string? json)
        => Leer<Dictionary<string, List<MailEntry>>>(json, "mailboxes", tolerante: false)?
               .ToDictionary(c => c.Key, c => (IReadOnlyList<MailEntry>)c.Value, StringComparer.OrdinalIgnoreCase)
           ?? new(StringComparer.OrdinalIgnoreCase);

    // ── Suelo ("suelo") ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El mismo formato de <c>MapObjects.json</c> —<c>{"&lt;mapa&gt;": [[x,y,objIndex,amount], ...]}</c>— a
    /// propósito: son el mismo concepto (objetos colocados en una baldosa) y así los dos archivos se pueden mirar uno al
    /// lado del otro. El del .csm trae el decorado del mundo; éste, lo que dejó la gente.</summary>
    public static string Suelo(IReadOnlyList<ObjetoEnElSuelo> suelo)
    {
        var porMapa = new Dictionary<string, List<int[]>>();
        foreach (var o in suelo)
        {
            string k = o.Posicion.Map.ToString(CultureInfo.InvariantCulture);
            if (!porMapa.TryGetValue(k, out var lista)) porMapa[k] = lista = new List<int[]>();
            lista.Add(new[] { o.Posicion.X, o.Posicion.Y, (int)o.Objeto, (int)o.Cantidad });
        }
        return JsonSerializer.Serialize(porMapa);
    }

    public static List<ObjetoEnElSuelo> Suelo(string? json)
    {
        var suelo = new List<ObjetoEnElSuelo>();
        var porMapa = Leer<Dictionary<string, List<int[]>>>(json, "suelo");
        if (porMapa is null) return suelo;
        foreach (var (mapa, objs) in porMapa)
        {
            if (!short.TryParse(mapa, NumberStyles.Integer, CultureInfo.InvariantCulture, out short m) || m <= 0) continue;
            foreach (var o in objs)
            {
                // Una fila corta o con valores fuera de rango se saltea sola. El archivo lo escribe el server, así que esto
                // no debería pasar nunca — pero es texto en el disco del host y se puede editar.
                if (o.Length < 4) continue;
                if (o[0] is < 0 or > byte.MaxValue || o[1] is < 0 or > byte.MaxValue) continue;
                if (o[2] is <= 0 or > short.MaxValue || o[3] is <= 0 or > short.MaxValue) continue;
                suelo.Add(new ObjetoEnElSuelo(new WorldPos(m, (byte)o[0], (byte)o[1]), (short)o[2], (short)o[3]));
            }
        }
        return suelo;
    }

    // ── Llaves vendidas ("propiedades"): ficha del mercader → ranura → objeto ──────────────────────────────────

    public static string Llaves(IReadOnlyList<LlaveVendida> llaves) => JsonSerializer.Serialize(
        llaves.GroupBy(l => l.Mercader).ToDictionary(g => g.Key, g => g.ToDictionary(l => l.Ranura, l => l.Objeto)));

    public static List<LlaveVendida> Llaves(string? json)
        => Leer<Dictionary<int, Dictionary<int, short>>>(json, "propiedades")?
               .SelectMany(m => m.Value.Select(r => new LlaveVendida(m.Key, r.Key, r.Value))).ToList()
           ?? new();

    // ── Récord de conectados ("record") ─────────────────────────────────────────────────────────────────────────

    public static string Record(int record) => record.ToString(CultureInfo.InvariantCulture);

    public static int Record(string? texto)
        => int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0;

    // ── IPs baneadas ("ips-baneadas") ───────────────────────────────────────────────────────────────────────────

    public static string Ips(IReadOnlyList<string> ips) => JsonSerializer.Serialize(ips);

    public static List<string> Ips(string? json) => Leer<List<string>>(json, "ips-baneadas") ?? new();

    // ── Ayudantes ───────────────────────────────────────────────────────────────────────────────────────────────

    private static KeyValuePair<string, string> Par(string clave, string valor) => new(clave, valor);

    private static Dictionary<string, string> Diccionario(IEnumerable<KeyValuePair<string, string>> pares)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pares) d[k] = v;
        return d;
    }

    private static T? Leer<T>(string? json, string que, bool tolerante = true) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException ex) when (tolerante)
        {
            Console.Error.WriteLine($"[{que}] lo guardado no se pudo leer, se arranca sin eso: {ex.Message}");
            return null;
        }
    }
}
