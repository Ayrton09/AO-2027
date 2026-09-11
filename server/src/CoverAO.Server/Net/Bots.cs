using System.Security.Cryptography;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>Cómo va a intentar entrar el bot en su próxima vida. Mientras no haya entrado nunca, si un intento
/// falla se pasa a la siguiente, así que en tres vidas prueba las tres formas: la cuenta y el personaje ya
/// existen, la cuenta existe pero le falta el personaje, y no existe nada. Ver <c>Bot.DespuesDeUnFracaso</c>.</summary>
public enum ComoEntrar { Entrar, SoloPersonaje, Alta }

/// <summary>Cómo huye un bot de lo que lo asusta: alejándose, pidiéndole al sacerdote que tiene a mano o con la runa a casa.</summary>
public enum Huida { Alejarse, Sacerdote, Runa }

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

    /// <summary>Cada cuánto, estando muerto, insiste con la resurrección. Es CORTO a propósito: el sacerdote
    /// del original CAMINA (<c>Movement=1</c> en su ficha), así que pedírselo una vez cada veinte segundos es
    /// apuntarle a donde estaba. Una persona tirada en el piso clica hasta que la levantan.</summary>
    private const int PruebaRevivirMs = 4_000;

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
    private const int EsperaTrabaMinMs = 800, EsperaTrabaMaxMs = 2_000;

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
    /// <summary>Cada cuánto vuelve a intentar ir a un mapa seguro el que tiene la sesión vencida en uno pk.</summary>
    private const int ReintentoDeRefugioMs = 60_000;

    /// <summary>Maná al que se sienta a meditar, y al que se levanta. Sólo lo hacen los que tienen maná.</summary>
    private const double MeditaBajo = 0.35, MeditaHasta = 0.90;

    /// <summary>Cuánto se aguanta meditando de una vez. Meditar se corta solo al caminar o al pelear, así que
    /// el tope es para no quedarse sentado toda la sesión si el maná no sube.</summary>
    private const int MeditaTopeMs = 45_000;

    /// <summary>Cuántas pociones de vida quiere tener encima, y desde cuántos pasos se acerca a un mercader.
    /// El kit de novato trae mil quinientas, así que el bot recién sale a comprar cuando gastó casi todas — lo
    /// que dispara la visita al mercader, en la práctica, es tener botín para vender.</summary>
    private const int PocionesQueQuiere = 200, RadioDeCompras = 15;

    /// <summary>Con menos de lo primero de comida o de bebida sale a reponer, y compra hasta lo segundo. Se cuenta
    /// en PUNTOS de hambre y de sed, no en unidades: diez manzanas llenan cien y diez pollos setecientos cincuenta.
    /// El hambre baja diez cada tres minutos, así que cien puntos son media hora y cuatrocientos, dos horas.</summary>
    private const int ComidaMinima = 100, ComidaQueQuiere = 400;
    private const int BebidaMinima = 100, BebidaQueQuiere = 400;

    /// <summary>Con menos de lo primero no se sale a comprar —no alcanza para un agua, que en caja cuesta cuatro— y
    /// en un apuro se venden de a lo segundo las unidades de comida o de bebida que hace falta cambiar.</summary>
    private const int MonedasDeApuro = 4, UnidadesDeApuro = 2;

    /// <summary>Ventas por visita. Se corta para que la parada en la tienda dure lo que dura una parada y no
    /// se coma la sesión vaciando la mochila de a un objeto por vez.</summary>
    private const int VentasPorVisita = 6;

    /// <summary>A cuántas baldosas del mercader alcanza con pararse: el servidor atiende a tres y NO exige
    /// línea de vista, así que se le compra a través de la pared del negocio.</summary>
    private const int RadioDelMostrador = 3;

    /// <summary>A cuántas baldosas del sacerdote alcanza con quedar parado. El servidor atiende hasta diez
    /// (Manhattan, VB6 Acciones.bas:306); se para bastante más cerca a propósito, porque lo que para un
    /// jugador es un error de puntería en un bot es sistemático.</summary>
    private const int RadioDelAltar = 3;

    /// <summary>
    /// Hasta dónde se le puede PEDIR, y va POR EJE porque así lo mide el servidor.
    ///
    /// <para>El doble clic no lo gatea la distancia del sacerdote sino la VENTANA DE VISIÓN —ocho de ancho
    /// por seis de alto, cada eje por su cuenta— y fuera de ahí el original sale mudo, sin contestar nada.
    /// Acá había un tope de ocho en Manhattan, que no es lo mismo: un cadáver a siete baldosas EN Y le
    /// parecía cerca y para el servidor estaba afuera, así que el pedido se perdía en silencio. Medido con
    /// una sonda: el bot mandaba el doble clic a (55,51) desde (55,44) y `HandleDoubleClick` cortaba en su
    /// primer renglón. Es la MISMA trampa que la del casteo —que ya se había arreglado con 6 y 4— y no se
    /// había traído hasta acá.</para>
    /// </summary>
    private const int AlcanceDelAltarX = 6, AlcanceDelAltarY = 4;

    /// <summary>¿Está el sacerdote adentro de la ventana donde el servidor atiende el pedido?</summary>
    private static bool AlAlcanceDelAltar(SentidosDelBot sentidos, SentidosDelBot.Visible cura)
    {
        var (x, y) = sentidos.Posicion;
        return Math.Abs(cura.X - x) <= AlcanceDelAltarX && Math.Abs(cura.Y - y) <= AlcanceDelAltarY;
    }

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
    /// <summary>Ya estuvo adentro del mundo desde que arrancó el servidor: la cuenta y el personaje existen.</summary>
    private bool _yaEntro;
    /// <summary>La sesión anterior la cerró él (se fue a dormir), no un corte.</summary>
    private bool _seFueSolo;
    /// <summary>Cuándo vuelve a intentar ir a un mapa seguro para irse a dormir.</summary>
    private long _proximoRefugio;

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
            else { fracasos++; _como = DespuesDeUnFracaso(_como, _yaEntro); }
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

    /// <summary>
    /// Con qué forma intenta la próxima vida, después de una que no llegó a estar adentro del mundo.
    ///
    /// <para>Mientras no entró nunca, pasa a la forma siguiente: es lo que saca a un bot que tiene la clave
    /// anotada pero cuya cuenta no está en los datos. Visto en una corrida de campo: tres bots en ese caso
    /// entraron así, los tres en el último intento que tenían.</para>
    ///
    /// <para>Una vez que entró, sigue con <c>Entrar</c>: la cuenta y el personaje existen, y lo que falle
    /// después —un plazo de respuesta vencido, una excepción de su conducta— no cambia eso. Pasar de forma
    /// ahí gastaba los dos intentos que le quedaban en crear un personaje con un nombre ya en uso y una cuenta
    /// que ya existe, y el bot se quedaba afuera hasta el próximo arranque del servidor.</para>
    /// </summary>
    public static ComoEntrar DespuesDeUnFracaso(ComoEntrar como, bool yaEntro) =>
        yaEntro || como == ComoEntrar.Alta ? ComoEntrar.Entrar : como + 1;

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
            _yaEntro = true;
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
        _viajesAlTemploFallidos = 0;
        _cotosFallidos = 0;
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
            // PERO NO EN UN MAPA PK: ahí el `Quit` no saca al personaje, arranca una cuenta de cinco segundos
            // (HandleQuit), y el bot cortaba a los 1,5 s sin tomar pociones con lo que tuviera encima. Medido: un
            // Minero cortó su sesión en el mapa 196 a los veinte minutos y cinco segundos —el vencimiento exacto de la
            // suya— después de doce pociones seguidas, y quedó guardado muerto. Se va a dormir en un mapa que no es
            // pk, y si no está en uno camina hasta el más cercano (la rama de abajo).
            if (Environment.TickCount64 >= finDeSesion && sentidos.Vivo && MapaSeguro(sentidos.Mapa))
            {
                transporte.Mandar(Paquete(ClientPacketId.Quit, _ => { }));
                await Task.Delay(1_500, ct);
                _seFueSolo = true;
                return;
            }
            // Y NO SE QUEDA ESPERANDO A PISAR UNO: camina hasta el mapa seguro más cercano, como el que se va a
            // desconectar a la ciudad. Según la data, de los 185 mapas con criaturas hostiles a seis saltos o menos de
            // la cuna, 176 son pk, y en tres de cada cuatro el mapa seguro más cercano queda a dos saltos o más:
            // esperando, la sesión de un bot que caza lejos se estiraba hasta el próximo viaje de compras. Si el viaje
            // se corta —una criatura, una traba— sigue jugando y lo vuelve a probar al rato.
            if (Environment.TickCount64 >= finDeSesion && sentidos.Vivo && Environment.TickCount64 >= _proximoRefugio)
            {
                _proximoRefugio = Environment.TickCount64 + ReintentoDeRefugioMs;
                if (_entorno.Mapas.MapaMasCercano(sentidos.Mapa, sentidos.Posicion, MapaSeguro, Pisable(sentidos)) is { } refugio)
                {
                    await IrHastaAsync(transporte, sentidos, refugio.Mapa, refugio.X, refugio.Y, cerrada, ct);
                    if (cerrada.IsCompleted) return;
                    continue;
                }
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
            // Con qué se cura, leído ANTES de buscar por lo mismo que la vida: la búsqueda tiene el modelo bajo su candado.
            bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
            bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
            // CON LA VIDA EN ROJO Y SIN POCIONES, TODO LO QUE PEGA ASUSTA: la pelea que queda no se gana sin nada que tomar. Medido en
            // `sano2`: las cinco muertes fueron de bots sin pociones contra el Águila, que para ellos es presa, con la vida ya en rojo.
            double saludAhora = sentidos.Salud;
            var peligro = sentidos.MasCercano(ficha => EsPeligroso(ficha, vidaTope, nivelAhora, conPociones, curaVeneno)
                || AsustaConLaVidaEnRojo(_entorno.Datos.Npc(ficha), conPociones, saludAhora));
            if (peligro is { } amenaza && amenaza.Pasos <= RadioDeSusto)
            {
                // CON LA VIDA EN ROJO Y SIN POCIONES, HUIR ES EL SACERDOTE SI ESTÁ A MANO Y SI NO LA RUNA, que fuera de un mapa pk lleva
                // a casa en el acto (VB6 InvUsuario.bas:1356). Alejarse caminando no alcanza: medido en `rojo1`, un bot con la vida en
                // 24 % se alejó 46 veces del Águila, las últimas 27 sin poder moverse de la baldosa, y murió. La sanación que usa la
                // runa no llegaba nunca, porque la huida corta la vuelta antes.
                //
                // Y LA RUNA, AUNQUE LA VIDA NO ESTÉ EN ROJO, CONTRA LO QUE LO MATA DE UN GOLPE. Medido en `rojo2`: bots de nivel 1 con 18 de
                // vida murieron huyendo del Lobo, que pega hasta 18, con la vida entre 67 y 100 %.
                //
                // EL PEDIDO AL SACERDOTE TIENE SU PROPIA ESPERA. Se usaba la de `_proximaSanacion`, que es la de caminar hasta uno: medido en
                // `rojo2`, un bot al 44 % en diagonal al sacerdote de la cuna y con un Lobo pegado se alejó en vez de pedirle, porque el
                // viaje que se acababa de cortar le había dejado de uno a dos minutos de espera, y murió.
                bool sacerdoteAMano = Environment.TickCount64 >= _proximoPedidoAlSacerdote
                    && sentidos.MasCercano(EsSacerdote) is { } alLado && AlAlcanceDelAltar(sentidos, alLado.Quien);
                bool unGolpeLoMata = UnGolpeLoMata(_entorno.Datos.Npc(amenaza.Quien.FichaNpc), (int)Math.Round(saludAhora * vidaTope));
                var huida = ComoHuye(conPociones, saludAhora, unGolpeLoMata, sacerdoteAMano, !_entorno.Datos.IsPkMap(sentidos.Mapa),
                                     Environment.TickCount64 >= _proximaRunaDeHuida);
                if (huida == Huida.Sacerdote)
                {
                    PedirResurreccion(transporte, sentidos);   // el mismo doble clic de la sanación
                    await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
                    if (sentidos.Salud < TomaPocion)
                        _proximoPedidoAlSacerdote = Environment.TickCount64 + _rng.Next(EsperaSanarFallidoMinMs, EsperaSanarFallidoMaxMs);
                    continue;
                }
                if (huida == Huida.Runa && Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna))
                {
                    // En casa, al sacerdote sin esperar la vuelta de la sanación. Y una runa por vez: la llegada puede tener criaturas
                    // cerca, y rebotar de runa en runa no cura.
                    _proximaRunaDeHuida = Environment.TickCount64 + EsperaEntreRunasDeHuidaMs;
                    _proximaSanacion = 0;
                    _sanacionesSinCamino = 0;
                    await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
                    continue;
                }
                if (!await AlejarseAsync(transporte, sentidos, amenaza.Quien, cerrada, ct)) return;
                continue;
            }

            if (Equiparse(transporte, sentidos)) { await Task.Delay(_rng.Next(400, 900), ct); continue; }
            if (await CurarVenenoAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await SanarseAsync(transporte, sentidos, cerrada, ct)) continue;
            if (RepartirPuntos(transporte, sentidos)) { await Task.Delay(_rng.Next(900, 2_500), ct); continue; }
            if (await LevantarBotinAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await IrDeComprasAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await MeditarAsync(transporte, sentidos, ct)) continue;
            if (await TrabajarAsync(transporte, sentidos, cerrada, ct)) continue;

            // La vida máxima se lee ANTES de buscar: adentro de la búsqueda el modelo está bajo su candado, y
            // preguntarle otra cosa desde ahí ata dos lecturas que no tienen por qué ir juntas.
            int vidaMaxima = sentidos.VidaMaxima;
            // LO QUE PAGA, PRIMERO. Elegir siempre lo más cercano dejaba al bot ciego al oro: en la cuna hay
            // nueve escorpiones que sueltan tres monedas cada uno entre treinta y siete criaturas que no sueltan
            // nada, así que lo más cercano casi nunca paga. Medido: diez minutos, ocho bots subiendo de nivel
            // —uno pasó de 6 a 7— y CERO monedas ganadas entre todos, con la herramienta del oficio a
            // doscientas. Quien necesita plata sale a cazar lo que la da. Si no hay nada que pague al alcance,
            // vale lo más cercano, como siempre.
            var presa = sentidos.MasCercano(ficha => EsPresa(ficha, vidaMaxima, nivelAhora, conPociones, curaVeneno) && PagaOro(ficha));
            // LO QUE PAGA SE VA A BUSCAR MÁS LEJOS. El radio del temperamento decide si vale la pena moverse por
            // cualquier cosa; por lo que da plata, cualquiera camina unas baldosas más. Medido con la sonda: una
            // Clériga tranquila —radio 6— con un escorpión a la vista a nueve pasos, pegándole al murciélago de al
            // lado, que no suelta nada.
            if (presa is not { } rica || rica.Pasos > RadioDeCaza)
                presa = sentidos.MasCercano(ficha => EsPresa(ficha, vidaMaxima, nivelAhora, conPociones, curaVeneno));
            int alcance = presa is { } elegida && PagaOro(elegida.Quien.FichaNpc) ? RadioDeCaza : RadioDeCazaSuyo;
            // SIN ENERGÍA PARA PELEAR NO SE EMPIEZA UNA PELEA. `CazarAsync` se aleja apenas arranca si no la tiene, así
            // que empezar igual era ir y volver. Medido con la sonda: un bot con la energía en cero eligiendo presa y
            // alejándose cada pocos segundos, sin hacer otra cosa. Con la regeneración cortada alcanza con que le quede
            // algo, porque esperar no la devuelve.
            bool conFuelle = TieneFuelleParaPelear(sentidos);
            // NI A LA VISTA DE ALGO PELIGROSO, que viene. Medido en `rojo1`: un Minero de nivel 1 recién escapado de un Lobo se puso a
            // pegarle a un Murciélago con el Lobo a dos y tres baldosas, dos veces seguidas, y el Lobo lo mató las dos.
            bool peligroALaVista = peligro is { } temible && AlAlcanceDeSuVista(sentidos.Posicion, (temible.Quien.X, temible.Quien.Y));
            if (presa is { } p && p.Pasos <= alcance && sentidos.Salud >= VuelveAPelear && conFuelle && !peligroALaVista && LeDanGanas())
            {
                // SI LO ÚNICO A MANO NO PAGA, ANTES DE PEGARLE SE PRUEBA IR A DONDE HAYA ALGO QUE SÍ. Sin esto,
                // tener cualquier presa a la vista cortaba el ciclo acá y nunca se llegaba a preguntar si convenía
                // mudarse. Medido: cinco bots amontonados en el mapa 1, que tiene UNA sola serpiente y paga cero,
                // subiendo de nivel de a poco y sin ganar una moneda — con nueve escorpiones que pagan a un salto.
                if (PagaOro(p.Quien.FichaNpc)) _ultimaPresaQuePaga = Environment.TickCount64;
                else if (await BuscarDondeCazarAsync(transporte, sentidos, vidaMaxima, nivelAhora, cerrada, ct)) continue;
                if (!await CazarAsync(transporte, sentidos, p.Quien.CharIndex, cerrada, ct)) return;
                continue;
            }

            if (MirarALaGente(transporte, sentidos)) { await Task.Delay(_rng.Next(1_200, 3_500), ct); continue; }

            // ACÁ NO HAY NADA QUE CAZAR, ASÍ QUE SE MUDA. Un paseo que pisa una puerta cambia de mapa, y el
            // bot toma el mapa nuevo como su zona; si cae en un pueblo —que no tiene criaturas— se queda
            // paseando ahí para siempre. Medido: cuatro de ocho bots parados en Ullathorpe y en el 457 con la
            // experiencia congelada durante una hora entera, mientras los que se quedaron en la cuna subían
            // de nivel. Un jugador que se cansa de dar vueltas por el pueblo agarra y sale a cazar.
            if (await BuscarDondeCazarAsync(transporte, sentidos, vidaMaxima, nivelAhora, cerrada, ct)) continue;

            if (!await PasearAsync(transporte, sentidos, cerrada, ct)) return;
            bool larga = ++pausas % UnaDeCadaTantas == 0;
            await QuedarseAsync(sentidos, larga ? _rng.Next(QuedoMinMs, QuedoMaxMs) : _rng.Next(PausaMinMs, PausaMaxMs), ct);
        }
    }

    /// <summary>
    /// ¿LO VE ESA CRIATURA? Las que persiguen miran un rectángulo de <see cref="NpcSystem.RangoVisionX"/> baldosas a los costados y
    /// <see cref="NpcSystem.RangoVisionY"/> arriba y abajo (VB6 RANGO_VISION_X/Y, AI_NPC.bas:50) y caminan hacia el primero que
    /// encuentran ahí: empezar una pelea a la vista de algo peligroso es pelear con los dos.
    /// </summary>
    public static bool AlAlcanceDeSuVista((byte X, byte Y) yo, (byte X, byte Y) ella) =>
        Math.Abs(yo.X - ella.X) <= NpcSystem.RangoVisionX && Math.Abs(yo.Y - ella.Y) <= NpcSystem.RangoVisionY;

    /// <summary>¿Tiene energía para pelear? La que pide `CazarAsync`; con la regeneración cortada (hambre o sed en
    /// cero) alcanza con que le quede algo, porque esperar no la devuelve.</summary>
    private static bool TieneFuelleParaPelear(SentidosDelBot s) =>
        s.Fuelle >= FuelleMinimo || ((s.ConHambre || s.ConSed) && s.Fuelle > 0);

    /// <summary>¿Esa criatura lo puede matar de pocos golpes? Es la que hay que esquivar, se la haya buscado
    /// o no.</summary>
    private bool EsPeligroso(short ficha, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno) =>
        EsPeligrosoPara(_entorno.Datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno);

    /// <summary>¿Asusta esta criatura a quien no tiene pociones y anda con la vida bajo el umbral de la poción? Toda la que pega.</summary>
    public static bool AsustaConLaVidaEnRojo(NpcInfo? n, bool conPociones, double salud) => !conPociones && salud < TomaPocion && n is { Hostile: true, MaxHit: > 0 };

    /// <summary>
    /// CÓMO HUYE de lo que asusta. Con pociones, alejándose. Sin pociones: con la vida en rojo, el sacerdote si está a mano; contra lo
    /// que lo mata de un golpe, o en rojo sin sacerdote, la runa, sólo fuera de un mapa pk y si no la acaba de usar. La runa va antes
    /// que el sacerdote contra un golpe que mata: el sacerdote cura hasta la vida máxima y ese golpe la alcanza. Sin runa lista y en
    /// rojo, el sacerdote igual, que sube la vida por encima del golpe más flojo; si no, alejándose.
    /// </summary>
    public static Huida ComoHuye(bool conPociones, double salud, bool unGolpeLoMata, bool sacerdoteAMano, bool mapaSinPk, bool runaLista)
    {
        if (conPociones) return Huida.Alejarse;
        bool enRojo = salud < TomaPocion;
        if (enRojo && sacerdoteAMano && !unGolpeLoMata) return Huida.Sacerdote;
        if ((enRojo || unGolpeLoMata) && mapaSinPk && runaLista) return Huida.Runa;
        return enRojo && sacerdoteAMano ? Huida.Sacerdote : Huida.Alejarse;
    }

    /// <summary>¿Lo puede matar de un golpe? El golpe máximo de su ficha contra la vida que le queda.</summary>
    public static bool UnGolpeLoMata(NpcInfo? n, int vida) => n is { MaxHit: > 0 } && n.MaxHit >= vida;

    /// <summary>La regla de <see cref="EsPeligroso"/> sobre la ficha sola.</summary>
    public static bool EsPeligrosoPara(NpcInfo? n, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno)
    {
        if (n is null || !n.Hostile) return false;
        // Por lo que pega, o por lo que ES: el que reparte mucha experiencia es un bicho grande aunque su
        // golpe a mano diga 0 porque pelea con magia.
        if (n.MaxHit * GolpesQueAguanta >= Math.Max(1, vidaMaxima) || n.GiveExp > ExperienciaQueSeAnima(nivel) * 3)
            return true;
        // SIN POCIONES, LO QUE NO ES PRESA SE ESQUIVA. Entre la presa y lo peligroso quedaba un hueco —lo que ni se pelea ni se
        // esquiva— y el que no tiene con qué curarse se moría adentro: medido en `sano1`, 21 muertes en media hora, todas de los
        // cinco bots sin pociones y 17 contra el Escorpión y el Lobo, mientras los tres con pociones no murieron nunca. Una
        // Guerrera con 58 de vida no le pegaba al Lobo (golpe 18) ni se le iba, y el Lobo la mataba parada.
        return !conPociones && n.MaxHit > 0 && !EsPresaPara(n, vidaMaxima, nivel, conPociones, curaVeneno);
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
        // EL PASEANTE TAMBIÉN CAZA. Acá había un 1: sólo peleaba con lo que tuviera PEGADO, o sea casi nunca.
        // Medido con la sonda: dos paseantes con `radio=1` sin presa en todas las vueltas. Y tiene que ser al
        // menos la tolerancia con la que se llega a un coto (`ToleranciaDelCoto`), o llegar no le sirve de
        // nada: se para a tres baldosas de la presa y no la alcanza.
        _ => RadioDeCaza / 3,
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
        //
        // Y EL QUE ELIGIÓ UNA CLASE QUE CASTEA LOS PONE EN MAGIA, que es lo mismo por el otro lado y estaba
        // sin cubrir: un caster nace con un solo hechizo —el original le da `UserHechizos(1) = 2` en el alta,
        // TCP.bas:541— y ese hechizo pide **cinco puntos de Magia** (`MinSkill=5` del Proyectil Mágico). El
        // servidor lo gatea (`Skills[Magia] < spell.MinSkill` → LocaleMsg 221), así que con los puntos al azar
        // el bot no casteaba NUNCA y se quedaba pegando con el báculo, que no mata nada. Medido: el Mago, el
        // Bardo y la Clériga con el maná lleno y la experiencia congelada mientras los guerreros subían a
        // nivel 5. Se reconoce por tener maná, que es lo que el propio servidor le contó al entrar.
        byte propia = SkillDeSuOficio != 0 ? SkillDeSuOficio
                    : sentidos.UsaMana ? (byte)GameLimits.SkillMagia
                    : (byte)0;
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
        // LO ESENCIAL NO ESPERA EL DESCANSO LARGO. Después de una compra el bot descansa de cinco a quince minutos, y si en ese
        // rato se le terminaban la comida o la bebida seguía esperando con el hambre o la sed en cero, sin regenerar. Medido en
        // `sano1`: un Gladiador compró dos pociones y pasó doce minutos sin volver, con 215 monedas encima y la comida y la
        // bebida en cero. El descanso corto de un viaje fallido sí se respeta: sin él reintentaría en cada vuelta.
        if (SaltaElDescanso(_descansoLargo, sentidos.Oro, sentidos.Suma(PuntosDeComida), sentidos.Suma(PuntosDeBebida)))
            _proximaCompra = 0;
        if (Environment.TickCount64 < _proximaCompra) return false;

        bool faltanPociones = sentidos.CuantoTiene(EsPocionDeVida) < PocionesQueQuiere;
        // LA COMIDA Y LA BEBIDA SE REPONEN ANTES DE QUE SE ACABEN. El kit de novato trae cien de cada una y el bot
        // nunca compraba más: después de horas de juego se le terminaban, el hambre y la sed llegaban a cero, y
        // con cualquiera de las dos en cero el servidor deja de regenerar. Sin energía `CazarAsync` se aleja en
        // cuanto empieza, así que no juntaba oro para comer: un espiral cerrado. Medido: un Gladiador de nivel 7
        // con la mochila sin una manzana ni un agua, la energía en 20 de 130, y cada caza durando un segundo.
        bool faltaComida = sentidos.Suma(PuntosDeComida) < ComidaMinima;
        bool faltaBebida = sentidos.Suma(PuntosDeBebida) < BebidaMinima;
        bool hayQueVender = sentidos.EnLaMochila(obj => EsBotin(sentidos, obj)) is not null;
        bool faltaHerramienta = LeFaltaLaHerramienta(sentidos);

        // EL PRECIO SALE DE LA DATA, no de un número a mano: hoy las tres herramientas valen 200, y el día que
        // la data cambie esto cambia solo.
        int cuestaLaHerramienta = HerramientaDeSuOficio > 0
            ? (_entorno.Datos.Obj(HerramientaDeSuOficio)?.Value ?? int.MaxValue) : int.MaxValue;

        // NO SE VIAJA A UNA TIENDA SI NO HAY NADA QUE HACER AHÍ, y ésta era la que le rompía la vida al bot de
        // oficio: que le faltara la herramienta alcanzaba para salir de compras, aunque tuviera CERO monedas.
        // Como no la puede pagar nunca, volvía con las manos vacías, esperaba el enfriamiento corto y salía
        // otra vez — la sesión entera yendo y viniendo del mostrador en vez de cazar.
        //
        // Medido a los quince minutos, ocho bots recién nacidos, experiencia acumulada:
        //   Gladiador 225, Guerrero 163  (no tienen oficio: nunca salen de compras)
        //   Pescador 60, Minero 30, Pescador 20  (los tres con oficio)
        // Cuatro a ocho veces menos, y la única diferencia de conducta era ésta. Sin oro no hay compra: primero
        // se junta cazando y vendiendo el botín, que es lo que hace cualquiera que empieza.
        // Y SÓLO SI HAY DÓNDE USARLA SIN JUGARSE LA VIDA: una herramienta que no se puede usar son doscientas monedas
        // tiradas, y se cae al morir. Medido: un Minero compró el piquete dos veces y los dos se le cayeron muriendo en el
        // camino a una mina; todas las minas del mundo están en mapas pk.
        bool vaPorLaHerramienta = faltaHerramienta && sentidos.Oro >= cuestaLaHerramienta && HayDondeTrabajarSeguro(sentidos);
        // CON MENOS DE LO QUE CUESTA UN AGUA EN CAJA NO SE SALE A COMPRAR, sólo a vender: el viaje es para volver
        // con las manos vacías. Medido: un bot con una y dos monedas, tres viajes seguidos al mostrador de comidas
        // sin llevarse nada.
        bool enApuro = sentidos.Oro < MonedasDeApuro;
        bool vaPorPociones = faltanPociones && !enApuro;
        bool vaPorComida = faltaComida && !enApuro;
        bool vaPorBebida = faltaBebida && !enApuro;
        if (!hayQueVender && !vaPorLaHerramienta && !vaPorPociones && !vaPorComida && !vaPorBebida) return false;

        // LO QUE ESTÁ EN CERO, PRIMERO. La comida se miraba antes que la bebida, así que con la sed en cero y el
        // hambre aguantando el bot igual iba al de comidas y gastaba ahí lo que era para el agua. Medido: cinco
        // viajes seguidos al de comidas y ninguno al tabernero, con la sed en cero y el hambre en 75. Sin tomar
        // no se recupera la energía; el hambre puede esperar.
        bool primeroLaBebida = vaPorBebida && sentidos.ConSed && !sentidos.ConHambre;
        bool primeroLaComida = vaPorComida && sentidos.ConHambre && !sentidos.ConSed;

        // QUÉ MERCADER SIRVE. Si va por la herramienta, uno que la venda; si no, uno que le compre lo que trae.
        // Con el filtro de la herramienta siempre puesto, el bot con oficio quedaba atado para siempre a los
        // cuatro que las venden — y como cuesta 200 y él tiene 0, tampoco podía vender botín ni comprar
        // pociones en ningún otro lado: el lazo se cerraba sobre sí mismo.
        // COMER ANTES QUE COMPRAR LA HERRAMIENTA. Con la herramienta en la lista, `Sirve` sólo aceptaba a quien
        // la vende — y si ese viaje fallaba, el bot nunca iba por comida aunque la tuviera al lado. Medido: un
        // Minero con 275 monedas y la comida a nueve pasos, veinte minutos sin comprar nada, mientras el
        // vendedor de piquetes quedaba detrás de una puerta sin camino. La herramienta se busca con la panza llena.
        short quiero = vaPorLaHerramienta && !vaPorComida && !vaPorBebida ? HerramientaDeSuOficio : (short)0;
        // Y SI VA A VENDER, QUE SEA A ALGUIEN QUE LE COMPRE. Cada mercader acepta UN tipo de objeto y rechaza
        // el resto en silencio (`TipoItems`, VB6 Comercio.bas:123), así que caer en el gremio equivocado es un
        // viaje entero para volver con lo mismo: el pollo se lo compra el de comidas, no el herrero. Eso lo
        // sabe cualquiera que jugó una tarde, y sale de la data.
        bool Sirve(int ficha)
        {
            if (_entorno.Datos.Npc(ficha) is not { StockItems: { Count: > 0 } stock } mercader) return false;
            if (quiero != 0) return stock.Any(s => s.ObjIndex == quiero);
            if (primeroLaBebida) return stock.Any(s => EsBebida((short)s.ObjIndex));
            if (primeroLaComida) return stock.Any(s => EsComida((short)s.ObjIndex));
            // CADA NECESIDAD, A QUIEN LA ATIENDE. Antes, sin nada que vender, servía cualquier mercader, y así se
            // podía viajar a buscar pociones hasta el herrero. Ahora el que va por comida busca a quien la vende,
            // y lo mismo la bebida y las pociones. Lo que hace falta para vivir, primero.
            if (vaPorComida && stock.Any(s => EsComida((short)s.ObjIndex))) return true;
            if (vaPorBebida && stock.Any(s => EsBebida((short)s.ObjIndex))) return true;
            if (vaPorPociones && stock.Any(s => EsPocionDeVida((short)s.ObjIndex))) return true;
            return hayQueVender && sentidos.EnLaMochila(obj => LeVendeA(sentidos, mercader.TipoItems, obj)) is not null;
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
            if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, Sirve, MapaAtiende, Pisable(sentidos)) is not { } lugar)
            {
                // SIN TIENDA A LA QUE SE LLEGUE CAMINANDO, LA RUNA. Hay mapas a los que se entra y de los que no se
                // sale a pie: medido, desde el mapa 62 no hay a pie ningún vendedor de piquetes ni de hachas, y la
                // única salida hacia la cuna está en un cuarto cerrado. Después de un par de intentos, a casa, igual
                // que con el coto; el contador es otro porque cazar bien no arregla no poder comprar.
                // Sólo si hay algo que COMPRAR: nadie vuelve a casa con la runa para ir a vender.
                bool vaAComprar = vaPorLaHerramienta || vaPorComida || vaPorBebida || vaPorPociones;
                if (intento == 0 && vaAComprar && ++_tiendasSinCamino >= CotosFallidosAntesDeLaRuna
                    && !_entorno.Datos.IsPkMap(sentidos.Mapa))
                {
                    _tiendasSinCamino = 0;
                    Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna);
                }
                PostergarCompra(); return intento > 0;
            }
            if (!await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, RadioDelMostrador)
                && sentidos.Mapa != lugar.Mapa)
            { PostergarCompra(); return true; }   // ni siquiera se llegó al mapa
            tienda = MapaAtiende(sentidos.Mapa) ? sentidos.MasCercano(ficha => Sirve(ficha)) : null;
        }
        if (tienda is null || tienda.Value.Pasos > RadioDeCompras) { PostergarCompra(); return true; }
        _tiendasSinCamino = 0;
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
        // El descanso largo es para el que se llevó lo que fue a buscar. Si todavía le falta comer o tomar —la
        // comida la vende uno y la bebida otro—, se sale enseguida por lo que falta: con hambre o sed en cero no
        // se regenera nada, y quince minutos de espera son quince minutos sin poder pelear.
        bool sigueFaltando = sentidos.Suma(PuntosDeComida) < ComidaMinima || sentidos.Suma(PuntosDeBebida) < BebidaMinima;
        PostergarCompra(exito: compro && !sigueFaltando);
        return true;
    }

    /// <summary>Cuándo puede volver a intentar una compra. El ÉXITO descansa quince minutos —ya compró— pero
    /// un FRACASO no puede costar lo mismo: con el mismo castigo en las seis salidas, un tropiezo de tres
    /// segundos se comía el cupo de toda la sesión, y la corrida terminaba con cero compras sin que el bot
    /// hubiera llegado a intentarlo más de una vez.</summary>
    private void PostergarCompra(bool exito = false)
    {
        _descansoLargo = exito;
        _proximaCompra = Environment.TickCount64 + (exito
            ? _rng.Next(EnfriamientoCompraMinM, EnfriamientoCompraMaxM + 1) * 60_000L
            : _rng.Next(EnfriamientoFalloMinS, EnfriamientoFalloMaxS + 1) * 1_000L);
    }

    /// <summary>¿Se corta el descanso largo? Sólo el que viene de una compra, con plata para comprar y con la comida o la
    /// bebida por debajo de lo mínimo.</summary>
    public static bool SaltaElDescanso(bool descansoLargo, int oro, int puntosDeComida, int puntosDeBebida) =>
        descansoLargo && oro >= MonedasDeApuro && (puntosDeComida < ComidaMinima || puntosDeBebida < BebidaMinima);

    private bool _descansoLargo;

    /// <summary>
    /// LO QUE SE GUARDA PARA LO OTRO ESENCIAL. Comprando una cosa se gastaba todo menos una moneda, y si la otra también faltaba
    /// no quedaba con qué: medido en `sano1`, una Guerrera recién salida del pozo con 33 monedas se las gastó en pollos y siguió
    /// con la sed en cero, sin regenerar. Se guarda lo que cuesta llevar la comida o la bebida hasta su mínimo al precio por
    /// punto más barato del juego, que sale de la data.
    /// </summary>
    private int ReservaPara(SentidosDelBot sentidos, bool comida, bool bebida) =>
        (comida ? ReservaParaLoQueFalta(CostoPorPunto(esBebida: false), sentidos.Suma(PuntosDeComida), ComidaMinima) : 0)
        + (bebida ? ReservaParaLoQueFalta(CostoPorPunto(esBebida: true), sentidos.Suma(PuntosDeBebida), BebidaMinima) : 0);

    /// <summary>Las monedas para llevar de <paramref name="tiene"/> a <paramref name="minimo"/> puntos a ese costo por punto. El
    /// épsilon es por la coma flotante: setenta por 0,1 da 7,000000000000001 y el techo lo volvía ocho.</summary>
    public static int ReservaParaLoQueFalta(double costoPorPunto, int tiene, int minimo) =>
        tiene >= minimo ? 0 : (int)Math.Ceiling((minimo - tiene) * costoPorPunto - 1e-9);

    /// <summary>El precio por punto más barato de la comida o de la bebida, calculado una vez: la data no cambia con el servidor
    /// andando.</summary>
    private double CostoPorPunto(bool esBebida)
    {
        if (esBebida) return _costoPorPuntoDeBebida ??= CostoPorPuntoEn(_entorno.Datos, esBebida: true);
        return _costoPorPuntoDeComida ??= CostoPorPuntoEn(_entorno.Datos, esBebida: false);
    }

    private double? _costoPorPuntoDeBebida, _costoPorPuntoDeComida;

    /// <summary>El precio de lista por punto más barato de la comida o de la bebida entre todo lo que venden los mercaderes del
    /// mundo. 0 si no hay ninguna.</summary>
    public static double CostoPorPuntoEn(GameData datos, bool esBebida)
    {
        double mejor = double.MaxValue;
        foreach (var npc in datos.Npcs.Values)
            if (npc.StockItems is { } stock)
                foreach (var (obj, _) in stock)
                    if (datos.Obj((short)obj) is { Value: > 0 } o)
                    {
                        int puntos = esBebida ? (o.IsDrink ? o.MinSed : 0) : (o.IsFood ? o.MinHam : 0);
                        if (puntos > 0) mejor = Math.Min(mejor, o.Value / (double)puntos);
                    }
        return mejor == double.MaxValue ? 0 : mejor;
    }

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
            if (sentidos.EnLaMochila(obj => LeVendeA(sentidos, TipoDeMercader(fichaDelMercader), obj)) is not { } item) break;
            short queOfrezco = sentidos.QueHayEn(item.Ranura);
            short cuantas = CuantoVender(sentidos, queOfrezco, item.Cantidad);
            int teniaAntes = sentidos.CuantoTiene(obj => obj == queOfrezco);
            transporte.Mandar(Paquete(ClientPacketId.CommerceSell,
                w => w.WriteByte(item.Ranura).WriteInteger(cuantas)));
            await Task.Delay(_rng.Next(500, 1_200), ct);
            // SI SIGUE AHÍ, NO SE LO COMPRAN. El `TipoItems` ya se miró antes de venir, pero el original
            // rechaza además por otras razones (lo de novato, lo de facción) y siempre en silencio; sin
            // anotarlo, el bot volvería a ofrecer lo mismo para siempre.
            //
            // Se anota contra EL GREMIO, no contra el objeto a secas: el que no le compra el pollo al herrero
            // se lo compra el de comidas a doce baldosas. Anotado por objeto, un intento contra el mostrador
            // equivocado lo dejaba invendible para el resto de la vida del bot — y en la cuna el pollo es lo
            // único que tiene para vender.
            // Se mira la cantidad y no la ranura: de la comida se vende sólo una parte y el resto sigue ahí.
            if (sentidos.CuantoTiene(obj => obj == queOfrezco) >= teniaAntes) _noLoCompran.Add((TipoDeMercader(fichaDelMercader), queOfrezco));
        }

        // LA HERRAMIENTA PRIMERO: es lo que habilita el oficio, y sin oficio no hay con qué comprar lo demás.
        if (LeFaltaLaHerramienta(sentidos) && sentidos.EnLaTienda(obj => obj == HerramientaDeSuOficio) is { } h)
        {
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(h.Ranura).WriteInteger(1)));
            await Task.Delay(_rng.Next(600, 1_400), ct);
            compro = !LeFaltaLaHerramienta(sentidos);
        }

        // Lo esencial primero: sin comer ni tomar no se recupera la energía, y sin energía no se pelea. Las pociones
        // sobran —el kit de novato trae más de mil—, así que van al final con lo que quede.
        // Sin una gota encima, con la sed en cero y el hambre aguantando, la plata es para el agua: la comida no
        // se compra aunque esté enfrente. Y al revés con el hambre.
        if (!(SinNadaQueTomar(sentidos) && !sentidos.ConHambre))
            compro |= await ReponerAsync(transporte, sentidos, PuntosDeComida, ComidaQueQuiere, ct, ReservaPara(sentidos, comida: false, bebida: true));
        if (!(SinNadaQueComer(sentidos) && !sentidos.ConSed))
            compro |= await ReponerAsync(transporte, sentidos, PuntosDeBebida, BebidaQueQuiere, ct, ReservaPara(sentidos, comida: true, bebida: false));
        compro |= await ReponerAsync(transporte, sentidos, UnaSiEsPocion, PocionesQueQuiere, ct, ReservaPara(sentidos, comida: true, bebida: true));
        return compro;
    }

    /// <summary>Compra lo que falte de algo hasta juntar <paramref name="quiere"/> puntos, según
    /// <paramref name="rinde"/> (lo que da una unidad; cero = no es de eso), eligiendo lo que MÁS RINDE POR MONEDA
    /// entre lo que le alcanza. Devuelve si se llevó algo.</summary>
    private async Task<bool> ReponerAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Func<short, int> rinde,
                                          int quiere, CancellationToken ct, int reserva = 0)
    {
        int faltan = quiere - sentidos.Suma(rinde);
        if (faltan <= 0 || !sentidos.Comerciando) return false;

        // No se gasta hasta el último cobre: dejar algo encima es lo que hace cualquiera, y además EL PRECIO
        // DE LA VITRINA NO ES EL DE LA CAJA — el mostrador canta la división cruda y la compra redondea para
        // arriba con la cantidad adentro (ver `Comercio`), así que la cuenta de acá queda siempre corta.
        // Precio 0 = gratis: se lleva lo que necesita. Con la cuenta de abajo un objeto gratis daba "puedo
        // comprar cero", que es justo al revés.
        // Con poca plata el 80 % deja afuera justo lo que hace falta —con ocho monedas, el pollo de siete de
        // vitrina y ocho en caja no entraba—, así que se puede gastar todo menos la moneda que redondea la caja.
        // Y LO QUE SE GUARDA PARA LO OTRO ESENCIAL NO SE GASTA ACÁ (ver `ReservaPara`).
        double plata = Math.Max(0, Math.Max(sentidos.Oro * 0.8, sentidos.Oro - 1) - reserva);
        if (sentidos.MejorOfertaEnLaTienda(rinde, plata) is not { } oferta) return false;
        int porUnidad = rinde(oferta.Obj);
        int unidades = (faltan + porUnidad - 1) / porUnidad;
        int puede = oferta.Precio > 0 ? (int)(plata / oferta.Precio) : unidades;
        int cuantas = Math.Clamp(Math.Min(unidades, puede), 0, short.MaxValue);
        if (cuantas <= 0) return false;

        int teniaAntes = sentidos.Suma(rinde);
        transporte.Mandar(Paquete(ClientPacketId.CommerceBuy,
            w => w.WriteByte(oferta.Ranura).WriteInteger((short)cuantas)));
        await Task.Delay(_rng.Next(600, 1_400), ct);
        return sentidos.Suma(rinde) > teniaAntes;
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

    /// <summary>Lo que saca su oficio, con los mismos números que usa el servidor para saber qué da cada trabajo
    /// (el pez de la caña, la leña y el hierro).</summary>
    private short ProductoDeSuOficio => _clase switch
    {
        ClasePescador => Pargo,
        ClaseLenador => Lena,
        ClaseMinero => Hierro,
        _ => 0,
    };

    private const short Pargo = 139, Lena = 58, Hierro = 192;

    /// <summary>Qué cuenta como lugar de trabajo para su oficio: null para el agua (la pesca mira el terreno), o qué
    /// objetos del mapa sirven (los árboles para el Leñador, los yacimientos para el Minero).</summary>
    private Func<int, bool>? RecursoDeSuOficio()
    {
        byte skill = SkillDeSuOficio;
        if (skill == GameLimits.SkillPesca) return null;
        return obj => _entorno.Datos.Obj(obj) is { } o && (skill == GameLimits.SkillTalar ? o.IsTree : o.IsDeposit);
    }

    /// <summary>Un mapa donde se trabaja: los pk quedan afuera.</summary>
    private bool MapaSeguro(int mapa) => !_entorno.Datos.IsPkMap(mapa);

    /// <summary>
    /// A QUÉ MAPAS PUEDE ENTRAR. A partir del nivel 16 el servidor no deja entrar a la zona NEWBIE (la cuna y el 208): al que pisa la
    /// puerta lo rebota a la baldosa estable de al lado sin cambiar de mapa (PacketDispatcher, con `GameLimits.NewbieMaxLevel`). Sin
    /// esto el bot la seguía eligiendo para comprar, dormir, trabajar o curarse —la cuna es a la vez segura, atiende y tiene taberna y
    /// sacerdote— y chocaba contra la misma puerta para siempre. Encontrado leyendo: hoy ningún bot llegó a 16.
    /// </summary>
    private Func<int, bool> Pisable(SentidosDelBot sentidos)
    {
        int nivel = sentidos.Nivel;
        return mapa => PuedeEntrar(nivel, _entorno.Datos.MapZone(mapa));
    }

    /// <summary>¿Deja el servidor entrar con ese nivel a un mapa de esa zona? La misma cuenta que hace al pisar la puerta.</summary>
    public static bool PuedeEntrar(int nivel, string? zona) =>
        nivel <= GameLimits.NewbieMaxLevel || !string.Equals(zona?.Trim(), "NEWBIE", StringComparison.OrdinalIgnoreCase);

    /// <summary>¿Hay un lugar a salvo donde ejercer su oficio? Se recalcula al cambiar de mapa o cada cinco minutos:
    /// buscarlo es recorrer varios mapas, y esto se pregunta en cada vuelta.</summary>
    private bool HayDondeTrabajarSeguro(SentidosDelBot sentidos)
    {
        if (sentidos.Mapa == _trabajoSeguroMapa && Environment.TickCount64 < _trabajoSeguroHasta) return _trabajoSeguro;
        bool agua = SkillDeSuOficio == GameLimits.SkillPesca;
        _trabajoSeguro = SkillDeSuOficio != 0
            && _entorno.Mapas.DondeSeTrabaja(sentidos.Mapa, agua, RecursoDeSuOficio(), sentidos.Posicion, MapaSeguro, Pisable(sentidos)) is not null;
        _trabajoSeguroMapa = sentidos.Mapa;
        _trabajoSeguroHasta = Environment.TickCount64 + 5 * 60_000;
        return _trabajoSeguro;
    }

    private int _trabajoSeguroMapa = -1;
    private long _trabajoSeguroHasta;
    private bool _trabajoSeguro;

    /// <summary>¿Le falta la herramienta de su oficio? Ninguna clase nace con ella —el Pescador, el Leñador y
    /// el Minero reciben todos el mismo martillo de piedra— así que hay que ir a comprarla, igual que un
    /// jugador nuevo.</summary>
    private bool LeFaltaLaHerramienta(SentidosDelBot sentidos) =>
        HerramientaDeSuOficio > 0 && sentidos.CuantoTiene(obj => obj == HerramientaDeSuOficio) == 0;

    /// <summary>¿Es botín? Todo lo que apareció durante la sesión menos las pociones y el oro. Lo que lleva
    /// puesto ya lo descarta la mochila.</summary>
    private bool EsBotin(SentidosDelBot sentidos, short obj)
    {
        // LO QUE TRAJO DE CASA NO SE VENDE. La regla anterior era por descarte —todo lo que no fuera poción,
        // comida, bebida ni oro— y con eso el bot le ofrecía al mercader su propia arma: medido en los
        // `.chr`, terminaban peleando con el martillo en la mochila. Acá se invierte: se vende lo que
        // APARECIÓ durante la sesión, que es exactamente el botín.
        // LA HERRAMIENTA DEL OFICIO TAMPOCO. Se compra a mitad de sesión, así que no está en lo que trajo de
        // casa — y a un salto de la cuna, en el mapa 20, el Vendedor General compra de todo, herramientas
        // incluidas. Sin esto, la caña recién pagada caía desequipada en la mochila y, en cuanto vencía el
        // enfriamiento de compras, el bot se la volvía a vender: doscientas monedas de caza tiradas por una
        // regla de "qué es botín".
        // LO QUE SACÓ DE SU OFICIO SÍ SE VENDE, aunque venga de otra sesión. `_traidoDeCasa` es una foto de la mochila
        // al entrar, así que el pescado de la sesión anterior quedaba "de casa" y no se vendía nunca: medido, una
        // Pescadora que entró con 135 pargos, llegó a 158, y en toda la corrida no salió una sola vez a vender. La
        // comida que necesita la sigue guardando la reserva.
        // Y EN EL POZO, LO DE CASA TAMBIÉN (`EnElPozo`). Sin plata y sin nada que comer o tomar la energía no vuelve y no
        // se puede cazar para salir: dos bots quedaron así corrida tras corrida, con el hambre y la sed en cero y en la
        // mochila una Runa del kit de valor 100, que el Vendedor General del mapa 20 —a un salto de la cuna— paga 33,
        // cuando la comida y la bebida cuestan del orden de una moneda cada diez puntos. Lo de novato, las pociones y lo
        // que no vale nada siguen afuera (abajo), y lo puesto ya lo descarta la mochila.
        bool enElPozo = EnElPozo(sentidos.Oro, SinNadaQueComer(sentidos), SinNadaQueTomar(sentidos));
        if ((_traidoDeCasa.Contains(obj) && obj != ProductoDeSuOficio && !enElPozo) || obj == HerramientaDeSuOficio) return false;
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
        // Y LO QUE NO VALE NADA TAMPOCO. Con `Valor=0` el mercader lo paga a cero, y dejarlo como botín tenía un
        // costo escondido: la mochila nunca se vaciaba de "cosas para vender", así que el bot tenía siempre un
        // motivo para salir de compras. Medido: un Gladiador con 96 Anillos del Bardo encima, que pagan cero.
        // Y DE LA COMIDA Y LA BEBIDA, SÓLO LO QUE SOBRA. El pollo que se compró y el que soltó una gallina son el
        // mismo objeto, así que sin reserva el bot vendía a dos lo que acababa de pagar siete, y volvía a comprar.
        // Medido: un bot que gastó cuatro de sus cinco monedas en dos tartas y en el viaje siguiente las vendió por
        // una. Se vende lo que pasa de la reserva, y por debajo sólo en un apuro: sin una gota encima, con la sed
        // en cero y sin plata para el agua, la comida se cambia por agua (y al revés).
        if (o is { IsFood: true }
            && !SobraOSeCambia(sentidos, PuntosDeComida, obj, ComidaQueQuiere,
                               SinNadaQueTomar(sentidos) && sentidos.Oro < MonedasDeApuro)) return false;
        if (o is { IsDrink: true }
            && !SobraOSeCambia(sentidos, PuntosDeBebida, obj, BebidaQueQuiere,
                               SinNadaQueComer(sentidos) && sentidos.Oro < MonedasDeApuro)) return false;
        return o is not null && !o.IsPotion && !o.IsGold && !o.IsNewbie && o.Value > 0;
    }

    /// <summary>¿ESE mercader se lo compra? Tiene que ser botín, el gremio tiene que aceptar ese tipo de
    /// objeto —o comprar de todo— y no puede estar anotado como ya rechazado por ese mismo gremio.</summary>
    private bool LeVendeA(SentidosDelBot sentidos, int tipoDeMercader, short obj) =>
        EsBotin(sentidos, obj)
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

    /// <summary>Búsquedas de tienda seguidas sin ninguna a la que se llegue caminando.</summary>
    private int _tiendasSinCamino;

    private bool EsPocionDeVida(short obj) => _entorno.Datos.Obj(obj) is { IsPotion: true, SubType: 3 };
    private bool EsComida(short obj) => _entorno.Datos.Obj(obj) is { IsFood: true };
    private bool EsBebida(short obj) => _entorno.Datos.Obj(obj) is { IsDrink: true };
    private int PuntosDeComida(short obj) => _entorno.Datos.Obj(obj) is { IsFood: true } o ? o.MinHam : 0;
    private int PuntosDeBebida(short obj) => _entorno.Datos.Obj(obj) is { IsDrink: true } o ? o.MinSed : 0;
    private int UnaSiEsPocion(short obj) => EsPocionDeVida(obj) ? 1 : 0;
    private bool SinNadaQueTomar(SentidosDelBot s) => s.ConSed && s.Suma(PuntosDeBebida) == 0;
    private bool SinNadaQueComer(SentidosDelBot s) => s.ConHambre && s.Suma(PuntosDeComida) == 0;

    /// <summary>¿Está en el pozo? Sin plata para un agua y sin nada que comer o tomar: con el hambre o la sed en cero la
    /// energía no vuelve, sin energía no se caza, y sin cazar no hay con qué comprar.</summary>
    public static bool EnElPozo(int oro, bool sinNadaQueComer, bool sinNadaQueTomar) =>
        oro < MonedasDeApuro && (sinNadaQueComer || sinNadaQueTomar);

    /// <summary>¿Se puede vender una unidad de esta comida o bebida? Si sobra por encima de la reserva, sí; si no,
    /// sólo cuando hace falta cambiarla por lo otro.</summary>
    private static bool SobraOSeCambia(SentidosDelBot s, Func<short, int> rinde, short obj, int reserva, bool hayApuro) =>
        hayApuro || s.Suma(rinde) - rinde(obj) >= reserva;

    /// <summary>Cuánto vender de una ranura: del botín que no se come ni se toma, todo; de la comida y la bebida,
    /// lo que pasa de la reserva, y en un apuro un par.</summary>
    private short CuantoVender(SentidosDelBot s, short obj, short enLaRanura)
    {
        bool esComida = PuntosDeComida(obj) > 0, esBebida = PuntosDeBebida(obj) > 0;
        if ((!esComida && !esBebida) || enLaRanura <= 1) return enLaRanura;
        Func<short, int> rinde = esComida ? PuntosDeComida : PuntosDeBebida;
        int reserva = esComida ? ComidaQueQuiere : BebidaQueQuiere;
        int sobran = (s.Suma(rinde) - reserva) / rinde(obj);
        return (short)Math.Clamp(sobran > 0 ? sobran : UnidadesDeApuro, 1, enLaRanura);
    }

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
        Func<int, bool>? recurso = RecursoDeSuOficio();

        // ¿Hay dónde trabajar acá cerca? Si no, se viaja hasta donde haya.
        // Y SÓLO DONDE NO SE MATA A LA GENTE: nada de mapas pk. Medido: un Minero de nivel 5 al que se mandaba a la mina
        // del mapa 33, a cinco mapas, cruzando zonas pk con gladiadores que pegan 282 y trece lobos, murió al menos dos
        // veces en veinte minutos y perdió los dos piquetes, que se caen al morir. Todas las minas del mundo están en
        // mapas pk; el agua y los árboles de la cuna, no.
        var punto = MapaSeguro(sentidos.Mapa)
            ? _entorno.Mapas.DondeTrabajar(sentidos.Mapa, sentidos.Posicion, RadioDeFaena, agua, recurso) : null;
        if (punto is null)
        {
            if (_entorno.Mapas.DondeSeTrabaja(sentidos.Mapa, agua, recurso, sentidos.Posicion, MapaSeguro, Pisable(sentidos)) is not { } lejos) { PostergarFaena(); return false; }
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
        // CON ALGO PEGADO NO SE SIENTA: el primer golpe corta la meditación, y sentarse otra vez en cada vuelta era comer
        // golpes sin recuperar maná.
        if (!sentidos.UsaMana || sentidos.Mana >= MeditaBajo || sentidos.AlgoPegado()) return false;

        // EL PAQUETE ES UN INTERRUPTOR, así que se manda según lo que dice el servidor y no a ciegas: si el servidor ya
        // había cortado la meditación por un golpe o un paso, el pedido de levantarse la volvía a prender.
        if (!sentidos.Meditando) transporte.Mandar(Paquete(ClientPacketId.Meditate, _ => { }));
        for (int pasado = 0; pasado < MeditaTopeMs; pasado += RebanadaDePausaMs)
        {
            await Task.Delay(RebanadaDePausaMs, ct);
            // Meditar se corta solo al caminar o al pelear, así que cualquier urgencia levanta al bot igual.
            if (!sentidos.Vivo || sentidos.Salud < TomaPocion || sentidos.AlgoPegado()) break;
            if (sentidos.Mana >= MeditaHasta) break;
            if (!sentidos.Meditando) break;   // la cortó el servidor
        }
        if (sentidos.Meditando) transporte.Mandar(Paquete(ClientPacketId.Meditate, _ => { }));   // el mismo paquete la apaga
        return true;
    }

    /// <summary>
    /// De muerto: ir hasta un sacerdote. Es lo único que le queda por hacer, y no hay otra forma de volver.
    ///
    /// <para><b>Se VIAJA, no se da vueltas.</b> Antes esto miraba sólo lo que tenía a la vista —la ventana de
    /// visión son ocho baldosas por cuatro— y si no había ninguno se ponía a pasear adentro de su zona, veinte
    /// baldosas. O sea que un cadáver a treinta baldosas del templo, o en el mapa de al lado, no volvía nunca.
    /// Medido: tres de siete bots muertos y quietos durante cuarenta y cinco minutos, y uno estaba en un mapa
    /// que TIENE sacerdote. El navegante sabe dónde están —salen de los puntos de aparición del mundo, igual
    /// que los mercaderes—, así que se le pregunta y se camina hasta ahí.</para>
    /// </summary>
    private async Task<bool> BuscarSacerdoteAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                                  CancellationToken ct)
    {
        // YA ESTÁ AL ALCANCE: no se camina más. Caminar hacia un sacerdote que se tiene al lado es empujarle
        // la baldosa —que está ocupada por él— y quedarse ahí forcejeando; lo único que falta es insistir con
        // el pedido, que es lo que hace el ciclo de arriba. El alcance se mide POR EJE, igual que lo mide el
        // servidor: con Manhattan, un cadáver a siete baldosas en Y se quedaba quieto pidiendo algo que nunca
        // iba a llegar.
        if (sentidos.MasCercano(EsSacerdote) is { } c && AlAlcanceDelAltar(sentidos, c.Quien))
        {
            await Task.Delay(RebanadaDePausaMs, ct);
            return true;
        }

        // Y SI LO VE PERO LEJOS, TAMPOCO SE VA DERECHO. Acá se daba un paso codicioso hacia él, y el paso
        // codicioso se traba con cualquier pared en el medio: medido, un cadáver a ocho baldosas del sacerdote
        // en el mapa de la cuna se pasó cuatro minutos oscilando entre dos baldosas. Peor todavía, esa rama
        // devolvía "hice algo", así que nunca se llegaba a la salida de volver al hogar. Se va por el
        // navegante, igual que al mercader, que aprendió lo mismo.

        if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => EsSacerdote((short)f), pisable: Pisable(sentidos)) is { } templo)
        {
            if (await IrHastaAsync(transporte, sentidos, templo.Mapa, templo.X, templo.Y, cerrada, ct,
                                   RadioDelAltar, deMuerto: true)) return true;
            // NO LLEGÓ. Se sigue intentando —está muerto, no tiene otra cosa que hacer— pero CON UNA PAUSA:
            // sin ella este camino vuelve enseguida sin haber esperado nada y el ciclo de decisión gira sobre
            // sí mismo a toda velocidad. Medido con el perfilador: un bot muerto en un mapa desde el que no se
            // llega a ningún templo dio DOSCIENTAS SESENTA MIL vueltas en un minuto, un hilo entero quemado.
            await Task.Delay(RebanadaDePausaMs, ct);
            SiNoSePuedeLlegar(transporte);
            // Y EL CICLO SIGUE: `false` quiere decir "se cayó la conexión", y el que llama corta la sesión entera. Acá
            // se devolvía `!sentidos.Vivo`, así que el bot que revivía justo después de un viaje fallido al templo —por
            // la vuelta al hogar, o porque el sacerdote lo alcanzó igual— terminaba su propia sesión. Medido: una
            // Guerrera que moría seguido cerró la sesión dos veces en quince minutos sin ningún aviso del servidor
            // ("receive loop ended") y volvió a entrar con otro índice de personaje.
            return !cerrada.IsCompleted;
        }

        await Task.Delay(RebanadaDePausaMs, ct);   // idem: PasearAsync también puede volver sin esperar
        SiNoSePuedeLlegar(transporte);
        return await PasearAsync(transporte, sentidos, cerrada, ct);
    }

    /// <summary>
    /// Cuando hace rato que no aparece una presa, viajar a un mapa donde haya. Devuelve si se ocupó de esto.
    ///
    /// <para>La PACIENCIA es lo que hace que esto no sea un bot que vive viajando: recién se mueve después de
    /// varios minutos sin ver nada a lo que animarse, que es más o menos lo que aguanta una persona dando
    /// vueltas al pedo. Y el destino sale del navegante con el MISMO criterio con el que elige presa, así que
    /// no viaja a un mapa lleno de cosas que le quedan grandes.</para>
    /// </summary>
    private async Task<bool> BuscarDondeCazarAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                                   int vidaMaxima, int nivel, Task cerrada, CancellationToken ct)
    {
        // SIN ENERGÍA PARA PELEAR TAMPOCO SE SALE A BUSCAR DÓNDE CAZAR: se llega y no se puede pegar. Medido: con
        // GolpeTolerable en 3, las diez muertes de una corrida de quince minutos fueron de los dos bots con la energía en
        // cero (ocho y dos), y la sonda ya los había visto así saliendo a buscar coto. Para ellos, con el valor 3 el lobo
        // pasa a ser presa, y el lobo paga cincuenta: los cotos que se elegían eran de lobos.
        if (!TieneFuelleParaPelear(sentidos)) return false;
        if (Environment.TickCount64 - _ultimaPresaQuePaga < PacienciaSinPresaMs) return false;
        _ultimaPresaQuePaga = Environment.TickCount64;   // se haya podido o no: no se reintenta en el paso siguiente

        // PRIMERO UN COTO QUE PAGUE, y recién si no hay ninguno al alcance, cualquiera. El coto se elegía por
        // "hay al menos una presa", sin mirar si soltaba oro — y así la única serpiente del mapa 1 alcanzaba para
        // mandar ahí a cinco bots, que se la repartieron durante media hora sin ganar una moneda.
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        Func<int, bool> sirve = f => EsPresa((short)f, vidaMaxima, nivel, conPociones, curaVeneno) && PagaOro((short)f);
        var hallado = _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, sirve, pisable: Pisable(sentidos));
        if (hallado is null)
        {
            sirve = f => EsPresa((short)f, vidaMaxima, nivel, conPociones, curaVeneno);
            hallado = _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, sirve, pisable: Pisable(sentidos));
        }
        if (hallado is not { } coto) return false;

        // REPARTIRSE. El navegante devuelve siempre el MISMO spawn —el más cercano— así que todos los bots
        // que salían a buscar coto iban al mismo punto. Medido: desde la cuna y desde el mapa 1 elegía (67,25)
        // para cualquiera, y cuatro bots quedaron amontonados a dos baldosas uno del otro, lejos de los
        // escorpiones — que no caminan (`Movement=0`) y están repartidos en tres lugares del mapa. Adentro del
        // mapa donde está, el bot elige al azar entre los spawns que le sirven y a los que se llega; cada bot
        // tiene su propio azar, así que se reparten solos. Para otro mapa vale el que eligió el navegante, que
        // ya verificó que se llega: al entrar, la próxima búsqueda reparte.
        var destino = (X: coto.X, Y: coto.Y);
        if (coto.Mapa == sentidos.Mapa && _entorno.Datos.MapSpawns.TryGetValue(coto.Mapa, out var spawns))
        {
            var opciones = spawns.Where(sp => sirve(sp.NpcIndex))
                                 .Select(sp => (X: (byte)sp.X, Y: (byte)sp.Y))
                                 .Distinct()
                                 .Where(t => _entorno.Mapas.HayComoLlegar(coto.Mapa, sentidos.Posicion, (t.X, t.Y),
                                                                          ToleranciaDelCoto) is not null)
                                 .ToList();
            if (opciones.Count > 0) destino = opciones[_rng.Next(opciones.Count)];
        }

        // "HAY PRESAS EN ESTE MAPA" NO ES "HAY PRESAS DONDE ESTOY PARADO". Acá se devolvía false cuando el
        // coto caía en el mapa propio, dando por hecho que entonces ya estaba bien ubicado — y no: el paseo
        // no se aleja más de `RadioDeCasa` de donde entró, así que un bot que aparece en una punta de un mapa
        // de cien por cien se queda en su burbuja de veinte baldosas para siempre. Medido con el perfilador:
        // cuatro bots con `pasear` al 100% del tiempo y "sin presa a la vista" en TODAS las vueltas, en mapas
        // que sí tienen criaturas. Se camina hasta donde aparecen, esté en este mapa o en otro.
        if (!await IrHastaAsync(transporte, sentidos, coto.Mapa, destino.X, destino.Y, cerrada, ct, ToleranciaDelCoto))
        {
            SiEstaEncerrado(transporte, sentidos);
            return false;
        }
        _cotosFallidos = 0;

        // Y EL COTO PASA A SER SU ZONA. Sin esto el arreglo se anula solo cuando el viaje es adentro del mismo
        // mapa: `PasearAsync` sólo re-elige la zona al CAMBIAR de mapa, así que el bot llegaba hasta las
        // criaturas y el paseo siguiente lo devolvía a la burbuja de donde había salido.
        _casa = sentidos.Posicion;
        return true;
    }

    /// <summary>Cuándo cazó por última vez algo que SUELTA ORO. Es contra esto que se mide la paciencia para
    /// mudarse: cazar criaturas que no pagan no cuenta, porque con eso un bot se queda para siempre donde sólo
    /// hay experiencia y ni una moneda. Arranca en cero a propósito: un bot que aparece en un pueblo no tiene
    /// por qué esperar los primeros minutos para salir de ahí.</summary>
    private long _ultimaPresaQuePaga;

    /// <summary>Cuánto aguanta sin ver nada que cazar antes de mudarse.</summary>
    private const int PacienciaSinPresaMs = 3 * 60_000;

    /// <summary>A cuántas baldosas del spawn se para al llegar al coto. Chico a propósito: la presa que paga no
    /// camina, y con la tolerancia de antes —el radio de caza, doce— el bot podía frenar fuera de la ventana de
    /// visión (ocho por seis, por eje) y no verla nunca.</summary>
    private const int ToleranciaDelCoto = 3;

    /// <summary>
    /// AL CADÁVER QUE NO PUEDE LLEGAR A NINGÚN TEMPLO LE QUEDA VOLVER A CASA, y el original le da justo eso:
    /// <c>RegresarHogar</c> (id 99), que sólo atiende estando muerto y deja al jugador en la baldosa de
    /// resurrección de su ciudad — que está a dos pasos del sacerdote de esa ciudad, porque para eso existe.
    ///
    /// <para>Hace falta porque un fantasma choca con las paredes igual que un vivo (<c>PosLegal</c> no
    /// exceptúa al muerto), así que "hay un templo a un salto de mapa" no quiere decir que se pueda llegar:
    /// medido, un bot murió en una punta del mapa 814 y la puerta hacia el 815 no tenía camino desde ahí. Sin
    /// esta salida se quedaba tirado hasta que alguien apagara el servidor.</para>
    ///
    /// <para>Se espera a fallar unas cuantas veces antes de usarlo: caminar hasta el templo es lo que hace
    /// alguien que puede, y volver a casa deja el cuerpo del otro lado del mundo.</para>
    /// </summary>
    private void SiNoSePuedeLlegar(TransporteDeBot transporte)
    {
        if (++_viajesAlTemploFallidos < ViajesAntesDeVolverAlHogar) return;
        _viajesAlTemploFallidos = 0;
        transporte.Mandar(Paquete(ClientPacketId.RegresarHogar, _ => { }));
    }

    private int _viajesAlTemploFallidos;

    /// <summary>Cuántos viajes fallidos al templo antes de tirar la toalla y volver a casa.</summary>
    private const int ViajesAntesDeVolverAlHogar = 6;

    /// <summary>
    /// EL QUE QUEDÓ ENCERRADO VUELVE A CASA CON LA RUNA. Un paseo o una puerta de un solo sentido pueden dejar
    /// a un bot en un rincón sin camino a ninguna salida y sin nada que cazar. Medido: un Mago de nivel uno en un
    /// bolsillo del mapa 20 —cero presas para él, y ninguna de las seis salidas del mapa alcanzable desde ahí—
    /// paseando más de una hora con la experiencia congelada.
    ///
    /// <para>El juego ya trae la salida: todo kit de novato lleva una Runa de teletransportación, que devuelve a
    /// la baldosa de nacimiento de la ciudad-hogar y NO se gasta al usarla. Estando vivo sólo viaja si el mapa
    /// no es PK (o si se está parado en una arena); si no, el servidor contesta el 468 y no mueve a nadie
    /// (VB6 InvUsuario.bas:1356-1372). Por eso se mira el mapa antes de pedirla, y se espera a haber fallado el
    /// viaje al coto un par de veces: a un rincón al que no se llega se le da una oportunidad antes de irse.</para>
    /// </summary>
    private void SiEstaEncerrado(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        if (++_cotosFallidos < CotosFallidosAntesDeLaRuna) return;
        _cotosFallidos = 0;
        if (_entorno.Datos.IsPkMap(sentidos.Mapa)) return;
        Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna);
    }

    private int _cotosFallidos;

    /// <summary>Cuántos viajes fallidos al coto antes de volver a casa con la Runa.</summary>
    private const int CotosFallidosAntesDeLaRuna = 2;

    /// <summary>VB6 <c>otRuna</c> (Declares.bas:615).</summary>
    private const int ObjTypeRuna = 38;

    /// <summary>El sacerdote que resucita: <c>NpcType 1</c> (VB6 <c>eNPCType.Revividor</c>).</summary>
    private bool EsSacerdote(short ficha) => _entorno.Datos.Npc(ficha)?.NpcType == 1;

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

    /// <summary>
    /// PONERSE EL EQUIPO: el arma, la armadura, el escudo y el casco, lo mejor que tenga de cada cosa. Devuelve si
    /// mandó a equipar algo.
    ///
    /// <para>El servidor le saca TODO lo puesto al que muere (VB6 UserDie, `UnequipAll`), y acá sólo se equipaba la
    /// herramienta del oficio: después de la primera muerte el bot peleaba a mano limpia y sin ropa para siempre.
    /// Medido en los `.chr`: una Guerrera que murió ocho veces en quince minutos tenía el martillo y la ropa en la
    /// mochila sin poner, igual que un Minero que murió tres; la Pescadora que no había muerto los tenía puestos. Las
    /// armas arrojadizas quedan afuera, porque cambian la forma de pelear.</para>
    /// </summary>
    private bool Equiparse(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        if (Environment.TickCount64 < _proximoEquipo) return false;
        // SI EL SERVIDOR NO LO DEJÓ PONER (la clase, el nivel), no se insiste con eso.
        if (_equipoPedido is { } pedido)
        {
            if (!sentidos.EstaPuesto(pedido.Ranura) && sentidos.QueHayEn(pedido.Ranura) == pedido.Obj) _noSeLoPone.Add(pedido.Obj);
            _equipoPedido = null;
        }

        foreach (var esDeEso in QueSePone)
        {
            if (sentidos.LlevaPuesto(obj => _entorno.Datos.Obj(obj) is { } puesto && esDeEso(puesto))) continue;
            if (MejorParaPoner(sentidos, esDeEso) is { } ranura) return PedirQueSePonga(transporte, sentidos, ranura);
        }

        // LOS NUDILLOS, SÓLO PARA EL QUE NO TIENE ARMA. El servidor saca uno al ponerse el otro (VB6
        // InvUsuario.bas:991-997 y :1051-1058), así que con los dos en la mochila el bot se pasaría la vida cambiando.
        // Medido con la sonda: un Gladiador sin arma, con los Nudillos de Hierro en la mochila sin poner.
        static bool EsArma(ObjInfo o) => o.IsWeapon && o.Proyectil == 0;
        if (!sentidos.LlevaPuesto(obj => _entorno.Datos.Obj(obj) is { } puesto && (EsArma(puesto) || puesto.IsKnuckles))
            && MejorParaPoner(sentidos, EsArma) is null
            && MejorParaPoner(sentidos, o => o.IsKnuckles) is { } nudillos)
            return PedirQueSePonga(transporte, sentidos, nudillos);
        return false;
    }

    /// <summary>La ranura con lo mejor de eso que tiene sin poner y que puede usar, o null.</summary>
    private byte? MejorParaPoner(SentidosDelBot sentidos, Func<ObjInfo, bool> esDeEso)
    {
        byte mejor = 0;
        int valorMejor = -1;
        foreach (byte ranura in sentidos.RanurasOcupadas())
        {
            short obj = sentidos.QueHayEn(ranura);
            if (sentidos.EstaPuesto(ranura) || _noSeLoPone.Contains(obj)) continue;
            if (_entorno.Datos.Obj(obj) is not { } o || !esDeEso(o) || o.ClassForbidden(_clase) || o.MinLevel > sentidos.Nivel)
                continue;
            int valor = o.IsWeapon || o.IsKnuckles ? o.MaxHit : o.MaxDef;
            if (valor > valorMejor) { mejor = ranura; valorMejor = valor; }
        }
        return mejor == 0 ? null : mejor;
    }

    private bool PedirQueSePonga(TransporteDeBot transporte, SentidosDelBot sentidos, byte ranura)
    {
        _equipoPedido = (ranura, sentidos.QueHayEn(ranura));
        _proximoEquipo = Environment.TickCount64 + EsperaEquipoMs;
        return transporte.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(ranura)));
    }

    private static readonly Func<ObjInfo, bool>[] QueSePone =
    {
        o => o.IsWeapon && o.Proyectil == 0, o => o.IsArmor, o => o.IsShield, o => o.IsHelmet,
    };

    private const int EsperaEquipoMs = 1_500;
    private long _proximoEquipo;
    private (byte Ranura, short Obj)? _equipoPedido;
    private readonly HashSet<short> _noSeLoPone = new();

    /// <summary>
    /// CURARSE EL VENENO. Devuelve si se ocupó de esto.
    ///
    /// <para>El veneno del original no se pasa solo: saca de uno a cinco de vida cada veinte segundos hasta que
    /// alguien lo cura, y puede matar. En la cuna lo meten las serpientes y los escorpiones, que es justo lo que
    /// caza un recién nacido, y nada en el bot lo miraba. Medido: seis de ocho bots con el veneno encima en el
    /// `.chr`, tomando poción tras poción. Lo que hace una persona es tomar la poción violeta si tiene, o ir al
    /// sacerdote, que al que llega vivo y herido lo cura (VB6 Acciones.bas:316).</para>
    /// </summary>
    private async Task<bool> CurarVenenoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                              CancellationToken ct)
    {
        if (!sentidos.Envenenado || Environment.TickCount64 < _proximaCura) return false;

        if (Usar(transporte, sentidos, o => o is { IsPotion: true, SubType: PocionQueCuraElVeneno }))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            sentidos.OlvidarVeneno();
            return true;
        }

        if (sentidos.MasCercano(EsSacerdote) is { } cura && AlAlcanceDelAltar(sentidos, cura.Quien))
        {
            // A VIDA LLENA NO CURA: el original calla y el veneno queda. Se espera al próximo mordisco, que baja la
            // vida, y ahí se le pide.
            if (sentidos.Salud < 1.0)
            {
                PedirResurreccion(transporte, sentidos);
                await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
                if (sentidos.Salud >= 1.0) sentidos.OlvidarVeneno();
            }
            else await Task.Delay(RebanadaDePausaMs, ct);
            return true;
        }

        var templo = _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => EsSacerdote((short)f), pisable: Pisable(sentidos));
        if (templo is not null) _curasSinCamino = 0;
        if (templo is { } t && await IrHastaAsync(transporte, sentidos, t.Mapa, t.X, t.Y, cerrada, ct, RadioDelAltar))
            return true;

        // SIN SACERDOTE AL QUE SE LLEGUE CAMINANDO, LA RUNA A CASA, que en la cuna hay uno. Medido sin servidor: desde
        // el mapa 62 no se llega a pie a ningún sacerdote —la única salida hacia la cuna está en un cuarto cerrado—,
        // y una Pescadora envenenada ahí siguió cazando con el veneno encima. Después de un par de intentos, igual que
        // con el coto y con las tiendas; el contador es otro porque cada cosa se destraba por su lado.
        if (templo is null && ++_curasSinCamino >= CotosFallidosAntesDeLaRuna && !_entorno.Datos.IsPkMap(sentidos.Mapa))
        {
            _curasSinCamino = 0;
            if (Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna))
            {
                await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
                return true;
            }
        }

        // NO HAY A QUIÉN IR: se sigue con lo demás y se vuelve a probar más tarde. Sin la espera, un bot envenenado
        // lejos de todo templo reintentaría el viaje en cada vuelta del ciclo.
        _proximaCura = Environment.TickCount64 + _rng.Next(EsperaCuraMinMs, EsperaCuraMaxMs);
        return false;
    }

    /// <summary>
    /// SIN POCIONES Y CON LA VIDA BAJA, AL SACERDOTE. Al vivo herido lo cura del todo con el doble clic (VB6 Acciones.bas:316),
    /// que es lo que hace cualquiera que empieza y se quedó sin pociones. Sin esto un bot sin pociones sólo podía esperar a que
    /// la vida volviera sola, y con el hambre o la sed en cero no vuelve: medido en `largo2`, una Guerrera con 5 de 58 de vida
    /// trabada en el pozo, y un Mago sin pociones muerto por una Gallina Salvaje. Sólo si hay uno a
    /// <see cref="SaltosHastaElSacerdote"/> mapas o menos; si no, se vuelve a probar más tarde. Devuelve si se ocupó de esto.
    /// </summary>
    private async Task<bool> SanarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (!sentidos.Vivo || !TocaSanarse(sentidos.Salud, sentidos.CuantoTiene(EsPocionDeVida))) return false;

        // EL SACERDOTE A MANO, ANTES QUE LA ESPERA DEL VIAJE: pedirle es un doble clic, y `_proximaSanacion` es la espera de caminar
        // hasta uno o de usar la runa. El pedido tiene la suya, que sólo se pone si no curó.
        if (Environment.TickCount64 >= _proximoPedidoAlSacerdote
            && sentidos.MasCercano(EsSacerdote) is { } cura && AlAlcanceDelAltar(sentidos, cura.Quien))
        {
            PedirResurreccion(transporte, sentidos);   // el mismo doble clic: al vivo herido lo cura
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            // SI NO LO CURÓ, SE ESPERA ANTES DE INSISTIR: sin la espera el ciclo volvería acá en el acto y giraría sin ceder.
            if (sentidos.Salud < TomaPocion)
                _proximoPedidoAlSacerdote = Environment.TickCount64 + _rng.Next(EsperaSanarFallidoMinMs, EsperaSanarFallidoMaxMs);
            return true;
        }
        if (Environment.TickCount64 < _proximaSanacion) return false;

        if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => EsSacerdote((short)f), pisable: Pisable(sentidos)) is { Saltos: <= SaltosHastaElSacerdote } templo
            && await IrHastaAsync(transporte, sentidos, templo.Mapa, templo.X, templo.Y, cerrada, ct, RadioDelAltar))
        {
            // LLEGÓ PERO NO LO TIENE AL ALCANCE (el sacerdote no está en su lugar): la misma espera, por lo mismo.
            if (sentidos.MasCercano(EsSacerdote) is not { } visto || !AlAlcanceDelAltar(sentidos, visto.Quien))
                _proximaSanacion = Environment.TickCount64 + _rng.Next(EsperaSanarFallidoMinMs, EsperaSanarFallidoMaxMs);
            return true;
        }

        // SIN SACERDOTE AL QUE SE LLEGUE, LA RUNA A CASA, que en la cuna hay uno: lo mismo que para el veneno. Medido en `sano2`: bots
        // sin pociones con la vida entre 7 y 30 % en el mapa 62, desde donde no se llega a pie a ningún sacerdote, esperando ahí hasta
        // que un Águila los terminaba. Sólo fuera de un mapa pk, donde la runa no lleva.
        if (++_sanacionesSinCamino >= CotosFallidosAntesDeLaRuna && !_entorno.Datos.IsPkMap(sentidos.Mapa)
            && Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna))
        {
            _sanacionesSinCamino = 0;
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        _proximaSanacion = Environment.TickCount64 + _rng.Next(EsperaCuraMinMs, EsperaCuraMaxMs);
        return false;
    }

    /// <summary>¿Toca ir al sacerdote? Con la vida bajo el umbral de la poción y sin ninguna poción que tomar.</summary>
    public static bool TocaSanarse(double salud, int pocionesDeVida) => salud < TomaPocion && pocionesDeVida == 0;

    private const int SaltosHastaElSacerdote = 2;
    private const int EsperaSanarFallidoMinMs = 20_000, EsperaSanarFallidoMaxMs = 40_000;
    private long _proximaSanacion;
    private int _sanacionesSinCamino;
    private long _proximaRunaDeHuida;
    private long _proximoPedidoAlSacerdote;
    private const int EsperaEntreRunasDeHuidaMs = 30_000;

    private const int PocionQueCuraElVeneno = 5;
    private const int EsperaCuraMinMs = 60_000, EsperaCuraMaxMs = 120_000;
    private long _proximaCura;

    /// <summary>Búsquedas de sacerdote seguidas sin ninguno al que se llegue caminando.</summary>
    private int _curasSinCamino;

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
            // SALVO QUE ESPERAR NO DEVUELVA LA ENERGÍA: con el hambre o la sed en cero el servidor no la recupera, así
            // que alejarse por tenerla baja dejaba al bot sin pelear para siempre, y sin pelear no hay botín con qué
            // pagar el agua. Medido: un bot con 20 de 130 de energía, la sed en cero y dos monedas, veinte minutos
            // sin matar una criatura. Mientras le quede algo, pelea con lo que tiene.
            bool noVaARegenerar = sentidos.ConHambre || sentidos.ConSed;
            if (sentidos.Salud < SeRetira || (sentidos.Fuelle < FuelleMinimo && !noVaARegenerar) || sentidos.Fuelle <= 0)
                return await AlejarseAsync(transporte, sentidos, quien, cerrada, ct);

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

    /// <summary>
    /// ALEJARSE de lo que lo está lastimando: unos pasos por el camino a la baldosa libre más lejos de la criatura, a
    /// <see cref="PasosDeHuida"/> o menos, sabiendo dónde están las paredes, el agua y quién está parado. No es huir del todo —el
    /// bot no sabe a dónde ir— pero le saca la criatura de encima mientras la vida vuelve.
    ///
    /// <para>Antes se caminaba a ciegas en contra de la criatura y, al chocar, se probaba un costado alternando de lado según la
    /// vuelta. En un rincón eso rebotaba entre las dos paredes sin probar nunca la salida: medido en `rojo1`, los cinco bots sin
    /// pociones murieron alguna vez después de varias vueltas huyendo sin moverse de la baldosa, uno de ellos veinticuatro vueltas
    /// seguidas en (47,48) de la cuna, con pared al norte y al oeste y piso libre al sur y al este.</para>
    /// </summary>
    private async Task<bool> AlejarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                           SentidosDelBot.Visible de, Task cerrada, CancellationToken ct)
    {
        var donde = sentidos.Posicion;
        var camino = _entorno.Mapas.LejosDe(sentidos.Mapa, donde, (de.X, de.Y), PasosDeHuida, sentidos.BaldosasOcupadas());
        // ACORRALADO DE VERDAD: ninguna baldosa a la que se llegue queda más lejos. Se espera un paso y la vuelta que viene lo vuelve
        // a mirar, que la criatura se mueve.
        if (camino is null) { await Task.Delay(UnPaso(), ct); return true; }
        foreach (var baldosa in camino.Take(_rng.Next(3, 7)))
        {
            if (cerrada.IsCompleted) break;
            byte rumbo = SentidosDelBot.RumboHacia(baldosa.X - donde.X, baldosa.Y - donde.Y);
            if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return false;
            sentidos.PredecirPaso(rumbo);
            await Task.Delay(UnPaso(), ct);
            // El servidor corrige con un PosUpdate cuando el paso se rechaza: algo se paró adelante, y la vuelta que viene recalcula.
            if (sentidos.Posicion != baldosa) break;
            donde = baldosa;
        }
        return true;
    }

    private const int PasosDeHuida = 8;

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
                                          Task cerrada, CancellationToken ct, int tolerancia = 1,
                                          bool deMuerto = false)
    {
        // LO QUE TAPÓ UN PASO SE RODEA. El mapa sólo sabe de paredes y agua: una criatura quieta o un personaje
        // parado en un pasillo no están ahí, así que recalcular devolvía el mismo camino y el bot chocaba contra lo
        // mismo hasta gastar todos los tramos. Medido: tres bots de la cuna sin llegar nunca a la comida, uno
        // doce veces contra la misma baldosa y dos atascados uno detrás del otro en el mismo pasillo.
        var trabas = new HashSet<(byte X, byte Y)>();
        for (int tramos = 0; tramos < TramosPorViaje && !ct.IsCancellationRequested && !cerrada.IsCompleted; tramos++)
        {
            // De muerto se viaja igual: es la única forma de llegar al sacerdote.
            if (!sentidos.Ubicado || (!deMuerto && !sentidos.Vivo)) return false;
            int mapa = sentidos.Mapa;
            var desde = sentidos.Posicion;

            // El destino puede estar en otro mapa: en ese caso el tramo termina en la puerta.
            bool esLaMeta = mapa == mapaDestino;
            (byte X, byte Y) meta;
            if (esLaMeta) meta = (destinoX, destinoY);
            else if (_entorno.Mapas.PuertaHacia(mapa, mapaDestino, desde, (destinoX, destinoY), Pisable(sentidos)) is { } salida) meta = salida.Puerta;
            else return false;   // desde acá no se llega

            // Ya llegó: al lado alcanza, porque encima del destino no se puede parar si hay alguien ahí —y
            // el destino típico es justamente la baldosa donde está parado un mercader.
            if (esLaMeta && Math.Abs(desde.X - destinoX) + Math.Abs(desde.Y - destinoY) <= tolerancia) return true;

            var camino = esLaMeta ? _entorno.Mapas.HayComoLlegar(mapa, desde, meta, tolerancia, trabas)
                                   : _entorno.Mapas.Camino(mapa, desde, meta, trabas);
            // Si esquivando no queda camino, lo que tapaba era el único paso: se espera a que se corra y se vuelve a
            // probar por donde se venía.
            if ((camino is null || camino.Count == 0) && trabas.Count > 0)
            {
                trabas.Clear();
                await Task.Delay(_rng.Next(EsperaTrabaMinMs, EsperaTrabaMaxMs), ct);
                continue;
            }
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
                var como = await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct, deMuerto);
                // CURARSE EN EL CAMINO EN VEZ DE VOLVERSE. Un golpe que bajaba la vida del umbral de la poción
                // abortaba el viaje entero aunque el bot tuviera mil pociones encima, y el intento siguiente
                // arrancaba otra vez desde donde estuviera. Mientras no esté en peligro de verdad se toma la poción y
                // se sigue caminando; con la vida bajo `SeRetira` el viaje se corta igual que antes.
                if (como == Paso.Urgencia && !deMuerto && sentidos.Vivo && sentidos.Salud >= SeRetira
                    && await CuidarseAsync(transporte, sentidos, ct))
                    como = Paso.Dado;
                // SIN POCIONES Y SIN NADA HOSTIL CERCA, EL VIAJE SIGUE. Cortarlo sirve para tomar una poción o para escapar de
                // lo que pega; sin pociones y sin nadie que ataque, pararse no cura nada. Medido en `largo2`: una Guerrera con 5
                // de 58 de vida, sin pociones, sin comida ni bebida y con una moneda cortaba cada minuto, en el primer paso, el
                // viaje a vender la Runa que la sacaba del pozo.
                if (como == Paso.Urgencia && SigueCaminandoConUrgencia(sentidos.Vivo, deMuerto,
                        sentidos.CuantoTiene(EsPocionDeVida), HostilCerca(sentidos)))
                    como = Paso.Dado;
                if (como != Paso.Dado) return false;   // sin conexión, o algo más urgente que el viaje
                if (_pasoTrabado) { trabas.Add(baldosa); break; }   // algo se paró en el camino: rodearlo
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
                                              CancellationToken ct, bool deMuerto = false)
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
        return Urge(sentidos, deMuerto) ? Paso.Urgencia : Paso.Dado;
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
    ///
    /// <para><b>Y AL MUERTO NO LE URGE NADA</b>, que era la otra mitad del mismo error. Con la regla de
    /// arriba, un cadáver devolvía urgencia en CADA paso: o sea que no podía caminar a ningún lado, ni
    /// siquiera hasta el sacerdote, que es lo único que le queda por hacer. Medido: tres de siete bots
    /// muertos y clavados en la misma baldosa durante cuarenta y cinco minutos, uno de ellos en un mapa que
    /// tiene sacerdote. Cuando va de muerto, lo único que cambia el cuadro es volver a estar vivo.</para>
    /// </summary>
    private static bool Urge(SentidosDelBot sentidos, bool deMuerto = false) =>
        deMuerto ? sentidos.Vivo
                 : !sentidos.Vivo || sentidos.Salud < TomaPocion;

    private bool _pasoTrabado;

    /// <summary>¿Sigue el viaje aunque la vida pida parar? Sólo si parar no sirve para nada: vivo, sin pociones que tomar y
    /// sin ninguna criatura hostil cerca de la que haya que escaparse. El viaje de muerto tiene su propia regla.</summary>
    public static bool SigueCaminandoConUrgencia(bool vivo, bool deMuerto, int pocionesDeVida, bool hostilCerca) =>
        vivo && !deMuerto && pocionesDeVida == 0 && !hostilCerca;

    /// <summary>¿Hay una criatura hostil a <see cref="RadioDeSusto"/> pasos o menos?</summary>
    private bool HostilCerca(SentidosDelBot sentidos) =>
        sentidos.MasCercano(ficha => _entorno.Datos.Npc(ficha) is { Hostile: true }) is { Pasos: <= RadioDeSusto };

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
    private bool EsPresa(short ficha, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno) =>
        EsPresaPara(_entorno.Datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno);

    /// <summary>La regla de <see cref="EsPresa"/> sobre la ficha sola.</summary>
    public static bool EsPresaPara(NpcInfo? n, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno)
    {
        if (n is null || !n.Hostile || !n.Attackable) return false;
        // LA EXPERIENCIA QUE DA ES LA MEDIDA DE LO QUE ES, y el golpe a mano solo NO ALCANZA: un Espectro
        // tiene MaxHit 0 porque pelea con hechizos, así que por el golpe parecía inofensivo — y da treinta y
        // dos mil de experiencia, o sea que mata a un personaje de nivel uno sin despeinarse. Con la
        // experiencia de por medio, un bicho así queda afuera aunque no pegue trompadas.
        if (n.GiveExp > ExperienciaQueSeAnima(nivel)) return false;
        // SIN POCIONES, LO QUE ENVENENA QUEDA AFUERA SI NO HAY CON QUÉ CURARLO. El veneno no se pasa solo, y sin la poción
        // violeta sólo lo cura un sacerdote: medido en `largo2` y `sano1`, un Mago sin pociones muerto una y otra vez por
        // escorpiones y por el veneno.
        if (!conPociones && n.Veneno > 0 && !curaVeneno) return false;
        // Y SIN NADA QUE TOMAR, LA PELEA SE GANA CON LA VIDA QUE SE TIENE: el golpe del bicho tiene que entrar el doble de veces.
        int tolerable = conPociones ? GolpeTolerable : GolpeTolerableSinPociones;
        return n.MaxHit * tolerable <= Math.Max(1, vidaMaxima);
    }

    /// <summary>Cuántos golpes máximos tiene que aguantar con su vida para animarse a una criatura sin pociones que tomar: el
    /// doble que con pociones.</summary>
    private const int GolpeTolerableSinPociones = GolpeTolerable * 2;

    /// <summary>Con menos pociones de vida que éstas pelea como el que no tiene ninguna: dos o tres peleas duras se las comen.</summary>
    private const int PocionesParaPelear = 10;

    private bool EsPocionQueCuraElVeneno(short obj) => _entorno.Datos.Obj(obj) is { IsPotion: true, SubType: PocionQueCuraElVeneno };

    /// <summary>La experiencia de la criatura más grande a la que se anima, según su nivel. Es la forma de
    /// medir "qué tan brava es" que el dato del juego da directo.</summary>
    private static int ExperienciaQueSeAnima(int nivel) => 30 + nivel * 40;

    /// <summary>¿Suelta oro al morir? Es el <c>GiveGLD</c> de su ficha, que el servidor le paga directo al que
    /// la mata (VB6 MODULO_NPCs.bas:910).</summary>
    private bool PagaOro(short ficha) => _entorno.Datos.Npc(ficha)?.GiveGld > 0;

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
        if (sentidos.MasCercano(EsSacerdote) is not { } c || !AlAlcanceDelAltar(sentidos, c.Quien)) return;
        // DOBLE CLIC, no clic + Resucitate. Es la misma trampa que con la tienda: el `LeftClick` del servidor
        // sondea PRIMERO la baldosa de ABAJO del punto clickeado y `PlayerAt` no excluye al que clickea, así
        // que un bot parado justo debajo del sacerdote se targeteaba A SÍ MISMO y `Resucitate` contestaba "no
        // tenés nada clickeado". `HandleDoubleClick` resuelve el NPC de la baldosa y revive él mismo
        // (VB6 Acciones.bas:304), sin pasar por el target.
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(c.Quien.X).WriteByte(c.Quien.Y)));
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
