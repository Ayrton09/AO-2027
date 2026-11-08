using CoverAO.Server.Data;

namespace CoverAO.Server.Net;

/// <summary>Lo que un <see cref="Bot"/> necesita del servidor para vivir: a quién le habla, con qué reglas, y
/// dónde anotar la contraseña de su cuenta.</summary>
public sealed class EntornoDeBots
{
    public PacketDispatcher Dispatcher { get; }
    public ServerIni Ini { get; }
    public long AuthTimeoutMs { get; }
    public int MaxInboundBytes { get; }
    /// <summary>Las fichas del juego. El bot las mira para lo mismo que una persona mira al bicho que tiene
    /// enfrente y decide si le va a ganar: si la criatura ataca, si se la puede atacar, y cuánto pega.</summary>
    public GameData Datos { get; }
    /// <summary>Los mapas: cómo ir de un lado a otro. Uno solo para todos los bots — se arma una vez y
    /// después no se toca.</summary>
    public Navegante Mapas { get; }
    internal MetasDeBots? Metas { get; init; }
    internal Func<short, int> DemandaDeSubasta { get; init; }
    /// <summary>El hogar de un personaje conectado, por su nombre (<see cref="PacketDispatcher.HogarDe"/>): lo que su dueño sabe
    /// porque lo eligió, y el bot no recibe por el protocolo. Null si no se sabe.</summary>
    public Func<string, byte?> HogarDe { get; }
    private readonly CredencialesDeBots _credenciales;
    private readonly Persistence? _db;

    /// <param name="db">Para mirar, antes de cada vida, si el nombre ya es de otro (<see cref="NombreAjenoAsync"/>). Sin ella
    /// no se mira.</param>
    /// <param name="hogarDe">El hogar por el nombre; sin él, el del <paramref name="dispatcher"/>.</param>
    public EntornoDeBots(PacketDispatcher dispatcher, ServerIni ini, long authTimeoutMs, int maxInboundBytes,
        GameData datos, Navegante mapas, CredencialesDeBots credenciales, Persistence? db = null,
        Func<string, byte?>? hogarDe = null)
    {
        HogarDe = hogarDe ?? (nombre => dispatcher?.HogarDe(nombre));
        DemandaDeSubasta = objeto => dispatcher?.DemandaDeSubasta(objeto) ?? 0;
        Dispatcher = dispatcher;
        Ini = ini;
        AuthTimeoutMs = authTimeoutMs;
        MaxInboundBytes = maxInboundBytes;
        Datos = datos;
        Mapas = mapas;
        _credenciales = credenciales;
        _db = db;
    }

    public void GuardarClave(string nombre, string clave) => _credenciales.Guardar(nombre, clave);

    /// <summary>¿El nombre ya es de otro? (<see cref="PobladoDeBots.NombreAjeno"/>) Null si no se supo: la base no contestó en el
    /// plazo con que la espera el ingreso, o no hay base.</summary>
    internal Task<bool?> NombreAjenoAsync(string nombre, CancellationToken ct) => _db is null
        ? Task.FromResult<bool?>(null)
        : PobladoDeBots.NombreAjenoAsync(nombre, _db, Dispatcher?.PlazoDeLaLecturaDeEntrada ?? TimeSpan.FromSeconds(10), ct);
}

/// <summary>
/// Las contraseñas de las cuentas de los bots, en <c>data/bots.txt</c>.
///
/// <para><b>Por qué existe el archivo.</b> Los bots tienen que poder VOLVER a entrar a la misma cuenta, o
/// perderían el personaje —y con él todo lo que progresaron— en cada reinicio. Y la contraseña no puede
/// derivarse del nombre: eso la haría calculable por cualquiera que sepa a quién pertenece, o sea una puerta
/// de atrás a la cuenta. Entonces se sortea una vez y se anota.</para>
///
/// <para>Vive adentro de <c>data/</c>, que es la carpeta que el armador NO empaqueta: las contraseñas no
/// viajan en el zip. Cada servidor sortea las suyas la primera vez que enciende sus bots.</para>
/// </summary>
public sealed class CredencialesDeBots
{
    private const string Encabezado =
        "; Las cuentas de los bots (la clave Bots del Server.ini). Las crea el servidor solo.\r\n" +
        "; SON CONTRASENAS: no compartir este archivo ni copiarlo afuera de esta instalacion.\r\n";

    private readonly string _ruta;
    private readonly Dictionary<string, string> _claves = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _candado = new();
    /// <summary>Hay una clave en memoria que no llegó al archivo.</summary>
    private bool _sinGuardar;

