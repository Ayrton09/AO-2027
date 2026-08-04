namespace CoverAO.Server.Game;

/// <summary>A guild/clan (modGuilds). Slice: name, founder/leader, description, and a member roster by
/// character name. Alliances/wars/elections/codex are future work.</summary>
public sealed class Guild
{
    public required string Name { get; init; }
    public string LeaderName { get; set; } = "";
    public string Description { get; set; } = "";
    public string News { get; set; } = "";

    /// <summary>Sitio web del clan (VB6 <c>guilds(Gi).SetURL</c>, modGuilds.bas:399). Se emite en GuildDetails;
    /// antes el handler leía la URL y la tiraba, y GuildDetails la mandaba hardcodeada en vacío.</summary>
    public string Url { get; set; } = "";

    /// <summary>Códex del clan: las reglas que fija el líder (VB6 <c>SetCodex</c>, modGuilds.bas:417). El
    /// original guarda hasta CANTIDADMAXIMACODEX líneas; acá va como un solo texto porque el paquete
    /// GuildDetails lo transporta así.</summary>
    public string Codex { get; set; } = "";
    /// <summary>Alignment chosen at fundation (1=Real/Armada, 2=Caos, 0=Neutral).</summary>
    public byte Alignment { get; set; }
    public HashSet<string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>La cola de aspirantes de <c>clsClan</c>: nombre del personaje → <b>texto de su solicitud</b>,
    /// esperando que el líder acepte o rechace (el AO real no admite solo: decide el líder).
    ///
    /// El texto importa y antes se tiraba: es el "Detalle" que guarda <c>NuevoAspirante(Nombre, Peticion)</c>
    /// en SOLICITUDES.dat y lo único que transporta el paquete <c>ShowUserRequest</c>, con el que el líder mira
    /// la solicitud antes de decidir (<c>a_DetallesAspirante</c> → <c>DetallesSolicitudAspirante</c>).</summary>
    public Dictionary<string, string> JoinRequests { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Guild names this clan is at war with (attacks between them don't incur the criminal flag).</summary>
    public HashSet<string> Enemies { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Guild names this clan is allied with.</summary>
    public HashSet<string> Allies { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Propuestas recibidas: nombre del clan que ofreció → TEXTO de su propuesta. El texto importa y antes se
    // tiraba (`r.ReadAsciiString(); // consumido para mantener sync`): es lo que guarda `GetPropuesta` y lo
    // único que transporta el paquete `OfferDetails`, con el que el líder LEE la propuesta antes de aceptarla
    // (modGuilds.bas r_VerPropuesta:1773).
    /// <summary>Clanes que nos ofrecieron ALIANZA, con el texto de su propuesta (pendientes de aceptación).</summary>
    public Dictionary<string, string> AllianceOffers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Clanes que nos ofrecieron PAZ, con el texto de su propuesta.</summary>
    public Dictionary<string, string> PeaceOffers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Whether leadership elections are currently open.</summary>
    public bool ElectionsOpen { get; set; }
    /// <summary>Current ballot: voter name → candidate name.</summary>
    public Dictionary<string, string> Votes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quién gana la elección de líder con los votos emitidos hasta ahora, o <c>null</c> si todavía
    /// no ganó nadie.
    ///
    /// La regla es <b>MAYORÍA ABSOLUTA de los miembros</b>, no "el más votado hasta ahora". La diferencia no es
    /// sutil: con "el más votado", el PRIMER voto de la elección instala líder al instante — un solo miembro se
    /// apodera del clan. Y como se recuenta después de cada voto, tampoco alcanza con "mayoría de los votos
    /// emitidos", que al principio es lo mismo que el primero que vota.
    ///
    /// Vive acá, y no dentro del handler, para poder testear el umbral: es una regla de una línea donde un
    /// <c>&gt;=</c> en vez de un <c>&gt;</c> entrega el clan con la mitad justa, y eso no lo ve ningún flow.</summary>
    public static string? GanadorDeLaEleccion(IEnumerable<string> votos, int cantidadDeMiembros)
    {
        var masVotado = votos.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(g => g.Count())
                             .FirstOrDefault();
        if (masVotado is null) return null;
        return masVotado.Count() > cantidadDeMiembros / 2 ? masVotado.Key : null;
    }
}

/// <summary>One piece of mail (correo): a message and an optional attached item, delivered to a
/// character's mailbox and handed over when they next log in.</summary>
/// <summary>Una carta del buzón (VB6 <c>tCorreo</c>). <paramref name="Leida"/> es el flag que el original
/// guarda por carta y que viaja en el paquete <c>CorreoList</c>: el cliente pinta distinto las no leídas.
/// Tiene default para que los buzones ya persistidos en JSON sigan cargando (ahí el campo no existe).</summary>
public sealed record MailEntry(string Sender, string Message, short ObjIndex, short Amount, byte Leida = 0);
