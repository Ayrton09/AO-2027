using CoverAO.Protocol;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LO QUE EL BOT SABE DEL MUNDO, y no sabe nada más.
///
/// <para>Se arma con los paquetes que el servidor le manda, exactamente los mismos que recibe el cliente de
/// una persona. Eso es deliberado y es la regla que hace que un bot se comporte como un jugador: si leyera el
/// estado del servidor por adentro vería lo que hay del otro lado de una pared, sabría la vida exacta de lo
/// que tiene enfrente y le apuntaría a cosas que una persona no puede ver. Acá no puede: sabe lo que le
/// llegó por el cable.</para>
///
/// <para><b>Por qué no hace falta un decodificador completo.</b> El escritor de la sesión llama a
/// <c>Send</c> una vez por paquete (ver <see cref="TransporteDeBot"/>), así que acá cada paquete llega entero
/// y solo. Se decodifican los que interesan y el resto se descarta sin leerlo — que es lo que evita
/// tener que portar el layout de los ciento ocho que el servidor sabe mandar.</para>
///
/// <para>Lo tocan dos hilos —el escritor de la sesión, que trae los paquetes, y el del bot, que decide— así
/// que todo pasa por el candado, y las decisiones que miran varias cosas a la vez (a quién le pego, qué tomo)
/// se resuelven ACÁ ADENTRO en vez de exponer el estado suelto y que el que decide lea una foto rota.</para>
/// </summary>
public sealed class SentidosDelBot
{
    /// <summary>Alguien a la vista. <see cref="FichaNpc"/> en 0 significa que es una persona.
    ///
    /// <para>La distinción sale de donde la saca el cliente del original: el "nombre" de un NPC no es un
    /// nombre sino su NÚMERO de ficha, y el VB6 usa <c>IsNumeric(Name)</c> para saber que lo que tiene
    /// enfrente es una criatura (Protocol.bas:1733).</para>
    ///
    /// <para><see cref="Estado"/> es el byte que sigue al nombre en el <c>CharacterCreate</c>: el bando de una persona y el
    /// <c>Status</c> de la ficha de una criatura.</para></summary>
    public readonly record struct Visible(short CharIndex, byte X, byte Y, short FichaNpc, byte Estado = 0, string Nombre = "")
    {
        public bool EsNpc => FichaNpc > 0;
    }

    /// <summary>Las bajas de personas por el bando de la víctima, como las cuenta el servidor (la ficha de estadísticas, MiniStats).
    /// Es lo que piden el enlistador y la recompensa (ver <c>Bot.BajasParaSuBando</c>).</summary>
    public readonly record struct Bajas(int Ciudadanos, int Renegados, int Republicanos, int Caos, int Armada, int Milicianos);

    private readonly object _candado = new();
    private byte _miEstado;
    private Bajas? _bajas;
    private long _ultimaBajaDeVida;
    private readonly Dictionary<short, Visible> _visibles = new();
    private readonly (short Obj, short Cantidad, bool Puesto)[] _inventario =
        new (short, short, bool)[GameLimits.MaxInventorySlots + 1];

    private readonly short[] _hechizos = new short[GameLimits.MaxUserSpells + 1];

    /// <summary>Lo que hay tirado en el piso, por baldosa. Es lo que deja una criatura al morir, y es de donde
    /// el bot sabe que hay algo para levantar. Ojo que el servidor NO manda el decorado fijo —árboles,
    /// carteles, yacimientos—: eso el cliente lo dibuja de su propio mapa, así que acá sólo hay cosas sueltas.</summary>
    private readonly Dictionary<(byte X, byte Y), short> _enElPiso = new();

    /// <summary>El mostrador del mercader con el que está comerciando: ranura → qué vende, cuánto hay y a qué
    /// precio. Se vacía al cerrar la ventana.</summary>
    private readonly Dictionary<byte, (short Obj, short Cantidad, float Precio)> _tienda = new();

    private short _miCharIndex;
    private byte _x, _y;
    private bool _tengoPosicion;
    private int _vida, _vidaMax = 1, _energia, _energiaMax = 1;
    private int _mana, _manaMax = 1, _oro, _puntosLibres, _experiencia;
    private int _banco = -1, _vecesQueVioElBanco;
    private IReadOnlyList<ServerMessages.FilaDeSubasta> _subastas = Array.Empty<ServerMessages.FilaDeSubasta>();
    private int _vecesQueVioLasSubastas;
    private readonly Dictionary<byte, (short Obj, short Cantidad)> _boveda = new();
    private int _vecesQueVioLaBoveda;
    private readonly List<string> _loQueDejoLaSubasta = new();
    private short _nivel, _mapa;
    private byte _hambre, _sed;
    private long _ultimoAvisoDeVeneno;
    private long _ultimoRechazoDeMagia;
    private bool _mapaNuevo, _comerciando, _meditando, _paralizado;

    // ── Lo que el que decide le pregunta ──────────────────────────────────────────────────────────────

    public short MiCharIndex { get { lock (_candado) return _miCharIndex; } }
    /// <summary>Su bando, el que dice su propio <c>CharacterCreate</c> (o un <c>UpdateTagAndStatus</c> después). 0 mientras no
    /// lo sabe.</summary>
    public byte MiEstado { get { lock (_candado) return _miEstado; } }

    /// <summary>Las bajas de la última ficha de estadísticas que mandó el servidor, o null si todavía no pidió ninguna.</summary>
    public Bajas? MisBajas { get { lock (_candado) return _bajas; } }

    /// <summary>El nivel que llega en la ficha de una persona (<c>CharMsgStatus</c>, la respuesta al clic) cuando está más de 5 niveles
    /// por encima del que mira: el original lo esconde (Protocol.bas:17082).</summary>
    public const short NivelEscondido = 255;

    private readonly Dictionary<short, (short Nivel, long Cuando)> _nivelDe = new();

    /// <summary>El nivel de esa persona según la última ficha que llegó de ella, si llegó hace menos de <paramref name="vigenciaMs"/>;
    /// <see cref="NivelEscondido"/> si está más de 5 niveles por encima; null si no se sabe.</summary>
    public short? NivelDe(short charIndex, long vigenciaMs)
    {
        lock (_candado)
            return _nivelDe.TryGetValue(charIndex, out var n) && Environment.TickCount64 - n.Cuando < vigenciaMs ? n.Nivel : null;
    }

    /// <summary>Hace cuánto (ms) le bajó la vida por última vez; <see cref="long.MaxValue"/> si nunca. Es lo que dice que alguien le
    /// está pegando aunque no se vea una criatura encima.</summary>
    public long HaceCuantoLeBajoLaVida
    { get { lock (_candado) return _ultimaBajaDeVida == 0 ? long.MaxValue : Environment.TickCount64 - _ultimaBajaDeVida; } }
    public bool Ubicado { get { lock (_candado) return _tengoPosicion; } }
    public (byte X, byte Y) Posicion { get { lock (_candado) return (_x, _y); } }
    public short Nivel { get { lock (_candado) return _nivel; } }
    public bool Vivo { get { lock (_candado) return _vida > 0; } }

    /// <summary>La vida que le queda, de 0 a 1. Es con lo que decide si toma una poción o si se retira.</summary>
    public double Salud { get { lock (_candado) return _vidaMax <= 0 ? 1 : (double)_vida / _vidaMax; } }

    /// <summary>La vida máxima, en puntos. Con esto mide si una criatura le queda grande.</summary>
    public int VidaMaxima { get { lock (_candado) return _vidaMax; } }

    /// <summary>¿Cambió de mapa desde la última vez que se preguntó? Se consume al leerlo. Sirve para que el
    /// bot vuelva a tomar el lugar donde está como su zona: la referencia del mapa anterior no sirve acá.</summary>
    public bool CambioDeMapa()
    {
        lock (_candado)
        {
            bool si = _mapaNuevo;
            _mapaNuevo = false;
            return si;
        }
    }

