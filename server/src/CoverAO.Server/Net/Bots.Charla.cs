using System.Globalization;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Game;
using Como = CoverAO.Server.Net.SentidosDelBot.ComoLoDijo;

namespace CoverAO.Server.Net;

/// <summary>
/// LA CHARLA CON LA GENTE (pedido del autor, 19/10: que los bots hablen con los jugadores). El bot oye lo que le dicen
/// (<see cref="SentidosDelBot.TomarLoOido"/>) y contesta con frases armadas según el tema (<see cref="DeQueLeHablan"/>).
/// El susurro se responde por susurro y la charla se responde en la charla.
///
/// <para>Las consultas concretas se contestan con el estado actual del bot. Los saludos y las frases ambiguas se sortean.
/// Las respuestas tienen demora de escritura y cupo por minuto. Los insultos quedan sin respuesta.</para>
///
/// <para><b>Sin ida y vuelta entre bots</b>: un bot nunca habla primero (lo único que dice sin que le hablen es la despedida a la
/// persona que lo invitó a cazar, ver <see cref="DejarDeAcompanar"/>), y lo que contesta no nombra a nadie, así que la respuesta de
/// uno no le habla a otro.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>De qué le hablan. El orden de <see cref="Temas"/> decide cuando el texto toca dos.</summary>
    public enum DeQueHabla { Saludo, Despedida, QueHace, Nivel, Pide, Comercio, Grupo, EsBot, Gracias, Insulto, Otro, Ubicacion, Clase, Meta }

    /// <summary>Cuántas veces de cada cien contesta un susurro, una charla que lo nombra y lo que no entiende.</summary>
    public const int ContestaSusurro = 85, ContestaCharla = 70, ContestaLoQueNoEntiende = 40;

    /// <summary>Al que le sigue hablando le contesta una vez cada tanto, y en total no más que esto por minuto.</summary>
    public const int EntreRespuestasAlMismoMs = 20_000, RespuestasPorMinuto = 4;
    private const int EntreConsultasDistintasMs = 2_000;

    /// <summary>
    /// LAS PALABRAS DE CADA TEMA, ya sin mayúsculas, tildes ni signos (<see cref="Normalizar"/>). Una palabra suelta tiene que estar
    /// entera en el texto; una con espacio, también ("que haces"). El primer tema que aparece gana: "dame oro y hola" es un pedido, y
    /// el insulto va primero para que "sos un bot forro" no se conteste.
    /// </summary>
    public static readonly (DeQueHabla Tema, string[] Palabras)[] Temas =
    {
        (DeQueHabla.Insulto, new[] { "puto", "puta", "forro", "pelotudo", "hdp", "conchudo", "mogolico", "manco", "noob", "basura",
                                     "idiota", "imbecil", "estupido", "mierda", "sorete", "pajero", "tarado" }),
        (DeQueHabla.EsBot, new[] { "bot", "bots", "macro", "macreando", "automatico", "sos real", "sos humano", "sos una persona",
                                   "sos persona", "robot", "sos ia", "una ia" }),
        (DeQueHabla.Pide, new[] { "dame", "regala", "regalame", "regalas", "prestame", "presta", "prestas", "tirame", "pasame",
                                  "dona", "donas", "donar", "limosna" }),
        (DeQueHabla.Comercio, new[] { "vendo", "vendes", "vende", "venta", "compro", "compras", "compra", "cambio", "cambias",
                                      "precio", "cuanto sale", "comerciar", "trade", "tradeo" }),
        (DeQueHabla.Ubicacion, new[] { "donde estas", "donde andas", "donde te encuentro", "en que mapa", "que mapa" }),
        (DeQueHabla.Clase, new[] { "que clase", "que oficio" }),
        (DeQueHabla.Meta, new[] { "tu meta", "tu objetivo", "que buscas" }),
        (DeQueHabla.Gracias, new[] { "gracias", "grax", "graciass", "thx", "ty" }),
        (DeQueHabla.Despedida, new[] { "chau", "chao", "adios", "nos vemos", "me voy", "hasta luego", "bye" }),
        (DeQueHabla.Grupo, new[] { "vamos", "party", "grupo", "acompaname", "acompanas", "ayuda", "ayudame", "ayudas", "juntos",
                                   "cazamos", "cacemos", "venis", "veni", "sumate", "con quien estas" }),
        (DeQueHabla.Nivel, new[] { "nivel", "lvl", "lv", "nv", "level" }),
        (DeQueHabla.QueHace, new[] { "que haces", "que hace", "que estas haciendo", "en que andas", "haciendo",
                                     "que andas haciendo", "en que estas" }),
        (DeQueHabla.Saludo, new[] { "hola", "holaa", "holis", "buenas", "buen dia", "buenos dias", "hey", "ey", "que tal",
                                    "como va", "como andas", "que onda", "alo", "wenas" }),
    };

    /// <summary>LAS FRASES de cada tema, cortas y en minúscula, como escribe cualquiera en el juego. <c>{nivel}</c> es su nivel. Ninguna
    /// nombra a nadie (ver la nota de la clase).</summary>
    public static readonly IReadOnlyDictionary<DeQueHabla, string[]> Frases = new Dictionary<DeQueHabla, string[]>
    {
        [DeQueHabla.Saludo] = new[] { "hola", "buenas", "ey que tal", "hola, todo bien?", "holaa", "que onda" },
        [DeQueHabla.Despedida] = new[] { "chau", "nos vemos", "suerte", "abrazo", "chau suerte" },
        [DeQueHabla.QueHace] = new[] { "cazando", "aca subiendo un poco", "cazando un rato", "juntando oro", "subiendo nivel, vos?" },
        [DeQueHabla.Nivel] = new[] { "{nivel}", "soy {nivel}", "nivel {nivel}", "{nivel} y vos?" },
        [DeQueHabla.Pide] = new[] { "no tengo nada", "estoy pobre jaja", "nah, junta vos", "no tengo ni para pociones" },
        [DeQueHabla.Comercio] = new[] { "no vendo nada", "no, no tengo nada para vender", "no compro, gracias", "nah, nada" },
        [DeQueHabla.Grupo] = new[] { "no, voy solo", "estoy bien solo, gracias", "otro dia", "ahora no" },
        [DeQueHabla.EsBot] = new[] { "jaja", "que?", "?", "estoy cazando che" },
        [DeQueHabla.Gracias] = new[] { "de nada", "nada", "dale" },
        [DeQueHabla.Otro] = new[] { "?", "que?", "no entendi", "jaja" },
    };

    /// <summary>Sin mayúsculas ni tildes, con signos como espacios y abreviaturas de chat expandidas por palabra.</summary>
    public static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length + 2).Append(' ');
        foreach (char c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char m = char.ToLowerInvariant(c);
            sb.Append(char.IsLetterOrDigit(m) ? m : ' ');
        }
        string s = sb.Append(' ').ToString();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        // Las mismas abreviaturas sirven para clasificar el tema y distinguir una consulta de una invitación.
        return s.Replace(" q ", " que ", StringComparison.Ordinal)
                .Replace(" dnd ", " donde ", StringComparison.Ordinal)
                .Replace(" tas ", " estas ", StringComparison.Ordinal)
                .Replace(" tnes ", " tenes ", StringComparison.Ordinal)
                .Replace(" vamo ", " vamos ", StringComparison.Ordinal);
    }

    /// <summary>¿De qué le hablan? El primer tema de <see cref="Temas"/> con una de sus palabras entera en el texto; si ninguno,
    /// <see cref="DeQueHabla.Otro"/>.</summary>
    public static DeQueHabla DeQueLeHablan(string texto)
    {
        string t = Normalizar(texto);
        foreach (var (tema, palabras) in Temas)
            if (Toca(t, palabras)) return tema;
        return DeQueHabla.Otro;
    }

    /// <summary>¿Se despide? Aunque el texto toque antes otro tema: "chau, gracias" es un agradecimiento para contestar, pero
    /// también una despedida para dejar de acompañar (<see cref="SeDespidio"/>).</summary>
    public static bool SeDespide(string texto) =>
        Toca(Normalizar(texto), Temas.First(t => t.Tema == DeQueHabla.Despedida).Palabras);

    private static bool Toca(string normalizado, string[] palabras) =>
        palabras.Any(p => normalizado.Contains(' ' + p + ' ', StringComparison.Ordinal));

    /// <summary>Lo que hace de un mensaje del tema <see cref="DeQueHabla.Grupo"/> una invitación a cazar juntos.</summary>
    private static readonly string[] Invitan =
        { "vamos", "cazamos", "cacemos", "veni a cazar", "venis a cazar", "acompaname", "acompanas", "sumate", "hace party", "hacemos party", "armamos party",
          "queres party", "sale party", "hacemos grupo", "armamos grupo", "queres grupo", "sale grupo" };

    private static readonly string[] PreguntanPorElGrupo =
        { "estas en grupo", "estas en un grupo", "tenes grupo", "estas en party", "tenes party", "con quien estas" };

    private static readonly string[] PreguntanPorElPlan =
        { "a donde vamos", "adonde vamos", "donde vamos", "cuando vamos", "que es party", "que es un grupo" };

    /// <summary>
    /// ¿LO INVITA A CAZAR? No todo lo del tema <see cref="DeQueHabla.Grupo"/> es una invitación: "ayuda con mi pj?" o "venis seguido?"
    /// se contestan que no, y con un "no" adentro ("no quiero grupo") tampoco. Sin esto aceptaba cualquiera de esos y se iba detrás.
    /// </summary>
    public static bool EsInvitacion(string texto, string? nombre = null)
    {
        string t = Normalizar(texto);
        if (nombre is not null)
        {
            string destinatario = Normalizar(nombre).Trim();
            string corta = t.Trim();
            if (corta.StartsWith(destinatario + " ", StringComparison.Ordinal)) corta = corta[(destinatario.Length + 1)..];
            else if (corta.EndsWith(" " + destinatario, StringComparison.Ordinal)) corta = corta[..^(destinatario.Length + 1)];
            if (corta is "party" or "grupo") return true;
        }
        if (t is " party " or " grupo ") return true;
        return Toca(t, Invitan) && !t.Contains(" no ", StringComparison.Ordinal)
            && !Toca(t, PreguntanPorElGrupo) && !Toca(t, PreguntanPorElPlan) && !SeDespide(texto);
    }

    /// <summary>Lo oído hace más que esto no se contesta: entre dos vueltas largas (un viaje, una tienda) se juntaban mensajes de
    /// minutos atrás y el bot saludaba a destiempo.</summary>
    public const int OidoViejoMs = 30_000;

    public static bool EsViejo(long ahora, long cuando) => ahora - cuando > OidoViejoMs;

    /// <summary>Una frase fija del tema, o null si el tema no tiene una. <paramref name="sorteo"/> elige la frase.</summary>
    public static string? QueLeContesta(DeQueHabla tema, int nivel, int sorteo) =>
        Frases.TryGetValue(tema, out var frases) ? frases[sorteo % frases.Length].Replace("{nivel}", nivel.ToString()) : null;

    /// <summary>Cuánto tarda en contestar: un poco en leer y lo que se tarda en escribir la frase, hasta ocho segundos.</summary>
    public static int DemoraMs(string frase, int sorteo) => Math.Min(8_000, 1_500 + sorteo % 1_500 + frase.Length * 180);

    /// <summary>
    /// ¿ES PARA ÉL? El susurro siempre: sólo le llega al que se lo mandan. La charla, si lo nombra (la palabra entera, sin importar
    /// mayúsculas): es lo que hace cualquiera para hablarle a alguien que tiene al lado. El grito y el global son para todos.
    /// </summary>
    public static bool LeHablanAEl(SentidosDelBot.LoOido oido, string nombre) => oido.Como switch
    {
        Como.Susurro => true,
        Como.Charla => LoNombra(oido.Texto, nombre),
        _ => false,
    };

    /// <summary>
    /// La línea del log para lo que le dijeron: el texto del jugador SANEADO (<see cref="PacketDispatcher.ParaElLog"/>: sin saltos de
    /// línea ni caracteres de control que inventen líneas en la consola) y cortado a <see cref="LargoEnElLog"/>, que el susurro puede
    /// traer mucho más que los 80 de la charla.
    /// </summary>
    public static string LineaDelLog(string bot, SentidosDelBot.LoOido oido)
    {
        string texto = oido.Texto.Length > LargoEnElLog ? oido.Texto[..LargoEnElLog] + "..." : oido.Texto;
        return $"[bot {bot}] {oido.Como} de {PacketDispatcher.ParaElLog(oido.Quien)}: {PacketDispatcher.ParaElLog(texto)}";
    }

    public const int LargoEnElLog = 80;

    /// <summary>¿El texto tiene el nombre como palabra entera? "hola Bruma" y "bruma, vení" sí; "Brumas" no.</summary>
    public static bool LoNombra(string texto, string nombre)
    {
        if (nombre.Length == 0) return false;
        for (int i = texto.IndexOf(nombre, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = texto.IndexOf(nombre, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            bool antes = i == 0 || !char.IsLetterOrDigit(texto[i - 1]);
            int fin = i + nombre.Length;
            bool despues = fin == texto.Length || !char.IsLetterOrDigit(texto[fin]);
            if (antes && despues) return true;
        }
        return false;
    }

    /// <summary>
    /// ¿LE CONTESTA? <paramref name="sorteo"/> de 0 a 99 contra la probabilidad del modo (y la de lo que no entiende), al mismo no
    /// antes de <see cref="EntreRespuestasAlMismoMs"/> y no más de <see cref="RespuestasPorMinuto"/> en el último minuto.
    /// </summary>
    public static bool LeContesta(Como como, DeQueHabla tema, int sorteo, long ahora, long? ultimaAlMismo, int enElUltimoMinuto)
    {
        if (tema == DeQueHabla.Insulto) return false;
        if (ultimaAlMismo is { } antes && ahora - antes < EntreRespuestasAlMismoMs) return false;
        if (enElUltimoMinuto >= RespuestasPorMinuto) return false;
        if (EsConsulta(tema)) return true;
        int prob = como == Como.Susurro ? ContestaSusurro : ContestaCharla;
        if (tema == DeQueHabla.Otro) prob = Math.Min(prob, ContestaLoQueNoEntiende);
        return sorteo < prob;
    }

    private static bool EsConsulta(DeQueHabla tema) =>
        tema is DeQueHabla.Nivel or DeQueHabla.Ubicacion or DeQueHabla.Clase or DeQueHabla.QueHace or DeQueHabla.Meta;

    // La confirmación pertenece a una salida a cazar. Una respuesta local pertenece al mapa donde se la pidieron.
    private readonly record struct Respuesta(long Cuando, long PedidaEn, Como Por, string A, string Texto, long? Acompanamiento,
        int CambioDeMapa, DeQueHabla Tema, int Sorteo);

    private readonly List<Respuesta> _porContestar = new();
    private readonly Queue<(string A, long Cuando, DeQueHabla Tema)> _respuestasRecientes = new();

    private string? RespuestaSegunElEstado(SentidosDelBot sentidos, DeQueHabla tema, int sorteo)
    {
        switch (tema)
        {
            case DeQueHabla.Ubicacion:
                short mapa = sentidos.Mapa;
                if (mapa <= 0) return null;
                string nombre = PacketDispatcher.ParaElLog(_entorno.Datos.MapName(mapa)).Trim();
                return nombre.Length is > 0 and <= 50 ? $"ando en {nombre} (mapa {mapa})" : $"ando en el mapa {mapa}";
            case DeQueHabla.Clase:
                return $"soy de clase {GameLimits.NombreDeClase(_clase).ToLowerInvariant()}";
            case DeQueHabla.Meta:
                return ContarSuMeta() ?? "por ahora sigo mejorando el personaje";
            case DeQueHabla.QueHace:
                if (!sentidos.Vivo) return "estoy buscando donde revivir";
                if (sentidos.Meditando) return "estoy meditando un toque";
                if (_acompana is not null) return "ando acompanando a alguien";
                return _clase switch
                {
                    ClaseMinero => "me dedico a sacar mineral y fundirlo",
                    ClaseHerrero => "me dedico a fabricar equipo en la fragua",
                    _ when sentidos.Nivel >= Leveling.MaxLevel => "ando juntando oro un rato",
                    _ => QueLeContesta(tema, sentidos.Nivel, sorteo),
                };
            case DeQueHabla.Comercio when EsFabricanteMetalurgico:
                return _clase == ClaseMinero
                    ? "cuando tengo lingotes de sobra los publico en subastas"
                    : "cuando me sobra equipo de la fragua lo publico en subastas";
            default:
                return QueLeContesta(tema, sentidos.Nivel, sorteo);
        }
    }

    /// <summary>
    /// LO QUE LE DICEN Y LO QUE CONTESTA. Se llama seguido desde los bucles del bot (el ciclo, la caza, cada paso, las esperas y la
    /// meditación), porque el canal hacia el servidor tiene un solo escritor (<see cref="TransporteDeBot"/>): la respuesta no puede
    /// salir de un temporizador aparte. Lo oído que es para él va al log (<see cref="LineaDelLog"/>) y, si le contesta, queda en
    /// espera hasta su <see cref="DemoraMs"/>; lo que ya venció se manda.
    /// </summary>
    private void Charlar(TransporteDeBot transporte, SentidosDelBot sentidos) =>
        AtenderCharla(transporte, sentidos, Environment.TickCount64);

    internal void AtenderCharla(TransporteDeBot transporte, SentidosDelBot sentidos, long ahora)
    {
        while (_respuestasRecientes.TryPeek(out var vieja) && ahora - vieja.Cuando >= 60_000)
            _respuestasRecientes.Dequeue();

        bool SigueVigente(Respuesta r) => ahora - r.Cuando <= OidoViejoMs
            && (r.Por != Como.Charla || (r.CambioDeMapa == sentidos.NumeroDeCambiosDeMapa && sentidos.PersonaLlamada(r.A) is not null))
            && (r.Acompanamiento is not { } salida || (_acompana is { } actual && salida == _numeroDeAcompanamiento
                && string.Equals(actual.Quien, r.A, StringComparison.OrdinalIgnoreCase)));

        foreach (var oido in sentidos.TomarLoOido())
        {
            if (EsViejo(ahora, oido.Cuando)) continue;
            if (oido.Como == Como.Charla && oido.CambioDeMapa != sentidos.NumeroDeCambiosDeMapa) continue;
            bool dirigido = LeHablanAEl(oido, Nombre);
            if (AtenderOrdenDeCompania(oido, dirigido)) continue;
            if (!dirigido) continue;
            // Una respuesta pendiente reserva lugar hasta que se envía o pierde vigencia.
            _porContestar.RemoveAll(r => !SigueVigente(r));
            Console.WriteLine(LineaDelLog(Nombre, oido));
            var tema = DeQueLeHablan(oido.Texto);
            // LA QUE ACOMPAÑA Y SE DESPIDE: ver `SeDespidio`.
            if (SeDespide(oido.Texto))
            {
                SeDespidio(oido.Quien);
                if (_acompana is null) sentidos.ObservarSalidaDe(null);
            }
            if (oido.Como == Como.Charla && sentidos.PersonaLlamada(oido.Quien) is null) continue;
            // Una consulta distinta puede seguir a un saludo. Repetir la misma consulta conserva el intervalo largo.
            bool Bloquea(string a, long cuando, DeQueHabla anterior) => string.Equals(a, oido.Quien, StringComparison.OrdinalIgnoreCase)
                && ahora - cuando < (EsConsulta(tema) && anterior != tema ? EntreConsultasDistintasMs : EntreRespuestasAlMismoMs);
            bool bloqueada = _respuestasRecientes.Any(r => Bloquea(r.A, r.Cuando, r.Tema))
                || _porContestar.Any(r => Bloquea(r.A, r.PedidaEn, r.Tema));
            if (bloqueada || !LeContesta(oido.Como, tema, _rng.Next(100), ahora, null, _respuestasRecientes.Count + _porContestar.Count))
                continue;
            // Y LA INVITACIÓN A CAZAR JUNTOS: ver `ContestarLaInvitacion` (Bots.Acompanar.cs).
            int sorteo = tema == DeQueHabla.Grupo ? 0 : _rng.Next(1_000);
            var (frase, acompanamiento) = tema == DeQueHabla.Grupo
                ? ResolverLaInvitacion(sentidos, oido.Quien, oido.Texto, ahora)
                : (RespuestaSegunElEstado(sentidos, tema, sorteo), (long?)null);
            if (frase is null) continue;
            long cuando = ahora + DemoraMs(frase, _rng.Next(1_000));
            foreach (var anterior in _porContestar)
                if (string.Equals(anterior.A, oido.Quien, StringComparison.OrdinalIgnoreCase))
                    cuando = Math.Max(cuando, anterior.Cuando + 1_000);
            _porContestar.Add(new Respuesta(cuando, ahora, oido.Como, oido.Quien, frase, acompanamiento, oido.CambioDeMapa, tema, sorteo));
        }
        for (int i = 0; i < _porContestar.Count;)
        {
            var r = _porContestar[i];
            bool vigente = SigueVigente(r);
            if (vigente && r.Cuando > ahora) { i++; continue; }
            _porContestar.RemoveAt(i);
            if (!vigente) continue;
            string? texto = EsConsulta(r.Tema) ? RespuestaSegunElEstado(sentidos, r.Tema, r.Sorteo) : r.Texto;
            if (texto is null) continue;
            bool salio = r.Por == Como.Susurro
                ? transporte.Mandar(Paquete(ClientPacketId.Whisper, w => w.WriteAsciiString(r.A).WriteAsciiString(texto)))
                : transporte.Mandar(Paquete(ClientPacketId.Talk, w => w.WriteAsciiString(texto).WriteByte((byte)Como.Charla)));
            if (salio)
            {
                _respuestasRecientes.Enqueue((r.A, ahora, r.Tema));
                Console.WriteLine($"[bot {Nombre}] le contesta a {PacketDispatcher.ParaElLog(r.A)}: {texto}");
            }
        }
    }

    /// <summary>El transporte de la conexión de ahora, para <see cref="Charlar"/> desde las esperas que no lo reciben.</summary>
    private TransporteDeBot? _transporte;

    /// <summary>Al conectarse de nuevo: lo que iba a contestar en la conexión anterior ya no se contesta.</summary>
    private void OlvidarLaCharla()
    {
        _porContestar.Clear();
        // Y LA PERSONA QUE ACOMPAÑABA: la vida nueva (dormir, reconectar) arranca sola, sin despedirse fuera de tiempo.
        _acompana = null;
        _esperaAcompanando = false;
    }
}
