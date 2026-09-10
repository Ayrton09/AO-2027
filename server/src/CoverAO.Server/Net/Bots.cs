using System.Security.Cryptography;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>Cómo va a intentar entrar el bot en su próxima vida. Si un intento falla se pasa a la siguiente,
/// así que en tres vidas prueba las tres formas: la cuenta y el personaje ya existen, la cuenta existe pero
/// le falta el personaje, y no existe nada.</summary>
internal enum ComoEntrar { Entrar, SoloPersonaje, Alta }

/// <summary>
/// Qué clase de jugador es este bot. Sale del nombre, así que cada uno es siempre el mismo.
///
/// <para>Existe porque la diferencia entre "hay gente jugando" y "hay bots" no está en lo que hace UNO sino
/// en que todos hagan lo mismo. Seis personajes que salen a cazar con el mismo ritmo, la misma pausa y el
/// mismo radio se leen como seis copias; uno que caza, otro que da vueltas por el pueblo y otro que se
/// conecta un rato corto se leen como tres personas.</para>
/// </summary>
internal enum Temperamento
{
    /// <summary>Sale a matar criaturas y vuelve. Es el que sube de nivel.</summary>
    Cazador,
    /// <summary>Caza, pero con menos ganas: pasea más, se aleja menos y descansa seguido.</summary>
    Tranquilo,
    /// <summary>Casi no pelea: da vueltas, se queda parado, mira. El que está conectado haciendo nada.</summary>
    Paseante,
}

/// <summary>
/// Un bot: una cuenta, un personaje y una conducta. Habla el protocolo AO por un
/// <see cref="TransporteDeBot"/>, así que para el resto del servidor es un jugador más — pasa por el mismo
/// login, los mismos intervalos, el mismo anticheat y los mismos topes de cadencia.
///
/// <para><b>Lo que sabe hacer</b>: entrar, pasear por su zona, cazar criaturas que le queden chicas —a mano o
/// con hechizos, si sabe—, levantar lo que dejan, tomar pociones, comer, beber, meditar, repartir los puntos
/// que da subir de nivel, ir a un mercader a vender el botín y reponer, cortar la pelea cuando queda débil,
/// buscar un sacerdote si muere, irse a dormir cuando se le hizo tarde y volver más tarde. Lo que ve del
/// mundo se lo arma <see cref="SentidosDelBot"/> con los paquetes que le manda el servidor — los mismos que
/// recibe el cliente de una persona, ni uno más. Lo único que no hace es HABLAR.</para>
///
/// <para><b>El orden de las decisiones ES la conducta</b>, y va de lo urgente a lo opcional: no morirse,
/// comer, repartir puntos, juntar lo que quedó tirado, ir a vender, recuperar maná, pelear, y pasear sólo si
/// no hay nada mejor. Cada bot además tiene un <see cref="Temperamento"/> que sale de su nombre, así que dos
/// no hacen la misma mezcla.</para>
///
/// <para><b>El ritmo no es decorativo.</b> Un bot que camina a intervalos clavados se distingue de un humano
/// desde la primera pantalla, y además choca contra los relojes del servidor: el paso mínimo lo pone
/// <c>IntervaloParaCaminar</c> y el techo de paquetes por segundo lo corta la sesión. Los números de acá
/// están holgados contra los dos.</para>
/// </summary>
public sealed class Bot
{
    // ── El ritmo humano ───────────────────────────────────────────────────────────────────────────────
    private const int PasoMinMs = 180, PasoMaxMs = 420;         // entre dos pasos de una misma caminata
    private const int PasosMin = 2, PasosMax = 7;               // largo de una caminata antes de doblar
    private const int PausaMinMs = 1_500, PausaMaxMs = 9_000;   // parado entre caminata y caminata
    private const int QuedoMinMs = 15_000, QuedoMaxMs = 60_000; // el rato largo sin hacer nada
    private const int UnaDeCadaTantas = 8;                      // cada cuántas pausas toca una larga
    private const int MirarAlrededorMs = 900;                   // lo que tarda en dar el primer paso al entrar

    /// <summary>Cuánto se le da al servidor para contestar un paso del login. Generoso a propósito: verificar
    /// una contraseña son 210.000 vueltas de PBKDF2 y, con varios bots entrando a la vez, esperan en fila.</summary>
    private const int PlazoDeRespuestaMs = 20_000;

    /// <summary>Intentos seguidos de entrar sin lograrlo antes de que el bot se dé por vencido y lo diga. No
    /// se reintenta para siempre: si el nombre lo tiene un jugador de verdad, o el mundo está lleno, insistir
    /// cada quince segundos hasta que alguien apague el servidor no arregla nada y ensucia el log.</summary>
    private const int IntentosAntesDeRendirse = 3;

    private const int ReconectaMinS = 5, ReconectaMaxS = 20;

    // ── La caza ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Vida (de 0 a 1) a la que toma una poción roja, a la que corta la pelea y se aleja, y a la que
    /// vuelve a pelear. Los tres están holgados a propósito: un bot que muere se queda cadáver donde cayó
    /// —revivir necesita un sacerdote— así que la conducta cuidadosa no es prolijidad, es lo que lo mantiene
    /// en el mundo.</summary>
    private const double TomaPocion = 0.60, SeRetira = 0.35, VuelveAPelear = 0.80;

    /// <summary>Energía mínima para pegar. El golpe la gasta (VB6 <c>StaRequerido</c>) y sin ella el servidor
    /// no swingea, así que seguir mandando golpes sería mandar paquetes al vacío.</summary>
    private const double FuelleMinimo = 0.20;

    /// <summary>Pasos hasta la presa: más lejos que esto no vale la pena, y encima probablemente esté del otro
    /// lado de algo. Es la mitad de lo que el jugador ve en pantalla.</summary>
    private const int RadioDeCaza = 12;

    /// <summary>Pasos gastados persiguiendo antes de soltar la presa. Sin esto, un bot separado por una pared
    /// empuja contra ella para siempre.</summary>
    private const int PacienciaPersiguiendo = 30;

    /// <summary>Cuánto se le puede animar: el golpe máximo de la criatura no puede pasar de esta parte de su
    /// vida. Es el equivalente a que una persona mire al bicho y calcule.</summary>
    private const int GolpeTolerable = 5;

    private const int GolpeMinMs = 1_100, GolpeMaxMs = 1_700;
    private const int PocionMinMs = 700, PocionMaxMs = 1_400;

    /// <summary>Cada cuánto, estando muerto, prueba pedir la resurrección. Sólo funciona con un sacerdote a la
    /// vista, así que la mayoría de las veces no pasa nada — igual que un jugador tirado que insiste.</summary>
    private const int PruebaRevivirMs = 20_000;

    /// <summary>En cuánto se parte una pausa larga para poder reaccionar en el medio. NINGUNA espera puede ser
    /// un solo <c>Delay</c> largo: la primera versión se quedaba quieta hasta un minuto entero y en ese rato
    /// una criatura lo mataba sin que llegara a tomar una poción — dos de seis bots muertos en cuatro minutos,
    /// con mil quinientas pociones en la mochila.</summary>
    private const int RebanadaDePausaMs = 400;

    /// <summary>Radio de la zona propia, en baldosas. Más lejos que esto vuelve para adentro.
    ///
    /// <para>No es prolijidad: sin correa los bots se iban caminando de mapa en mapa y terminaban en cuevas
    /// que los mataban —medido, aparecieron en los mapas 20, 34 y 208 a los pocos minutos—. Un jugador de
    /// nivel uno se queda cerca de donde nació, y además un bot que se aleja deja de verse en la zona donde el
    /// que hostea quiere que se vea gente.</para></summary>
    private const int RadioDeCasa = 20;

    /// <summary>Desde cuántas baldosas se asusta de una criatura grande, y de cuántos golpes suyos se muere
    /// para considerarla grande. Tres golpes es poco margen a propósito: con la vida de un personaje nuevo,
    /// para cuando reacciona ya le pegaron uno.</summary>
    private const int RadioDeSusto = 3, GolpesQueAguanta = 3;

    /// <summary>Desde cuántas baldosas se da vuelta a mirar a alguien, y cada cuántas decisiones le pasa.</summary>
    private const int RadioDeMirar = 6, ChanceDeMirar = 25;

    /// <summary>Tramos de un viaje: cada uno es "caminar hasta la próxima puerta, o hasta el destino". Doce
    /// alcanzan para cruzar varios mapas y recalcular unas cuantas veces en el camino.</summary>
    private const int TramosPorViaje = 12;

    /// <summary>Cuántas baldosas alrededor se busca dónde trabajar, cuántas unidades se hacen de una sentada,
    /// cuánto tarda cada una, y cuánto se descansa entre tandas. Trabajar sin parar toda la sesión no lo hace
    /// nadie, y además el servidor cobra energía por cada unidad.</summary>
    private const int RadioDeFaena = 25, FaenasPorTanda = 12;

    /// <summary>Baldosas (Manhattan) a las que hay que estar del árbol o del yacimiento para que el clic
    /// cuente. El agua no lleva tope; estos dos sí (HandleWorkLeftClick: "Estás demasiado lejos").</summary>
    private const int DistanciaParaFaenar = 2;
    private const int FaenaMinMs = 1_100, FaenaMaxMs = 2_000;
    private const int EnfriamientoFaenaMinS = 60, EnfriamientoFaenaMaxS = 240;

    /// <summary>Cuánto dura una sesión y cuánto se queda afuera después, en minutos.
    ///
    /// <para>Que un personaje esté conectado DÍAS SEGUIDOS es de las cosas que más delatan: nadie juega así.
    /// Con esto la lista de conectados respira —entran unos, se van otros— que es lo que se ve en un servidor
    /// con gente. La sesión se cierra con <c>Quit</c>, o sea el mismo camino que usa el que se va del juego.</para></summary>
    private const int SesionMinM = 20, SesionMaxM = 90, AfueraMinM = 5, AfueraMaxM = 40;

    /// <summary>Maná al que se sienta a meditar, y al que se levanta. Sólo lo hacen los que tienen maná.</summary>
    private const double MeditaBajo = 0.35, MeditaHasta = 0.90;

