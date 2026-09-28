using System.Collections.Concurrent;
using System.Threading.Tasks;
using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// Account/character persistence. High-level logic (create, seed, summaries) is backend-agnostic; what
/// gets stored goes through an <see cref="IAlmacen"/> —cuentas, fichas de personaje y lo que guarda el
/// mundo—, so switching backends changes nothing about the domain.
///
/// <para><b>Todo pasa por la cola de la base</b> (<see cref="ColaDeLaBase"/>): un solo escritor, en orden. Las
/// escrituras se encolan y siguen; las lecturas que hace el hilo del mundo se piden con respuesta (las
/// <c>...Async</c>) y vuelven por el loop; las sincrónicas son para quien NO es el loop —el pool, el panel, el
/// arranque, los tests— y, si igual las llama el loop, lo frenan y se avisa en el log. Ver fase5/postgres/DISENO.md,
/// sección 3.</para>
/// </summary>
public sealed class Persistence : IDisposable
{
    private readonly IAlmacen _almacen;
    private readonly ColaDeLaBase _cola;
    private readonly Leveling _leveling;
    private readonly Random _rng = new();   // tiradas de vida/stamina al nacer (VB6 RandomNumber)

    /// <summary>
    /// QUIÉN EXISTE, EN MEMORIA: los nombres de los personajes vivos y de las cuentas. "¿Existe tal personaje?" lo
    /// preguntan quince lugares del loop —el <c>/BAN</c>, el correo, los amigos— y cada pregunta al almacén sería disco o
    /// un viaje a la base. Se carga al arrancar y se actualiza con cada alta y cada borrado; como todas pasan por acá,
    /// no puede quedar desfasado.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _personajes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _cuentas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _lecturasAvisadas = new();

    public Persistence(IAlmacen almacen, Leveling leveling)
    {
        _almacen = almacen;
        _leveling = leveling;
        foreach (var nombre in almacen.NombresDePersonajes()) _personajes[nombre] = 0;
        foreach (var nombre in almacen.NombresDeCuentas()) _cuentas[nombre] = 0;
        _cola = new ColaDeLaBase(almacen);
    }

    /// <summary>Sobre un almacén de TEXTO (los archivos o el SQLite viejo), con el adaptador de siempre.</summary>
    public Persistence(IStore store, Leveling leveling) : this(new AlmacenDeTexto(store), leveling) { }

    /// <summary>Cierra la cola esperando lo que quedaba encolado.</summary>
    public void Dispose() => _cola.Dispose();

    public bool AccountExists(string name) => _cuentas.ContainsKey(name);
    public bool CharacterExists(string name) => _personajes.ContainsKey(name);

    /// <summary>Aparta el personaje sin destruirlo (borrado de personaje, 1b). Ver <see cref="IAlmacen.ApartarPersonaje"/>.
    /// Sincrónico: lo llama el borrado de personaje, que corre en el pool.</summary>
    public bool ArchivarCharacter(string name)
    {
        bool apartado = Esperar("apartar un personaje", name, a => a.ApartarPersonaje(name));
        if (apartado) _personajes.TryRemove(name, out _);
        return apartado;
    }

    /// <summary>
    /// La respuesta de la cola, esperándola. Para los que NO son el loop: el pool (la contraseña, el alta de cuentas, el
    /// borrado), el panel, el arranque y los tests. Desde el loop funciona igual pero lo frena, así que se avisa una vez
    /// por operación: es la forma de encontrar una lectura que se escapó de la conversión.
    /// </summary>
    private T Esperar<T>(string operacion, string? sobre, Func<IAlmacen, T> pedido)
    {
        if (_cola.EsElEscritor) return pedido(_almacen);
        if (GameLoop.EnUnaVuelta && _lecturasAvisadas.TryAdd(operacion, 0))
            Console.Error.WriteLine($"[base] el hilo del mundo espera a la base: {operacion}");
        return _cola.PedirAsync(sobre is null ? operacion : $"{operacion} ({sobre})", pedido).GetAwaiter().GetResult();
    }

    /// <summary>Lo que guarda el mundo: se escribe por la cola, con el texto ya armado en el hilo del que llama.</summary>
    private void GuardarEnElMundo(string clave, string texto) =>
        _cola.Escribir($"guardar {clave}", a => a.GuardarEnElMundo(clave, texto));

    /// <summary>Lo que guarda el mundo: se lee al ARRANCAR, antes de que corra el loop.</summary>
    private string? LeerDelMundo(string clave) => Esperar("leer del mundo", clave, a => a.LeerDelMundo(clave));

