using System.Globalization;
using System.Text;
using CoverAO.Protocol;
using Como = CoverAO.Server.Net.SentidosDelBot.ComoLoDijo;

namespace CoverAO.Server.Net;

/// <summary>
/// LA CHARLA CON LA GENTE (pedido del autor, 19/10: que los bots hablen con los jugadores). El bot oye lo que le dicen
/// (<see cref="SentidosDelBot.TomarLoOido"/>) y contesta con FRASES ARMADAS (decisión del autor, 20/10): según de qué le hablan
/// (<see cref="DeQueLeHablan"/>) elige una de las frases de ese tema (<see cref="Frases"/>), y la manda por donde le llegó: el susurro
/// con un susurro y la charla con la charla.
///
/// <para><b>Como una persona</b>: no contesta todo (<see cref="ContestaSusurro"/>, <see cref="ContestaCharla"/>), tarda lo que se
/// tarda en escribir (<see cref="DemoraMs"/>), al que le sigue hablando le contesta cada tanto (<see cref="EntreRespuestasAlMismoMs"/>)
/// y no más de <see cref="RespuestasPorMinuto"/> por minuto. No contesta los insultos, y a "sos bot?" le contesta con evasivas: no dice
/// que es una persona.</para>
///
/// <para><b>Sin ida y vuelta entre bots</b>: un bot nunca habla primero (lo único que dice sin que le hablen es la despedida a la
/// persona que lo invitó a cazar, ver <see cref="DejarDeAcompanar"/>), y lo que contesta no nombra a nadie, así que la respuesta de
/// uno no le habla a otro.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>De qué le hablan. El orden de <see cref="Temas"/> decide cuando el texto toca dos.</summary>
    public enum DeQueHabla { Saludo, Despedida, QueHace, Nivel, Pide, Comercio, Grupo, EsBot, Gracias, Insulto, Otro }

    /// <summary>Cuántas veces de cada cien contesta un susurro, una charla que lo nombra y lo que no entiende.</summary>
    public const int ContestaSusurro = 85, ContestaCharla = 70, ContestaLoQueNoEntiende = 40;

    /// <summary>Al que le sigue hablando le contesta una vez cada tanto, y en total no más que esto por minuto.</summary>
    public const int EntreRespuestasAlMismoMs = 20_000, RespuestasPorMinuto = 4;

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
        (DeQueHabla.Grupo, new[] { "vamos", "party", "grupo", "acompaname", "acompanas", "ayuda", "ayudame", "ayudas", "juntos",
                                   "cazamos", "venis", "veni", "sumate" }),
        (DeQueHabla.Nivel, new[] { "nivel", "lvl", "lv", "nv", "level" }),
        (DeQueHabla.QueHace, new[] { "que haces", "q haces", "que hace", "que estas haciendo", "en que andas", "haciendo" }),
        (DeQueHabla.Gracias, new[] { "gracias", "grax", "graciass", "thx", "ty" }),
        (DeQueHabla.Despedida, new[] { "chau", "chao", "adios", "nos vemos", "me voy", "hasta luego", "bye" }),
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

    /// <summary>Sin mayúsculas, sin tildes y con los signos cambiados por espacios: "¿Qué hacés?" queda " que haces ".</summary>
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
        return s;
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
        { "vamos", "cazamos", "cazar", "cazemos", "party", "grupo", "acompaname", "acompanas", "sumate", "juntos" };

    /// <summary>
    /// ¿LO INVITA A CAZAR? No todo lo del tema <see cref="DeQueHabla.Grupo"/> es una invitación: "ayuda con mi pj?" o "venis seguido?"
    /// se contestan que no, y con un "no" adentro ("no quiero grupo") tampoco. Sin esto aceptaba cualquiera de esos y se iba detrás.
    /// </summary>
    public static bool EsInvitacion(string texto)
    {
        string t = Normalizar(texto);
        return Toca(t, Invitan) && !t.Contains(" no ", StringComparison.Ordinal);
    }

    /// <summary>Lo oído hace más que esto no se contesta: entre dos vueltas largas (un viaje, una tienda) se juntaban mensajes de
    /// minutos atrás y el bot saludaba a destiempo.</summary>
    public const int OidoViejoMs = 30_000;

    public static bool EsViejo(long ahora, long cuando) => ahora - cuando > OidoViejoMs;

    /// <summary>La frase que contesta, o null si a eso no se contesta (el insulto). <paramref name="sorteo"/> elige entre las del
    /// tema (cualquier número no negativo).</summary>
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
        int prob = como == Como.Susurro ? ContestaSusurro : ContestaCharla;
        if (tema == DeQueHabla.Otro) prob = Math.Min(prob, ContestaLoQueNoEntiende);
        return sorteo < prob;
    }

    // La confirmación pertenece a UNA salida a cazar: si terminó antes de enviarla, no promete seguir a nadie.
    // Las respuestas normales no tienen acompañamiento y conservan su entrega y sus cupos.
    private readonly record struct Respuesta(long Cuando, Como Por, string A, string Texto, long? Acompanamiento = null);

    private readonly List<Respuesta> _porContestar = new();
    private readonly Dictionary<string, long> _ultimaRespuestaA = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<long> _respuestasRecientes = new();

    /// <summary>
    /// LO QUE LE DICEN Y LO QUE CONTESTA. Se llama seguido desde los bucles del bot (el ciclo, la caza, cada paso, las esperas y la
    /// meditación), porque el canal hacia el servidor tiene un solo escritor (<see cref="TransporteDeBot"/>): la respuesta no puede
    /// salir de un temporizador aparte. Lo oído que es para él va al log (<see cref="LineaDelLog"/>) y, si le contesta, queda en
    /// espera hasta su <see cref="DemoraMs"/>; lo que ya venció se manda.
    /// </summary>
    private void Charlar(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        long ahora = Environment.TickCount64;
        foreach (var oido in sentidos.TomarLoOido())
        {
            if (!LeHablanAEl(oido, Nombre)) continue;
            if (EsViejo(ahora, oido.Cuando)) continue;
            Console.WriteLine(LineaDelLog(Nombre, oido));
            while (_respuestasRecientes.Count > 0 && ahora - _respuestasRecientes.Peek() > 60_000) _respuestasRecientes.Dequeue();
            var tema = DeQueLeHablan(oido.Texto);
            // LA QUE ACOMPAÑA Y SE DESPIDE: ver `SeDespidio`.
            if (SeDespide(oido.Texto))
            {
                SeDespidio(oido.Quien);
                if (_acompana is null) sentidos.ObservarSalidaDe(null);
            }
            if (!LeContesta(oido.Como, tema, _rng.Next(100), ahora,
                            _ultimaRespuestaA.TryGetValue(oido.Quien, out long antes) ? antes : null, _respuestasRecientes.Count)) continue;
            // Y LA INVITACIÓN A CAZAR JUNTOS: ver `ContestarLaInvitacion` (Bots.Acompanar.cs).
            var (frase, acompanamiento) = tema == DeQueHabla.Grupo
                ? ResolverLaInvitacion(sentidos, oido.Quien, oido.Texto, ahora)
                : (QueLeContesta(tema, sentidos.Nivel, _rng.Next(1_000)), (long?)null);
            if (frase is null) continue;
            _ultimaRespuestaA[oido.Quien] = ahora;
            _respuestasRecientes.Enqueue(ahora);
            _porContestar.Add(new Respuesta(ahora + DemoraMs(frase, _rng.Next(1_000)), oido.Como, oido.Quien, frase, acompanamiento));
        }
        for (int i = _porContestar.Count - 1; i >= 0; i--)
        {
            var r = _porContestar[i];
            if (r.Cuando > ahora) continue;
            _porContestar.RemoveAt(i);
            if (r.Acompanamiento is { } salida && (_acompana is not { } actual || salida != _numeroDeAcompanamiento
                || !string.Equals(actual.Quien, r.A, StringComparison.OrdinalIgnoreCase))) continue;
            bool salio = r.Por == Como.Susurro
                ? transporte.Mandar(Paquete(ClientPacketId.Whisper, w => w.WriteAsciiString(r.A).WriteAsciiString(r.Texto)))
                : transporte.Mandar(Paquete(ClientPacketId.Talk, w => w.WriteAsciiString(r.Texto).WriteByte((byte)Como.Charla)));
            if (salio) Console.WriteLine($"[bot {Nombre}] le contesta a {PacketDispatcher.ParaElLog(r.A)}: {r.Texto}");
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
    }
}
