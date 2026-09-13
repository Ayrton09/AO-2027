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
    private readonly CredencialesDeBots _credenciales;

    public EntornoDeBots(PacketDispatcher dispatcher, ServerIni ini, long authTimeoutMs, int maxInboundBytes,
        GameData datos, Navegante mapas, CredencialesDeBots credenciales)
    {
        Dispatcher = dispatcher;
        Ini = ini;
        AuthTimeoutMs = authTimeoutMs;
        MaxInboundBytes = maxInboundBytes;
        Datos = datos;
        Mapas = mapas;
        _credenciales = credenciales;
    }

    public void GuardarClave(string nombre, string clave) => _credenciales.Guardar(nombre, clave);
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
        catch (IOException ex) { Console.Error.WriteLine($"[bots] no pude leer {_ruta}: {ex.Message}"); }
    }

    public string? Clave(string nombre)
    {
        lock (_candado) return _claves.TryGetValue(nombre, out var c) ? c : null;
    }

    /// <summary>Anota la contraseña de un bot y reescribe el archivo. Se llama una vez por bot en la vida de
    /// una instalación, así que reescribirlo entero no cuesta nada y evita un archivo a medio escribir.</summary>
    public void Guardar(string nombre, string clave)
    {
        lock (_candado)
        {
            _claves[nombre] = clave;
            var texto = new System.Text.StringBuilder(Encabezado);
            foreach (var par in _claves.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                texto.Append(par.Key).Append('=').Append(par.Value).Append("\r\n");
            try { EscrituraAtomica.Escribir(_ruta, texto.ToString(), new System.Text.UTF8Encoding(false)); }
            catch (IOException ex) { Console.Error.WriteLine($"[bots] no pude guardar {_ruta}: {ex.Message}"); }
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
    };

    /// <summary>En qué lugar de la lista está ese nombre, o -1. Es lo que decide qué clase le toca: ver
    /// <see cref="Bot.Reparto"/>.</summary>
    public static int LugarEnElPoblado(string nombre)
    {
        for (int i = 0; i < Nombres.Count; i++)
            if (string.Equals(Nombres[i], nombre, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Cuánto se espera entre el arranque de un bot y el del siguiente. Entran de a poco por dos
    /// motivos: cada alta de cuenta es un PBKDF2, y una tanda de gente que aparece toda junta en el mismo
    /// segundo no se parece a nadie entrando a jugar.</summary>
    private const int EntreUnoYOtroMs = 1_500;

    private readonly List<Bot> _bots = new();
    public IReadOnlyList<Bot> Bots => _bots;

    private PobladoDeBots() { }

    /// <summary>Enciende <paramref name="cantidad"/> bots. Devuelve null si no hay ninguno que encender.</summary>
    public static PobladoDeBots? Arrancar(int cantidad, PacketDispatcher dispatcher, ServerIni ini,
        string dataRoot, long authTimeoutMs, int maxInboundBytes, GameData datos, CancellationToken ct)
    {
        if (cantidad <= 0) return null;
        var credenciales = new CredencialesDeBots(dataRoot);
        var entorno = new EntornoDeBots(dispatcher, ini, authTimeoutMs, maxInboundBytes, datos,
                                        new Navegante(datos), credenciales);
        var poblado = new PobladoDeBots();

        // Se filtran por las mismas reglas de nombre que el servidor le aplica a un jugador: si alguna vez la
        // lista de nombres prohibidos veta uno de estos, el bot no arranca en vez de intentar y ser rechazado.
        var usables = Nombres.Where(n => Naming.IsValidCharName(n) && Naming.IsValidName(n + "_bot")).ToList();
        int cuantos = Math.Min(cantidad, usables.Count);

        for (int i = 0; i < cuantos; i++)
        {
            string nombre = usables[i];
            var bot = new Bot(nombre, credenciales.Clave(nombre), entorno, semilla: Environment.TickCount + i * 7919);
            poblado._bots.Add(bot);
            int retardo = i * EntreUnoYOtroMs;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(retardo, ct); } catch (OperationCanceledException) { return; }
                await bot.VivirAsync(ct);
            }, CancellationToken.None);
        }

        Console.WriteLine($"BOTS: {cuantos} ({string.Join(", ", poblado._bots.Select(b => b.Nombre))})");
        if (cantidad > usables.Count)
            Console.WriteLine($"   pedidos {cantidad}, pero hay {usables.Count} nombres disponibles.");
        return poblado;
    }
}