    // --- Guild registry persistence (a single JSON blob under the "guilds" key) ---
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
        // `-votaciones.vot` (:591-611)— y acá vivían sólo en memoria: un reinicio borraba una elección en curso y
        // las propuestas de paz sin contestar. Campos nuevos con default, como los de arriba: un blob viejo se
        // sigue leyendo.
        Dictionary<string, string>? PeaceOfferTexts = null, bool ElectionsOpen = false,
        Dictionary<string, string>? Votes = null);

    public void SaveGuilds(IEnumerable<Game.Guild> guilds)
    {
        var dtos = guilds.Select(g => new GuildDto(g.Name, g.LeaderName, g.Description,
            g.Members.ToList(), g.Enemies.ToList(), g.Allies.ToList(), g.AllianceOffers.Keys.ToList(),
            g.News, g.Alignment, g.JoinRequests.Keys.ToList(), g.Url, g.Codex,
            new Dictionary<string, string>(g.JoinRequests),
            new Dictionary<string, string>(g.AllianceOffers),
            new Dictionary<string, string>(g.PeaceOffers), g.ElectionsOpen,
            new Dictionary<string, string>(g.Votes))).ToList();
        GuardarEnElMundo("guilds", System.Text.Json.JsonSerializer.Serialize(dtos));
    }

    /// <summary>El pico histórico de conectados (VB6 <c>recordusuarios</c>). El original lo guarda en su ini;
    /// acá va con el resto de los records del mundo, en <c>Misc/</c>, así entra en el backup.</summary>
    public void SaveRecordUsuarios(int record) => GuardarEnElMundo("record", record.ToString(
        System.Globalization.CultureInfo.InvariantCulture));

    public int LoadRecordUsuarios()
        => int.TryParse(LeerDelMundo("record"), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int r) ? r : 0;

    /// <summary>Las llaves ya vendidas (<see cref="Game.World.LlavesVendidas"/>): ficha del mercader → ranura → objeto.</summary>
    public void SaveLlavesVendidas(IReadOnlyDictionary<int, Dictionary<int, short>> vendidas)
        => GuardarEnElMundo("propiedades", System.Text.Json.JsonSerializer.Serialize(vendidas));

    /// <summary>Lo guardado por <see cref="SaveLlavesVendidas"/>. Un archivo ausente o ilegible da el mapa vacío y NO
    /// tira, como el suelo: con eso las casas vuelven a la venta, que es menos grave que un servidor que no arranca.</summary>
    public Dictionary<int, Dictionary<int, short>> LoadLlavesVendidas()
    {
        string? json = LeerDelMundo("propiedades");
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, Dictionary<int, short>>>(json) ?? new(); }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"[propiedades] el archivo guardado no se pudo leer, se arranca sin él: {ex.Message}");
            return new();
        }
    }

    /// <summary>Los motivos de rechazo pendientes (<see cref="Game.World.MotivosDeRechazo"/>): personaje → motivo.</summary>
    public void SaveMotivosDeRechazo(IReadOnlyDictionary<string, string> motivos)
        => GuardarEnElMundo("rechazos-de-clan", System.Text.Json.JsonSerializer.Serialize(motivos));

    /// <summary>Lo guardado por <see cref="SaveMotivosDeRechazo"/>; ausente o ilegible, el mapa vacío.</summary>
    public Dictionary<string, string> LoadMotivosDeRechazo()
    {
        string? json = LeerDelMundo("rechazos-de-clan");
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"[rechazos-de-clan] el archivo guardado no se pudo leer, se arranca sin él: {ex.Message}");
            return new();
        }
    }

    public void SaveMailboxes(IReadOnlyDictionary<string, List<Game.MailEntry>> boxes)
        => GuardarEnElMundo("mailboxes", System.Text.Json.JsonSerializer.Serialize(boxes));

    /// <summary>El suelo de los jugadores, cuando la regla de la casa <c>PersistirSuelo</c> está prendida.
    ///
    /// El formato es el mismo de <c>MapObjects.json</c> —<c>{"&lt;mapa&gt;": [[x,y,objIndex,amount], ...]}</c>—
    /// a propósito: son el mismo concepto (objetos colocados en un tile) y así los dos archivos se pueden
    /// mirar uno al lado del otro. El del .csm trae el decorado del mundo; éste, lo que dejó la gente.</summary>
    public void SaveSuelo(IEnumerable<(Game.WorldPos Pos, short ObjIndex, short Amount)> suelo)
    {
        var porMapa = new Dictionary<string, List<int[]>>();
        foreach (var (pos, obj, amount) in suelo)
        {
            string k = pos.Map.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!porMapa.TryGetValue(k, out var lista)) porMapa[k] = lista = new List<int[]>();
            lista.Add(new[] { pos.X, pos.Y, (int)obj, (int)amount });
        }
        GuardarEnElMundo("suelo", System.Text.Json.JsonSerializer.Serialize(porMapa));
    }

    /// <summary>Lo guardado por <see cref="SaveSuelo"/>. Un archivo ausente, vacío o ilegible devuelve la lista
    /// vacía y NO tira: perder el suelo de un reinicio es molesto; no arrancar el servidor por un JSON roto es
    /// mucho peor, y este archivo no es autoridad de nada — el mundo se puebla igual del .csm.</summary>
    public List<(Game.WorldPos Pos, short ObjIndex, short Amount)> LoadSuelo()
    {
        var result = new List<(Game.WorldPos, short, short)>();
        string? json = LeerDelMundo("suelo");
        if (string.IsNullOrWhiteSpace(json)) return result;
        Dictionary<string, List<int[]>>? porMapa;
        try { porMapa = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<int[]>>>(json); }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"[suelo] el archivo guardado no se pudo leer, se arranca sin él: {ex.Message}");
            return result;
        }
        if (porMapa is null) return result;
        foreach (var (mapa, objs) in porMapa)
        {
            if (!short.TryParse(mapa, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out short m) || m <= 0) continue;
            foreach (var o in objs)
            {
                // Una fila corta o con valores fuera de rango se saltea sola. El archivo lo escribe el server,
                // así que esto no debería pasar nunca — pero es texto en el disco del host y se puede editar.
                if (o.Length < 4) continue;
                if (o[0] is < 0 or > byte.MaxValue || o[1] is < 0 or > byte.MaxValue) continue;
                if (o[2] is <= 0 or > short.MaxValue || o[3] is <= 0 or > short.MaxValue) continue;
                result.Add((new Game.WorldPos(m, (byte)o[0], (byte)o[1]), (short)o[2], (short)o[3]));
            }
        }
        return result;
    }

    public Dictionary<string, List<Game.MailEntry>> LoadMailboxes()
    {
        var json = LeerDelMundo("mailboxes");
        if (string.IsNullOrWhiteSpace(json)) return new();
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<Game.MailEntry>>>(json) ?? new();
    }

    public List<Game.Guild> LoadGuilds()
    {
        var json = LeerDelMundo("guilds");
        var result = new List<Game.Guild>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        var dtos = System.Text.Json.JsonSerializer.Deserialize<List<GuildDto>>(json) ?? new();
        foreach (var d in dtos)
        {
            var g = new Game.Guild { Name = d.Name, LeaderName = d.Leader, Description = d.Desc, News = d.News,
                                     Alignment = d.Alignment, Url = d.Url, Codex = d.Codex };
            foreach (var m in d.Members) g.Members.Add(m);
            foreach (var e in d.Enemies) g.Enemies.Add(e);
            foreach (var a in d.Allies) g.Allies.Add(a);
            foreach (var o in d.Offers) g.AllianceOffers[o] = d.OfferTexts?.GetValueOrDefault(o, "") ?? "";
            foreach (var j in d.JoinRequests ?? new())
                g.JoinRequests[j] = d.JoinRequestTexts?.GetValueOrDefault(j, "") ?? "";
            foreach (var (clan, texto) in d.PeaceOfferTexts ?? new()) g.PeaceOffers[clan] = texto;
            g.ElectionsOpen = d.ElectionsOpen;
            foreach (var (votante, candidato) in d.Votes ?? new()) g.Votes[votante] = candidato;
            result.Add(g);
        }
        return result;
    }

    /// <summary>Sincrónicas, para quien no es el loop (ver <see cref="Esperar"/>).</summary>
    public Account? LoadAccount(string name) => Esperar("leer una cuenta", name, a => a.LeerCuenta(name));
    public IEnumerable<string> AllAccountNames() => Esperar("listar las cuentas", null, a => a.NombresDeCuentas().ToList());
    public Player? LoadCharacter(string name, short charIndex, string account)
        => Esperar("leer un personaje", name, a => a.LeerPersonaje(name))?.CrearPlayer(charIndex, account);
    public CharacterSummary? LoadSummary(string name) => Esperar("leer un resumen", name, a => a.LeerResumen(name));

    /// <summary>Guarda la cuenta por la cola, con una COPIA: la viva puede cambiar mientras se escribe.</summary>
    public void SaveAccount(Account acc)
    {
        var copia = acc.Copia();
        _cuentas[copia.Name] = 0;
        _cola.Escribir($"guardar la cuenta {copia.Name}", a => a.GuardarCuenta(copia));
    }

    /// <summary>Guarda al personaje por la cola. Se llama en el hilo del mundo: arma la ficha con el personaje vivo
    /// (<see cref="FichaDePersonaje.Desde"/>), que después viaja sola.</summary>
    public void SaveCharacter(Player p) => GuardarFicha(FichaDePersonaje.Desde(p));

    /// <summary>La otra mitad de un guardado: encola una ficha ya armada. Las DOS mitades van separadas.
    ///
    /// <para>Guardar a todos los conectados es lo que hacen el autosave y el backup. La mitad de armar la ficha TIENE
    /// que correr en el loop —lee unos cien campos del <see cref="Player"/> vivo, y hacerlo desde otro hilo devuelve un
    /// personaje partido al medio: el oro ya descontado y el ítem todavía sin agregar—. Ésta no: es disco, y el disco no
    /// puede estar adentro de una vuelta de 40 ms. Medido en este equipo: 0,75 ms por <c>.chr</c>, o sea que con cien
    /// conectados el autosave se comía dos ticks enteros. Se puede llamar desde cualquier hilo.</para></summary>
    public void GuardarFicha(FichaDePersonaje ficha)
    {
        _personajes[ficha.Nombre] = 0;
        _cola.Escribir($"guardar a {ficha.Nombre}", a => a.GuardarPersonaje(ficha));
    }

    /// <summary>Espera a que la cola aplique todo lo encolado. La llaman el APAGADO —sin esto, el que se desconectó un
    /// segundo antes de bajar el servidor pierde su partida— y el backup, que tiene que salir con lo último. El plazo es
    /// para no colgar el apagado si el disco no responde.</summary>
    public void EsperarEscriturasPendientes()
    {
        if (!_cola.Vaciar(TimeSpan.FromSeconds(30)))
            Console.Error.WriteLine($"[save] quedaron {_cola.Pendientes} escrituras sin terminar");
    }

    // ── LO QUE PIDE EL HILO DEL MUNDO: con respuesta, sin esperar ──────────────────────────────────────────────

    /// <summary>Lee un personaje por la cola.</summary>
    public Task<FichaDePersonaje?> LeerPersonajeAsync(string nombre)
        => _cola.PedirAsync($"leer a {nombre}", a => a.LeerPersonaje(nombre));

    /// <summary>
    /// CAMBIA UNA CUENTA adentro del escritor: la lee, le aplica <paramref name="cambio"/> y la guarda sin que nada se meta
    /// en el medio. Es la forma de tocar una cuenta que ya existe. Leerla, cambiarla afuera y guardarla entera pisa lo que
    /// cambió desde que se la leyó: un baneo del panel, una trampa, un personaje borrado desde otra conexión.
    ///
    /// <para>Devuelve la cuenta ya cambiada (el escritor no la vuelve a tocar), o null si no existe. El cambio corre en el
    /// hilo de la cola: sólo puede tocar la cuenta que recibe.</para>
    /// </summary>
    public Task<Account?> CambiarCuentaAsync(string nombre, Action<Account> cambio)
        => _cola.PedirAsync($"cambiar la cuenta {nombre}", a => CambiarCuentaEn(a, nombre, cambio));

    /// <summary>La misma, esperándola: para el pool y el panel (ver <see cref="Esperar"/>).</summary>
    public Account? CambiarCuenta(string nombre, Action<Account> cambio)
        => Esperar("cambiar una cuenta", nombre, a => CambiarCuentaEn(a, nombre, cambio));

    private static Account? CambiarCuentaEn(IAlmacen a, string nombre, Action<Account> cambio)
    {
        var cuenta = a.LeerCuenta(nombre);
        if (cuenta is null) return null;
        cambio(cuenta);
        a.GuardarCuenta(cuenta);
        return cuenta;
    }

    /// <summary>El último ingreso y desde qué IP, como el original al entrar a la cuenta (VB6 <c>EntrarCuenta</c>,
    /// Cuentas.bas:443-444). Sólo esos dos datos, adentro del escritor.</summary>
    public void AnotarIngreso(string cuenta, string ip)
    {
        var cuando = DateTime.UtcNow;
        _cola.Escribir($"anotar el ingreso de {cuenta}", a => CambiarCuentaEn(a, cuenta, c =>
        {
            c.UltimoIngreso = cuando;
            c.UltimaIp = ip;
        }));
    }

    /// <summary>
    /// EL HASH VIEJO, YA MIGRADO. Verificar una contraseña con hash SHA-256 lo pasa a PBKDF2 en la cuenta leída
    /// (<see cref="Account.VerifyPassword(string, out bool)"/>), y eso hay que guardarlo. Se guarda SÓLO la clave, y sólo si
    /// la guardada sigue siendo vieja: si mientras tanto se cambió la contraseña, la nueva ya es moderna y no se toca.
    /// </summary>
    public void GuardarClaveMigrada(Account acc)
    {
        string hash = acc.PasswordHash;
        _cola.Escribir($"migrar la clave de {acc.Name}", a => CambiarCuentaEn(a, acc.Name, c =>
        {
            if (PasswordHasher.IsModern(c.PasswordHash)) return;
            c.PasswordHash = hash;
            c.Salt = "";
        }));
    }

    /// <summary>Suma a la bóveda de un personaje DESCONECTADO, sin cargarlo entero, por la cola.
    ///
    /// <para>Es el <c>GetVar</c>/<c>WriteVar</c> sobre <c>[STATS] BANCO</c> que usa la transferencia de oro del
    /// original (VB6 Protocol.bas:15832): el destinatario puede no estar en el mundo, y la única forma de
    /// acreditarle es tocarle lo guardado. Se hace por el saldo solo (<see cref="IAlmacen.AcreditarBoveda"/>) y no leyendo
    /// y guardando el personaje completo para mover un número.</para>
    ///
    /// <para>Devuelve el saldo que quedó, o <c>null</c> si no existe el personaje.</para></summary>
    public Task<long?> AcreditarBovedaAsync(string nombre, long monto)
        => _cola.PedirAsync($"acreditar a la bóveda de {nombre}", a => a.AcreditarBoveda(nombre, monto));

    /// <param name="userCode">Código de recuperación (VB6 .cnt "UserCodigo"). Se hashea como la contraseña.
    /// Vacío = la cuenta no se puede recuperar, que es lo que pasaba con TODAS hasta ahora: el server leía el
    /// campo del CreateNewAccount y lo tiraba.</param>
    /// <summary><paramref name="esBot"/> marca la cuenta como de un bot del servidor, y esa marca es lo que
    /// impide que se abra desde afuera (ver <see cref="Account.EsBot"/>). Va como parámetro y no se pone
    /// después: entre crear la cuenta y marcarla habría una ventana en la que su contraseña —que queda
    /// escrita en <c>data/bots.txt</c>— sí serviría desde la red.</summary>
    public Account CreateAccount(string name, string password, string userCode = "", bool esBot = false)
    {
        var acc = CuentaNueva(name, password, userCode, esBot);
        SaveAccount(acc);
        return acc;
    }

    private static Account CuentaNueva(string name, string password, string userCode, bool esBot) => new()
    {
        // New accounts use the modern slow KDF (PBKDF2); the salt is embedded in the hash string.
        Name = name,
        Salt = "",
        PasswordHash = PasswordHasher.HashModern(password),
        UserCodeHash = string.IsNullOrEmpty(userCode) ? "" : PasswordHasher.HashModern(userCode),
        EsBot = esBot,
    };

    /// <summary>Armadura inicial POR RAZA (VB6 RellenarInventario, TCP.bas:2212): el slot 4 del kit no sale
    /// del .dat (viene en 0), lo rellena el server según eRaza.</summary>
    private static int RacialArmor(byte race) => race switch
    {
        2 => 464,   // Elfo
        3 => 465,   // Drow (Elfo Oscuro)
        4 or 5 => 466,   // Gnomo / Enano
        6 => 1087,  // Orco
        _ => 463,   // Humano (y default del VB6)
    };

    // Clases con maná al nacer (TCP.bas:509-520 Select Case UserClase). El Mago recibe Inteligencia*3 y el
    // resto de los lanzadores 50 fijo. OJO: el original tiene ahí un `If Not eClass.Paladin Then` que NO hace
    // lo que aparenta —`Not 9` es -10, siempre distinto de cero— así que el Paladín TAMBIÉN cobra sus 50.
    private const byte ClsClerigo = 1, ClsMago = 2, ClsAsesino = 4, ClsBardo = 6, ClsDruida = 7,
        ClsPaladin = 9, ClsNigromante = 18;

    private static bool IsCaster(byte klass) =>
        klass is ClsMago or ClsClerigo or ClsDruida or ClsBardo or ClsAsesino or ClsNigromante or ClsPaladin;

    /// <summary>
    /// Stats con los que nace un personaje REAL (VB6 ConnectNewUser, TCP.bas:517-560): vida y stamina por
    /// tirada sobre Constitución/Agilidad, maná por clase, golpe 1-2, cero oro y CERO skills — los 10
    /// SkillPts se reparten después con ModifySkills.
    /// </summary>
    private void ApplyNewCharacterStats(Player p)
    {
        // VB6 RandomNumber(1, X) es inclusivo en X; Random.Next(1, X+1) es el equivalente.
        int cons = Math.Max(1, p.Attributes[GameLimits.AttrConstitucion] / 3);
        p.MaxHp = p.Hp = 15 + _rng.Next(1, cons + 1);                       // :519-520

        int agi = Math.Max(1, p.Attributes[GameLimits.AttrAgilidad] / 6);
        int mi = _rng.Next(1, agi + 1);
        if (mi == 1) mi = 2;                                                // :523
        p.MaxStamina = p.Stamina = 20 * mi;                                 // :524-525

        p.MaxMana = p.Mana = p.Class switch
        {
            ClsMago => p.Attributes[GameLimits.AttrInteligencia] * 3,       // :531
            _ when IsCaster(p.Class) => 50,                                 // :537
            _ => 0,                                                         // :542 (guerreros y trabajadores)
        };
        if (IsCaster(p.Class)) p.Spells[1] = 2;                             // .Stats.UserHechizos(1) = 2

        p.MinHit = 1; p.MaxHit = 2;                                         // :560-561
        p.Gold = 0;                                                         // :563 .Stats.GLD = 0
    }

    /// <summary>
    /// Kit de nacimiento POR CLASE (VB6 RellenarInventario, TCP.bas:2195). El orden del .dat es el orden de
    /// los slots: el 3 es el arma (equipada) y el 4 la armadura racial (el .dat trae 0 ahí a propósito).
    /// </summary>
    private static void ApplyNewbieKit(Player p, IReadOnlyList<NewbieItem> kit, byte race)
    {
        // Y LA MUJER, LA VERSIÓN DE MUJER: ver `Equipment.RopaDeNovatoDeMujer`.
        short armadura = (short)RacialArmor(race);
        if (p.Gender == Equipment.Mujer) armadura = Equipment.RopaDeNovatoDeMujer(armadura);
        for (int i = 0; i < kit.Count && i < GameLimits.MaxInventorySlots; i++)
        {
            var it = kit[i];
            int slot = i + 1;
            int obj = slot == 4 ? armadura : it.ObjIndex;
            if (obj <= 0 || it.Amount <= 0) continue;
            bool equipped = it.Equipped || slot is 3 or 4;   // arma y armadura nacen puestas
            p.Inventory[slot] = new InventorySlot { ObjIndex = (short)obj, Amount = (short)it.Amount, Equipped = equipped };
        }
    }

    /// <summary>
    /// Creates a fresh starting character, saves it, and adds it to the account roster.
    /// Con <paramref name="newbieKit"/> (NACIMIENTO.dat) el personaje nace con el kit de SU CLASE, como en el
    /// VB6. Sin él cae al kit demo maestro — el que usa el fixture y del que dependen los flows (espada en el
    /// slot 2, armadura en el 15, comida en el 18, skills de oficio al 100).
    /// </summary>
    /// <param name="modRaza">Bonos de atributo por raza (Balance.dat §MODRAZA). Opcional: sin él los atributos
    /// quedan en el 18 plano y la raza no cambia nada, que es como estaba antes de portarlo.</param>
    public Player CreateCharacter(Account acc, string name, short charIndex, byte race, byte gender, byte @class, byte homeland, short head,
        IReadOnlyList<NewbieItem>? newbieKit = null, Func<int, int, int>? modRaza = null)
    {
        var p = new Player
        {
            CharIndex = charIndex,
            Name = name,
            Account = acc.Name,
            Race = race,
            Gender = gender,
            Class = @class,
            Homeland = homeland,
            // La tierra natal NO es el hogar, y el recién nacido tampoco aparece en ella. El original manda a
            // todos al Dungeon Newbie —`.Hogar = cDungeonNewbie` y `.Pos = tCiudades.DungeonNewbie`,
            // TCP.bas:502 y :573— y la ciudad elegida sirve para una sola cosa: el bando (ver más abajo).
            // Acá se hacía lo intuitivo, hogar y spawn en la tierra natal, y con eso el jugador nuevo se
            // salteaba entero el dungeon de aprendizaje que el juego usa como tutorial.
            HomeCity = Cities.DungeonNewbie,
            // El BANDO CON EL QUE SE NACE (VB6 ConnectNewUser, TCP.bas:494-500): ResetFacciones y después
            // Illiandor → Status 3 (Republicano), cualquier otra → Status 2 (Ciudadano Imperial). La pantalla
            // de creación del original lo dice sin vueltas: "elegir Nix te hará miembro del IMPERIO".
            // Faltaba, así que TODO el que se creaba un personaje nacía imperial y la mitad republicana del
            // mundo —su ciudad, su ejército (la Milicia), sus enemigos— no tenía forma de existir.
            FactionStatus = homeland == Cities.Illiandor ? Player.StatusRepublicano : Player.StatusCiudadano,
            Head = head,
            Body = 1,
            Pos = Cities.SpawnTile(Cities.DungeonNewbie),
            MaxHp = 100, Hp = 100,
            // ENERGIA: 100, y NO se sube. Cuando el golpe pasó a costar energía (VB6 UsuarioAtaca) esto se
            // probó en 500 para que las ráfagas largas de los flows no se quedaran cortas — y rompió
            // `craft_flow`, que DRENA la energía a propósito para ejercitar la rama "sin energía el oficio no
            // fabrica". Ese techo bajo es lo que hace alcanzable la rama del agotamiento en todo el arnés, así
            // que es carga estructural, no un número al azar. El maná (2000) y el oro (5000) sí están inflados
            // porque nada mide "quedarse sin maná" ni "sin oro". Un personaje REAL nace con 40-60
            // (`ApplyNewCharacterStats`, 20*mi): 100 ya es holgado.
            MaxStamina = 100, Stamina = 100,
            MaxMana = 2000, Mana = 2000,
            MinHit = 2, MaxHit = 5,
            // Oro del FIXTURE solamente: un personaje real nace con 0 (`ApplyNewCharacterStats` lo pisa, fiel a
            // `.Stats.GLD = 0`, TCP.bas:563). Subió de 500 a 5000 cuando el correo pasó a cobrar sus 1750 de
            // oro (VB6 mod_Correos.bas:47): con 500 en la mochila, los tres flows de correo del laboratorio no
            // podían mandar ni una carta y fallaban por falta de fondos, no por un bug.
            Gold = 5000,
        };
        // ATRIBUTOS: base POR CLASE más el modificador de la RAZA (VB6 `AsignarAtributos`, TCP.bas:309-347).
        //
        // Acá había un 18 plano para los cinco atributos, con la cita a TCP.bas:343-347. Esas cinco líneas son
        // el FINAL de la función —las que suman `+ ModRaza(.raza).X`—; las variables que suman vienen fijadas
        // treinta líneas antes por un `Select Case .Clase`, y **ninguna clase tiene 18 en todo**:
        //
        //     Clerigo/Asesino/Bardo/Druida/Paladin/Nigromante (:322)   F14 A14 I18 C10 Co18
        //     Mago (:328)                                              F 6 A18 I18 C10 Co18
        //     Case Else — todas las clases sin magia (:334)            F18 A18 I 6 C10 Co18
        //
        // O sea: la CARISMA base es 10 en las dieciocho clases (nunca 18), la INTELIGENCIA base es 6 para todo
        // guerrero/trabajador, y la FUERZA base es 6 para el Mago. Un Humano Guerrero nacía acá con
        // (19,19,19,18,20) contra los (19,19,7,10,20) del original: +12 de inteligencia y +8 de carisma.
        //
        // Y arrastra los derivados, porque `ApplyNewCharacterStats` corre después: la Agilidad alimenta el
        // MaxSta (`Agilidad \ 6`) y la Inteligencia el maná del Mago.
        //
        // El barrido del oráculo no podía verlo: compara el TIPO de paquete, y `RequestAtributes` respondía
        // `atributes` en los dos servers — con los cinco bytes distintos adentro.
        (int f, int a, int i, int c, int co) baseAttrs = @class switch
        {
            ClsClerigo or ClsAsesino or ClsBardo or ClsDruida or ClsPaladin or ClsNigromante => (14, 14, 18, 10, 18),
            ClsMago => (6, 18, 18, 10, 18),
            _ => (18, 18, 6, 10, 18),
        };
        int[] bases = [0, baseAttrs.f, baseAttrs.a, baseAttrs.i, baseAttrs.c, baseAttrs.co];
        // El clamp a 0 sólo lo tiene la INTELIGENCIA en el original (`IIf(... < 0, 0, ...)`, TCP.bas:345); los
        // otros cuatro no lo llevan. Con la base correcta el margen se achica: un Enano u Orco (−5 int) sobre
        // una clase sin magia queda en 1, no en 13.
        for (int attr = 1; attr <= 5; attr++)
        {
            int valor = bases[attr] + (modRaza?.Invoke(race, attr) ?? 0);
            if (attr == GameLimits.AttrInteligencia) valor = Math.Max(0, valor);
            p.Attributes[attr] = (byte)Math.Clamp(valor, 0, GameLimits.MaxAttribute);
        }
        _leveling.InitNewCharacter(p);
        p.FreeSkillPoints = 10;   // .Stats.SkillPts = 10 (TCP.bas:504) — en los dos caminos

        if (newbieKit is { Count: > 0 })
        {
            // ── Personaje REAL ────────────────────────────────────────────────────────────────────────
            // Stats de nacimiento del VB6 (ConnectNewUser, TCP.bas:517-560). Antes todo personaje nuevo
            // nacía con los valores generosos del fixture —100/100 de vida y stamina, 2000 de maná, 500 de
            // oro y OCHO oficios en 100—, o sea maestro de todo y rico desde el minuto cero.
            ApplyNewCharacterStats(p);
            ApplyNewbieKit(p, newbieKit, race);
            if (!acc.Characters.Contains(name)) acc.Characters.Add(name);
            GuardarAlta(p, acc);
            return p;
        }

        // ── Personaje DEMO del fixture ────────────────────────────────────────────────────────────────
        // Maxeado a propósito: los flows dependen de este layout (espada slot 2, armadura 15, comida 18) y
        // de poder fabricar/pescar/talar sin entrenar. NO es lo que recibe un jugador real.
        //
        // El GM del fixture (Rugura) YA NO se cablea acá: el privilegio NO se persiste en el .chr. Se decide
        // en CADA login desde la config —Server.ini + el cableado de fixture— en AplicarPrivilegiosDeStaff.
        // Baquearlo en el personaje dejaba un .chr con Privilegios=8 que, movido a un server de producción con
        // ese mismo data/, hacía Dios a quien entrara como Rugura. Ahora el .chr no es autoridad de rango.
        p.Skills[GameLimits.SkillWrestling] = 15;
        p.Skills[GameLimits.SkillTacticas] = 15;
        p.Skills[GameLimits.SkillMagia] = 15;
        p.Skills[GameLimits.SkillHerreria] = 100;   // master blacksmith (can forge any weapon)
        p.Skills[GameLimits.SkillCarpinteria] = 100; // master carpenter
        p.Skills[GameLimits.SkillSastreria] = 100;   // master tailor
        p.Skills[GameLimits.SkillAlquimia] = 100;    // master alchemist
        p.Skills[GameLimits.SkillTalar] = 100;       // gathering: woodcutting
        p.Skills[GameLimits.SkillPesca] = 100;       // fishing
        p.Skills[GameLimits.SkillMineria] = 100;     // mining
        p.Skills[GameLimits.SkillBotanica] = 100;    // botany (raíces con las tijeras)
        p.Skills[GameLimits.SkillOcultar] = 100;     // esconderse (suerte alta, para los flows de oficio)
        p.Skills[GameLimits.SkillRobar] = 100;       // hurtar
        p.Skills[GameLimits.SkillDomar] = 100;       // taming
        p.Inventory[1] = new InventorySlot { ObjIndex = 38, Amount = 5 };  // red potions (SubTipo 3 = HP)
        p.Inventory[2] = new InventorySlot { ObjIndex = 2, Amount = 1 };   // espada larga (equipable)
        p.Inventory[3] = new InventorySlot { ObjIndex = 37, Amount = 5 };  // blue potions (SubTipo 4 = mana)
        p.Inventory[4] = new InventorySlot { ObjIndex = 39, Amount = 5 };  // green potions (SubTipo 2 = strength)
        p.Inventory[5] = new InventorySlot { ObjIndex = 36, Amount = 5 };  // yellow potions (SubTipo 1 = agility)
        p.Inventory[6] = new InventorySlot { ObjIndex = 386, Amount = 400 }; // iron ingots (blacksmithing)
        p.Inventory[7] = new InventorySlot { ObjIndex = 387, Amount = 100 }; // silver ingots
        p.Inventory[8] = new InventorySlot { ObjIndex = 58, Amount = 500 };  // leña (carpentry wood)
        p.Inventory[9] = new InventorySlot { ObjIndex = 414, Amount = 50 };  // wolf skins (tailoring)
        p.Inventory[10] = new InventorySlot { ObjIndex = 127, Amount = 1 };  // Hacha de Leñador (woodcutting)
        p.Inventory[11] = new InventorySlot { ObjIndex = 881, Amount = 1 };  // Caña de Pesca (fishing)
        p.Inventory[12] = new InventorySlot { ObjIndex = 187, Amount = 1 };  // Piquete de Minero (mining)
        p.Inventory[13] = new InventorySlot { ObjIndex = 888, Amount = 200 }; // raíces (alchemy)
        p.Inventory[14] = new InventorySlot { ObjIndex = 1342, Amount = 1 };  // Montura (equitación)
        p.Inventory[15] = new InventorySlot { ObjIndex = 1480, Amount = 1 };  // armadura (SubTipo 0, def 50)
        p.Inventory[16] = new InventorySlot { ObjIndex = 659, Amount = 1 };   // Casco Imperial (SubTipo 1, def 20)
        p.Inventory[17] = new InventorySlot { ObjIndex = 128, Amount = 1 };   // Escudo de Hierro (SubTipo 2, def 6)
        p.Inventory[18] = new InventorySlot { ObjIndex = 573, Amount = 3 };    // Manzana Roja Newbies (comida, MinHam 10)
        p.Inventory[19] = new InventorySlot { ObjIndex = 886, Amount = 1 };    // Costurero (herramienta de sastrería)
        p.Inventory[20] = new InventorySlot { ObjIndex = 887, Amount = 1 };    // Olla (herramienta de alquimia)
        // OJO al elegir slot: el 13 parecía libre y NO lo estaba —tiene las 200 raíces de la alquimia—, así que
        // poner acá las tijeras las pisó y craft_flow se puso rojo en "alquimia consume 14 raíces". Los slots
        // 1..20 del kit común están todos ocupados; el 21 y 22 son de Rugura (arco y flechas).
        p.Inventory[23] = new InventorySlot { ObjIndex = 885, Amount = 1 };    // Tijeras (botánica: raíces del árbol)
        // El arco NO va en el kit común: es un arma (categoría 1) y su munición son flechas — dárselos a todo char
        // demo rompería a los que dependen del layout (weapon_skill_flow quiere a Rugura "sin arma"; craft_flow
        // construye flechas desde cero). El combate a distancia lo ejerce un char dedicado, "Arquero" (ver más abajo).
        p.Spells[1] = 6;   // Saeta ignea (daño)
        p.Spells[2] = 9;   // Paralizar (estado)
        p.Spells[3] = 10;  // Remover paralisis
        p.Spells[4] = 4;   // Envenenar (estado)
        p.Spells[5] = 1;   // Curar Veneno
        p.Spells[6] = 95;  // Invocar esqueletos (invocación, 3x NPC 503)
        // OJO, el comentario que había acá decía "ejercita CreateFX" y era falso: la Bomba mágica es un hechizo
        // de ÁREA (HechizoDeArea=1, AreaEfecto=2, Target 4), así que NO manda CreateFX sino EfectoTerrenoFX, y
        // resuelve el 3×3 entero. Pide MinSkill 38 y estos chars tienen 15 de Magia, así que sólo la lanza el GM
        // (que saltea PuedeLanzar): ningún char del fixture llega a 38 de Magia.
        p.Spells[7] = 8;   // Bomba mágica (ÁREA, daño 5-5, FXgrh 50 + WAV 129)
        p.Spells[8] = 5;   // Curar heridas graves (SubeHP=1 + FXgrh=9) — self-cast, ejercita CreateFX sobre el caster
        p.Spells[9] = 11;  // Resucitar (Revivir=1) — revive PvP a un jugador muerto al frente
        // Rayo solar es el hechizo con Ceguera REAL más barato (700 de maná, y el char arranca con 2000) —
        // ejercita Blind/BlindNoMore. Antes acá iba el 67 "Curar heridas críticas", elegido porque parecía tener
        // `SubeHP=1 + Ceguera=1`: las dos cosas eran CONTAMINACIÓN del transcoder (se había comido 27 claves de
        // los hechizos 68-77, que se perdían). El 67 real no tiene Ceguera ni FXgrh. Ojo: el 77 es de DAÑO
        // (SubeHP=2), así que el Blind le llega al OBJETIVO, no al lanzador.
        p.Spells[10] = 77; // Rayo solar (Ceguera=1, daño) — ejercita Blind sobre el objetivo
        // Invisibilidad: hace falta para ejercer el segundo gate de /MODMAPINFO INVISINEFECTO (el otro es
        // Ocultarse). Pide MinSkill 87 de magia, que estos chars no tienen, pero un GM saltea PuedeLanzar.
        p.Spells[11] = 14; // Invisibilidad (Invisibilidad=1, Target 1)
        // Los otros dos hechizos de ÁREA que hacen falta para cubrir las tres formas que toma el 3×3:
        // la CURA de área (SubeHP=1, y con partícula en vez de grh) y el que no cura ni daña (SubeHP=0), que
        // en el original se queda en puro efecto visual pese a prometer parálisis.
        p.Spells[12] = 75; // Círculo curativo (ÁREA, cura 25-35, Particle 17 + WAV 18)
        p.Spells[13] = 65; // Tornado (ÁREA, SubeHP=0: sólo FX — la parálisis nunca se aplica, ver CastAreaSpell)
        // El PORTAL DE LUM. Va por índice cableado (53) y no por ningún campo del data, así que es el único
        // hechizo que no se puede ejercitar con otro: o está en el libro, o su subsistema no corre nunca.
        p.Spells[14] = 53; // Portal planar (abre a los 5 s, dura 10, salida a Intermundia)
        // METAMORFOSIS: MURCIÉLAGO, la más barata de las once (MinSkill 5, 50 de maná, 10 de energía). Es la
        // única que estos chars pueden lanzar sin ser GM: tienen 15 de Magia, y la que sigue pide 15 justos —
        // quedarse sin margen ahí es pedir un flow que falla el día que alguien toque la skill inicial.
        // Sólo hace algo con la regla de la casa `MetamorfosisDelDruida` prendida, que es lo que trae
        // `metamorfosis_flow.ini`; con la regla apagada esta ranura no hace nada ni avisa, que es lo que hace el
        // original.
        p.Spells[15] = 49; // Metamorfosis: Murciélago (body 9, head 0, +6 al golpe, +20 a la defensa)
        // INCINERAR: el único camino por el que se puede prender fuego a alguien, y sin él `EfectoIncinerado`
        // —que tiene dos relojes distintos y una duración al azar— no lo ejercita nada. Pide MinSkill 90 y 920
        // de maná: la skill se la saltea el GM y el maná le sobra (arranca con 2000).
        // Va en el 16 y no desplaza a nadie: el libro tiene 35 ranuras y de la 16 en adelante están libres. La
        // advertencia de "el fixture no tiene huecos" es del INVENTARIO, donde las ranuras sí se pelean.
        p.Spells[16] = 56; // Incinerar (Incinera=1)
        if (!acc.Characters.Contains(name)) acc.Characters.Add(name);
        GuardarAlta(p, acc);
        return p;
    }

    /// <summary>EL ALTA DE UN PERSONAJE: su ficha y la cuenta que lo lista, en UN solo pedido. Eran dos escrituras
    /// sueltas, y un corte entre las dos dejaba un personaje que no figuraba en ninguna cuenta con el nombre tomado para
    /// siempre. El nombre entra al índice ya, en el hilo del que crea: un segundo alta con el mismo nombre lo ve en el
    /// acto.
    ///
    /// <para>El personaje se suma a la cuenta GUARDADA, no se guarda la que trae el que crea: ésa se leyó al verificar la
    /// contraseña, y lo que cambió desde entonces —un baneo del panel, un personaje borrado desde otra conexión— se
    /// perdería. La traída sirve sólo si la cuenta todavía no llegó a la base (<see cref="IAlmacen.CrearPersonaje"/>).</para>
    ///
    /// <para>El tope de personajes lo mira el alta del juego antes de llegar acá; la base lo vuelve a mirar adentro de su
    /// transacción. Si esa segunda mirada dice que no —dos altas a la vez en la misma cuenta—, queda en el log.</para></summary>
    private void GuardarAlta(Player p, Account acc)
    {
        var ficha = FichaDePersonaje.Desde(p);
        var cuenta = acc.Copia();
        _personajes[ficha.Nombre] = 0;
        _cuentas[cuenta.Name] = 0;
        _cola.Escribir($"alta de {ficha.Nombre}", a =>
        {
            if (a.CrearPersonaje(cuenta, ficha)) return;
            _personajes.TryRemove(ficha.Nombre, out _);   // no quedó nada guardado: el nombre no puede figurar como tomado
            Console.Error.WriteLine($"[base] alta de {ficha.Nombre}: la cuenta {cuenta.Name} ya tiene " +
                                    $"{AccountFile.MaxCharacters} personajes");
        });
    }

    /// <summary>
    /// CREA LA CUENTA SI EL NOMBRE ESTÁ LIBRE, en un solo paso. El "¿existe?" y el "escribir" iban separados, y dos altas
    /// simultáneas del mismo nombre —dos conexiones, dos hilos del pool— podían pasar las dos la pregunta: la segunda
    /// pisaba a la primera. Adentro del escritor no hay nada en el medio. El hash (PBKDF2, 25 ms) se hace ANTES, en el hilo
    /// del que llama: la cola no puede frenarse con eso. Devuelve la cuenta, o null si el nombre ya estaba.
    /// </summary>
    public Account? CrearCuentaSiNoExiste(string name, string password, string userCode = "", bool esBot = false)
    {
        var acc = CuentaNueva(name, password, userCode, esBot);
        var copia = acc.Copia();
        bool creada = Esperar("crear una cuenta", name, a =>
        {
            if (a.ExisteCuenta(name)) return false;
            a.GuardarCuenta(copia);
            return true;
        });
        if (!creada) return null;
        _cuentas[name] = 0;
        return acc;
    }

    /// <summary>Seeds a demo account + characters on first run so the flows work out of the box.</summary>
    public void SeedDemoIfEmpty()
    {
        const string account = "cuenta_test";
        const string password = "hash_password";
        if (AccountExists(account)) return;
        var acc = CreateAccount(account, password);
        // Los chars del FIXTURE se fijan en (15,50,50): el mapa 15 es un mapa ABIERTO (sin paredes en la zona de
        // juego), compatible con el gate de blocking; los NPCs de test (Program.cs, FixtureMap=15) y las rutas de
        // los flows están calibrados ahí. Un personaje real creado por LoginNewChar sí nace en la ciudad de su
        // tierra natal (mapa 1 Ullathorpe, etc.). El mapa 1 tiene el spawn (50,50) sobre pared del .csm.
        var demoPos = new WorldPos(15, 50, 50);
        var rugura = CreateCharacter(acc, "Rugura", charIndex: 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        rugura.Pos = demoPos;
        // Rugura es también el char de test de COMBATE A DISTANCIA: lleva un Arco Simple (obj 989, Proyectil=1) y
        // 100 Flechas (obj 480, otFlechas), pero SIN equipar — ranged_flow los equipa antes de disparar. Van sólo
        // en Rugura (no en el kit común) y desequipados a propósito: equipados serían "un arma", y weapon_skill_flow
        // lo quiere sin arma; el melee de Rugura sigue saliendo de Wrestling. La skill Proyectiles da el acierto.
        // REPUBLICANO, no ciudadano. Es lo que hace posible todo el PvP del fixture: `PuedeAtacar` prohíbe
        // ocho pares de facción y ciudadano-contra-ciudadano es uno de ellos, así que con los dos civiles
        // Rugura no le podía pegar a Merlin y se caían los 25 flows que usan ese par. Republicano contra
        // ciudadano NO está prohibido, y Merlin sigue siendo objetivo LEGAL — o sea que el "seguro" que lo
        // vuelve criminal (FlagCriminalIfLegalTarget) se dispara igual y faction_flow/robar_flow siguen
        // probando lo mismo que probaban.
        //
        // No es un valor inventado para el laboratorio: en el original nace republicano el que elige
        // Illiandor como tierra natal (TCP.bas:497). Acá se fija el status a mano y se le deja la tierra
        // natal 1, que es de la que dependen otros flows.
        rugura.FactionStatus = Player.StatusRepublicano;
        rugura.Skills[GameLimits.SkillProyectiles] = 15;
        rugura.Inventory[21] = new InventorySlot { ObjIndex = 989, Amount = 1 };    // Arco Simple (Proyectil=1, [5,8])
        rugura.Inventory[22] = new InventorySlot { ObjIndex = 480, Amount = 100 };  // Flecha (otFlechas, [4,6]) — munición
        // RUGURA PELEA VESTIDO, y es lo único que lo distingue del kit común. Es el que mata en quince flows, y
        // desde que el desnudo no recupera energía —fiel: TCP.bas:2070 deriva el flag del slot de armadura y
        // SistemaCombate.bas:999 cobra RandomNumber(1,10) por golpe a mano limpia— una ráfaga de 120 golpes lo dejaba
        // sin fuelle antes de matar a nadie. Medido: sin esto `death_flow` da FALLO y con esto OK, en el commit que
        // lo rompió y en la cabeza.
        //
        // No se toca la energía del kit (100) ni la de Merlin: `craft_flow` DRENA la de Merlin a propósito para
        // ejercitar «sin energía el oficio no fabrica», y `stamina_golpe_flow` vacía la de Rugura a ráfaga para
        // ejercitar el freno del golpe. Lo que cambia acá es el ESTADO —vestido, como pelea cualquiera— y no el
        // tamaño del pozo.
        //
        // El único que necesitaba a Rugura desnudo era `body_flow`, que mide el cuerpo al vestirse; usa a Merlin,
        // que es de la misma raza y género y trae la misma armadura en el mismo slot.
        rugura.Inventory[15] = new InventorySlot { ObjIndex = 1480, Amount = 1, Equipped = true };
        // Los nudillos y el anillo mágico de `nudillos_flow` NO van acá: se crean con /CI, como en aura_flow.
        // Se intentó ponerlos en el inventario y salió mal por partida doble — el slot 23 ya tenía las Tijeras
        // (las pisó y gather_flow se puso rojo) y el 24 era el anteúltimo libre, así que Rugura se quedó sin
        // lugar para los /CI de donador, mineral, pergamino, aura y stack_tope. El kit no tiene huecos.
        SaveCharacter(rugura);
        var merlin = CreateCharacter(acc, "Merlin", charIndex: 2, race: 1, gender: 1, @class: 2, homeland: 1, head: 2);
        merlin.Pos = demoPos; SaveCharacter(merlin);
        // "Marino": char de test de NAVEGACIÓN. El mapa 1 (Ullathorpe) no tiene una sola casilla de agua, así
        // que vive en la costa del mapa 17: tierra firme en (89,61) con mar abierto justo al este. Lleva una
        // Barca (obj 474, MinSkill 40) y la skill de Navegación para poder embarcar.
        var marino = CreateCharacter(acc, "Marino", charIndex: 3, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        marino.Pos = new WorldPos(17, 89, 61);
        marino.Skills[GameLimits.SkillNavegacion] = 50;                                  // >= MinSkill de la Barca (40)
        marino.Inventory[1] = new InventorySlot { ObjIndex = 474, Amount = 1 };          // Barca
        marino.Inventory[2] = new InventorySlot { ObjIndex = 475, Amount = 1 };          // Galera (MinSkill 55: no alcanza)
        SaveCharacter(marino);
        // "Soldado": char de test de FACCIONES. Ciudadano nivel 25 con 15 criminales matados — cumple justo los
        // requisitos para enlistarse en la Armada Real. Junto a los enlistadores de (48,52)/(52,54) del fixture.
        var soldado = CreateCharacter(acc, "Soldado", charIndex: 4, race: 1, gender: 1, @class: 3, homeland: 1, head: 1);
        soldado.Pos = demoPos;
        soldado.Level = 25;
        soldado.RenegadosMatados = 15;   // renegados matados: es lo que le cuenta a la Armada
        SaveCharacter(soldado);
        // "Capitan": char de test del ASCENSO de facción. Guerrero (clase 3) humano nivel 25 con 40 criminales
        // matados — el acumulado que pide matadosArmada para escalar del rango 1 al 6 (20/25/30/35/40). Al llegar
        // a rango 6 (2ª jerarquía) el líder le entrega su armadura faccionaria (obj 1570, Guerrero Imperial).
        var capitan = CreateCharacter(acc, "Capitan", charIndex: 5, race: 1, gender: 1, @class: 3, homeland: 1, head: 1);
        capitan.Pos = demoPos;
        capitan.Level = 25;
        capitan.RenegadosMatados = 40;   // el acumulado que pide matadosArmada para llegar al rango 6
        SaveCharacter(capitan);
        // "Rebelde": char de test de la rama REPUBLICANA. Renegado (criminal) nivel 25 con 15 ciudadanos matados —
        // le cuentan a la Milicia. Se hace Republicano con el convertidor y recién ahí puede enlistarse en la
        // Milicia (el VB6 exige Status=3). Renegado es también el único status desde el que se da la ciudadanía.
        var rebelde = CreateCharacter(acc, "Rebelde", charIndex: 6, race: 1, gender: 1, @class: 3, homeland: 1, head: 1);
        rebelde.Pos = demoPos;
        rebelde.Level = 25;
        rebelde.FactionStatus = Player.StatusCriminal;
        rebelde.CiudadanosMatados = 15;
        // ORO PARA PAGAR LA CIUDADANÍA. Desde que el Convertidor cobra las 100.000 del original, los 5.000 con
        // los que nace un char del fixture ya no alcanzan, y sin esto la rama republicana entera —convertirse,
        // enlistarse en la Milicia, ascender— quedaba inalcanzable para `faction_milicia_flow`. Es carga
        // estructural del laboratorio, como el techo de 100 de energía: el número sale del gate, no del aire.
        rebelde.Gold = 150_000;
        SaveCharacter(rebelde);
        // OJO: acá NO se puede agregar un personaje más. La cuenta demo ya tiene los DIEZ que caben
        // (`AccountFile.MaxCharacters = 10`, fiel al [PJS] PJ1..PJ10 del VB6, Cuentas.bas:100). Un char extra
        // entra en memoria pero al guardar la cuenta se cae del .ini el ÚLTIMO creado, y al siguiente arranque
        // ese personaje ya no existe: cuando se probó con un "Brujo" para los hechizos de área, el que
        // desapareció fue Cerrajero y doors_flow se puso rojo 3/3 con un "login FALLO" que no decía por qué.
        // Si hace falta un char con capacidades nuevas, hay que dárselas a uno de estos diez.
        // --- Chars del MUNDO REAL (mapa 1 = Ullathorpe, poblado desde MapSpawns.json con la data de NPCs.dat) ---
        // Todo lo demás vive en el fixture (mapa 15), donde los NPCs se colocan a mano y con los flags fijados.
        // Estos dos ejercen el mundo auténtico, que es donde la hostilidad la decide la DATA
        // (Program.cs: aggro = t.Hostile && t.GiveExp > 0).
        //
        // "Ulla": pegado al Gallo (81,41) — Hostile=0 y GiveEXP=0 en su ficha, ni mercader ni con rol, así que
        // es de los pocos NPCs reales que pasan por el gate de NpcTryAttack. Su corral está cercado: se llega a
        // él por (80,41) y el resto del pueblo queda a 142 pasos rodeando, de ahí que el cazador vaya aparte.
        var ulla = CreateCharacter(acc, "Ulla", charIndex: 7, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        ulla.Pos = new WorldPos(1, 79, 41);
        SaveCharacter(ulla);
        // "Cazador": a 7 tiles de la Serpiente (22,40) — o sea FUERA de su AggroRange de 6, para poder ver cómo
        // se activa al acercarse. Es el ÚNICO bicho agresivo de Ullathorpe (Hostile=1, GiveEXP=25) y es débil
        // (18 HP, pega 2-3), así que no lo mata. La columna x=22 está despejada de y=47 a y=40.
        var cazador = CreateCharacter(acc, "Cazador", charIndex: 8, race: 1, gender: 1, @class: 3, homeland: 1, head: 1);
        cazador.Pos = new WorldPos(1, 22, 47);
        SaveCharacter(cazador);
        // "Portero": justo al sur de la puerta (49,40), la del templo de Ullathorpe — el bolsón de 72 tiles al
        // norte, donde viven el Sacerdote (51,31), la Veterinaria (48,31), el Bardo (46,33) y el Paladín (53,33).
        // El .csm trae el tile de la puerta bloqueado, así que hasta que se la abre NO se puede entrar. Es la
        // única forma de llegar a esos NPCs: los edificios no tienen tile-exit, se entra por la puerta.
        var portero = CreateCharacter(acc, "Portero", charIndex: 9, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        portero.Pos = new WorldPos(1, 49, 41);
        SaveCharacter(portero);
        // "Cerrajero": al sur de la puerta CON LLAVE (29,40) — obj 53, `Llave=1`, `clave=2`, una de las 4 casas
        // de la columna x=29. El VB6 no la abre nunca con doble-clic: responde LocaleMsg(172).
        var cerrajero = CreateCharacter(acc, "Cerrajero", charIndex: 10, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);
        cerrajero.Pos = new WorldPos(1, 29, 41);
        SaveCharacter(cerrajero);
        Console.WriteLine($"Seed: cuenta demo '{account}' + personajes 'Rugura', 'Merlin', 'Marino', 'Soldado', 'Capitan', 'Rebelde' (fixture) + 'Ulla', 'Cazador' (mapa 1) creados.");
    }
}