    /// <summary>Hay una criatura pegada. Con esto una pausa se corta cuando algo se le vino encima, en vez de
    /// quedarse quieto los cuarenta segundos que había decidido descansar.</summary>
    public bool AlgoPegado()
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return false;
            foreach (var v in _visibles.Values)
                if (v.EsNpc && Math.Abs(v.X - _x) + Math.Abs(v.Y - _y) <= 1) return true;
            return false;
        }
    }

    /// <summary>La energía, de 0 a 1. Sin energía no se puede pegar (el golpe la gasta), así que un bot
    /// agotado tiene que parar aunque le sobre vida.</summary>
    public double Fuelle { get { lock (_candado) return _energiaMax <= 0 ? 1 : (double)_energia / _energiaMax; } }

    /// <summary>El hambre y la sed, de 0 a 100: la barra que el jugador mira para saber cuándo comer.</summary>
    public int Hambre { get { lock (_candado) return _hambre; } }

    /// <inheritdoc cref="Hambre"/>
    public int Sed { get { lock (_candado) return _sed; } }

    /// <summary>En CERO, que es donde se corta la regeneración de energía. Para decidir cuándo comer está
    /// <see cref="Bot.HayQueReponerLaBarra"/>: esperar a esto es esperar demasiado.</summary>
    public bool ConHambre { get { lock (_candado) return _hambre == 0; } }

    /// <inheritdoc cref="ConHambre"/>
    public bool ConSed { get { lock (_candado) return _sed == 0; } }

    /// <summary>¿Está meditando? Lo dice el servidor: contesta cada pedido con el <c>MeditateToggle</c>, y lo manda también
    /// cuando corta la meditación por su cuenta, al caminar o al recibir un golpe.</summary>
    public bool Meditando { get { lock (_candado) return _meditando; } }

    /// <summary>¿Está paralizado? El servidor lo prende y lo apaga con el mismo <c>ParalizeOK</c>.</summary>
    public bool Paralizado { get { lock (_candado) return _paralizado; } }

    /// <summary>Cómo le llegó lo que oyó: los tres modos del chat (VB6 HandleTalk, Protocol.bas:1394-1412) y el susurro.</summary>
    public enum ComoLoDijo : byte { Charla = 1, Grito = 2, Global = 3, Susurro = 4 }

    /// <summary>Algo que una persona dijo y el bot oyó: quién (el nombre, sin el clan), qué y cómo.</summary>
    public readonly record struct LoOido(string Quien, string Texto, ComoLoDijo Como, long Cuando);

    /// <summary>
    /// LO QUE OYÓ desde la última vez, y se lo lleva. Llega como le llega al cliente de una persona:
    /// <list type="bullet">
    /// <item>la CHARLA, el globo sobre la cabeza del que habla (<c>ChatOverHead</c> modo 1, al área), con el nombre que trajo su
    /// <c>CharacterCreate</c>;</item>
    /// <item>el GRITO, el GLOBAL y el SUSURRO, la línea "[Nombre] texto" de la consola con su fuente (25, 23 y 22). El grito, el
    /// global y el susurro de cerca traen además un globo, que no se cuenta: la línea ya trae el nombre, y el globo del global le
    /// llega aunque no lo vea.</item>
    /// </list>
    /// Lo que dice él mismo no se oye, ni lo que dicen las criaturas. Guarda hasta <see cref="OidoMax"/>; lo más viejo se pierde.
    /// </summary>
    public IReadOnlyList<LoOido> TomarLoOido()
    {
        lock (_candado)
        {
            if (_oido.Count == 0) return Array.Empty<LoOido>();
            var oido = new List<LoOido>(_oido);
            _oido.Clear();
            return oido;
        }
    }

    /// <summary>Cuántas cosas oyó desde que se conectó, de cada modo (el índice es <see cref="ComoLoDijo"/>).</summary>
    public int OidoEnTotal(ComoLoDijo como) { lock (_candado) return _oidoEnTotal[(int)como]; }

    public const int OidoMax = 32;

    private readonly Queue<LoOido> _oido = new();
    private readonly int[] _oidoEnTotal = new int[5];

    /// <summary>El nombre de una persona a la vista, sin el clan, o null.</summary>
    public string? NombreDe(short charIndex)
    {
        lock (_candado) return _visibles.TryGetValue(charIndex, out var v) && !v.EsNpc && v.Nombre.Length > 0 ? v.Nombre : null;
    }

    /// <summary>Su propio nombre, para no oírse en la consola. Lo pone el bot al conectarse.</summary>
    public string MiNombre { get; init; } = "";

    /// <summary>El nombre sin el clan: el <c>CharacterCreate</c> de una persona con clan trae "Nombre &lt;Clan&gt;".</summary>
    public static string SinElClan(string tag) => tag.IndexOf(" <", StringComparison.Ordinal) is int i and >= 0 ? tag[..i] : tag;

    /// <summary>
    /// "[Nombre] texto", la línea de consola del grito, el global y el susurro (<see cref="ServerMessages.LineaDeChat"/>). El nombre
    /// puede llevar espacios ("Juan Perez", ver <c>Naming</c>) pero no corchetes, así que el primer ']' es el que lo cierra; lo que no
    /// tiene esa forma no es una línea de chat.
    /// </summary>
    public static (string Quien, string Texto)? LineaDeChat(string linea)
    {
        if (linea.Length < 3 || linea[0] != '[') return null;
        int cierre = linea.IndexOf(']');
        if (cierre < 2 || cierre + 2 >= linea.Length || linea[cierre + 1] != ' ') return null;
        string quien = linea[1..cierre];
        if (quien.Contains('[') || quien != quien.Trim()) return null;
        return (quien, linea[(cierre + 2)..]);
    }

    /// <summary>El modo de la línea de consola por su fuente, o null si no es de chat.</summary>
    public static ComoLoDijo? ModoDeLaFuente(byte fuente) => fuente switch
    {
        PacketDispatcher.FuenteGrito => ComoLoDijo.Grito,
        PacketDispatcher.FuenteGlobal => ComoLoDijo.Global,
        PacketDispatcher.FuenteSusurro => ComoLoDijo.Susurro,
        _ => null,
    };

    /// <summary>Lo anota. Se llama con el candado tomado.</summary>
    private void Oir(string quien, string texto, ComoLoDijo como)
    {
        if (quien.Length == 0 || string.Equals(quien, MiNombre, StringComparison.OrdinalIgnoreCase)) return;
        if (_oido.Count >= OidoMax) _oido.Dequeue();
        _oido.Enqueue(new LoOido(quien, texto, como, Environment.TickCount64));
        _oidoEnTotal[(int)como]++;
    }

    /// <summary>¿Está envenenado? No hay un paquete de estado para esto: el servidor avisa con el <c>LocaleMsg 47</c>
    /// cada vez que el veneno le saca vida, que es cada veinte segundos (VB6 EfectoVeneno), y el veneno no se pasa
    /// solo. Se toma como vigente mientras el último aviso tenga menos de dos intervalos.</summary>
    public bool Envenenado
    {
        get { lock (_candado) return _ultimoAvisoDeVeneno != 0 && Environment.TickCount64 - _ultimoAvisoDeVeneno < VenenoVigenteMs; }
    }

    /// <summary>Después de curarse no se espera a que venzan los dos intervalos: si sigue envenenado, el próximo
    /// aviso lo vuelve a marcar.</summary>
    public void OlvidarVeneno() { lock (_candado) _ultimoAvisoDeVeneno = 0; }

    private const long VenenoVigenteMs = 45_000;
    private const short AvisoDeVeneno = 47;

    /// <summary>Cuando fue la ultima vez que el servidor le rechazo un casteo por falta de Magia, o 0 si
    /// nunca. Es la unica senal del rechazo: <c>PuedeLanzar</c> corta con el <c>LocaleMsg 221</c> y no manda
    /// nada mas.</summary>
    public long UltimoRechazoDeMagia { get { lock (_candado) return _ultimoRechazoDeMagia; } }

    /// <summary>"No tienes suficientes puntos de magia" (VB6 PuedeLanzar, modHechizos.bas:319). Es el
    /// PRIMER requisito que mira el servidor, antes que la energia (93) y el mana (222).</summary>
    private const short AvisoDeFaltaDeMagia = 221;

    /// <summary>El maná, de 0 a 1. Los que lanzan hechizos meditan cuando esto baja.</summary>
    public double Mana { get { lock (_candado) return _manaMax <= 0 ? 0 : (double)_mana / _manaMax; } }

    /// <summary>¿Tiene maná para gastar? Un guerrero tiene el máximo en 0 y no medita nunca.</summary>
    public bool UsaMana { get { lock (_candado) return _manaMax > 1; } }

    /// <summary>El maná que le queda, en puntos. Es contra esto que se compara lo que sale un hechizo.</summary>
    public int ManaEnPuntos { get { lock (_candado) return _mana; } }

    /// <summary>El número de hechizo que hay en esa ranura, o 0. Hace falta para recordar CUÁL se pidió
    /// cuando hay que anotar que el servidor lo rechazó.</summary>
    public short HechizoEn(byte ranura)
    {
        // `_hechizos` es un ARRAY indexado por ranura, no una lista de pares.
        lock (_candado) return ranura > 0 && ranura < _hechizos.Length ? _hechizos[ranura] : (short)0;
    }

    /// <summary>La energía EN PUNTOS, que es como la pide el servidor para dejar lanzar un hechizo
    /// (<c>p.Stamina &lt; spell.StaRequerido</c>). <see cref="Fuelle"/> es la fracción, que sirve para otra cosa.</summary>
    public int EnergiaEnPuntos { get { lock (_candado) return _energia; } }

    /// <summary>La energía y el maná máximos, en puntos: lo que tiene que alcanzar para lanzar un hechizo que pide tanto.</summary>
    public int EnergiaMaxima { get { lock (_candado) return _energiaMax; } }

    /// <inheritdoc cref="EnergiaMaxima"/>
    public int ManaMaximo { get { lock (_candado) return _manaMax; } }

    public int Oro { get { lock (_candado) return _oro; } }

    /// <summary>EL ORO DE LA BÓVEDA, -1 mientras no lo sabe. Lo dice el panel del oro del banquero (el <c>BankInit</c> del doble
    /// clic); el servidor no lo manda al entrar, así que un bot recién conectado no sabe lo que guardó en la vida anterior hasta
    /// que vuelve a un banquero.</summary>
    public int Banco { get { lock (_candado) return _banco; } }

    /// <summary>Toda su plata: la que lleva encima más la de la bóveda (si no la sabe, sólo la de encima).</summary>
    public int Plata { get { lock (_candado) return _oro + Math.Max(0, _banco); } }

    /// <summary>La última lista del Centro de Subastas que le llegó (vacía si nunca la pidió).</summary>
    public IReadOnlyList<ServerMessages.FilaDeSubasta> Subastas { get { lock (_candado) return _subastas; } }

    /// <summary>Cuántas listas del Centro de Subastas le llegaron: sirve para esperar la respuesta al pedido.</summary>
    public int VecesQueVioLasSubastas { get { lock (_candado) return _vecesQueVioLasSubastas; } }

    /// <summary>La ranura de la bóveda de ítems donde hay de eso, la última vez que la abrió; null si no hay.</summary>
    public (byte Ranura, short Cantidad)? EnLaBoveda(short obj)
    {
        lock (_candado)
            foreach (var (ranura, item) in _boveda)
                if (item.Obj == obj && item.Cantidad > 0) return (ranura, item.Cantidad);
        return null;
    }

    /// <summary>Cuántas veces se abrió la bóveda de ítems (el <c>BankStart</c>): sirve para esperar las ranuras.</summary>
    public int VecesQueVioLaBoveda { get { lock (_candado) return _vecesQueVioLaBoveda; } }

    /// <summary>Cuántas veces se abrió el panel del oro: sirve para esperar la respuesta del doble clic al banquero.</summary>
    public int VecesQueVioElBanco { get { lock (_candado) return _vecesQueVioElBanco; } }

    /// <summary>El oro de "El Centro de Subastas dejó en tu bóveda N monedas de oro." (PacketDispatcher.Subastas); null si es otra cosa.</summary>
    public static int? OroQueDejoLaSubasta(string linea)
    {
        const string Aviso = "El Centro de Subastas dej", Monedas = " monedas de oro";
        if (!linea.StartsWith(Aviso, StringComparison.Ordinal) || !linea.Contains(Monedas, StringComparison.Ordinal)) return null;
        var numero = linea[..linea.IndexOf(Monedas, StringComparison.Ordinal)];
        int desde = numero.LastIndexOf(' ') + 1;
        return int.TryParse(numero[desde..], out int oro) && oro > 0 ? oro : null;
    }

    /// <summary>El nombre del objeto de "El Centro de Subastas dejó en tu bóveda: N Nombre." (lo comprado, o lo propio que volvió);
    /// null si es otra cosa.</summary>
    public static string? ObjetoQueDejoLaSubasta(string linea)
    {
        const string Aviso = "El Centro de Subastas dej";
        int dosPuntos = linea.IndexOf(": ", StringComparison.Ordinal);
        if (!linea.StartsWith(Aviso, StringComparison.Ordinal) || dosPuntos < 0 || !linea.EndsWith('.')) return null;
        var resto = linea[(dosPuntos + 2)..^1];
        int espacio = resto.IndexOf(' ');
        return espacio > 0 && int.TryParse(resto[..espacio], out _) ? resto[(espacio + 1)..] : null;
    }

    /// <summary>Los objetos que el Centro de Subastas le dejó en la bóveda desde la última vez que preguntó (por nombre).</summary>
    public IReadOnlyList<string> TomarLoQueDejoLaSubasta()
    {
        lock (_candado)
        {
            if (_loQueDejoLaSubasta.Count == 0) return Array.Empty<string>();
            var todo = _loQueDejoLaSubasta.ToArray();
            _loQueDejoLaSubasta.Clear();
            return todo;
        }
    }

    /// <summary>Anota lo que se acaba de depositar (positivo) o retirar (negativo), ya confirmado por el oro de encima.</summary>
    public void MovioAlBanco(int monto)
    {
        lock (_candado) if (_banco >= 0) _banco = Math.Max(0, _banco + monto);
    }

    /// <summary>La experiencia acumulada en el nivel. Sube con cada golpe que da experiencia (<c>CalcularDarExp</c>), no sólo al matar.</summary>
    public int Experiencia { get { lock (_candado) return _experiencia; } }

    /// <summary>Puntos de skill sin repartir. El servidor los anuncia al subir de nivel, y un jugador los
    /// gasta: dejarlos juntándose es de las cosas que más delatan que del otro lado no hay nadie.</summary>
    public int PuntosLibres { get { lock (_candado) return _puntosLibres; } }

    /// <summary>
    /// LO QUE LE SACÓ A LO QUE LE PEGA desde la última vez que se preguntó: cuántos golpes cuerpo a cuerpo le resolvió el servidor,
    /// cuántos entraron —los que no dieron 0— y la suma de lo que hicieron. Es el número que el servidor le pone sobre la cabeza a
    /// cada golpe (el modo 2 de
    /// <c>ChatOverHeadLocale</c>): 0 si erró, y NEGATIVO si la criatura se curó, porque el original le resta la defensa al golpe
    /// sin piso (SistemaCombate.bas:465). La puñalada suma lo suyo sin contar como otro golpe.
    /// </summary>
    public (int Golpes, int Entraron, int Dano) TomarLoQuePego()
    {
        lock (_candado)
        {
            var loQuePego = (_golpesDados, _golpesQueEntraron, _danoDado);
            _golpesDados = 0;
            _golpesQueEntraron = 0;
            _danoDado = 0;
            return loQuePego;
        }
    }

    private int _golpesDados, _golpesQueEntraron, _danoDado;

    /// <summary>El modo de <c>ChatOverHeadLocale</c> que lleva el número de un golpe (<see cref="ServerMessages.DamageOverHead"/>).</summary>
    private const byte ModoDelGolpe = 2;

    /// <summary>Cuánto sacó la puñalada que avisa ese mensaje de consola, o null si el mensaje es otro. Se mira el comienzo sobre
    /// los bytes antes de armar el texto: el bot recibe muchos mensajes de consola y casi ninguno es éste.</summary>
    private static int? DanoDeLaPunalada(byte[] p)
    {
        // [id][largo, 2 bytes][texto][fuente]
        int largo = p.Length >= 3 ? (short)(p[1] | (p[2] << 8)) : 0;
        if (largo <= BytesDeLaPunalada.Length || 3 + largo > p.Length
            || !p.AsSpan(3, BytesDeLaPunalada.Length).SequenceEqual(BytesDeLaPunalada)) return null;
        string numero = Codec.Ansi.GetString(p, 3 + BytesDeLaPunalada.Length, largo - BytesDeLaPunalada.Length).TrimEnd('.');
        return int.TryParse(numero, out int dano) ? dano : null;
    }

    private static readonly byte[] BytesDeLaPunalada = Codec.Ansi.GetBytes(ServerMessages.ComienzoDeLaPunalada);

    /// <summary>
    /// CUÁNTO TIENE DE ESA SKILL, o -1 si todavía no lo sabe. Llega entera con <c>SendSkills</c> —el servidor la manda al
    /// entrar si hay puntos libres, después de cada reparto y cuando se la pide— y de a una con el aviso de que subió
    /// (<c>LocaleMsg 454</c>, <c>"*skill%valor"</c>). Hace falta para elegir qué fabricar: cada receta pide una skill
    /// mínima y el servidor rechaza en SILENCIO la que no llega (VB6, sin <c>Else</c>).
    /// </summary>
    public int Skill(int skill)
    {
        lock (_candado) return !_sabeSusSkills || skill < 1 || skill >= _skills.Length ? -1 : _skills[skill];
    }

    private readonly byte[] _skills = new byte[GameLimits.NumSkills + 1];
    private bool _sabeSusSkills;

    /// <summary>"Tu habilidad en X subió a Y" (VB6 SubirSkill): es el ÚNICO aviso de que una skill subió por usarla.</summary>
    private const short AvisoDeSkillQueSube = 454;

    /// <summary>Lee el <c>"*skill%valor"</c> del aviso de que una skill subió. Null si no tiene esa forma.</summary>
    public static (int Skill, int Valor)? SkillQueSubio(string argumentos)
    {
        if (argumentos.Length < 4 || argumentos[0] != '*') return null;
        int pct = argumentos.IndexOf('%');
        return pct > 1 && int.TryParse(argumentos[1..pct], out int skill) && int.TryParse(argumentos[(pct + 1)..], out int valor)
            ? (skill, valor) : null;
    }

    /// <summary>La ventana de comercio está abierta.</summary>
    public bool Comerciando { get { lock (_candado) return _comerciando; } }

    /// <summary>Descuenta los puntos que se acaban de repartir. Se hace ACÁ y no confiando en que el servidor
    /// vuelva a avisar: pedir más de los que quedan lo toma el anticheat como intento de hackeo y cierra la
    /// conexión (HandleModifySkills), así que la cuenta del bot tiene que ser la buena.</summary>
    public void GastarPuntos(int cuantos)
    {
        lock (_candado) _puntosLibres = Math.Max(0, _puntosLibres - cuantos);
    }

    /// <summary>El mapa donde está.</summary>
    public short Mapa { get { lock (_candado) return _mapa; } }

    /// <summary>La ranura del mostrador que cumpla la condición, con su precio. Para comprar hay que decir la
    /// ranura DEL MERCADER, no el objeto.</summary>
    public (byte Ranura, float Precio)? EnLaTienda(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            foreach (var (ranura, item) in _tienda)
                if (item.Obj > 0 && item.Cantidad > 0 && sirve(item.Obj)) return (ranura, item.Precio);
            return null;
        }
    }

    /// <summary>La ranura de la vitrina que MÁS RINDE POR MONEDA entre las que no pasan de <paramref name="plata"/>.
    /// <paramref name="rinde"/> dice los puntos que da una unidad, y cero es que no sirve. Importa el rendimiento y
    /// no el precio: la manzana de la cuna cuesta dos y llena diez de hambre, el queso cuesta cinco y llena
    /// cincuenta, así que comprando lo más barato se pagaba el doble por comer. Y el tope de plata es por el que
    /// se quedó sin comida, que es justo el que menos tiene: si el queso no le alcanza, la manzana sí. Lo gratis
    /// rinde más que todo.</summary>
    public (byte Ranura, short Obj, float Precio)? MejorOfertaEnLaTienda(Func<short, int> rinde, double plata)
    {
        lock (_candado)
        {
            (byte Ranura, short Obj, float Precio)? mejor = null;
            double mejorPorMoneda = 0;
            foreach (var (ranura, item) in _tienda)
            {
                if (item.Obj <= 0 || item.Cantidad <= 0 || item.Precio > plata) continue;
                int puntos = rinde(item.Obj);
                if (puntos <= 0) continue;
                double porMoneda = item.Precio > 0 ? puntos / (double)item.Precio : double.PositiveInfinity;
                if (mejor is null || porMoneda > mejorPorMoneda)
                {
                    mejor = (ranura, item.Obj, item.Precio);
                    mejorPorMoneda = porMoneda;
                }
            }
            return mejor;
        }
    }

    /// <summary>
    /// La ranura de la vitrina con lo MEJOR que pueda pagar, según <paramref name="valor"/> (cero = no sirve).
    ///
    /// <para>Va por el valor y no por el rendimiento por moneda como <see cref="MejorOfertaEnLaTienda"/>: la comida
    /// se compra por lo que rinde cada moneda, pero un arma no —de dos que se pueden pagar se lleva la que más
    /// pega, aunque la otra sea más barata por punto de daño—.</para>
    /// </summary>
    public (byte Ranura, short Obj, float Precio)? LoMejorDeLaVitrina(Func<short, int> valor, double plata)
    {
        lock (_candado)
        {
            (byte Ranura, short Obj, float Precio)? mejor = null;
            int mejorValor = 0;
            foreach (var (ranura, item) in _tienda)
            {
                if (item.Obj <= 0 || item.Cantidad <= 0 || item.Precio > plata) continue;
                int valen = valor(item.Obj);
                if (valen <= 0) continue;
                if (mejor is null || valen > mejorValor)
                {
                    mejor = (ranura, item.Obj, item.Precio);
                    mejorValor = valen;
                }
            }
            return mejor;
        }
    }

    /// <summary>La primera ranura del inventario que cumpla la condición, con cuánto hay. Para vender hace
    /// falta la cantidad, no sólo la ranura. Lo que está PUESTO no se ofrece nunca: vender el arma que lleva
    /// encima es lo primero que no haría nadie.</summary>
    public (byte Ranura, short Cantidad)? EnLaMochila(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0 && _inventario[s].Cantidad > 0 && !_inventario[s].Puesto
                    && sirve(_inventario[s].Obj))
                    return (s, _inventario[s].Cantidad);
            return null;
        }
    }

    /// <summary>Lo que vale lo que NO lleva puesto y cumple la condición, con <paramref name="precio"/> por objeto y cantidad.</summary>
    public long LoQueVale(Func<short, bool> sirve, Func<short, int, long> precio)
    {
        lock (_candado)
        {
            long total = 0;
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0 && _inventario[s].Cantidad > 0 && !_inventario[s].Puesto && sirve(_inventario[s].Obj))
                    total += precio(_inventario[s].Obj, _inventario[s].Cantidad);
            return total;
        }
    }

    /// <summary>¿Lleva PUESTO algo que cumpla la condición?</summary>
    public bool LlevaPuesto(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0 && _inventario[s].Puesto && sirve(_inventario[s].Obj)) return true;
            return false;
        }
    }

    /// <summary>¿Lo de esa ranura está puesto?</summary>
    public bool EstaPuesto(byte ranura)
    {
        lock (_candado) return ranura > 0 && ranura < _inventario.Length && _inventario[ranura].Puesto;
    }

    /// <summary>Las ranuras con algo adentro.</summary>
    public IReadOnlyList<byte> RanurasOcupadas()
    {
        lock (_candado)
        {
            var lista = new List<byte>();
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0) lista.Add(s);
            return lista;
        }
    }

    /// <summary>Qué objeto hay en esa ranura, o 0. Sirve para saber si una venta ocurrió: si después de
    /// ofrecerlo la ranura sigue teniendo lo mismo, el mercader no lo quiso.</summary>
    public short QueHayEn(byte ranura)
    {
        lock (_candado) return ranura > 0 && ranura < _inventario.Length ? _inventario[ranura].Obj : (short)0;
    }

    /// <summary>Cuánto tiene de lo que cumpla la condición, sumando ranuras. Se pregunta por CONDICIÓN y no
    /// por número de objeto porque hay varias pociones de vida distintas —la del kit de novato y la que vende
    /// el mercader son objetos diferentes— y para el bot son la misma cosa.</summary>
    public int CuantoTiene(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            int n = 0;
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0 && sirve(_inventario[s].Obj)) n += _inventario[s].Cantidad;
            return n;
        }
    }

    /// <summary>Lo que suma la mochila según <paramref name="porUnidad"/>: los puntos de hambre que carga en comida,
    /// por ejemplo. Cuenta también lo puesto, igual que <see cref="CuantoTiene"/>.</summary>
    public int Suma(Func<short, int> porUnidad)
    {
        lock (_candado)
        {
            int n = 0;
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0) n += _inventario[s].Cantidad * porUnidad(_inventario[s].Obj);
            return n;
        }
    }

    /// <summary>Las ranuras de hechizo ocupadas, con el número de hechizo de cada una.</summary>
    public IReadOnlyList<(byte Ranura, short Hechizo)> Hechizos()
    {
        lock (_candado)
        {
            var lista = new List<(byte, short)>();
            for (byte s = 1; s < _hechizos.Length; s++)
                if (_hechizos[s] > 0) lista.Add((s, _hechizos[s]));
            return lista;
        }
    }

    /// <summary>La baldosa con algo tirado más cercana, y a cuántos pasos. Es lo que deja una criatura al
    /// morir. <paramref name="sirveLaBaldosa"/> deja afuera baldosas aunque lo tirado sirva: el botín que queda al lado de lo
    /// peligroso no se elige (ver <c>Bot.ArrimaAlPeligroAlBotin</c>).</summary>
    public ((byte X, byte Y) Donde, int Pasos)? BotinMasCercano(Func<short, bool> vale, Func<(byte X, byte Y), bool>? sirveLaBaldosa = null)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            ((byte, byte), int)? mejor = null;
            foreach (var (baldosa, obj) in _enElPiso)
            {
                if (!vale(obj) || sirveLaBaldosa?.Invoke(baldosa) == false) continue;
                int pasos = Math.Abs(baldosa.X - _x) + Math.Abs(baldosa.Y - _y);
                if (mejor is null || pasos < mejor.Value.Item2) mejor = (baldosa, pasos);
            }
            return mejor;
        }
    }

    /// <summary>
    /// LO TIRADO FUERA DEL ÁREA SE OLVIDA, como hace el cliente original al cambiar de área (CambioDeArea, ModAreas.bas del
    /// cliente). El servidor avisa que alguien levantó algo sólo a los que ven esa baldosa, y al volver a entrar reenvía
    /// lo que sigue tirado pero no lo que ya no está: sin olvidarlo, lo que otro levantaba lejos quedaba en el piso para
    /// siempre y el bot caminaba hasta ahí a agacharse en vano. El área son los bloques de <see cref="World.AreaBlock"/>
    /// baldosas vecinos al propio, la misma cuenta de <see cref="World.InView"/>. Se llama con el candado tomado.
    /// </summary>
    private void OlvidarElPisoLejano()
    {
        List<(byte X, byte Y)>? lejos = null;
        foreach (var baldosa in _enElPiso.Keys)
            if (Math.Abs(baldosa.X / World.AreaBlock - _x / World.AreaBlock) > 1
                || Math.Abs(baldosa.Y / World.AreaBlock - _y / World.AreaBlock) > 1)
                (lejos ??= new()).Add(baldosa);
        if (lejos is not null)
            foreach (var baldosa in lejos) _enElPiso.Remove(baldosa);
    }

    /// <summary>
    /// LO QUE EL SERVIDOR LE HACE AL QUE MUERE SIN AVISARLE: le apaga la meditación y le saca todo lo puesto (`KillPlayer`
    /// llama a `UnequipAll`, VB6 UserDie). El original avisa cada ranura desequipada con el paquete corto (acción 2, al final
    /// de `Desequipar`, InvUsuario.bas:827) y el port no manda nada: sin esto el bot se seguía creyendo vestido y armado, y
    /// `Equiparse` no le volvía a poner nada. Si algún día el servidor avisa, esto no cambia nada. Se llama con el candado
    /// tomado.
    /// </summary>
    private void AlMorir()
    {
        _meditando = false;
        for (int s = 1; s < _inventario.Length; s++)
            if (_inventario[s].Puesto) _inventario[s] = (_inventario[s].Obj, _inventario[s].Cantidad, false);
    }

    /// <summary>
    /// LO MÁS CERCANO DEL PISO que cumpla <paramref name="sirve"/>, con su baldosa y a cuántos pasos está. Es el
    /// hermano de <see cref="MasCercano"/> para las cosas tiradas: hasta ahora sólo se podía preguntar por UNA
    /// baldosa a la vez, así que el bot no tenía forma de encontrar, por ejemplo, la puerta cerrada más próxima.
    /// </summary>
    public ((byte X, byte Y) Donde, short Obj, int Pasos)? LoMasCercanoDelPiso(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            ((byte X, byte Y) Donde, short Obj, int Pasos)? mejor = null;
            foreach (var (donde, obj) in _enElPiso)
            {
                if (obj <= 0 || !sirve(obj)) continue;
                int pasos = Math.Abs(donde.X - _x) + Math.Abs(donde.Y - _y);
                if (mejor is { } ya && ya.Pasos <= pasos) continue;
                mejor = (donde, obj, pasos);
            }
            return mejor;
        }
    }

    /// <summary>TODO lo del piso que sirve, de lo más cerca a lo más lejos. Hermano de
    /// <see cref="LoMasCercanoDelPiso"/> para cuando no alcanza con la primera: entre varias puertas a la vista hay
    /// que elegir la que destraba el camino, y la más cercana casi nunca es ésa.</summary>
    public IReadOnlyList<((byte X, byte Y) Donde, short Obj, int Pasos)> TodoLoDelPiso(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return Array.Empty<((byte X, byte Y), short, int)>();
            var todo = new List<((byte X, byte Y) Donde, short Obj, int Pasos)>();
            foreach (var (donde, obj) in _enElPiso)
                if (obj > 0 && sirve(obj))
                    todo.Add((donde, obj, Math.Abs(donde.X - _x) + Math.Abs(donde.Y - _y)));
            todo.Sort((a, b) => a.Pasos.CompareTo(b.Pasos));
            return todo;
        }
    }

    /// <summary>Qué hay tirado en esa baldosa, o 0.</summary>
    public short EnElPiso((byte X, byte Y) baldosa)
    {
        lock (_candado) return _enElPiso.TryGetValue(baldosa, out var o) ? o : (short)0;
    }

    /// <summary>La criatura más cercana que cumpla <paramref name="sirve"/>, o null. Devuelve también a qué
    /// distancia está, contada en pasos (Manhattan): es la que corresponde, porque el bot camina en cruz.
    ///
    /// <para><paramref name="salvo"/> deja afuera a UNA criatura por su índice: la que el bot ya probó y no alcanzó. Sin eso, la
    /// vuelta siguiente vuelve a elegir la misma —es la más cercana— y choca contra la misma pared.</para></summary>
    public (Visible Quien, int Pasos)? MasCercano(Func<short, bool> sirve, IReadOnlySet<short>? salvo = null)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            foreach (var v in _visibles.Values)
            {
                if (!v.EsNpc || v.CharIndex == _miCharIndex || salvo?.Contains(v.CharIndex) == true
                    || !sirve(v.FichaNpc)) continue;
                int pasos = Math.Abs(v.X - _x) + Math.Abs(v.Y - _y);
                if (mejor is null || pasos < mejor.Value.Item2) mejor = (v, pasos);
            }
            return mejor;
        }
    }

    /// <summary>La criatura que cumple <paramref name="sirve"/> más cerca de <paramref name="punto"/>, a <paramref name="radio"/> pasos
    /// o menos de él, y a cuántos pasos está del bot; o null. Es la que pelea, o tiene al lado, la persona que está en ese punto.</summary>
    public (Visible Quien, int Pasos)? MasCercanoA((byte X, byte Y) punto, Func<short, bool> sirve, int radio,
                                                  IReadOnlySet<short>? salvo = null)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            int mejorAlPunto = int.MaxValue;
            foreach (var v in _visibles.Values)
            {
                if (!v.EsNpc || salvo?.Contains(v.CharIndex) == true || !sirve(v.FichaNpc)) continue;
                int alPunto = Math.Abs(v.X - punto.X) + Math.Abs(v.Y - punto.Y);
                if (alPunto > radio || alPunto >= mejorAlPunto) continue;
                mejorAlPunto = alPunto;
                mejor = (v, Math.Abs(v.X - _x) + Math.Abs(v.Y - _y));
            }
            return mejor;
        }
    }

    /// <summary>La persona a la vista con ese nombre (sin importar mayúsculas), o null.</summary>
    public Visible? PersonaLlamada(string nombre)
    {
        lock (_candado)
            foreach (var v in _visibles.Values)
                if (!v.EsNpc && v.CharIndex != _miCharIndex && string.Equals(v.Nombre, nombre, StringComparison.OrdinalIgnoreCase))
                    return v;
        return null;
    }

    /// <summary>La criatura que cumple <paramref name="sirve"/> a <paramref name="alcance"/> pasos o menos con el mayor
    /// <paramref name="puntaje"/> (de su ficha y los pasos), y a cuántos pasos; o null. Como <see cref="MasCercano"/>, sin las de
    /// <paramref name="salvo"/>; entre puntajes iguales, la más cercana.</summary>
    public (Visible Quien, int Pasos)? LaQueMasRinde(Func<short, bool> sirve, Func<short, int, double> puntaje, int alcance,
                                                     IReadOnlySet<short>? salvo = null)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            double suPuntaje = double.MinValue;
            foreach (var v in _visibles.Values)
            {
                if (!v.EsNpc || v.CharIndex == _miCharIndex || salvo?.Contains(v.CharIndex) == true || !sirve(v.FichaNpc)) continue;
                int pasos = Math.Abs(v.X - _x) + Math.Abs(v.Y - _y);
                if (pasos > alcance) continue;
                double p = puntaje(v.FichaNpc, pasos);
                if (mejor is null || p > suPuntaje || (p == suPuntaje && pasos < mejor.Value.Item2)) { mejor = (v, pasos); suPuntaje = p; }
            }
            return mejor;
        }
    }

    /// <summary>Las criaturas pegadas (a un paso, en cruz) que cumplen <paramref name="sirve"/>: las que le pueden estar pegando.</summary>
    public List<Visible> CriaturasPegadas(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            var pegadas = new List<Visible>();
            if (!_tengoPosicion) return pegadas;
            foreach (var v in _visibles.Values)
                if (v.EsNpc && v.CharIndex != _miCharIndex && Math.Abs(v.X - _x) + Math.Abs(v.Y - _y) <= 1 && sirve(v.FichaNpc))
                    pegadas.Add(v);
            return pegadas;
        }
    }

    /// <summary>La PERSONA más cercana (no criatura), sin contarse a sí mismo.</summary>
    public (Visible Quien, int Pasos)? PersonaMasCercana()
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            foreach (var v in _visibles.Values)
            {
                if (v.EsNpc || v.CharIndex == _miCharIndex) continue;
                int pasos = Math.Abs(v.X - _x) + Math.Abs(v.Y - _y);
                if (mejor is null || pasos < mejor.Value.Item2) mejor = (v, pasos);
            }
            return mejor;
        }
    }

    /// <summary>La PERSONA más cercana que <paramref name="sirve"/> (por quién es), sin contarse a sí mismo ni a las de
    /// <paramref name="salvo"/>.</summary>
    public (Visible Quien, int Pasos)? PersonaMasCercana(Func<Visible, bool> sirve, IReadOnlySet<short>? salvo = null)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            foreach (var v in _visibles.Values)
            {
                if (v.EsNpc || v.CharIndex == _miCharIndex || (salvo?.Contains(v.CharIndex) ?? false) || !sirve(v)) continue;
                int pasos = Math.Abs(v.X - _x) + Math.Abs(v.Y - _y);
                if (mejor is null || pasos < mejor.Value.Item2) mejor = (v, pasos);
            }
            return mejor;
        }
    }

    /// <summary>Dónde están las criaturas que <paramref name="sirve"/>, de las que ve.</summary>
    public List<(byte X, byte Y)> DondeEstan(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            var donde = new List<(byte X, byte Y)>();
            foreach (var v in _visibles.Values)
                if (v.EsNpc && sirve(v.FichaNpc)) donde.Add((v.X, v.Y));
            return donde;
        }
    }

    /// <summary>Cuántas PERSONAS ve (no criaturas), sin contarse a sí mismo: lo que le dice si el lugar está lleno.</summary>
    public int PersonasALaVista()
    {
        lock (_candado)
        {
            int cuantas = 0;
            foreach (var v in _visibles.Values)
                if (!v.EsNpc && v.CharIndex != _miCharIndex) cuantas++;
            return cuantas;
        }
    }

    /// <summary>Las baldosas donde hay alguien parado, criatura o persona, sin contarse a sí mismo: el mapa no las sabe y no se
    /// pueden pisar.</summary>
    public IReadOnlySet<(byte X, byte Y)> BaldosasOcupadas()
    {
        lock (_candado)
        {
            var ocupadas = new HashSet<(byte X, byte Y)>();
            foreach (var v in _visibles.Values)
                if (v.CharIndex != _miCharIndex) ocupadas.Add((v.X, v.Y));
            return ocupadas;
        }
    }

    /// <summary>Quién está parado en esa baldosa, criatura o persona, de lo que ve y sin contarse a sí mismo.</summary>
    public Visible? QuienEstaEn((byte X, byte Y) baldosa)
    {
        lock (_candado)
        {
            foreach (var v in _visibles.Values)
                if (v.CharIndex != _miCharIndex && v.X == baldosa.X && v.Y == baldosa.Y) return v;
            return null;
        }
    }

    /// <summary>Dónde está ese personaje ahora, si sigue a la vista.</summary>
    public Visible? Donde(short charIndex)
    {
        lock (_candado) return _visibles.TryGetValue(charIndex, out var v) ? v : null;
    }

    /// <summary>La primera ranura del inventario con un objeto que cumpla <paramref name="sirve"/>, o 0.</summary>
    public byte Ranura(Func<short, bool> sirve)
    {
        lock (_candado)
        {
            for (byte s = 1; s < _inventario.Length; s++)
                if (_inventario[s].Obj > 0 && _inventario[s].Cantidad > 0 && sirve(_inventario[s].Obj)) return s;
            return 0;
        }
    }

    /// <summary>Adelanta la posición propia un paso, como el cliente de una persona que mueve el muñeco antes de que
    /// llegue la respuesta: el servidor contesta cada paso con un <c>PosUpdate</c>, aceptado o rechazado, y ése corrige
    /// lo predicho. PARALIZADO NO SE PREDICE: ahí el servidor rechaza el paso sin mandar nada, y cada paso predicho
    /// quedaba como una posición que el bot creía tener.</summary>
    public void PredecirPaso(byte rumbo)
    {
        lock (_candado)
        {
            if (!_tengoPosicion || _paralizado) return;
            var (dx, dy) = Paso(rumbo);
            _x = (byte)Math.Clamp(_x + dx, 0, 255);
            _y = (byte)Math.Clamp(_y + dy, 0, 255);
        }
    }

    /// <summary>El desplazamiento de un rumbo: 1=Norte, 2=Este, 3=Sur, 4=Oeste.</summary>
    public static (int Dx, int Dy) Paso(byte rumbo) => rumbo switch
    {
        1 => (0, -1),
        2 => (1, 0),
        3 => (0, 1),
        _ => (-1, 0),
    };

    /// <summary>Hacia dónde hay que mirar para tener esa baldosa enfrente. Sólo tiene sentido con la baldosa
    /// pegada, que es la única desde la que se puede pegar a mano.</summary>
    public static byte RumboHacia(int dx, int dy)
    {
        if (Math.Abs(dx) > Math.Abs(dy)) return dx > 0 ? (byte)2 : (byte)4;
        return dy > 0 ? (byte)3 : (byte)1;
    }

    // ── Los paquetes que sí se leen ───────────────────────────────────────────────────────────────────

    public void Recibir(byte[] p)
    {
        if (p.Length == 0) return;
        var l = new Lector(p);
        try
        {
            switch ((ServerPacketId)l.Byte())
            {
                // CUÁL DE TODOS SOY. Los dos paquetes traen el mismo número y se escuchan LOS DOS a propósito:
                // el 23 llega antes del `CharacterCreate` propio y el 24 después (ver la secuencia de
                // SpawnIntoWorld). Atendiendo sólo al 24, cuando llegaba el CharacterCreate propio el bot
                // todavía no sabía que era él, no se guardaba la posición, y se quedaba esperando ubicarse
                // para siempre — quieto, conectado y sin hacer nada. Por eso además se rescata la posición del
                // que ya está a la vista, en vez de confiar en que el orden sea uno u otro.
                case ServerPacketId.UserIndexInServer:
                case ServerPacketId.UserCharIndexInServer:
                {
                    short mio = l.Entero();
                    lock (_candado)
                    {
                        _miCharIndex = mio;
                        if (_visibles.TryGetValue(mio, out var yo)) { _x = yo.X; _y = yo.Y; _tengoPosicion = true; _miEstado = yo.Estado; }
                    }
                    break;
                }

                case ServerPacketId.CharacterCreate:
                {
                    short ci = l.Entero();
                    l.Entero(); l.Entero();          // body, head
                    l.Byte();                        // heading
                    byte x = l.Byte(), y = l.Byte();
                    l.Entero(); l.Entero(); l.Entero(); l.Entero(); l.Entero();   // arma, escudo, casco, FX, FXLoops
                    string nombre = l.Cadena();
                    byte estado = l.Byte();
                    lock (_candado)
                    {
                        // El número de ficha si es criatura; 0 si es una persona. Un nombre vacío es el GM que
                        // se escondió con /SHOWNAME: tampoco es un NPC.
                        short ficha = short.TryParse(nombre, out var f) && f > 0 ? f : (short)0;
                        // Y EL NOMBRE DE LA PERSONA, para saber quién le habla (`NombreDe`).
                        _visibles[ci] = new Visible(ci, x, y, ficha, estado, ficha > 0 ? "" : SinElClan(nombre));
                        if (ci == _miCharIndex) { _x = x; _y = y; _tengoPosicion = true; _miEstado = estado; }
                    }
                    break;
                }

                // EL BANDO QUE CAMBIA sin volver a crear al personaje (volverse criminal, enlistarse). Sin nombre a la
                // vista el estado viaja en 0, y ése no se toma.
                case ServerPacketId.UpdateTagAndStatus:
                {
                    short ci = l.Entero();
                    string tag = l.Cadena();
                    byte estado = l.Byte();
                    if (estado == 0) break;
                    lock (_candado)
                    {
                        // Y EL NOMBRE, que cambia con el clan o con el /SHOWNAME de un GM.
                        if (_visibles.TryGetValue(ci, out var v))
                            _visibles[ci] = v with { Estado = estado, Nombre = v.EsNpc ? "" : SinElClan(tag) };
                        if (ci == _miCharIndex) _miEstado = estado;
                    }
                    break;
                }

                // EL BANDO QUE CAMBIA, como lo manda el original (CharStatus: charIndex + estado): enlistarse, volverse criminal.
                case ServerPacketId.CharStatus:
                {
                    short ci = l.Entero();
                    byte estado = l.Byte();
                    lock (_candado)
                    {
                        if (_visibles.TryGetValue(ci, out var v)) _visibles[ci] = v with { Estado = estado };
                        if (ci == _miCharIndex) _miEstado = estado;
                    }
                    break;
                }

                // LA FICHA DE UNA PERSONA, la respuesta al clic (VB6 WriteCharMsgStatus): de acá sólo el nivel, que viene escondido
                // (255) si está más de 5 por encima.
                case ServerPacketId.CharMsgStatus:
                {
                    short ci = l.Entero();
                    l.Byte(); l.Largo(); l.Entero(); l.Byte(); l.Byte();   // estado, vida en %, estados, estados extra, clase
                    short nivel = l.Entero();
                    lock (_candado) _nivelDe[ci] = (nivel, Environment.TickCount64);
                    break;
                }

                // LA FICHA DE ESTADÍSTICAS (VB6 WriteMiniStats): de acá sólo las bajas por bando.
                case ServerPacketId.MiniStats:
                {
                    int ciudadanos = l.Largo(), renegados = l.Largo();
                    l.Largo(); l.Entero();                    // usuarios matados, criaturas
                    l.Byte(); l.Byte(); l.Byte(); l.Largo();  // clase, raza, género, muertes
                    l.Byte();                                 // estado
                    int republicanos = l.Largo(), caos = l.Largo(), armada = l.Largo(), milicianos = l.Largo();
                    lock (_candado) _bajas = new Bajas(ciudadanos, renegados, republicanos, caos, armada, milicianos);
                    break;
                }

                case ServerPacketId.CharacterMove:
                {
                    short ci = l.Entero();
                    byte x = l.Byte(), y = l.Byte();
                    lock (_candado)
                    {
                        if (_visibles.TryGetValue(ci, out var v)) _visibles[ci] = v with { X = x, Y = y };
                        if (ci == _miCharIndex) { _x = x; _y = y; _tengoPosicion = true; }
                    }
                    break;
                }

                case ServerPacketId.CharacterRemove:
                {
                    short ci = l.Entero();
                    lock (_candado) _visibles.Remove(ci);
                    break;
                }

                case ServerPacketId.PosUpdate:
                {
                    byte x = l.Byte(), y = l.Byte();
                    lock (_candado)
                    {
                        _x = x; _y = y; _tengoPosicion = true;
                        OlvidarElPisoLejano();
                    }
                    break;
                }

                // CAMBIÓ DE MAPA. Lo que se ve se descarta entero —los personajes del mapa anterior ya no
                // están— y el bot toma este lugar como su nueva zona: las coordenadas de la anterior no
                // significan nada acá.
                case ServerPacketId.ChangeMap:
                {
                    short mapa = l.Entero();
                    lock (_candado)
                    {
                        _visibles.Clear();
                        _enElPiso.Clear();   // lo tirado era del mapa anterior
                        _mapa = mapa;
                        _mapaNuevo = true;
                    }
                    break;
                }

                case ServerPacketId.UpdateUserStats:
                {
                    int vidaMax = l.Entero(), vida = l.Entero();
                    int manaMax = l.Entero(), mana = l.Entero();
                    int enerMax = l.Entero(), ener = l.Entero();
                    int oro = l.Largo();
                    short nivel = l.Entero();
                    l.Largo();                                    // exp para subir
                    int experiencia = l.Largo();
                    l.Byte(); l.Byte();                           // agilidad, fuerza
                    byte hambre = l.Byte(); l.Byte();
                    byte sed = l.Byte();
                    lock (_candado)
                    {
                        _vidaMax = vidaMax; _vida = vida;
                        _manaMax = manaMax; _mana = mana;
                        _energiaMax = enerMax; _energia = ener;
                        _oro = oro; _experiencia = experiencia;
                        _nivel = nivel; _hambre = hambre; _sed = sed;
                        if (vida <= 0) AlMorir();
                    }
                    break;
                }

                case ServerPacketId.UpdateHP:
                    lock (_candado)
                    {
                        int antes = _vida;
                        _vida = l.Entero();
                        if (_vida < antes) _ultimaBajaDeVida = Environment.TickCount64;
                        if (_vida <= 0) AlMorir();
                    }
                    break;

                case ServerPacketId.UpdateMana:
                    lock (_candado) _mana = l.Entero();
                    break;

                case ServerPacketId.UpdateGold:
                    lock (_candado) _oro = l.Largo();
                    break;

                case ServerPacketId.UpdateExp:
                    lock (_candado) _experiencia = l.Largo();
                    break;

                // PUNTOS DE SKILL PARA REPARTIR. Van SUMANDO: el servidor avisa cuántos se ganaron en ESTE
                // nivel, no cuántos hay en total, así que dos niveles seguidos sin repartir dejan dos avisos.
                // Pedir más de los que hay es trampa y corta la conexión (HandleModifySkills), así que la
                // cuenta de acá tiene que ser exacta.
                case ServerPacketId.LevelUp:
                    lock (_candado) _puntosLibres += l.Entero();
                    break;

                case ServerPacketId.ChangeSpellSlot:
                {
                    byte slot = l.Byte();
                    short hechizo = l.Entero();
                    lock (_candado)
                        if (slot > 0 && slot < _hechizos.Length) _hechizos[slot] = hechizo;
                    break;
                }

                case ServerPacketId.ObjectCreate:
                {
                    byte x = l.Byte(), y = l.Byte();
                    short obj = l.Entero();
                    lock (_candado) _enElPiso[(x, y)] = obj;
                    break;
                }

                case ServerPacketId.ObjectDelete:
                {
                    byte x = l.Byte(), y = l.Byte();
                    lock (_candado) _enElPiso.Remove((x, y));
                    break;
                }

                // LAS RANURAS LLEGAN ANTES que el CommerceInit, las 25 y las vacías también (IniciarComercioNPC,
                // Comercio.bas:196-204), y pisan todas las de la tienda anterior: vaciar acá borraba la que llegaba.
                case ServerPacketId.CommerceInit:
                    lock (_candado) _comerciando = true;
                    break;

                // EL PANEL DEL ORO (goliath = 1) trae el oro de la bóveda; la bóveda de ítems lo manda en cero, así que ése no se lee.
                case ServerPacketId.BankInit:
                {
                    bool panelDelOro = l.Byte() != 0;
                    int enLaBoveda = l.Largo();
                    if (panelDelOro) lock (_candado) { _banco = enLaBoveda; _vecesQueVioElBanco++; }
                    else lock (_candado) _vecesQueVioLaBoveda++;   // la bóveda de ítems: las ranuras llegaron antes
                    break;
                }

                // UNA RANURA DE LA BÓVEDA DE ÍTEMS: llegan las cuarenta al abrirla, y la que cambia al retirar.
                case ServerPacketId.ChangeBankSlot:
                {
                    byte ranura = l.Byte();
                    short obj = l.Entero(), cant = l.Entero();
                    lock (_candado)
                        if (obj > 0 && cant > 0) _boveda[ranura] = (obj, cant); else _boveda.Remove(ranura);
                    break;
                }

                case ServerPacketId.CommerceEnd:
                    lock (_candado) { _comerciando = false; _tienda.Clear(); }
                    break;

                // EL CENTRO DE SUBASTAS (nuestro): el layout está en `ServerMessages.SubastasLista`.
                case ServerPacketId.SubastasLista:
                {
                    l.Byte();
                    int cuantas = l.Entero();
                    var filas = new List<ServerMessages.FilaDeSubasta>(Math.Max(0, cuantas));
                    for (int i = 0; i < cuantas; i++)
                    {
                        int id = l.Largo();
                        short obj = l.Entero(), cant = l.Entero();
                        int actual = l.Largo(), final = l.Largo();
                        string vendedor = l.Cadena();
                        int segundos = l.Largo();
                        filas.Add(new(id, obj, cant, actual, final, vendedor, segundos, l.Cadena()));
                    }
                    lock (_candado) { _subastas = filas; _vecesQueVioLasSubastas++; }
                    break;
                }

                case ServerPacketId.ChangeNPCInventorySlot:
                {
                    byte slot = l.Byte();
                    short cantidad = l.Entero();
                    float precio = l.Simple();
                    short obj = l.Entero();
                    lock (_candado) _tienda[slot] = (obj, cantidad, precio);
                    break;
                }

                case ServerPacketId.UpdateSta:
                    lock (_candado) _energia = l.Entero();
                    break;

                case ServerPacketId.UpdateHungerAndThirst:
                    lock (_candado) { _sed = l.Byte(); _hambre = l.Byte(); }
                    break;

                // AL COMER O TOMAR el servidor manda sólo lo que cambió, en paquetes sueltos (VB6 WriteUpdateHambre
                // y WriteUpdateSed); el combinado llega con el reloj del hambre. Sin leer estos, el bot seguía viendo
                // el hambre en cero después de comer y volvía a comer en cada vuelta: medido, doce manzanas para
                // pasar de 50 a 100, cuando alcanzaba con cinco.
                case ServerPacketId.UpdateHambre:
                    lock (_candado) _hambre = l.Byte();
                    break;

                case ServerPacketId.UpdateSed:
                    lock (_candado) _sed = l.Byte();
                    break;

                // SUBIÓ DE NIVEL. El servidor manda sólo los MÁXIMOS nuevos y el nivel (vida, maná, energía, nivel, y después
                // la experiencia para subir y la experiencia), no lo que le queda de cada cosa. La vida la deja llena como el
                // original (Modulo_UsUaRiOs.bas:575), y el cliente original la iguala al tope con este mismo paquete; el maná y la
                // energía quedan donde estaban, salvo con la regla de la casa `CuraAlSubirNivel`, que antes manda los tres
                // valores sueltos. Sin leerlo el bot seguía con los topes y el nivel del login.
                case ServerPacketId.UpdateUserStatsForLevel:
                {
                    int vidaMax = l.Entero(), manaMax = l.Entero(), enerMax = l.Entero();
                    short nivel = l.Entero();
                    l.Largo();                                    // exp para subir
                    int experiencia = l.Largo();
                    lock (_candado)
                    {
                        _vidaMax = vidaMax; _vida = vidaMax;
                        _manaMax = manaMax;
                        _energiaMax = enerMax;
                        _nivel = nivel; _experiencia = experiencia;
                    }
                    break;
                }

                // MEDITAR ES UN INTERRUPTOR, y lo mueve el servidor: contesta cada pedido con este paquete y lo manda también
                // cuando corta la meditación por su cuenta, al caminar o al recibir un golpe. Sin leerlo, el bot al que un
                // golpe levantaba volvía a sentarse con su propio pedido de levantarse.
                case ServerPacketId.MeditateToggle:
                    lock (_candado) _meditando = !_meditando;
                    break;

                // PARALIZADO: el mismo paquete lo prende y lo apaga. Mientras dura, el servidor rechaza el paso sin mandar
                // ningún PosUpdate, así que la posición no se predice (ver PredecirPaso).
                case ServerPacketId.ParalizeOK:
                    lock (_candado) _paralizado = !_paralizado;
                    break;

                // "Estás envenenado" (VB6 EfectoVeneno): es la única señal de que el veneno sigue encima.
                case ServerPacketId.LocaleMsg:
                {
                    short aviso = l.Entero();
                    if (aviso == AvisoDeVeneno) lock (_candado) _ultimoAvisoDeVeneno = Environment.TickCount64;
                    // Y el rechazo del casteo por falta de Magia, que es lo que dejaba al bot eligiendo para
                    // siempre un hechizo que no puede lanzar. Ver `Bot.SeLoRechazoPorMagia`.
                    else if (aviso == AvisoDeFaltaDeMagia) lock (_candado) _ultimoRechazoDeMagia = Environment.TickCount64;
                    // UNA SKILL SUBIÓ POR USARLA: el servidor no vuelve a mandar las 27, sólo este aviso. Es un paquete
                    // DELTA, y sin leerlo el bot seguiría creyendo que su skill de fabricar está donde la dejó el reparto.
                    else if (aviso == AvisoDeSkillQueSube && SkillQueSubio(l.Cadena()) is { } subio)
                        lock (_candado)
                            if (subio.Skill >= 1 && subio.Skill < _skills.Length) _skills[subio.Skill] = (byte)Math.Clamp(subio.Valor, 0, 255);
                    break;
                }

                case ServerPacketId.SendSkills:
                    lock (_candado)
                    {
                        for (int i = 1; i < _skills.Length; i++) _skills[i] = l.Byte();
                        _sabeSusSkills = true;
                    }
                    break;

                case ServerPacketId.ChangeInventorySlot:
                {
                    byte slot = l.Byte();
                    short obj = l.Entero(), cantidad = l.Entero();
                    bool puesto = l.Byte() != 0;
                    lock (_candado)
                        if (slot > 0 && slot < _inventario.Length) _inventario[slot] = (obj, cantidad, puesto);
                    break;
                }

                // LA COMIDA Y LA BEBIDA SE DESCUENTAN CON EL PAQUETE CORTO. Al comer o tomar, el servidor no reenvía la
                // ranura entera: manda sólo la cantidad nueva (acción 3, y en cero la ranura queda vacía), y es el único
                // aviso de lo consumido. Sin leerlo el bot seguía contando las botellas que ya se había tomado: no salía
                // a comprar agua y, terminada la de verdad, pedía tomar de una ranura vacía que el servidor ignora.
                // Medido: dos Pescadoras con 787 y 5962 monedas, la sed en cero y ni una gota en la mochila al final de
                // la corrida. Las acciones 1 y 2 son equipar y desequipar (writeChangeInventorySlotUser, Protocol.bas:17447).
                case ServerPacketId.ChangeInventorySlotUser:
                {
                    byte slot = l.Byte(), accion = l.Byte();
                    short valor = l.Entero();
                    lock (_candado)
                        if (slot > 0 && slot < _inventario.Length && _inventario[slot].Obj > 0)
                        {
                            var (obj, cantidad, puesto) = _inventario[slot];
                            _inventario[slot] = accion switch
                            {
                                1 => (obj, cantidad, true),
                                2 => (obj, cantidad, false),
                                3 when valor <= 0 => ((short)0, (short)0, false),
                                3 => (obj, valor, puesto),
                                _ => (obj, cantidad, puesto),
                            };
                        }
                    break;
                }

                // LO QUE LE SACÓ A LO QUE LE PEGA (ver `TomarLoQuePego`). Sobre la cabeza de otro es el golpe de otro.
                case ServerPacketId.ChatOverHeadLocale:
                {
                    short ci = l.Entero();
                    int valor = l.Largo();
                    if (l.Byte() != ModoDelGolpe) break;
                    lock (_candado)
                        if (ci == _miCharIndex)
                        {
                            _golpesDados++;
                            if (valor != 0) _golpesQueEntraron++;
                            _danoDado += valor;
                        }
                    break;
                }

                // Y LA PUÑALADA, que va como texto y sin número (VB6 Trabajo.bas:1800).
                // Y EL GRITO, EL GLOBAL Y EL SUSURRO: ver `TomarLoOido`. La fuente va después del texto y se mira antes de leerlo.
                case ServerPacketId.ConsoleMsg:
                {
                    if (DanoDeLaPunalada(p) is { } punalada) { lock (_candado) _danoDado += punalada; break; }
                    // [id][largo, 2 bytes][texto][fuente]
                    int largo = (short)(p[1] | (p[2] << 8));
                    if (largo <= 0 || 3 + largo >= p.Length) break;
                    if (ModoDeLaFuente(p[3 + largo]) is not { } modo)
                    {
                        // LO QUE EL CENTRO DE SUBASTAS DEJA EN LA BÓVEDA: el oro de una venta suma a lo que sabe que guardó.
                        string aviso = l.Cadena();
                        if (OroQueDejoLaSubasta(aviso) is { } oro) MovioAlBanco(oro);
                        else if (ObjetoQueDejoLaSubasta(aviso) is { } cual) lock (_candado) if (_loQueDejoLaSubasta.Count < OidoMax) _loQueDejoLaSubasta.Add(cual);
                        break;
                    }
                    if (LineaDeChat(l.Cadena()) is { } chat) lock (_candado) Oir(chat.Quien, chat.Texto, modo);
                    break;
                }

                // LA CHARLA: el globo sobre la cabeza de una persona a la vista. Los otros modos llegan también por la consola.
                case ServerPacketId.ChatOverHead:
                {
                    string texto = l.Cadena();
                    short ci = l.Entero();
                    if (l.Byte() != (byte)ComoLoDijo.Charla) break;
                    lock (_candado)
                        if (ci != _miCharIndex && _visibles.TryGetValue(ci, out var quien) && !quien.EsNpc)
                            Oir(quien.Nombre, texto, ComoLoDijo.Charla);
                    break;
                }
            }
        }
        // Un paquete más corto de lo que dice su layout no puede tumbar al bot: se descarta y se sigue. Pasa
        // con los de layout variable que acá no se leen, si alguna vez cambiara un id.
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    /// <summary>Cursor sobre un paquete ya frameado. Los enteros son de 16 bits con signo y los largos de 32,
    /// little-endian, como los escribe <see cref="ByteQueueWriter"/>.</summary>
    private sealed class Lector(byte[] datos)
    {
        private int _i;

        public byte Byte() => datos[_i++];

        public short Entero()
        {
            short v = (short)(datos[_i] | (datos[_i + 1] << 8));
            _i += 2;
            return v;
        }

        public int Largo()
        {
            int v = datos[_i] | (datos[_i + 1] << 8) | (datos[_i + 2] << 16) | (datos[_i + 3] << 24);
            _i += 4;
            return v;
        }

        public float Simple() { float v = BitConverter.ToSingle(datos, _i); _i += 4; return v; }

        public string Cadena()
        {
            int largo = (short)(datos[_i] | (datos[_i + 1] << 8));
            _i += 2;
            if (largo <= 0 || _i + largo > datos.Length) return "";
            string s = Codec.Ansi.GetString(datos, _i, largo);
            _i += largo;
            return s;
        }
    }
}