    /// <summary>Cuánto se aguanta meditando de una vez. Meditar se corta solo al caminar o al pelear, así que
    /// el tope es para no quedarse sentado toda la sesión si el maná no sube.</summary>
    private const int MeditaTopeMs = 45_000;

    /// <summary>Cuántas pociones de vida quiere tener encima, y desde cuántos pasos se acerca a un mercader.
    /// El kit de novato trae mil quinientas, así que el bot recién sale a comprar cuando gastó casi todas — lo
    /// que dispara la visita al mercader, en la práctica, es tener botín para vender.</summary>
    private const int PocionesQueQuiere = 200, RadioDeCompras = 15;

    /// <summary>Ventas por visita. Se corta para que la parada en la tienda dure lo que dura una parada y no
    /// se coma la sesión vaciando la mochila de a un objeto por vez.</summary>
    private const int VentasPorVisita = 6;

    /// <summary>A cuántas baldosas del mercader alcanza con pararse: el servidor atiende a tres y NO exige
    /// línea de vista, así que se le compra a través de la pared del negocio.</summary>
    private const int RadioDelMostrador = 3;

    /// <summary>Minutos entre una visita a la tienda y la siguiente.</summary>
    private const int EnfriamientoCompraMinM = 5, EnfriamientoCompraMaxM = 15;

    /// <summary>Lo que se espera cuando la salida a comprar FRACASA: segundos, no minutos.</summary>
    private const int EnfriamientoFalloMinS = 45, EnfriamientoFalloMaxS = 120;


    /// <summary>Pasos que se aleja para levantar algo del piso. Más lejos que esto, no vale la pena.</summary>
    private const int RadioDeBotin = 8;

    /// <summary>
    /// Desde dónde tira un hechizo, POR EJE. El casteo va en dos paquetes —elegir la ranura y marcar la
    /// baldosa— así que no hace falta estar pegado, que es la gracia de pelear con magia.
    ///
    /// <para>Y va por eje, no por distancia total, porque así lo mide el servidor: la ventana de visión es 8
    /// de ancho y 6 de alto, y una baldosa fuera de ella queda anotada en el log de trampas como "ataque
    /// fuera de rango". Con un radio de 8 en distancia total, una criatura ocho baldosas al norte estaba
    /// adentro de mi cuenta y afuera de la del servidor: **medido, un bot llenó el log con nueve intentos**.
    /// No lo hacía banear —ese caso el servidor lo loguea sin contarlo, justamente porque un jugador que
    /// camina mientras castea cae ahí— pero le ensucia al que hostea el registro donde busca tramposos, que
    /// es casi peor. Los números de acá dejan margen adentro de los del servidor.</para>
    /// </summary>
    private const int AlcanceHechizoX = 6, AlcanceHechizoY = 4;

    /// <summary>La MAC que declara el cliente. Los bots declaran todos la misma y no molesta: el servidor la
    /// registra pero no cuenta cupos por ella (los cupos son por dirección, y cada bot tiene la suya).</summary>
    private const string Mac = "00-00-00-00-00-00";

    public string Nombre { get; }
    private readonly string _cuenta;
    private readonly EntornoDeBots _entorno;
    private readonly Random _rng;
    /// <summary>Multiplicador del paso de este bot. Ver el constructor.</summary>
    private readonly double _ritmo;
    private string? _clave;
    private ComoEntrar _como;
    /// <summary>La sesión anterior la cerró él (se fue a dormir), no un corte.</summary>
    private bool _seFueSolo;

    /// <summary>Cómo es este bot. Sale de una semilla derivada del NOMBRE y no del azar del arranque, igual
    /// que su cara: así el que hostea reconoce a "Bruma, la que anda siempre por el pueblo" y no a un
    /// personaje que cada día juega distinto.</summary>
    private readonly Temperamento _temperamento;

    /// <summary>La clase del personaje. Sale de la MISMA cuenta con la que se creó (derivada del nombre), así
    /// que es la que tiene de verdad — y es la que decide si le sirve una caña, un hacha o un piquete.</summary>
    private readonly byte _clase;

    public Bot(string nombre, string? clave, EntornoDeBots entorno, int semilla)
    {
        Nombre = nombre;
        _cuenta = nombre + "_bot";
        _clave = clave;
        _entorno = entorno;
        _rng = new Random(semilla);
        _como = clave is null ? ComoEntrar.Alta : ComoEntrar.Entrar;

        int propio = 17;
        foreach (char c in nombre) propio = unchecked(propio * 31 + c);
        _temperamento = (Temperamento)(Math.Abs(propio) % 3);
        // El paso propio: entre 0,8 y 1,3 veces el de referencia. Dos personas no caminan igual, y con el
        // mismo número para todos una fila de bots se mueve como un desfile.
        _ritmo = 0.8 + (Math.Abs(propio) % 6) * 0.1;
        _clase = Aspecto().Clase;
    }

