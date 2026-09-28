using System.Collections.Immutable;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// EL CLAN TAL COMO SE GUARDA: una foto inmutable del <see cref="Guild"/>, armada en el hilo del mundo y escrita en el de la
/// base (<see cref="ColaDeLaBase"/>), como la <see cref="FichaDePersonaje"/> del personaje.
///
/// <para>Las listas conservan el ORDEN del juego —el de los aspirantes es el que ve el líder— y los pares van como pares y
/// no como diccionario, que no lo garantiza. Todo va por NOMBRE, como en el juego: el líder, los miembros, los clanes
/// aliados, los votos.</para>
///
/// <para><b>La membresía está en dos lados, como en el original</b>: acá (la lista del clan, el <c>MEMBERSFILE</c> del
/// VB6) y en el personaje (<see cref="FichaDePersonaje.Clan"/>, su <c>GUILDINDEX</c>). El juego mantiene los dos iguales,
/// también con el desconectado (clsClan.cls:299 y :343).</para>
/// </summary>
public sealed record ClanGuardado
{
    public required string Nombre { get; init; }
    public string Lider { get; init; } = "";
    public string Descripcion { get; init; } = "";
    public string Noticias { get; init; } = "";
    public byte Alineacion { get; init; }
    public string Url { get; init; } = "";
    public string Codex { get; init; } = "";
    public bool EleccionesAbiertas { get; init; }
    public ImmutableArray<string> Miembros { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableArray<string> Aliados { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableArray<string> Enemigos { get; init; } = ImmutableArray<string>.Empty;
    /// <summary>Las propuestas de alianza RECIBIDAS: el clan que la manda y su texto.</summary>
    public ImmutableArray<KeyValuePair<string, string>> PropuestasDeAlianza { get; init; } = ImmutableArray<KeyValuePair<string, string>>.Empty;
    /// <summary>Las propuestas de paz recibidas: el clan que la manda y su texto.</summary>
    public ImmutableArray<KeyValuePair<string, string>> PropuestasDePaz { get; init; } = ImmutableArray<KeyValuePair<string, string>>.Empty;
    /// <summary>Los aspirantes, en el orden en que pidieron entrar, con su solicitud.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Aspirantes { get; init; } = ImmutableArray<KeyValuePair<string, string>>.Empty;
    /// <summary>Los votos de la elección abierta: votante y candidato.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Votos { get; init; } = ImmutableArray<KeyValuePair<string, string>>.Empty;

    /// <summary>La foto de un clan vivo. En el hilo del mundo: las colecciones del <see cref="Guild"/> cambian ahí.</summary>
    public static ClanGuardado Desde(Guild g) => new()
    {
        Nombre = g.Name,
        Lider = g.LeaderName,
        Descripcion = g.Description,
        Noticias = g.News,
        Alineacion = g.Alignment,
        Url = g.Url,
        Codex = g.Codex,
        EleccionesAbiertas = g.ElectionsOpen,
        Miembros = g.Members.ToImmutableArray(),
        Aliados = g.Allies.ToImmutableArray(),
        Enemigos = g.Enemies.ToImmutableArray(),
        PropuestasDeAlianza = g.AllianceOffers.ToImmutableArray(),
        PropuestasDePaz = g.PeaceOffers.ToImmutableArray(),
        Aspirantes = g.JoinRequests.ToImmutableArray(),
        Votos = g.Votes.ToImmutableArray(),
    };

    /// <summary>El <see cref="Guild"/> de esta foto, para cargar el mundo.</summary>
    public Guild CrearGuild()
    {
        var g = new Guild
        {
            Name = Nombre, LeaderName = Lider, Description = Descripcion, News = Noticias, Alignment = Alineacion,
            Url = Url, Codex = Codex, ElectionsOpen = EleccionesAbiertas,
        };
        foreach (var m in Miembros) g.Members.Add(m);
        foreach (var a in Aliados) g.Allies.Add(a);
        foreach (var e in Enemigos) g.Enemies.Add(e);
        foreach (var (clan, texto) in PropuestasDeAlianza) g.AllianceOffers[clan] = texto;
        foreach (var (clan, texto) in PropuestasDePaz) g.PeaceOffers[clan] = texto;
        foreach (var (nombre, texto) in Aspirantes) g.JoinRequests[nombre] = texto;
        foreach (var (votante, candidato) in Votos) g.Votes[votante] = candidato;
        return g;
    }
}

/// <summary>Un objeto que dejó un jugador en el suelo (regla de la casa <c>PersistirSuelo</c>).</summary>
public readonly record struct ObjetoEnElSuelo(WorldPos Posicion, short Objeto, short Cantidad);

/// <summary>Una llave de casa ya vendida (<see cref="World.LlavesVendidas"/>): la ficha del mercader, su ranura y la llave.</summary>
public readonly record struct LlaveVendida(int Mercader, int Ranura, short Objeto);
