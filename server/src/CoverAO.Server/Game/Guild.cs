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
    /// <summary>La alineación del clan, que NO la elige el fundador sino su FACCIÓN (VB6
    /// <c>ALINEACION_GUILD</c>, modGuilds.bas:36-41): <b>1 = Republicano, 2 = Imperial, 3 = Caótico,
    /// 4 = Renegado</b>. El 0 no lo produce ninguna fundación — el paso 1 rechaza al que no cae en ninguna de
    /// las cuatro— y sólo aparece en un clan viejo guardado antes de que esto existiera.</summary>
    public byte Alignment { get; set; }

    /// <summary>El nombre de la alineación, como lo escribe el original (VB6 <c>Alineacion2String</c>,
    /// modGuilds.bas:827). Es lo que viaja en <c>GuildDetails</c> y lo que el jugador lee en la ficha del clan.
    ///
    /// <para>Acá decía <c>1 → "Real"</c>, <c>2 → "Caos"</c> y todo lo demás <c>"Neutral"</c>, o sea que las
    /// CUATRO alineaciones se mostraban mal: un clan Republicano decía "Real" y uno Imperial decía "Caos".
    /// Venía de una numeración inventada que después se corrigió en la fundación —donde ya usa la del
    /// original— y el texto se quedó atrás.</para>
    ///
    /// <para>El valor desconocido devuelve la cadena VACÍA, y eso también es del original: su
    /// <c>Select Case</c> no tiene <c>Case Else</c>, así que la función sale sin asignar.</para></summary>
    public static string NombreDeAlineacion(byte alineacion) => alineacion switch
    {
        1 => "Republicano",
        2 => "Imperial",
        3 => "Caótico",
        4 => "Renegado",
        _ => "",
    };
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