    /// <summary>La vida del bot: entrar, vivir, y volver a entrar cuando se cae. Termina cuando se apaga el
    /// servidor o cuando se da por vencido.</summary>
    public async Task VivirAsync(CancellationToken ct)
    {
        int fracasos = 0;
        while (!ct.IsCancellationRequested && fracasos < IntentosAntesDeRendirse)
        {
            bool entro;
            try { entro = await UnaVidaAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"[bot {Nombre}] error: {ex.Message}"); entro = false; }

            if (entro) fracasos = 0;
            else { fracasos++; _como = _como == ComoEntrar.Alta ? ComoEntrar.Entrar : _como + 1; }
            if (ct.IsCancellationRequested) break;
            // El que se fue a dormir vuelve dentro de un rato LARGO; al que se le cortó, enseguida. Es la
            // diferencia entre una lista de conectados que respira y una donde están siempre los mismos.
            var espera = _seFueSolo
                ? TimeSpan.FromMinutes(_rng.Next(AfueraMinM, AfueraMaxM + 1))
                : TimeSpan.FromSeconds(_rng.Next(ReconectaMinS, ReconectaMaxS + 1));
            _seFueSolo = false;
            try { await Task.Delay(espera, ct); }
            catch (OperationCanceledException) { break; }
        }
        if (fracasos >= IntentosAntesDeRendirse)
            Console.WriteLine($"[bot {Nombre}] no pudo entrar {fracasos} veces seguidas; se queda afuera.");
    }

    /// <summary>Una conexión, de punta a punta. Devuelve si llegó a estar adentro del mundo, para distinguir
    /// "se cayó jugando" —que se reintenta sin castigo— de "no pudo entrar", que cuenta como fracaso.</summary>
    private async Task<bool> UnaVidaAsync(CancellationToken ct)
    {
        var transporte = new TransporteDeBot(Nombre);
        var enElMundo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tieneCuenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cerrada = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string aviso = "";
        // Los sentidos son de ESTA vida: al reconectar el mundo se vuelve a armar desde cero con los paquetes
        // del login. Reusarlos arrastraría criaturas que ya no están y una posición vieja.
        var sentidos = new SentidosDelBot();

        transporte.Recibido += p =>
        {
            if (p.Length == 0) return;
            sentidos.Recibir(p);
            switch ((ServerPacketId)p[0])
            {
                case ServerPacketId.Logged: enElMundo.TrySetResult(); break;
                case ServerPacketId.AbrirFormularios: tieneCuenta.TrySetResult(); break;
                case ServerPacketId.ConsoleMsg: aviso = TextoDeConsola(p); break;
            }
        };
        transporte.Cerrado += motivo =>
        {
            cerrada.TrySetResult(motivo);
            // Destraba al que esté esperando un paso del login: sin esto, un rechazo se pagaría con el plazo
            // entero de espera aunque la sesión ya esté cerrada.
            enElMundo.TrySetException(new IOException(motivo));
            tieneCuenta.TrySetException(new IOException(motivo));
        };

        var ini = _entorno.Ini;
        var sesion = new ClientSession(transporte, _entorno.Dispatcher, _entorno.AuthTimeoutMs, _entorno.MaxInboundBytes,
            paquetesPorSegundo: ini.MaxPaquetesPorSegundo, rafagaDePaquetes: ini.RafagaDePaquetes,
            msPorMensaje: ini.MsEntreMensajes, rafagaMensajes: ini.RafagaDeMensajes,
            msPorConsulta: ini.MsEntreConsultas, rafagaConsultas: ini.RafagaDeConsultas);
        // El bucle de recepción corre aparte, igual que en el aceptador de TCP: es el que framea lo que manda
        // el bot y se lo pasa al dispatcher.
        var lector = Task.Run(() => sesion.ReceiveLoopAsync(ct), CancellationToken.None);

        try
        {
            if (!await EntrarAlMundoAsync(transporte, enElMundo, tieneCuenta, ct))
            {
                Console.WriteLine($"[bot {Nombre}] no entró{(aviso.Length > 0 ? ": " + aviso : "")}");
                return false;
            }
            _como = ComoEntrar.Entrar;   // ya existen la cuenta y el personaje: de acá en más entra como cualquiera
            await ComportarseAsync(transporte, sentidos, cerrada.Task, ct);
            return true;
        }
        finally
        {
            transporte.Terminar();
            try { await lector.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>El login, por la forma que toque. false = el servidor lo rechazó, o no contestó.</summary>
    private async Task<bool> EntrarAlMundoAsync(TransporteDeBot transporte, TaskCompletionSource enElMundo,
        TaskCompletionSource tieneCuenta, CancellationToken ct)
    {
        if (_como == ComoEntrar.Alta)
        {
            _clave ??= NuevaClave();
            if (!transporte.Mandar(CrearCuenta())) return false;
            if (!await Espera(tieneCuenta.Task, ct)) return false;
            // La cuenta ya existe en el disco, así que la contraseña se guarda ACÁ y no al final. Si el
            // servidor se cayera entre la creación de la cuenta y la del personaje, sin este guardado quedaría
            // una cuenta cuya clave no conoce nadie y el bot no podría volver a entrar nunca.
            _entorno.GuardarClave(Nombre, _clave);
            _como = ComoEntrar.SoloPersonaje;
        }

        byte[] paquete = _como == ComoEntrar.SoloPersonaje ? CrearPersonaje() : Entrar();
        if (!transporte.Mandar(paquete)) return false;
        return await Espera(enElMundo.Task, ct);
    }

    /// <summary>
    /// La vida del personaje: cuidarse, cazar, y pasear cuando no hay nada que cazar. Termina cuando se cae la
    /// sesión o se apaga el servidor.
    ///
    /// <para>El orden de las decisiones ES la conducta, y va de lo urgente a lo opcional: primero no morirse,
    /// después comer, después pelear, y pasear sólo si no hay nada mejor. Un bot que revisara la caza antes que
    /// la vida seguiría pegando con la barra en rojo, que es lo que ningún jugador hace dos veces.</para>
    /// </summary>
    private async Task ComportarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                        CancellationToken ct)
    {
        _casa = null;            // la zona se elige de nuevo en cada vida: pudo entrar en otro lado
        _pasoTrabado = false;
        _traidoDeCasa.Clear();
        _nadaQueHacerAca.Clear();   // las baldosas descartadas eran de la vida anterior
        foreach (byte ranura in sentidos.RanurasOcupadas()) _traidoDeCasa.Add(sentidos.QueHayEn(ranura));
        await Task.Delay(_rng.Next(MirarAlrededorMs, MirarAlrededorMs * 4), ct);
        // El modo combate es un interruptor y hay que prenderlo: sin él, el servidor contesta el aviso 102 y no
        // swingea. Se prende una vez al entrar, como hace cualquiera que sale a cazar.
        transporte.Mandar(Paquete(ClientPacketId.CombatModeToggle, _ => { }));

        int pausas = 0;
        long proximaResurreccion = 0;
        // Cuánto va a jugar esta vez. Cada bot elige distinto y en cada sesión distinto, como cualquiera.
        long finDeSesion = Environment.TickCount64 + _rng.Next(SesionMinM, SesionMaxM + 1) * 60_000L;

        while (!ct.IsCancellationRequested && !cerrada.IsCompleted)
        {
            if (!sentidos.Ubicado) { await Task.Delay(500, ct); continue; }

            // SE VA A DORMIR. Se cierra con `Quit`, el mismo camino del jugador que se va: el servidor le
            // guarda el personaje y lo saca del mundo como corresponde.
            if (Environment.TickCount64 >= finDeSesion && sentidos.Vivo)
            {
                transporte.Mandar(Paquete(ClientPacketId.Quit, _ => { }));
                await Task.Delay(1_500, ct);
                _seFueSolo = true;
                return;
            }

            // MUERTO. El cadáver queda donde cayó y revivir necesita un sacerdote a la vista, así que lo único
            // que puede hacer es caminar buscando uno e insistir cada tanto. Es lo mismo que hace una persona.
            if (!sentidos.Vivo)
            {
                if (Environment.TickCount64 >= proximaResurreccion)
                {
                    proximaResurreccion = Environment.TickCount64 + PruebaRevivirMs;
                    PedirResurreccion(transporte, sentidos);
                }
                if (!await BuscarSacerdoteAsync(transporte, sentidos, cerrada, ct)) return;
                continue;
            }

            // CURARSE NO CONSUME EL TURNO. Antes esto era `if (...) continue;` y ahí estaba la trampa: un bot
            // al que una criatura le pega sin parar se cura, vuelve a empezar el ciclo, se cura otra vez — y
            // nunca llega a la decisión de HUIR, que es tres líneas más abajo. Medido en el log del servidor:
            // el mismo bot tomando poción tras poción, con la barra volviendo a 16/16 cada vez, sin moverse ni
            // hacer nada más en toda la sesión. Se cura y SIGUE decidiendo.
            await CuidarseAsync(transporte, sentidos, ct);
            if (!sentidos.Vivo) continue;

            // HUIR DE LO QUE LE QUEDA GRANDE. Elegir bien a quién atacar no alcanza: las criaturas atacan
            // solas, y una que pega la mitad de la vida de un golpe mata a un personaje de nivel uno antes de
            // que alcance a tomar dos pociones. Un jugador que ve algo así se va, y esto es eso.
            int vidaTope = sentidos.VidaMaxima;
            int nivelAhora = sentidos.Nivel;
            var peligro = sentidos.MasCercano(ficha => EsPeligroso(ficha, vidaTope, nivelAhora));
            if (peligro is { } amenaza && amenaza.Pasos <= RadioDeSusto)
            {
                if (!await AlejarseAsync(transporte, sentidos, amenaza.Quien, cerrada, ct)) return;
                continue;
            }

            if (RepartirPuntos(transporte, sentidos)) { await Task.Delay(_rng.Next(900, 2_500), ct); continue; }
            if (await LevantarBotinAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await IrDeComprasAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await MeditarAsync(transporte, sentidos, ct)) continue;
            if (await TrabajarAsync(transporte, sentidos, cerrada, ct)) continue;

            // La vida máxima se lee ANTES de buscar: adentro de la búsqueda el modelo está bajo su candado, y
            // preguntarle otra cosa desde ahí ata dos lecturas que no tienen por qué ir juntas.
            int vidaMaxima = sentidos.VidaMaxima;
            var presa = sentidos.MasCercano(ficha => EsPresa(ficha, vidaMaxima, nivelAhora));
            if (presa is { } p && p.Pasos <= RadioDeCazaSuyo && sentidos.Salud >= VuelveAPelear && LeDanGanas())
            {
                if (!await CazarAsync(transporte, sentidos, p.Quien.CharIndex, cerrada, ct)) return;
                continue;
            }

            if (MirarALaGente(transporte, sentidos)) { await Task.Delay(_rng.Next(1_200, 3_500), ct); continue; }

            if (!await PasearAsync(transporte, sentidos, cerrada, ct)) return;
            bool larga = ++pausas % UnaDeCadaTantas == 0;
            await QuedarseAsync(sentidos, larga ? _rng.Next(QuedoMinMs, QuedoMaxMs) : _rng.Next(PausaMinMs, PausaMaxMs), ct);
        }
    }

    /// <summary>¿Esa criatura lo puede matar de pocos golpes? Es la que hay que esquivar, se la haya buscado
    /// o no.</summary>
    private bool EsPeligroso(short ficha, int vidaMaxima, int nivel)
    {
        var n = _entorno.Datos.Npc(ficha);
        if (n is null || !n.Hostile) return false;
        // Por lo que pega, o por lo que ES: el que reparte mucha experiencia es un bicho grande aunque su
        // golpe a mano diga 0 porque pelea con magia.
        return n.MaxHit * GolpesQueAguanta >= Math.Max(1, vidaMaxima)
            || n.GiveExp > ExperienciaQueSeAnima(nivel) * 3;
    }

    /// <summary>
    /// GIRARSE A MIRAR AL QUE PASA. Si hay alguien cerca, el bot se da vuelta hacia esa persona y se queda un
    /// momento. Devuelve si lo hizo.
    ///
    /// <para>No cambia nada del juego y es de lo que más se nota: un mundo donde los personajes se ignoran
    /// entre sí no parece un mundo con gente. Es sólo un cambio de rumbo —el mismo paquete que manda el
    /// cliente cuando el jugador gira— y sale de vez en cuando, no siempre, porque nadie mira a todos.</para>
    /// </summary>
    private bool MirarALaGente(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        if (_rng.Next(100) >= ChanceDeMirar) return false;
        if (sentidos.PersonaMasCercana() is not { } quien || quien.Pasos > RadioDeMirar) return false;

        var (x, y) = sentidos.Posicion;
        int dx = quien.Quien.X - x, dy = quien.Quien.Y - y;
        if (dx == 0 && dy == 0) return false;
        return transporte.Mandar(Paquete(ClientPacketId.ChangeHeading,
            w => w.WriteByte(SentidosDelBot.RumboHacia(dx, dy))));
    }

    /// <summary>Cuánto lejos se va a buscar pelea, según cómo sea. El paseante ni mira.</summary>
    private int RadioDeCazaSuyo => _temperamento switch
    {
        Temperamento.Cazador => RadioDeCaza,
        Temperamento.Tranquilo => RadioDeCaza / 2,
        _ => 1,
    };

    /// <summary>¿Se pelea o lo deja pasar? El cazador siempre; el tranquilo casi siempre; el paseante sólo si
    /// se lo lleva por delante. Es la diferencia entre un personaje que barre el mapa y uno que anda dando
    /// vueltas — y la que hace que seis bots no se muevan como seis copias.</summary>
    private bool LeDanGanas() => _temperamento switch
    {
        Temperamento.Cazador => true,
        Temperamento.Tranquilo => _rng.Next(100) < 70,
        _ => _rng.Next(100) < 15,
    };

    /// <summary>Reparte los puntos que dio el nivel. Devuelve si mandó algo.
    ///
    /// <para>Se reparten de a uno y en una skill al azar de las que el personaje puede subir. Pedir más de los
    /// que hay NO es un pedido a recortar: el servidor lo cuenta como intento de hackeo y cierra la conexión
    /// (<c>HandleModifySkills</c>), así que el bot lleva la cuenta él y descuenta lo que gastó.</para></summary>
    private bool RepartirPuntos(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        int libres = sentidos.PuntosLibres;
        if (libres <= 0) return false;

        // Un jugador no reparte de a uno cada vez que sube: junta y gasta. Y no los mete todos en la misma.
        var reparto = new byte[GameLimits.NumSkills + 1];
        int gastados = 0;
        // LO PROPIO PRIMERO. Repartir los diez puntos al azar entre veintisiete skills deja al Pescador con
        // menos de un tercio de chance de tocar Pesca alguna vez, y sin su skill el oficio rinde casi nada.
        // Un jugador que eligió una clase de oficio pone ahí sus primeros puntos.
        byte propia = SkillDeSuOficio;
        if (propia > 0 && libres > 0)
        {
            byte suyos = (byte)Math.Min(libres, Math.Max(1, libres / 2));
            reparto[propia] = suyos;
            gastados = suyos;
        }
        for (int i = gastados; i < libres; i++)
        {
            int skill = _rng.Next(1, GameLimits.NumSkills + 1);
            if (reparto[skill] == byte.MaxValue) continue;
            reparto[skill]++;
            gastados++;
        }
        if (gastados == 0) return false;

        bool salio = transporte.Mandar(Paquete(ClientPacketId.ModifySkills, w =>
        {
            for (int i = 1; i <= GameLimits.NumSkills; i++) w.WriteByte(reparto[i]);
        }));
        if (salio) sentidos.GastarPuntos(gastados);
        return salio;
    }

    /// <summary>
    /// IR A UN MERCADER: vender lo que juntó y reponer pociones. Devuelve si se ocupó de esto.
    ///
    /// <para>Es la otra mitad del ciclo de cualquier jugador —matar, juntar, vender, reponer— y sin ella el
    /// botín se acumula sin sentido y las pociones se terminan sin reemplazo. Sólo se hace si hay un mercader
    /// A LA VISTA: el bot no sabe dónde queda la tienda, igual que alguien que recién empieza.</para>
    ///
    /// <para>Lo que está PUESTO no se vende nunca, y las pociones, la comida y la bebida tampoco: vender el
    /// arma que lleva encima, o el agua que va a necesitar, no lo hace nadie. Lo que queda es justamente lo
    /// que levantó del piso.</para>
    /// </summary>
    private async Task<bool> IrDeComprasAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                              CancellationToken ct)
    {
        // ENFRIAMIENTO. Sin esto el bot vuelve a la tienda en el instante siguiente: si el mercader NO le
        // compra lo que le ofrece —cada uno acepta un tipo de objeto y nada más— la mochila queda igual, la
        // condición se vuelve a cumplir, y el bot queda pegado al mostrador ofreciendo lo mismo para siempre.
        // Medido: ocho bots inmóviles en tres baldosas durante diez minutos, y dos muertos ahí parados porque
        // mientras negocian no miran la vida. Un jugador tampoco vuelve a entrar a la tienda a los cinco
        // segundos.
        if (Environment.TickCount64 < _proximaCompra) return false;

        bool faltanPociones = sentidos.CuantoTiene(EsPocionDeVida) < PocionesQueQuiere;
        bool hayQueVender = sentidos.EnLaMochila(EsBotin) is not null;
        bool faltaHerramienta = LeFaltaLaHerramienta(sentidos);
        if (!faltanPociones && !hayQueVender && !faltaHerramienta) return false;

        // QUÉ MERCADER SIRVE. Si falta la herramienta Y hay con qué pagarla, se busca uno que la venda; si no,
        // uno que le compre lo que trae. Con el filtro de la herramienta siempre puesto, el bot con oficio
        // quedaba atado para siempre a los cuatro que las venden — y como cuesta 200 y él tiene 0, tampoco
        // podía vender botín ni comprar pociones en ningún otro lado: el lazo se cerraba sobre sí mismo.
        // El precio sale de la DATA, no de un número a mano: las tres herramientas valen 200, así que los tres
        // oficios arrancan igual —primero juntar la plata vendiendo botín, y hasta entonces les sirve
        // cualquier mercader— y el día que la data cambie el precio esto cambia solo.
        int cuestaLaHerramienta = HerramientaDeSuOficio > 0
            ? (_entorno.Datos.Obj(HerramientaDeSuOficio)?.Value ?? int.MaxValue) : int.MaxValue;
        short quiero = faltaHerramienta && sentidos.Oro >= cuestaLaHerramienta ? HerramientaDeSuOficio : (short)0;
        // Y SI VA A VENDER, QUE SEA A ALGUIEN QUE LE COMPRE. Cada mercader acepta UN tipo de objeto y rechaza
        // el resto en silencio (`TipoItems`, VB6 Comercio.bas:123), así que caer en el gremio equivocado es un
        // viaje entero para volver con lo mismo: el pollo se lo compra el de comidas, no el herrero. Eso lo
        // sabe cualquiera que jugó una tarde, y sale de la data.
        bool Sirve(int ficha)
        {
            if (_entorno.Datos.Npc(ficha) is not { StockItems: { Count: > 0 } stock } mercader) return false;
            if (quiero != 0) return stock.Any(s => s.ObjIndex == quiero);
            if (!hayQueVender) return true;
            return sentidos.EnLaMochila(obj => LeVendeA(mercader.TipoItems, obj)) is not null;
        }

        // Y EL MAPA TIENE QUE ATENDER. Un mercader no le vende a un ciudadano donde el `battleMode` del mapa no
        // es 2 ni 3 (VB6 Protocol.bas:5205, portado igual), y TODOS los personajes nacen ciudadanos. Sin este
        // filtro el viaje terminaba siempre en el mapa vecino —que tiene los tres vendedores de herramientas y
        // battleMode 1— donde nadie lo atendía. De los 863 mapas del mundo, 55 tienen battleMode 2 o 3 y 23 de
        // ésos tienen mercader: destinos sobran.
        bool MapaAtiende(int mapa) => _entorno.Datos.MapBattleMode(mapa) is 2 or 3;

        var tienda = MapaAtiende(sentidos.Mapa) ? sentidos.MasCercano(ficha => Sirve(ficha)) : null;
        // NO HAY NINGUNO A LA VISTA: se viaja. Se intenta DOS veces porque el primer destino se elige sin
        // conocer el mapa de llegada —desde otro mapa no se puede saber por dónde se va a entrar— y muchos
        // mercaderes están adentro de un edificio al que no se llega caminando: medido, en el mapa donde nacen
        // los bots NINGUNO de los dos es alcanzable, y en el de al lado lo son tres de once. Ya en el mapa, la
        // segunda búsqueda sí exige camino y elige uno al que se llega.
        for (int intento = 0; intento < 3 && (tienda is null || tienda.Value.Pasos > RadioDeCompras); intento++)
        {
            if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, Sirve, MapaAtiende) is not { } lugar)
            { PostergarCompra(); return intento > 0; }
            if (!await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, RadioDelMostrador)
                && sentidos.Mapa != lugar.Mapa)
            { PostergarCompra(); return true; }   // ni siquiera se llegó al mapa
            tienda = MapaAtiende(sentidos.Mapa) ? sentidos.MasCercano(ficha => Sirve(ficha)) : null;
        }
        if (tienda is null || tienda.Value.Pasos > RadioDeCompras) { PostergarCompra(); return true; }
        var t = tienda.Value;

        // Acercarse al mostrador. Va por el navegante y no a los tumbos: el mercader atiende hasta tres
        // baldosas (VB6 HandleCommerceStart) y el paso codicioso se traba con cualquier mesa en el medio, que
        // adentro de una tienda es lo normal.
        if (sentidos.Donde(t.Quien.CharIndex) is { } donde
            && Math.Abs(donde.X - sentidos.Posicion.X) + Math.Abs(donde.Y - sentidos.Posicion.Y) > RadioDelMostrador
            && !await IrHastaAsync(transporte, sentidos, sentidos.Mapa, donde.X, donde.Y, cerrada, ct, RadioDelMostrador))
        { PostergarCompra(); return true; }

        if (sentidos.Donde(t.Quien.CharIndex) is not { } mostrador) return true;
        // SE ABRE CON DOBLE CLIC, no con clic + CommerceStart.
        //
        // El `LeftClick` del servidor sondea PRIMERO la baldosa de ABAJO del punto clickeado, y `PlayerAt` no
        // excluye al que clickea: cuando el bot quedaba parado justo debajo del mercader —que es donde lo deja
        // el camino más corto— se targeteaba A SÍ MISMO, `TargetNpc` quedaba en 0 y `HandleCommerceStart` salía
        // MUDO. Por eso en las corridas se veía llegar al bot y no abrirse nada. `HandleDoubleClick` resuelve
        // el NPC de la baldosa directo y abre la tienda él mismo.
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(mostrador.X).WriteByte(mostrador.Y)));

        for (int espera = 0; espera < 12 && !sentidos.Comerciando; espera++) await Task.Delay(250, ct);
        if (!sentidos.Comerciando) { PostergarCompra(); return true; }   // no atendió (facción, distancia, o no era mercader)

        bool compro = await VenderYComprarAsync(transporte, sentidos, t.Quien.FichaNpc, ct);
        transporte.Mandar(Paquete(ClientPacketId.CommerceEnd, _ => { }));
        await Task.Delay(_rng.Next(600, 1_500), ct);
        // Comprar es lo que se hace de vez en cuando; vender, cada vez que la mochila se llena. Al que sale
        // de la tienda con lo que fue a buscar se le da el descanso largo; al que sólo pasó a cambiar el
        // botín, el corto — si no, un Pescador que vive de vender pescado se pasaría la sesión esperando.
        PostergarCompra(exito: compro);
        return true;
    }

    /// <summary>Cuándo puede volver a intentar una compra. El ÉXITO descansa quince minutos —ya compró— pero
    /// un FRACASO no puede costar lo mismo: con el mismo castigo en las seis salidas, un tropiezo de tres
    /// segundos se comía el cupo de toda la sesión, y la corrida terminaba con cero compras sin que el bot
    /// hubiera llegado a intentarlo más de una vez.</summary>
    private void PostergarCompra(bool exito = false) =>
        _proximaCompra = Environment.TickCount64 + (exito
            ? _rng.Next(EnfriamientoCompraMinM, EnfriamientoCompraMaxM + 1) * 60_000L
            : _rng.Next(EnfriamientoFalloMinS, EnfriamientoFalloMaxS + 1) * 1_000L);

    /// <summary>Con la ventana abierta: primero vender —para tener con qué— y después comprar. Devuelve si
    /// se llevó algo.</summary>
    private async Task<bool> VenderYComprarAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                                 short fichaDelMercader, CancellationToken ct)
    {
        bool compro = false;
        for (int i = 0; i < VentasPorVisita && sentidos.Comerciando; i++)
        {
            // La vida primero, también acá dentro: negociar son varios segundos y una criatura no espera a
            // que el bot termine de vender.
            if (!sentidos.Vivo || sentidos.Salud < TomaPocion) return compro;
            if (sentidos.EnLaMochila(obj => LeVendeA(TipoDeMercader(fichaDelMercader), obj)) is not { } item) break;
            short queOfrezco = sentidos.QueHayEn(item.Ranura);
            transporte.Mandar(Paquete(ClientPacketId.CommerceSell,
                w => w.WriteByte(item.Ranura).WriteInteger(item.Cantidad)));
            await Task.Delay(_rng.Next(500, 1_200), ct);
            // SI SIGUE AHÍ, NO SE LO COMPRAN. El `TipoItems` ya se miró antes de venir, pero el original
            // rechaza además por otras razones (lo de novato, lo de facción) y siempre en silencio; sin
            // anotarlo, el bot volvería a ofrecer lo mismo para siempre.
            //
            // Se anota contra EL GREMIO, no contra el objeto a secas: el que no le compra el pollo al herrero
            // se lo compra el de comidas a doce baldosas. Anotado por objeto, un intento contra el mostrador
            // equivocado lo dejaba invendible para el resto de la vida del bot — y en la cuna el pollo es lo
            // único que tiene para vender.
            if (sentidos.QueHayEn(item.Ranura) == queOfrezco) _noLoCompran.Add((TipoDeMercader(fichaDelMercader), queOfrezco));
        }

        // LA HERRAMIENTA PRIMERO: es lo que habilita el oficio, y sin oficio no hay con qué comprar lo demás.
        if (LeFaltaLaHerramienta(sentidos) && sentidos.EnLaTienda(obj => obj == HerramientaDeSuOficio) is { } h)
        {
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(h.Ranura).WriteInteger(1)));
            await Task.Delay(_rng.Next(600, 1_400), ct);
            compro = !LeFaltaLaHerramienta(sentidos);
        }

        int faltan = PocionesQueQuiere - sentidos.CuantoTiene(EsPocionDeVida);
        if (faltan <= 0 || !sentidos.Comerciando) return compro;
        if (sentidos.EnLaTienda(EsPocionDeVida) is not { } oferta) return compro;

        // No se gasta hasta el último cobre: dejar algo encima es lo que hace cualquiera, y además EL PRECIO
        // DE LA VITRINA NO ES EL DE LA CAJA — el mostrador canta la división cruda y la compra redondea para
        // arriba con la cantidad adentro (ver `Comercio`), así que la cuenta de acá queda siempre corta.
        // Precio 0 = gratis: se lleva lo que necesita. Con la cuenta de abajo un objeto gratis daba "puedo
        // comprar cero", que es justo al revés.
        int puede = oferta.Precio > 0 ? (int)(sentidos.Oro * 0.8 / oferta.Precio) : faltan;
        int cuantas = Math.Clamp(Math.Min(faltan, puede), 0, short.MaxValue);
        if (cuantas <= 0) return compro;

        int teniaAntes = sentidos.CuantoTiene(EsPocionDeVida);
        transporte.Mandar(Paquete(ClientPacketId.CommerceBuy,
            w => w.WriteByte(oferta.Ranura).WriteInteger((short)cuantas)));
        await Task.Delay(_rng.Next(600, 1_400), ct);
        return compro || sentidos.CuantoTiene(EsPocionDeVida) > teniaAntes;
    }

    /// <summary>La herramienta del oficio de esta clase, o 0 si no tiene oficio. Los tres números son los del
    /// <c>obj.dat</c>, y son los que el servidor mira para saber qué oficio se está ejerciendo: el oficio lo
    /// declara la HERRAMIENTA EQUIPADA, no la skill que diga el cliente.</summary>
    private short HerramientaDeSuOficio => _clase switch
    {
        ClasePescador => CanaDePesca,
        ClaseLenador => HachaDeLenador,
        ClaseMinero => PiqueteDeMinero,
        _ => 0,
    };

    private const byte ClasePescador = 11, ClaseLenador = 13, ClaseMinero = 14;
    private const short CanaDePesca = 881, HachaDeLenador = 127, PiqueteDeMinero = 187;

    /// <summary>¿Le falta la herramienta de su oficio? Ninguna clase nace con ella —el Pescador, el Leñador y
    /// el Minero reciben todos el mismo martillo de piedra— así que hay que ir a comprarla, igual que un
    /// jugador nuevo.</summary>
    private bool LeFaltaLaHerramienta(SentidosDelBot sentidos) =>
        HerramientaDeSuOficio > 0 && sentidos.CuantoTiene(obj => obj == HerramientaDeSuOficio) == 0;

    /// <summary>¿Es botín? Todo lo que apareció durante la sesión menos las pociones y el oro. Lo que lleva
    /// puesto ya lo descarta la mochila.</summary>
    private bool EsBotin(short obj)
    {
        // LO QUE TRAJO DE CASA NO SE VENDE. La regla anterior era por descarte —todo lo que no fuera poción,
        // comida, bebida ni oro— y con eso el bot le ofrecía al mercader su propia arma: medido en los
        // `.chr`, terminaban peleando con el martillo en la mochila. Acá se invierte: se vende lo que
        // APARECIÓ durante la sesión, que es exactamente el botín.
        if (_traidoDeCasa.Contains(obj)) return false;
        var o = _entorno.Datos.Obj(obj);
        // LA COMIDA Y LA BEBIDA DEL BOTÍN SÍ SE VENDEN, y era lo único que tenían. En el Dungeon Newbie —donde
        // nace TODO personaje— las criaturas que un recién nacido puede matar dan CERO oro: murciélagos,
        // serpientes, gallinas y gallos. El escorpión da 3 y recién se le anima en nivel 2; el lobo da 50 y
        // hay que llegar a nivel 10. Pero las siete gallinas y los cinco gallos sueltan un Pollo cada uno con
        // probabilidad 100 y el Pollo vale 7, y el mercader de Comidas está a doce pasos de la baldosa de
        // nacimiento, en un mapa que atiende. O sea que los doscientos de la herramienta están ahí, en unos
        // treinta pollos, y con la comida en la lista de "no se vende" el bot los pisaba y seguía de largo:
        // el oro cerraba cada corrida en CERO y el oficio no arrancaba nunca.
        //
        // Vender el pollo y quedarse las manzanas no es una excepción: lo que come salió del kit de novato y
        // ya lo protege `_traidoDeCasa`, que son otros números de objeto. Las pociones sí quedan afuera,
        // porque son de lo que depende para no morirse, y lo de novato porque el mercader no lo acepta
        // (VB6 Comercio.bas:120) y encima `SalePrice` lo paga a cero.
        return o is not null && !o.IsPotion && !o.IsGold && !o.IsNewbie;
    }

    /// <summary>¿ESE mercader se lo compra? Tiene que ser botín, el gremio tiene que aceptar ese tipo de
    /// objeto —o comprar de todo— y no puede estar anotado como ya rechazado por ese mismo gremio.</summary>
    private bool LeVendeA(int tipoDeMercader, short obj) =>
        EsBotin(obj)
        && _entorno.Datos.Obj(obj) is { } o
        && (tipoDeMercader == o.ObjType || tipoDeMercader == TipoItemsCualquiera)
        && !_noLoCompran.Contains((tipoDeMercader, obj));

    /// <summary>VB6 <c>otCualquiera</c> (Declares.bas:498): el mercader que compra de todo. Lo traen cinco.</summary>
    private const int TipoItemsCualquiera = 1000;

    /// <summary>Lo que tenía encima al entrar al mundo. Se anota una vez por sesión.</summary>
    private readonly HashSet<short> _traidoDeCasa = new();

    /// <summary>Lo que ya se probó vender y ese tipo de mercader no quiso. Es por bot y sobrevive a la
    /// sesión: lo que no le compra un herrero tampoco se lo compra otro herrero, porque los dos miran el
    /// mismo <c>TipoItems</c>.</summary>
    private readonly HashSet<(int TipoDeMercader, short Obj)> _noLoCompran = new();

    /// <summary>Qué acepta comprar ese mercader (<c>TipoItems</c>). Es lo que hace que el rechazo se anote
    /// contra el gremio y no contra el objeto.</summary>
    private int TipoDeMercader(short ficha) => _entorno.Datos.Npc(ficha)?.TipoItems ?? 0;

    /// <summary>Cuándo puede volver a una tienda.</summary>
    private long _proximaCompra;

    private bool EsPocionDeVida(short obj) => _entorno.Datos.Obj(obj) is { IsPotion: true, SubType: 3 };

    /// <summary>Levanta lo que quedó tirado cerca. Es lo primero que hace cualquiera después de matar algo, y
    /// su ausencia se nota: un personaje que mata y sigue de largo dejando el oro en el piso no existe.</summary>
    private async Task<bool> LevantarBotinAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                                CancellationToken ct)
    {
        if (sentidos.BotinMasCercano(SePuedeLevantar) is not { } botin || botin.Pasos > RadioDeBotin
            || _nadaQueHacerAca.Contains((sentidos.Mapa, botin.Donde.X, botin.Donde.Y))) return false;

        for (int intentos = 0; intentos < RadioDeBotin * 2 && !cerrada.IsCompleted; intentos++)
        {
            var (x, y) = sentidos.Posicion;
            int dx = botin.Donde.X - x, dy = botin.Donde.Y - y;
            if (dx == 0 && dy == 0)
            {
                short queHabia = sentidos.EnElPiso(botin.Donde);
                transporte.Mandar(Paquete(ClientPacketId.PickUp, _ => { }));
                await Task.Delay(_rng.Next(400, 900), ct);
                // Si sigue ahí no se puede levantar (mochila llena, o algo que no es botín). Se marca la
                // baldosa: sin esto el bot vuelve a caminar hasta ella y a agacharse, para siempre.
                if (sentidos.EnElPiso(botin.Donde) == queHabia) _nadaQueHacerAca.Add((sentidos.Mapa, botin.Donde.X, botin.Donde.Y));
                return true;
            }
            if (await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct) is var r && r != Paso.Dado)
                return r != Paso.SinConexion;
        }
        return true;
    }

    /// <summary>
    /// ¿Es algo que se pueda levantar? El servidor manda como "objeto del piso" lo tirado Y LAS PUERTAS
    /// (ModAreas: el decorado fijo no viaja, pero las puertas sí, para que se sepa si están abiertas).
    ///
    /// <para>Sin este filtro el bot trataba a cada puerta del pueblo como botín: caminaba hasta ella, se
    /// agachaba, no levantaba nada, y volvía a empezar. Medido: ocho bots con oro en CERO después de nueve
    /// minutos, porque ninguno llegó nunca a un botín de verdad.</para>
    /// </summary>
    private bool SePuedeLevantar(short obj)
    {
        if (obj <= 0) return false;
        var o = _entorno.Datos.Obj(obj);
        return o is not null && !o.IsDoor;
    }

    /// <summary>Baldosas donde ya se agachó y no levantó nada.</summary>
    private readonly HashSet<(short Mapa, byte X, byte Y)> _nadaQueHacerAca = new();

    /// <summary>
    /// TRABAJAR EL OFICIO: pescar, talar o minar. Devuelve si se ocupó de esto.
    ///
    /// <para>La secuencia es la del cliente: se equipa la herramienta —el servidor decide QUÉ oficio se está
    /// ejerciendo por la herramienta que llevás puesta, no por lo que diga el paquete—, se manda
    /// <c>Work</c> con la skill, el servidor contesta pidiendo el objetivo, y se marca la baldosa con
    /// <c>WorkLeftClick</c>. Después se repite: cada clic es una unidad de trabajo.</para>
    ///
    /// <para>Sólo lo hacen las tres clases que tienen oficio, y sólo si ya consiguieron la herramienta —que
    /// ninguna trae de nacimiento y hay que ir a comprar—. Es lo que hace que un Pescador se distinga de un
    /// Guerrero mirándolo, en vez de ser el mismo bot con otro nombre de clase.</para>
    /// </summary>
    private async Task<bool> TrabajarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                           CancellationToken ct)
    {
        short herramienta = HerramientaDeSuOficio;
        if (herramienta == 0 || LeFaltaLaHerramienta(sentidos)) return false;
        if (Environment.TickCount64 < _proximaFaena) return false;
        if (sentidos.Fuelle < FuelleMinimo) return false;   // trabajar cuesta energía

        byte skill = SkillDeSuOficio;
        bool agua = skill == GameLimits.SkillPesca;
        Func<int, bool>? recurso = agua ? null
            : obj => _entorno.Datos.Obj(obj) is { } o && (skill == GameLimits.SkillTalar ? o.IsTree : o.IsDeposit);

        // ¿Hay dónde trabajar acá cerca? Si no, se viaja hasta donde haya.
        var punto = _entorno.Mapas.DondeTrabajar(sentidos.Mapa, sentidos.Posicion, RadioDeFaena, agua, recurso);
        if (punto is null)
        {
            if (_entorno.Mapas.DondeSeTrabaja(sentidos.Mapa, agua, recurso) is not { } lejos) { PostergarFaena(); return false; }
            if (!await IrHastaAsync(transporte, sentidos, lejos.Mapa, lejos.X, lejos.Y, cerrada, ct)) { PostergarFaena(); return true; }
            punto = _entorno.Mapas.DondeTrabajar(sentidos.Mapa, sentidos.Posicion, RadioDeFaena, agua, recurso);
            if (punto is null) { PostergarFaena(); return true; }
        }

        // La herramienta va PUESTA, y equiparla es un interruptor: sólo se manda si no la lleva.
        if (sentidos.EnLaMochila(obj => obj == herramienta) is { } enMochila)
        {
            transporte.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(enMochila.Ranura)));
            await Task.Delay(_rng.Next(400, 900), ct);
        }

        // CUÁNTO HAY QUE ACERCARSE, y no es lo mismo para los tres.
        //
        // Pescar no tiene tope de distancia: al servidor le alcanza con que la baldosa tenga agua y caiga en
        // la ventana de visión. Pero MINAR y TALAR exigen DOS baldosas contadas en Manhattan —"Estás demasiado
        // lejos"— y el bot se acercaba a seis: para el Minero y el Leñador el clic se rechazaba SIEMPRE, y las
        // doce faenas de la tanda se perdían contra ese aviso.
        int cerca = agua ? AlcanceHechizoX : DistanciaParaFaenar;
        for (int i = 0; i < RadioDeFaena * 2 && !cerrada.IsCompleted; i++)
        {
            var (x, y) = sentidos.Posicion;
            int dx = punto.Value.Donde.X - x, dy = punto.Value.Donde.Y - y;
            bool aTiro = agua
                ? Math.Abs(dx) <= AlcanceHechizoX && Math.Abs(dy) <= AlcanceHechizoY
                : Math.Abs(dx) + Math.Abs(dy) <= cerca;
            if (aTiro) break;
            if (await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct) != Paso.Dado) { PostergarFaena(); return true; }
        }

        // ¿QUEDÓ A TIRO? El acercamiento corta por intentos, no por haber llegado: si una pared lo dejó lejos,
        // los doce clics caen fuera de la ventana de visión del servidor y se pierden contra un PosUpdate mudo.
        var (fx, fy) = sentidos.Posicion;
        bool llego = agua
            ? Math.Abs(punto.Value.Donde.X - fx) <= AlcanceHechizoX && Math.Abs(punto.Value.Donde.Y - fy) <= AlcanceHechizoY
            : Math.Abs(punto.Value.Donde.X - fx) + Math.Abs(punto.Value.Donde.Y - fy) <= DistanciaParaFaenar;
        if (!llego) { PostergarFaena(); return true; }

        for (int golpe = 0; golpe < FaenasPorTanda && !cerrada.IsCompleted; golpe++)
        {
            if (Urge(sentidos)) break;
            transporte.Mandar(Paquete(ClientPacketId.Work, w => w.WriteByte(skill)));
            transporte.Mandar(Paquete(ClientPacketId.WorkLeftClick,
                w => w.WriteByte(punto.Value.Donde.X).WriteByte(punto.Value.Donde.Y).WriteByte(skill)));
            await Task.Delay(_rng.Next(FaenaMinMs, FaenaMaxMs), ct);
        }
        PostergarFaena();
        return true;
    }

    private void PostergarFaena() =>
        _proximaFaena = Environment.TickCount64 + _rng.Next(EnfriamientoFaenaMinS, EnfriamientoFaenaMaxS + 1) * 1_000L;

    private long _proximaFaena;

    /// <summary>La skill del oficio de esta clase.</summary>
    private byte SkillDeSuOficio => _clase switch
    {
        ClasePescador => (byte)GameLimits.SkillPesca,
        ClaseLenador => (byte)GameLimits.SkillTalar,
        ClaseMinero => (byte)GameLimits.SkillMineria,
        _ => 0,
    };

    /// <summary>Meditar para recuperar maná. Sólo los que tienen maná, y sólo cuando les falta bastante:
    /// sentarse a meditar con la barra llena es de las cosas que no hace nadie.</summary>
    private async Task<bool> MeditarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!sentidos.UsaMana || sentidos.Mana >= MeditaBajo) return false;

        transporte.Mandar(Paquete(ClientPacketId.Meditate, _ => { }));
        for (int pasado = 0; pasado < MeditaTopeMs; pasado += RebanadaDePausaMs)
        {
            await Task.Delay(RebanadaDePausaMs, ct);
            // Meditar se corta solo al caminar o al pelear, así que cualquier urgencia levanta al bot igual.
            if (!sentidos.Vivo || sentidos.Salud < TomaPocion || sentidos.AlgoPegado()) break;
            if (sentidos.Mana >= MeditaHasta) break;
        }
        transporte.Mandar(Paquete(ClientPacketId.Meditate, _ => { }));   // el mismo paquete lo apaga
        return true;
    }

    /// <summary>De muerto: caminar hacia el sacerdote si hay uno a la vista, y si no, dar vueltas buscando.
    /// Es lo que hace un jugador tirado — no hay otra forma de volver.</summary>
    private async Task<bool> BuscarSacerdoteAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                                  CancellationToken ct)
    {
        var cura = sentidos.MasCercano(ficha => _entorno.Datos.Npc(ficha)?.NpcType == 1);
        if (cura is not { } c) return await PasearAsync(transporte, sentidos, cerrada, ct);

        var (x, y) = sentidos.Posicion;
        return await UnPasoHaciaAsync(transporte, sentidos, c.Quien.X - x, c.Quien.Y - y, ct) != Paso.SinConexion;
    }

    /// <summary>Quedarse quieto un rato, pero MIRANDO. La espera se parte en rebanadas y se corta si la vida
    /// bajó o si algo se le puso al lado: un bot que descansa cuarenta segundos con los ojos cerrados se
    /// muere, y eso no es una figura — pasó, con la mochila llena de pociones.</summary>
    private static async Task QuedarseAsync(SentidosDelBot sentidos, int ms, CancellationToken ct)
    {
        for (int pasado = 0; pasado < ms; pasado += RebanadaDePausaMs)
        {
            await Task.Delay(Math.Min(RebanadaDePausaMs, ms - pasado), ct);
            if (!sentidos.Vivo || sentidos.Salud < TomaPocion || sentidos.AlgoPegado()) return;
        }
    }

    /// <summary>Lo urgente: la poción, la comida y el agua. Devuelve si hizo algo — si lo hizo, se vuelve a
    /// decidir desde arriba, porque tomar una poción cambia el cuadro.
    ///
    /// <para>El hambre y la sed no son decorado: con cualquiera de las dos en cero el servidor DEJA DE CURAR
    /// (el tick envuelve Sanar en <c>Hunger &gt; 0 &amp;&amp; Thirst &gt; 0</c>), así que un bot que no come se
    /// queda sin la única curación gratis que tiene.</para></summary>
    private async Task<bool> CuidarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (sentidos.Salud < TomaPocion && Usar(transporte, sentidos, o => o is { IsPotion: true, SubType: 3 }))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        if (sentidos.ConHambre && Usar(transporte, sentidos, o => o.IsFood))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        if (sentidos.ConSed && Usar(transporte, sentidos, o => o.IsDrink))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        return false;
    }

    /// <summary>Ir hasta la presa y pegarle hasta que muera, se pierda de vista, o convenga irse. Devuelve
    /// false sólo si se cayó la conexión.</summary>
    private async Task<bool> CazarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, short presa,
                                        Task cerrada, CancellationToken ct)
    {
        int pasosGastados = 0;
        while (!ct.IsCancellationRequested && !cerrada.IsCompleted && sentidos.Vivo)
        {
            // Se murió, se fue de la vista, o alguien más se la llevó: no hay a quién pegarle.
            if (sentidos.Donde(presa) is not { } quien) return true;
            // LA POCIÓN SE TOMA PELEANDO. Es lo que hace cualquiera —se toma y se sigue— y no salir del ciclo
            // para curarse es la diferencia entre aguantar la pelea y llegar al 35 % con la criatura encima:
            // medido, sacar esto de acá dejaba un muerto cada ocho bots en diez minutos.
            if (sentidos.Salud < TomaPocion && await CuidarseAsync(transporte, sentidos, ct)) continue;
            // Con la vida o la energía abajo igual se corta: si no hay pociones, o no alcanzan, hay que irse.
            if (sentidos.Salud < SeRetira || sentidos.Fuelle < FuelleMinimo) return await AlejarseAsync(transporte, sentidos, quien, cerrada, ct);

            var (x, y) = sentidos.Posicion;
            int dx = quien.X - x, dy = quien.Y - y;
            int pasos = Math.Abs(dx) + Math.Abs(dy);

            // EL QUE SABE MAGIA PELEA CON MAGIA, y de lejos. Es lo que hace distinto a un mago de un guerrero
            // visto desde afuera, y sin esto todas las clases peleaban igual: acercarse y pegar.
            if (Math.Abs(dx) <= AlcanceHechizoX && Math.Abs(dy) <= AlcanceHechizoY
                && LanzarAlgo(transporte, sentidos, quien))
            {
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                continue;
            }

            if (pasos == 1)
            {
                // Pegado y de frente: mirar y pegar. El golpe resuelve contra la baldosa que el atacante MIRA
                // (VB6 UsuarioAtaca), así que sin girar primero se le pega al aire.
                byte rumbo = SentidosDelBot.RumboHacia(dx, dy);
                if (!transporte.Mandar(Paquete(ClientPacketId.ChangeHeading, w => w.WriteByte(rumbo)))) return false;
                if (!transporte.Mandar(Paquete(ClientPacketId.attack, _ => { }))) return false;
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                continue;
            }

            if (pasos > RadioDeCaza || ++pasosGastados > PacienciaPersiguiendo) return true;   // se aleja o no se llega
            if (await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct) is var r && r != Paso.Dado)
                return r != Paso.SinConexion;
        }
        return !cerrada.IsCompleted;
    }

    /// <summary>
    /// Tira el hechizo de daño más fuerte que pueda pagar contra ese objetivo. false = no sabe magia, no le
    /// alcanza el maná, o no tiene ninguno de ataque.
    ///
    /// <para>El casteo son DOS paquetes, igual que en el cliente: <c>CastSpell</c> elige la ranura y el
    /// servidor contesta pidiendo el objetivo, y el <c>WorkLeftClick</c> sobre la baldosa lo marca. Mandar
    /// sólo el primero deja el hechizo elegido y nada más — que es exactamente lo que le pasa a un jugador
    /// que aprieta el hechizo y no clickea.</para>
    /// </summary>
    private bool LanzarAlgo(TransporteDeBot transporte, SentidosDelBot sentidos, SentidosDelBot.Visible a)
    {
        if (!sentidos.UsaMana) return false;
        int mana = sentidos.ManaEnPuntos;
        (byte Ranura, int Dano)? mejor = null;
        foreach (var (ranura, numero) in sentidos.Hechizos())
        {
            var h = _entorno.Datos.Spell(numero);
            if (h is null || h.SubeHp != 2) continue;            // SubeHp 2 = hace daño; 1 = cura
            if (h.ManaCost > mana) continue;
            if (mejor is null || h.MaxHp > mejor.Value.Dano) mejor = (ranura, h.MaxHp);
        }
        if (mejor is null) return false;

        return transporte.Mandar(Paquete(ClientPacketId.CastSpell, w => w.WriteByte(mejor.Value.Ranura)))
            && transporte.Mandar(Paquete(ClientPacketId.WorkLeftClick,
                   w => w.WriteByte(a.X).WriteByte(a.Y).WriteByte((byte)GameLimits.SkillMagia)));
    }

    /// <summary>Retroceder unos pasos en dirección contraria a lo que lo está lastimando. No es huir del todo
    /// —el bot no sabe a dónde ir— pero le saca la criatura de encima mientras la vida vuelve.</summary>
    private async Task<bool> AlejarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                           SentidosDelBot.Visible de, Task cerrada, CancellationToken ct)
    {
        var (x, y) = sentidos.Posicion;
        byte rumbo = SentidosDelBot.RumboHacia(x - de.X, y - de.Y);
        for (int i = 0; i < _rng.Next(3, 7) && !cerrada.IsCompleted; i++)
        {
            if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return false;
            sentidos.PredecirPaso(rumbo);
            await Task.Delay(UnPaso(), ct);
        }
        return true;
    }

    /// <summary>
    /// IR HASTA UN LUGAR, cruzando mapas si hace falta. Devuelve si llegó.
    ///
    /// <para>Dos capas: el <see cref="Navegante"/> dice por dónde —las baldosas adentro del mapa, y qué
    /// puerta tomar para acercarse a otro— y esto las camina revisando la vida en cada paso. Si el camino se
    /// corta a mitad (alguien parado en el medio, una puerta cerrada) se recalcula: pedir el camino de nuevo
    /// cuesta menos que quedarse empujando una pared.</para>
    ///
    /// <para>Es lo que separa a un bot que da vueltas de uno que hace cosas: sin esto no puede ir a comprar,
    /// ni volver al pueblo, ni buscar al sacerdote que lo revive.</para>
    /// </summary>
    private async Task<bool> IrHastaAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                          int mapaDestino, byte destinoX, byte destinoY,
                                          Task cerrada, CancellationToken ct, int tolerancia = 1)
    {
        for (int tramos = 0; tramos < TramosPorViaje && !ct.IsCancellationRequested && !cerrada.IsCompleted; tramos++)
        {
            if (!sentidos.Ubicado || !sentidos.Vivo) return false;
            int mapa = sentidos.Mapa;
            var desde = sentidos.Posicion;

            // El destino puede estar en otro mapa: en ese caso el tramo termina en la puerta.
            bool esLaMeta = mapa == mapaDestino;
            (byte X, byte Y) meta;
            if (esLaMeta) meta = (destinoX, destinoY);
            else if (_entorno.Mapas.PuertaHacia(mapa, mapaDestino) is { } salida) meta = salida.Puerta;
            else return false;   // desde acá no se llega

            // Ya llegó: al lado alcanza, porque encima del destino no se puede parar si hay alguien ahí —y
            // el destino típico es justamente la baldosa donde está parado un mercader.
            if (esLaMeta && Math.Abs(desde.X - destinoX) + Math.Abs(desde.Y - destinoY) <= tolerancia) return true;

            var camino = esLaMeta ? _entorno.Mapas.HayComoLlegar(mapa, desde, meta, tolerancia)
                                   : _entorno.Mapas.Camino(mapa, desde, meta);
            if (camino is null || camino.Count == 0) return false;

            foreach (var baldosa in camino)
            {
                if (cerrada.IsCompleted) return false;
                if (sentidos.Mapa != mapa) break;      // pisó la puerta: el tramo terminó
                var (x, y) = sentidos.Posicion;
                int dx = baldosa.X - x, dy = baldosa.Y - y;
                if (dx == 0 && dy == 0) continue;
                // El camino es de a un paso: cualquier otra cosa quiere decir que nos corrieron de lugar.
                if (Math.Abs(dx) + Math.Abs(dy) != 1) break;
                var como = await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct);
                if (como != Paso.Dado) return false;   // sin conexión, o algo más urgente que el viaje
                if (_pasoTrabado) break;               // algo se paró en el camino: recalcular
            }
        }
        return sentidos.Mapa == mapaDestino
            && Math.Abs(sentidos.Posicion.X - destinoX) + Math.Abs(sentidos.Posicion.Y - destinoY) <= tolerancia;
    }

    /// <summary>Cómo salió un paso. El <see cref="Urgencia"/> es lo importante: quiere decir "soltá lo que
    /// estabas haciendo y volvé a decidir".</summary>
    private enum Paso { Dado, Urgencia, SinConexion }

    /// <summary>
    /// Un paso hacia un objetivo. Va por el eje más largo, y si el paso no ocurrió —una pared, un árbol, otro
    /// personaje— prueba el otro eje en la vuelta siguiente, porque la posición no cambió. Es un rodeo pobre y
    /// a propósito: un bot que resuelve laberintos al primer intento no se mueve como nadie.
    ///
    /// <para><b>Acá se mira la vida, y por eso TODO lo que camina pasa por acá.</b> Cada recorrido largo del
    /// bot —perseguir una presa, ir a levantar el botín, acercarse a un mercader, buscar al sacerdote— es un
    /// bucle de varios segundos, y la primera versión de cada uno miraba la vida sólo al empezar. Medido:
    /// cuatro de ocho bots muertos en diez minutos caminando hacia cosas mientras una criatura los golpeaba.
    /// Poner el chequeo en cada bucle sería ponerlo cuatro veces y olvidarlo en el quinto.</para>
    /// </summary>
    private async Task<Paso> UnPasoHaciaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, int dx, int dy,
                                              CancellationToken ct)
    {
        byte rumbo = _pasoTrabado && dx != 0 && dy != 0
            ? SentidosDelBot.RumboHacia(Math.Abs(dx) > Math.Abs(dy) ? 0 : dx, Math.Abs(dx) > Math.Abs(dy) ? dy : 0)
            : SentidosDelBot.RumboHacia(dx, dy);
        var antes = sentidos.Posicion;
        if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return Paso.SinConexion;
        sentidos.PredecirPaso(rumbo);
        await Task.Delay(UnPaso(), ct);
        // El servidor corrige con un PosUpdate cuando el paso se rechaza, así que quedar donde estábamos
        // significa que hay algo adelante.
        _pasoTrabado = sentidos.Posicion == antes;
        return Urge(sentidos) ? Paso.Urgencia : Paso.Dado;
    }

    /// <summary>Lo que tarda ESTE bot en dar un paso.</summary>
    private int UnPaso() => (int)(_rng.Next(PasoMinMs, PasoMaxMs) * _ritmo);

    /// <summary>
    /// ¿Hay algo más importante que lo que está haciendo? Muerto o con la vida en rojo.
    ///
    /// <para>LA ENERGÍA NO ENTRA ACÁ, y es deliberado: caminar no la gasta (el servidor sólo la cobra al
    /// pegar, al trabajar y al nadar), así que cortar un viaje por tenerla baja dejaba a un bot que venía de
    /// pelear sin poder llegar NUNCA a la tienda — cada paso devolvía urgencia, el viaje se abortaba, y el
    /// enfriamiento de compras lo mandaba a esperar quince minutos para volver a fallar igual. Donde la
    /// energía sí importa —pelear y faenar— se mira aparte.</para>
    /// </summary>
    private static bool Urge(SentidosDelBot sentidos) =>
        !sentidos.Vivo || sentidos.Salud < TomaPocion;

    private bool _pasoTrabado;

    /// <summary>Un paseo corto, que es lo que hace cuando no hay nada que cazar. Si se alejó de su zona, el
    /// paseo es de vuelta para adentro.</summary>
    private async Task<bool> PasearAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                         CancellationToken ct)
    {
        // Al cambiar de mapa, este lugar pasa a ser su zona: las coordenadas de la anterior no significan nada.
        var (x, y) = sentidos.Posicion;
        if (sentidos.CambioDeMapa() || _casa is null) _casa = (x, y);

        int dx = _casa.Value.X - x, dy = _casa.Value.Y - y;
        bool lejos = Math.Abs(dx) + Math.Abs(dy) > RadioDeCasa;
        byte rumbo = lejos ? SentidosDelBot.RumboHacia(dx, dy) : (byte)_rng.Next(1, 5);   // 1=N, 2=E, 3=S, 4=O

        int pasos = _rng.Next(PasosMin, PasosMax);
        for (int i = 0; i < pasos && !cerrada.IsCompleted; i++)
        {
            if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return false;
            sentidos.PredecirPaso(rumbo);
            await Task.Delay(UnPaso(), ct);
            if (sentidos.Salud < TomaPocion) return true;   // se corta el paseo: hay algo más urgente
        }
        return true;
    }

    /// <summary>Dónde considera que es su zona. Se fija al entrar y se muda con él si cambia de mapa.</summary>
    private (byte X, byte Y)? _casa;

    /// <summary>¿Le conviene pelearse con esa criatura? Ataca sólo lo que ataca y se puede atacar —así no se
    /// mete con un ciudadano ni con un guardia, que lo dejarían criminal— y sólo si el golpe del bicho es
    /// chico contra su vida.</summary>
    private bool EsPresa(short ficha, int vidaMaxima, int nivel)
    {
        var n = _entorno.Datos.Npc(ficha);
        if (n is null || !n.Hostile || !n.Attackable) return false;
        // LA EXPERIENCIA QUE DA ES LA MEDIDA DE LO QUE ES, y el golpe a mano solo NO ALCANZA: un Espectro
        // tiene MaxHit 0 porque pelea con hechizos, así que por el golpe parecía inofensivo — y da treinta y
        // dos mil de experiencia, o sea que mata a un personaje de nivel uno sin despeinarse. Con la
        // experiencia de por medio, un bicho así queda afuera aunque no pegue trompadas.
        if (n.GiveExp > ExperienciaQueSeAnima(nivel)) return false;
        return n.MaxHit * GolpeTolerable <= Math.Max(1, vidaMaxima);
    }

    /// <summary>La experiencia de la criatura más grande a la que se anima, según su nivel. Es la forma de
    /// medir "qué tan brava es" que el dato del juego da directo.</summary>
    private static int ExperienciaQueSeAnima(int nivel) => 30 + nivel * 40;

    /// <summary>Usa el primer objeto del inventario que cumpla la condición. false = no tiene ninguno.</summary>
    private bool Usar(TransporteDeBot transporte, SentidosDelBot sentidos, Func<ObjInfo, bool> sirve)
    {
        byte ranura = sentidos.Ranura(obj => _entorno.Datos.Obj(obj) is { } info && sirve(info));
        return ranura != 0 && transporte.Mandar(Paquete(ClientPacketId.UseItem, w => w.WriteByte(ranura)));
    }

    /// <summary>Pide la resurrección: hay que tener CLICKEADO un sacerdote a diez baldosas, así que primero se
    /// lo clickea si hay uno a la vista. Sin sacerdote no pasa nada, que es lo correcto.</summary>
    private void PedirResurreccion(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        var cura = sentidos.MasCercano(ficha => _entorno.Datos.Npc(ficha)?.NpcType == 1);
        if (cura is not { } c || c.Pasos > 10) return;
        transporte.Mandar(Paquete(ClientPacketId.LeftClick, w => w.WriteByte(c.Quien.X).WriteByte(c.Quien.Y)));
        transporte.Mandar(Paquete(ClientPacketId.Resucitate, _ => { }));
    }

    // ── Los paquetes ──────────────────────────────────────────────────────────────────────────────────

    private byte[] CrearCuenta() => Paquete(ClientPacketId.CreateNewAccount, w => w
        .WriteAsciiString(_cuenta).WriteAsciiString(_clave!).WriteAsciiString(CodigoDeRecuperacion())
        .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision));

    private byte[] CrearPersonaje()
    {
        var (raza, genero, clase, hogar, cabeza) = Aspecto();
        return Paquete(ClientPacketId.LoginNewChar, w => w
            .WriteAsciiString(_cuenta).WriteAsciiString(_clave!)
            .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision)
            .WriteAsciiString(Nombre)
            .WriteByte(raza).WriteByte(genero).WriteByte(clase).WriteByte(hogar)
            .WriteInteger(cabeza)
            .WriteAsciiString(Mac).WriteLong(0));
    }

    private byte[] Entrar() => Paquete(ClientPacketId.LoginExistingChar, w => w
        .WriteAsciiString(_cuenta).WriteAsciiString(_clave!)
        .WriteByte(Version.Mayor).WriteByte(Version.Menor).WriteByte(Version.Revision)
        .WriteAsciiString(Nombre).WriteAsciiString(Mac).WriteLong(0));

    private static byte[] Paquete(ClientPacketId id, Action<ByteQueueWriter> campos)
    {
        var w = new ByteQueueWriter().WriteByte((byte)id);
        campos(w);
        return w.ToArray();
    }

    // ── Quién es este bot ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Raza, género, clase, tierra natal y cara. Sale de una semilla derivada del NOMBRE y no del
    /// azar del arranque: así el bot se ve siempre igual, aunque alguna vez haya que recrearle el personaje.</summary>
    private (byte Raza, byte Genero, byte Clase, byte Hogar, short Cabeza) Aspecto()
    {
        int semilla = 17;
        foreach (char c in Nombre) semilla = unchecked(semilla * 31 + c);
        var r = new Random(semilla);
        byte genero = (byte)r.Next(1, 3);
        byte raza = (byte)r.Next(1, 7);            // 1..6: las que tienen tabla de cabezas (Equipment.HeadRange)
        byte clase = (byte)r.Next(1, 19);
        byte hogar = (byte)r.Next(1, Cities.MaxTierraNatal + 1);
        var rango = Equipment.HeadRange(raza, genero);
        short cabeza = rango is null ? (short)1 : (short)r.Next(rango.Value.Min, rango.Value.Max + 1);
        return (raza, genero, clase, hogar, cabeza);
    }

    /// <summary>Contraseña larga y al azar. NO se deriva del nombre: una clave que se puede calcular sabiendo
    /// de quién es la cuenta es una puerta de atrás a esa cuenta, aunque la cuenta sea de un bot.</summary>
    private static string NuevaClave() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>El PIN de recuperación: cuatro números, como pide el alta de cuentas. Al azar, y no se guarda
    /// en ningún lado — con la contraseña alcanza para volver a entrar, y con el PIN se le cambia la
    /// contraseña a la cuenta.</summary>
    private static string CodigoDeRecuperacion() => RandomNumberGenerator.GetInt32(1000, 10000).ToString();

    /// <summary>La versión que declara el bot es la de ESTE servidor, así entra igual si el host prende el
    /// control de versión del cliente (la clave <c>Version</c> del Server.ini) con la que repartió.</summary>
    private static readonly (byte Mayor, byte Menor, byte Revision) Version = LeerVersion();

    private static (byte, byte, byte) LeerVersion()
    {
        var partes = Build.Version.Split('.');
        byte Parte(int i) => partes.Length > i && byte.TryParse(partes[i], out byte b) ? b : (byte)0;
        return (Parte(0), Parte(1), Parte(2));
    }

    // ── Ayudas ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El texto de un <c>ConsoleMsg</c>: id, Int16 de largo, y los bytes. Se decodifica a mano
    /// justamente porque es el ÚNICO paquete con contenido que el bot mira, y sólo para poder contar por qué
    /// el servidor lo rechazó. Interpretar el resto del mundo es la etapa siguiente.</summary>
    private static string TextoDeConsola(byte[] p)
    {
        if (p.Length < 3) return "";
        int largo = p[1] | (p[2] << 8);
        if (largo <= 0 || 3 + largo > p.Length) return "";
        return Codec.Ansi.GetString(p, 3, largo);
    }

    /// <summary>Espera un paso del login, con plazo. false = el servidor no contestó, o cerró la sesión.</summary>
    private static async Task<bool> Espera(Task senal, CancellationToken ct)
    {
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var demora = Task.Delay(PlazoDeRespuestaMs, plazo.Token);
        var cual = await Task.WhenAny(senal, demora);
        plazo.Cancel();   // corta la demora si ganó la señal
        return cual == senal && senal.IsCompletedSuccessfully;   // el cierre de la sesión la completa con excepción
    }
}