    /// <summary>El archivo está y no se pudo leer: un permiso que no lo deja, otro programa que lo tiene tomado. Sin sus claves, las
    /// cuentas que ya tienen los bots no se abren, y la primera clave nueva que se guardara lo pisaría con ésa sola.</summary>
    public bool NoSePudoLeer { get; }

    public CredencialesDeBots(string dataRoot)
    {
        _ruta = Path.Combine(dataRoot, "bots.txt");
        if (!File.Exists(_ruta)) return;
        try
        {
            foreach (var linea in File.ReadAllLines(_ruta))
            {
                if (linea.Length == 0 || linea[0] == ';' || linea[0] == '#') continue;
                int i = linea.IndexOf('=');
                if (i <= 0) continue;
                _claves[linea[..i].Trim()] = linea[(i + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NoSePudoLeer = true;
            Console.Error.WriteLine($"[bots] no pude leer {_ruta}: {ex.Message}");
        }
    }

    public string? Clave(string nombre)
    {
        lock (_candado) return _claves.TryGetValue(nombre, out var c) ? c : null;
    }

    /// <summary>Anota la contraseña de un bot y reescribe el archivo. Se llama una vez por bot en la vida de
    /// una instalación, así que reescribirlo entero no cuesta nada y evita un archivo a medio escribir.
    ///
    /// <para>Si el archivo no se pudo escribir (un antivirus o una sincronización que lo tiene tomado), la clave queda
    /// pendiente y se vuelve a escribir en el próximo llamado, aunque sea con la misma clave: el bot lo llama otra vez al
    /// entrar. Con todo ya en el archivo, ese llamado no escribe nada. Devuelve si quedó en el archivo.</para></summary>
    public bool Guardar(string nombre, string clave)
    {
        lock (_candado)
        {
            if (!_sinGuardar && _claves.TryGetValue(nombre, out var anotada) && anotada == clave) return true;
            _claves[nombre] = clave;
            var texto = new System.Text.StringBuilder(Encabezado);
            foreach (var par in _claves.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                texto.Append(par.Key).Append('=').Append(par.Value).Append("\r\n");
            try
            {
                EscrituraAtomica.Escribir(_ruta, texto.ToString(), new System.Text.UTF8Encoding(false));
                _sinGuardar = false;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _sinGuardar = true;
                Console.Error.WriteLine($"[bots] no pude guardar {_ruta}: {ex.Message}");
                return false;
            }
        }
    }
}

/// <summary>
/// Los bots que este servidor tiene poblando el mundo. Apagados por defecto: el que hostea decide si los
/// quiere y cuántos, con la clave <c>Bots</c> del <c>Server.ini</c>.
///
/// <para><b>Nunca en modo fixture.</b> Los flows de paridad miden qué paquetes salen ante una acción, y un
/// bot caminando cerca mete apariciones, desapariciones y movimientos que el flow no pidió. Con doscientos
/// dieciocho flows corriendo contra servidores de prueba, un solo bot suelto los volvería intermitentes de
/// una forma dificilísima de rastrear. Por eso <c>--fixture</c> los apaga y lo dice, en vez de confiar en que
/// ningún ini de prueba los prenda por accidente.</para>
/// </summary>
public sealed class PobladoDeBots
{
    /// <summary>Los nombres que puede tomar un bot, y por lo tanto el techo de cuántos puede haber. Se ven
    /// como nombres de jugador porque de eso se trata; el que hostea los reconoce por la cuenta, que lleva el
    /// sufijo <c>_bot</c>, y por el aviso del arranque.</summary>
    public static readonly IReadOnlyList<string> Nombres = new[]
    {
        "Aldo", "Bruma", "Ciro", "Dalia", "Elmo", "Fabia", "Gonzo", "Hilda",
        "Ivo", "Juana", "Kael", "Lira", "Milo", "Nadia", "Osmar", "Petra",
        "Quim", "Rufo", "Sila", "Tulio", "Ulma", "Vero", "Wilmar", "Xena",
        "Yago", "Zaira", "Bruno", "Celia", "Dorian", "Enzo", "Fausto", "Greta",
        "Homero", "Ilse", "Jonas", "Karim", "Lucho", "Maga", "Nilo", "Otto",
        "Pina", "Ramiro", "Sonia", "Tobal", "Urso", "Vito", "Wanda", "Zoe",
        "Abril", "Benito", "Carmela", "Dario", "Elena", "Fito", "Gala", "Hugo",
        "Ines", "Julian", "Lola", "Mateo", "Nora", "Oscar", "Paula", "Ruben",
        "Selva", "Tadeo", "Uma", "Valen", "Walter", "Ximena", "Yamila", "Zeno",
        "Amparo", "Bauti", "Chela", "Delfina", "Emilio", "Flora", "Gaspar", "Hebe",
        "Ismael", "Jazmin", "Lautaro", "Mirta", "Nahuel", "Olga", "Pancho", "Rocio",
        "Santino", "Tere", "Ulises", "Vilma", "Yanina", "Zulema", "Alma", "Cosme",
    };

    /// <summary>LOS DE RESERVA: el nombre de <see cref="Nombres"/> que ya es de un jugador (<see cref="NombreAjeno"/>) deja su
    /// lugar al primero de éstos que esté libre, así un <c>Bots=96</c> son 96 aunque alguien se haya quedado con uno (auditoría
    /// del 12/10: hasta ahí era un bot menos). En <see cref="LugarEnElPoblado"/> van después de los de la lista.</summary>
    public static readonly IReadOnlyList<string> Suplentes = new[]
    {
        "Aurelio", "Brenda", "Camilo", "Diana", "Ernesto", "Fermin", "Gisela", "Hernan",
        "Irene", "Joaquin", "Leandro", "Marisa", "Nemesio", "Ofelia", "Pascual", "Renata",
        "Sabino", "Telma", "Valeria", "Anselmo", "Berta", "Cirilo", "Dora", "Eladio",
    };

    /// <summary>En qué lugar del poblado está ese nombre (los de reserva, después de los de la lista), o -1. Es lo que decide
    /// qué clase le toca: ver <see cref="Bot.Reparto"/>.</summary>
    public static int LugarEnElPoblado(string nombre)
    {
        for (int i = 0; i < Nombres.Count; i++)
            if (string.Equals(Nombres[i], nombre, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < Suplentes.Count; i++)
            if (string.Equals(Suplentes[i], nombre, StringComparison.OrdinalIgnoreCase)) return Nombres.Count + i;
        return -1;
    }

    /// <summary>Los nombres de <paramref name="cantidad"/> bots: los de la lista en orden y después los de reserva, salteando el
    /// que el servidor no aceptaría como nombre (las mismas reglas que para un jugador) y el que ya es de otro.</summary>
    internal static List<string> NombresParaArrancar(int cantidad, Func<string, bool> esAjeno,
        Func<string, byte?>? claseGuardada = null)
    {
        var disponibles = Nombres.Concat(Suplentes)
            .Where(n => EsNombreDeBot(n) && !esAjeno(n))
            .ToList();
        var elegidos = disponibles.Take(Math.Max(0, cantidad)).ToList();
        if (claseGuardada is null || cantidad < Bot.Reparto.Count) return elegidos;
        byte Clase(string nombre) => claseGuardada(nombre) ?? Bot.ClaseQueLeToca(LugarEnElPoblado(nombre));
        // Los personajes anteriores al reparto metalúrgico conservan su clase. Dos suplentes cubren los oficios
        // que falten sin modificar personajes guardados ni superar la cantidad configurada.
        foreach (byte oficio in new byte[] { 14, 12 })
        {
            if (elegidos.Any(n => Clase(n) == oficio)) continue;
            string? relevo = disponibles.FirstOrDefault(n => !elegidos.Contains(n) && Clase(n) == oficio);
            int lugar = elegidos.FindLastIndex(n => Clase(n) is not (14 or 12));
            if (relevo is not null && lugar >= 0) elegidos[lugar] = relevo;
        }
        return elegidos;
    }

    /// <summary>¿El servidor lo aceptaría como nombre de personaje, y el de su cuenta?</summary>
    private static bool EsNombreDeBot(string nombre) => Naming.IsValidCharName(nombre) && Naming.IsValidName(nombre + "_bot");

    /// <summary>Cuánto se espera entre el arranque de un bot y el del siguiente. Entran de a poco por dos
    /// motivos: cada alta de cuenta es un PBKDF2, y una tanda de gente que aparece toda junta en el mismo
    /// segundo no se parece a nadie entrando a jugar.</summary>
    private const int EntreUnoYOtroMs = 1_500;

    /// <summary>LOS NUEVOS ENTRAN POR TANDAS: un personaje que se crea nace en el pueblo de inicio, y dieciocho recién nacidos en la
    /// misma plaza se reparten un coto que no da para tantos (medido el 06/10 en `early100` a `early144`: la primera búsqueda de coto
    /// los mandaba al 58 y a Lindos por "lleno"). De a <see cref="NuevosPorTanda"/>, la tanda anterior ya salió de la cuna cuando
    /// llega la siguiente. Los que ya tienen personaje entran como siempre: cada uno vuelve a donde estaba.</summary>
    public const int NuevosPorTanda = 6;

    /// <summary>Cuánto se espera entre una tanda de nuevos y la siguiente.</summary>
    public const int EntreTandasMs = 5 * 60_000;

    /// <summary>Cuánto espera para entrar el bot que es el <paramref name="orden"/>-ésimo de los suyos (nuevos o con personaje).</summary>
    public static int RetardoDeEntrada(bool esNuevo, int orden) =>
        esNuevo ? orden / NuevosPorTanda * EntreTandasMs + orden % NuevosPorTanda * EntreUnoYOtroMs
                : orden * EntreUnoYOtroMs;

    private readonly List<Bot> _bots = new();
    public IReadOnlyList<Bot> Bots { get { lock (_bots) return _bots.ToList(); } }

    /// <summary>Los nombres que ya tuvo un bot en esta vuelta del servidor, y los que ya eran de otro al arrancar.</summary>
    private readonly HashSet<string> _usados = new(StringComparer.OrdinalIgnoreCase);
    private readonly EntornoDeBots _entorno;
    private readonly CredencialesDeBots _credenciales;

    internal PobladoDeBots(EntornoDeBots entorno, CredencialesDeBots credenciales, IEnumerable<string> usados)
    {
        _entorno = entorno;
        _credenciales = credenciales;
        _usados.UnionWith(usados);
    }

    /// <summary>
    /// ¿El nombre ya es de otro? Un personaje con ese nombre en una cuenta que no es la del bot, o la cuenta
    /// <c>&lt;nombre&gt;_bot</c> registrada por una persona. El bot no podría entrar nunca: el servidor le contesta que el
    /// personaje o la cuenta ya existen, y en cada arranque gastaba sus tres intentos y se quedaba afuera.
    /// </summary>
    internal static bool NombreAjeno(string nombre, Persistence db)
        => AjenoSegunSuCuenta(nombre, db, db.AccountExists(nombre + "_bot") ? db.LoadAccount(nombre + "_bot") : null);

    /// <summary>Lo mismo, desde un bot que va a entrar, sin esperar a la base más de <paramref name="plazo"/>: null si no
    /// contestó.</summary>
    internal static async Task<bool?> NombreAjenoAsync(string nombre, Persistence db, TimeSpan plazo, CancellationToken ct)
    {
        string cuenta = nombre + "_bot";
        Account? suya = null;
        if (db.AccountExists(cuenta))
        {
            using var abandono = CancellationTokenSource.CreateLinkedTokenSource(ct);
            abandono.CancelAfter(plazo);
            try { suya = await db.LeerCuentaAsync(cuenta, abandono.Token); }
            catch (Exception) { return null; }
        }
        return AjenoSegunSuCuenta(nombre, db, suya);
    }

    /// <summary>La pregunta de <see cref="NombreAjeno"/>, con <paramref name="suya"/> la cuenta <c>_bot</c> del nombre, o null si
    /// no existe.</summary>
    private static bool AjenoSegunSuCuenta(string nombre, Persistence db, Account? suya)
    {
        if (suya is null) return db.CharacterExists(nombre);
        if (!suya.EsBot) return true;
        return db.CharacterExists(nombre) && !suya.Characters.Contains(nombre, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EL QUE RELEVA AL BOT CUYO NOMBRE PASÓ A SER DE OTRO —un jugador lo tomó después del arranque (auditoría del 13/10: hasta
    /// ahí el bot faltaba hasta reiniciar)—: el primer nombre libre en el orden del arranque, los de la lista y después los de
    /// reserva, que es el que tomaría el próximo arranque (<see cref="NombresParaArrancar"/>). Si también es de otro, el bot
    /// nuevo lo ve antes de entrar y lo releva el siguiente. Null si no queda ninguno.
    /// </summary>
    internal Bot? Relevo()
    {
        string? nombre;
        lock (_usados)
        {
            nombre = Nombres.Concat(Suplentes).FirstOrDefault(n => EsNombreDeBot(n) && !_usados.Contains(n));
            if (nombre is null) return null;
            _usados.Add(nombre);
        }
        var bot = new Bot(nombre, _credenciales.Clave(nombre), _entorno, semilla: Environment.TickCount + LugarEnElPoblado(nombre) * 7919);
        lock (_bots) _bots.Add(bot);
        return bot;
    }

    /// <summary>La vida de un bot y la de los que lo relevan, si deja el nombre porque ya es de otro (<see cref="Bot.VivirAsync"/>).</summary>
    internal async Task VivirConRelevosAsync(Bot bot, CancellationToken ct)
    {
        while (await bot.VivirAsync(ct))
        {
            if (Relevo() is not { } otro)
            {
                Console.WriteLine($"[bot {bot.Nombre}] no queda un nombre libre que lo releve.");
                return;
            }
            Console.WriteLine($"[bot {bot.Nombre}] lo releva {otro.Nombre}.");
            bot = otro;
        }
    }

    /// <summary>Enciende <paramref name="cantidad"/> bots. Devuelve null si no hay ninguno que encender.</summary>
    public static PobladoDeBots? Arrancar(int cantidad, PacketDispatcher dispatcher, ServerIni ini, Persistence db,
        string dataRoot, long authTimeoutMs, int maxInboundBytes, GameData datos, CancellationToken ct)
    {
        if (cantidad <= 0) return null;
        var credenciales = new CredencialesDeBots(dataRoot);
        if (credenciales.NoSePudoLeer)
        {
            Console.WriteLine($"BOTS: {cantidad} pedidos, pero data/bots.txt no se pudo leer y no arrancan: sin sus claves no entran " +
                              "a sus cuentas, y la primera clave nueva lo pisaría. Arreglá el permiso del archivo y reiniciá.");
            return null;
        }
        var entorno = new EntornoDeBots(dispatcher, ini, authTimeoutMs, maxInboundBytes, datos,
                                        new Navegante(datos), credenciales, db) { Metas = new MetasDeBots(dataRoot) };

        // Se filtran por las mismas reglas de nombre que el servidor le aplica a un jugador: si alguna vez la
        // lista de nombres prohibidos veta uno de estos, el bot no arranca en vez de intentar y ser rechazado.
        // Y se saltea el nombre que ya es de otro (NombreAjeno): su lugar lo toma el siguiente, y al final los de reserva.
        var ajenos = Nombres.Concat(Suplentes).Where(n => NombreAjeno(n, db)).ToList();
        var clases = new Dictionary<string, byte?>();
        byte? ClaseGuardada(string nombre)
        {
            if (!clases.TryGetValue(nombre, out var clase))
                clases[nombre] = clase = db.CharacterExists(nombre) ? db.LoadCharacter(nombre, 0, nombre + "_bot")?.Class : null;
            return clase;
        }
        var elegidos = NombresParaArrancar(cantidad, ajenos.Contains, ClaseGuardada);
        var poblado = new PobladoDeBots(entorno, credenciales, ajenos.Concat(elegidos));
        int cuantos = elegidos.Count;
        int nuevos = 0, conPersonaje = 0;

        for (int i = 0; i < cuantos; i++)
        {
            string nombre = elegidos[i];
            string? clave = credenciales.Clave(nombre);
            var bot = new Bot(nombre, clave, entorno, semilla: Environment.TickCount + i * 7919);
            lock (poblado._bots) poblado._bots.Add(bot);   // el primero ya puede estar llamando a un relevo
            int retardo = clave is null ? RetardoDeEntrada(true, nuevos++) : RetardoDeEntrada(false, conPersonaje++);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(retardo, ct); } catch (OperationCanceledException) { return; }
                // Nadie espera esta tarea: un error suelto acá se perdía sin dejar rastro.
                try { await poblado.VivirConRelevosAsync(bot, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine($"[bot {bot.Nombre}] se cayó su vida entera: {ex}"); }
            }, CancellationToken.None);
        }

        Console.WriteLine($"BOTS: {cuantos} ({string.Join(", ", elegidos)})");
        if (ajenos.Count > 0)
            Console.WriteLine($"   no entran como bots, porque el nombre (o su cuenta _bot) ya es de un jugador: {string.Join(", ", ajenos)}.");
        if (nuevos > NuevosPorTanda)
            Console.WriteLine($"   {nuevos} son nuevos: entran de a {NuevosPorTanda} cada {EntreTandasMs / 60_000} minutos.");
        if (cantidad > cuantos)
            Console.WriteLine($"   pedidos {cantidad}, pero hay {cuantos} nombres disponibles.");
        return poblado;
    }
}
