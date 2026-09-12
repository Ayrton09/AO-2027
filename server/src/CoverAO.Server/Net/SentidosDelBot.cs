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
    /// enfrente es una criatura (Protocol.bas:1733).</para></summary>
    public readonly record struct Visible(short CharIndex, byte X, byte Y, short FichaNpc)
    {
        public bool EsNpc => FichaNpc > 0;
    }

    private readonly object _candado = new();
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
    private int _mana, _manaMax = 1, _oro, _puntosLibres;
    private short _nivel, _mapa;
    private byte _hambre, _sed;
    private long _ultimoAvisoDeVeneno;
    private bool _mapaNuevo, _comerciando, _meditando, _paralizado;

    // ── Lo que el que decide le pregunta ──────────────────────────────────────────────────────────────

    public short MiCharIndex { get { lock (_candado) return _miCharIndex; } }
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

    /// <summary>El maná, de 0 a 1. Los que lanzan hechizos meditan cuando esto baja.</summary>
    public double Mana { get { lock (_candado) return _manaMax <= 0 ? 0 : (double)_mana / _manaMax; } }

    /// <summary>¿Tiene maná para gastar? Un guerrero tiene el máximo en 0 y no medita nunca.</summary>
    public bool UsaMana { get { lock (_candado) return _manaMax > 1; } }

    /// <summary>El maná que le queda, en puntos. Es contra esto que se compara lo que sale un hechizo.</summary>
    public int ManaEnPuntos { get { lock (_candado) return _mana; } }

    public int Oro { get { lock (_candado) return _oro; } }

    /// <summary>Puntos de skill sin repartir. El servidor los anuncia al subir de nivel, y un jugador los
    /// gasta: dejarlos juntándose es de las cosas que más delatan que del otro lado no hay nadie.</summary>
    public int PuntosLibres { get { lock (_candado) return _puntosLibres; } }

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
    /// morir.</summary>
    public ((byte X, byte Y) Donde, int Pasos)? BotinMasCercano(Func<short, bool> vale)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            ((byte, byte), int)? mejor = null;
            foreach (var (baldosa, obj) in _enElPiso)
            {
                if (!vale(obj)) continue;
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
    public (Visible Quien, int Pasos)? MasCercano(Func<short, bool> sirve, short salvo = 0)
    {
        lock (_candado)
        {
            if (!_tengoPosicion) return null;
            (Visible, int)? mejor = null;
            foreach (var v in _visibles.Values)
            {
                if (!v.EsNpc || v.CharIndex == _miCharIndex || v.CharIndex == salvo || !sirve(v.FichaNpc)) continue;
                int pasos = Math.Abs(v.X - _x) + Math.Abs(v.Y - _y);
                if (mejor is null || pasos < mejor.Value.Item2) mejor = (v, pasos);
            }
            return mejor;
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
                        if (_visibles.TryGetValue(mio, out var yo)) { _x = yo.X; _y = yo.Y; _tengoPosicion = true; }
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
                    lock (_candado)
                    {
                        // El número de ficha si es criatura; 0 si es una persona. Un nombre vacío es el GM que
                        // se escondió con /SHOWNAME: tampoco es un NPC.
                        short ficha = short.TryParse(nombre, out var f) && f > 0 ? f : (short)0;
                        _visibles[ci] = new Visible(ci, x, y, ficha);
                        if (ci == _miCharIndex) { _x = x; _y = y; _tengoPosicion = true; }
                    }
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
                    l.Largo(); l.Largo();                         // exp para subir, exp
                    l.Byte(); l.Byte();                           // agilidad, fuerza
                    byte hambre = l.Byte(); l.Byte();
                    byte sed = l.Byte();
                    lock (_candado)
                    {
                        _vidaMax = vidaMax; _vida = vida;
                        _manaMax = manaMax; _mana = mana;
                        _energiaMax = enerMax; _energia = ener;
                        _oro = oro;
                        _nivel = nivel; _hambre = hambre; _sed = sed;
                        if (vida <= 0) AlMorir();
                    }
                    break;
                }

                case ServerPacketId.UpdateHP:
                    lock (_candado)
                    {
                        _vida = l.Entero();
                        if (_vida <= 0) AlMorir();
                    }
                    break;

                case ServerPacketId.UpdateMana:
                    lock (_candado) _mana = l.Entero();
                    break;

                case ServerPacketId.UpdateGold:
                    lock (_candado) _oro = l.Largo();
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

                case ServerPacketId.CommerceInit:
                    lock (_candado) { _comerciando = true; _tienda.Clear(); }
                    break;

                case ServerPacketId.CommerceEnd:
                    lock (_candado) { _comerciando = false; _tienda.Clear(); }
                    break;

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
                    lock (_candado)
                    {
                        _vidaMax = vidaMax; _vida = vidaMax;
                        _manaMax = manaMax;
                        _energiaMax = enerMax;
                        _nivel = nivel;
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
                    if (l.Entero() == AvisoDeVeneno) lock (_candado) _ultimoAvisoDeVeneno = Environment.TickCount64;
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
