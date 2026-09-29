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

    /// <summary>Con esta cantidad en la mochila, una poción deja de ser un recurso que se cuida.</summary>
    private const int PocionesDeSobra = 20;

    /// <summary>
    /// ¿SE TOMA UNA POCIÓN? Con pocas, sólo para no morirse. Con la mochila llena, también para volver a pelear.
    ///
    /// <para>Entre <see cref="TomaPocion"/> y <see cref="VuelveAPelear"/> el bot no hacía ninguna de las dos cosas:
    /// no se curaba —porque le sobraba vida— y no peleaba —porque le faltaba—, así que esperaba la curación
    /// natural, que DE PIE devuelve dos puntos cada sesenta y cuatro segundos (`Survival.HealIntervalTicks`).
    /// Medido en `verdad7`: rachas de nueve, ocho y siete minutos sin matar nada, en un mapa con diecinueve
    /// murciélagos y nueve escorpiones, con mil quinientas pociones rojas encima. Un jugador con la mochila así
    /// se toma una y sigue.</para>
    /// </summary>
    public static bool HoraDeTomarPocion(double salud, int pociones) =>
        salud < TomaPocion ||
        (pociones >= PocionesDeSobra && salud < VuelveAPelear);

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

    /// <summary>Cuántos antídotos lleva encima. Son pocos porque el veneno no es constante y cada uno cura
    /// del todo: lo que importa es no quedarse sin ninguno, no acumular.</summary>
    private const int AntidotosQueQuiere = 5;

    /// <summary>Con menos de lo primero de comida o de bebida sale a reponer, y compra hasta lo segundo. Se cuenta
    /// en PUNTOS de hambre y de sed, no en unidades: diez manzanas llenan cien y diez pollos setecientos cincuenta.
    /// El hambre baja diez cada tres minutos, así que cien puntos son media hora y cuatrocientos, dos horas.</summary>
    private const int ComidaMinima = 100, ComidaQueQuiere = 400;

    /// <summary>
    /// CUÁNTO CARGA EL QUE ESTÁ POR IRSE DE LA ZONA DE NOVATOS. Son unas diez horas de hambre y de sed, contra las dos
    /// de <see cref="ComidaQueQuiere"/>.
    ///
    /// <para>Mientras es novato tiene la tienda de la cuna a mano y puede volver cuando quiera, así que compra para
    /// un rato. Pasado <see cref="GameLimits.NewbieMaxLevel"/> la cuna no lo deja entrar más y, desde los Campos
    /// Abiertos, Ullathorpe o Nix, NINGÚN pueblo que le venda se alcanza sin cruzar mapas donde lo matan: los
    /// mercaderes de esos tres no le venden a un ciudadano porque su territorio no es de su facción, que es del
    /// original (VB6 Protocol.bas:5203) y no un hueco del port. Con <c>QuitarEquipoNovato</c> —prendido en el
    /// `Server.ini` que se distribuye— además pierde el kit en esa misma subida de nivel.</para>
    ///
    /// <para>Así que llena la mochila mientras todavía tiene a quién comprarle, que es lo que hace cualquiera antes
    /// de dejar la zona de novatos. En unidades son veintisiete pollos, o sea un casillero de la mochila.</para>
    /// </summary>
    private const int PuntosParaElCamino = 2_000;

    /// <summary>Cuánta comida compra, según si todavía puede volver a la tienda de la cuna.</summary>
    public static int ComidaQueSeLleva(int nivel) => PorDejarDeSerNovato(nivel) ? PuntosParaElCamino : ComidaQueQuiere;

    /// <inheritdoc cref="ComidaQueSeLleva"/>
    public static int BebidaQueSeLleva(int nivel) => PorDejarDeSerNovato(nivel) ? PuntosParaElCamino : BebidaQueQuiere;

    /// <summary>
    /// ¿HAY QUE REPONER LA BARRA? Se come y se toma con la barra por la mitad, que es cuando lo hace un jugador, y no
    /// esperando el cero.
    ///
    /// <para>Esperar el cero salía carísimo: el hambre baja diez cada tres minutos y una manzana da diez, así que el
    /// bot vivía oscilando entre cero y diez —comiendo una manzana cada tres minutos con noventa y nueve en la
    /// mochila— y pasaba una parte de cada ciclo EN CERO. Ahí el original no regenera energía (`RecStamina`), y sin
    /// energía no se pelea: medido en `verdad7`, los ocho bots terminaron los quince minutos con el hambre en 10 de
    /// 100, la sed en 20, y ninguno subió de nivel.</para>
    /// </summary>
    public static bool HayQueReponerLaBarra(int barra) => barra <= BarraParaReponer;

    /// <summary>¿Tiene que comer? La barra se la dice el servidor, con su propio reloj.</summary>
    public static bool TieneQueComer(SentidosDelBot sentidos) => HayQueReponerLaBarra(sentidos.Hambre);

    /// <inheritdoc cref="TieneQueComer"/>
    public static bool TieneQueTomar(SentidosDelBot sentidos) => HayQueReponerLaBarra(sentidos.Sed);

    /// <summary>La mitad de la barra: de ahí para abajo se come o se toma.</summary>
    private const int BarraParaReponer = 50;
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

    /// <summary>Su bando y su nivel, leídos de los sentidos al empezar cada vuelta: con ellos sabe qué guardia va por él.</summary>
    private byte _miBando;
    private int _miNivel;
    /// <summary>Cuándo vuelve a intentar ir a un mapa seguro para irse a dormir.</summary>
    private long _proximoRefugio;

    /// <summary>Cómo es este bot. Sale de una semilla derivada del NOMBRE y no del azar del arranque, igual
    /// que su cara: así el que hostea reconoce a "Bruma, la que anda siempre por el pueblo" y no a un
    /// personaje que cada día juega distinto.</summary>
    private readonly Temperamento _temperamento;

    /// <summary>La semilla que sale del nombre: de ahí el temperamento, el paso y qué coto le gusta más entre los que rinden
    /// igual (<see cref="CuantoLeGusta"/>).</summary>
    private readonly int _propio;

    /// <summary>La semilla de un nombre: la misma cuenta para el mismo nombre, en cualquier arranque.</summary>
    public static int SemillaDelNombre(string nombre)
    {
        int propio = 17;
        foreach (char c in nombre) propio = unchecked(propio * 31 + c);
        return propio;
    }

    /// <summary>La clase del personaje. Sale de la MISMA cuenta con la que se creó (derivada del nombre), así
    /// que es la que tiene de verdad — y es la que decide si le sirve una caña, un hacha o un piquete.</summary>
    private readonly byte _clase, _raza, _genero;

    public Bot(string nombre, string? clave, EntornoDeBots entorno, int semilla)
    {
        Nombre = nombre;
        _cuenta = nombre + "_bot";
        _clave = clave;
        _entorno = entorno;
        _rng = new Random(semilla);
        _como = clave is null ? ComoEntrar.Alta : ComoEntrar.Entrar;

        int propio = SemillaDelNombre(nombre);
        _propio = propio;
        _temperamento = (Temperamento)(Math.Abs(propio) % 3);
        // El paso propio: entre 0,8 y 1,3 veces el de referencia. Dos personas no caminan igual, y con el
        // mismo número para todos una fila de bots se mueve como un desfile.
        _ritmo = 0.8 + (Math.Abs(propio) % 6) * 0.1;
        (_raza, _genero, _clase, _, _) = Aspecto();
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
        // Y MIRA SUS SKILLS, como el que abre la ventana de habilidades al entrar: el servidor las manda sólo si hay
        // puntos libres, y para fabricar hace falta saber qué receta le alcanza (ver `SentidosDelBot.Skill`).
        transporte.Mandar(Paquete(ClientPacketId.RequestSkills, _ => { }));

        int pausas = 0;
        long proximaResurreccion = 0;
        // Cuánto va a jugar esta vez. Cada bot elige distinto y en cada sesión distinto, como cualquiera.
        long finDeSesion = Environment.TickCount64 + _rng.Next(SesionMinM, SesionMaxM + 1) * 60_000L;

        while (!ct.IsCancellationRequested && !cerrada.IsCompleted)
        {
            if (!sentidos.Ubicado) { await Task.Delay(500, ct); continue; }
            _miBando = sentidos.MiEstado;
            _miNivel = sentidos.Nivel;

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

            // EN EL MAPA DE UN GUARDIA DEL OTRO BANDO NO SE QUEDA: se va apenas llega, antes de elegir coto y antes de que lo vean.
            // Medido en `tienda6`: un Guerrero que arrancó la sesión en la Cordillera del Norte (806) eligió cazar ahí mismo, caminó
            // hacia el sur —donde nacen sus cuatro guardias— y el Clérigo lo mató. Si no hay salida a la que se llegue, juega y lo
            // vuelve a probar al rato.
            if (HayGuardiaQueLoAtaca(sentidos.Mapa) && Environment.TickCount64 >= _proximaSalidaDelGuardia)
            {
                if (await SalirDelMapaAsync(transporte, sentidos, cerrada, ct)) continue;
                _proximaSalidaDelGuardia = Environment.TickCount64 + ReintentoDeSalidaDelGuardiaMs;
            }

            // HUIR DE LO QUE LE QUEDA GRANDE. Elegir bien a quién atacar no alcanza: las criaturas atacan
            // solas, y una que pega la mitad de la vida de un golpe mata a un personaje de nivel uno antes de
            // que alcance a tomar dos pociones. Un jugador que ve algo así se va, y esto es eso.
            int vidaTope = sentidos.VidaMaxima;
            int nivelAhora = sentidos.Nivel;
            _conQuePelea = (ArmaEnLaMano(sentidos), nivelAhora);
            // Con qué se cura, leído ANTES de buscar por lo mismo que la vida: la búsqueda tiene el modelo bajo su candado.
            bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
            bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
            // CON LA VIDA EN ROJO Y SIN POCIONES, TODO LO QUE PEGA ASUSTA: la pelea que queda no se gana sin nada que tomar. Medido en
            // `sano2`: las cinco muertes fueron de bots sin pociones contra el Águila, que para ellos es presa, con la vida ya en rojo.
            double saludAhora = sentidos.Salud;
            // Y LO QUE NO PUEDE LASTIMAR TAMBIÉN SE ESQUIVA: pegarle no sirve, y dejar que lo pique es tomar pociones para nada.
            var peligro = sentidos.MasCercano(ficha => EsPeligroso(ficha, vidaTope, nivelAhora, conPociones, curaVeneno)
                || AsustaConLaVidaEnRojo(_entorno.Datos.Npc(ficha), conPociones, saludAhora) || NoLaLastima(ficha));
            // Y LO DEL HUECO QUE YA LE PEGA EN UN MAPA INSEGURO, TAMBIÉN: ver `LoDelHuecoEsLaAmenaza`.
            if (sentidos.MasCercano(ficha => EsDelHueco(ficha, vidaTope, nivelAhora, conPociones, curaVeneno)) is { } delHueco
                && LoDelHuecoEsLaAmenaza(_entorno.Datos.IsPkMap(sentidos.Mapa), _entorno.Datos.Npc(delHueco.Quien.FichaNpc),
                                         delHueco.Pasos, peligro?.Pasos))
                peligro = delHueco;
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
                // DE UN GUARDIA DEL OTRO BANDO NO SE HUYE ALEJÁNDOSE: SE SALE DEL MAPA. El guardia (regla de la casa GuardiasAtacan)
                // persigue mientras uno siga en su mapa y tira hechizos de lejos, así que alejarse no lo saca nunca de encima, y
                // devolverle el golpe a uno de nivel 50 tampoco. Medido en `tienda4`: un Guerrero que arrancó la sesión en la
                // Cordillera del Norte (806), con cuatro guardias republicanos, se pasó 19 de sus 20 minutos huyendo adentro del
                // mapa y tomando pociones. Si no se pudo mover, la huida de siempre.
                if (NpcSystem.GuardiaVaPor(_entorno.Datos.Npc(amenaza.Quien.FichaNpc), _miBando, _miNivel, _entorno.Datos, _entorno.Ini)
                    && await SalirDelMapaAsync(transporte, sentidos, cerrada, ct)) continue;
                // DE LO QUE NO PUEDE LASTIMAR Y NO SE SACA DE ENCIMA, TAMBIÉN SE SALE DEL MAPA: devolverle el golpe no la mata nunca
                // (ver `NoLaVaAMatar`), y la persecución se suelta al cambiar de mapa. Y A ESE MAPA NO VUELVE POR UN RATO, como al
                // lugar lleno: la criatura se queda donde lo dejó de ver, que suele ser la entrada. Medido en `pelea_b2`: una Ladrona
                // echada del 262 por un Esqueleto guerrero volvió doce veces por la misma puerta a cazar los Zombies de ahí, y cada
                // vez la esperaba el mismo Esqueleto.
                // Y LO MISMO CON LO DEL HUECO, PERO SÓLO EN UN MAPA INSEGURO: ver `SeVaDelMapaPor`.
                // Y SI NO PUDO SALIR, NO LO REINTENTA EN LA VUELTA SIGUIENTE, como con el guardia: mientras tanto pelea o se aleja.
                // Medido en `hueco2`: un bot Pescador en (10,11) del 12, con un Goblin tapándole la esquina (9,7) por donde salía, lo
                // intentó cada doce segundos durante minutos sin moverse, devolviendo un golpe entre intento e intento.
                if (SeVaDelMapaPor(NoLaLastima(amenaza.Quien.FichaNpc),
                                   EsDelHueco(amenaza.Quien.FichaNpc, vidaTope, nivelAhora, conPociones, curaVeneno),
                                   _entorno.Datos.IsPkMap(sentidos.Mapa), _huidas.DeLaMismaCriatura)
                    && Environment.TickCount64 >= _proximaSalidaPorLaAmenaza)
                {
                    int deDonde = sentidos.Mapa;
                    if (await SalirDelMapaAsync(transporte, sentidos, cerrada, ct))
                    {
                        _llenoHasta[deDonde] = Environment.TickCount64 + LlenoMs;
                        // Y TAMPOCO SE LO VUELVE A CRUZAR DE VIAJE POR UN RATO: ver `NoVuelveACruzar`.
                        _huyoDe[deDonde] = Environment.TickCount64 + LlenoMs;
                        continue;
                    }
                    _proximaSalidaPorLaAmenaza = Environment.TickCount64 + ReintentoDeSalidaDelGuardiaMs;
                }
                // ACORRALADO SE PELEA. Si tres huidas seguidas no lo movieron de la baldosa, insistir es quedarse quieto recibiendo:
                // medido en `normal1`, un Mago de nivel 1 con pociones se pasó veinticinco de treinta minutos huyendo sin moverse en
                // dos bolsillos de la cuna, uno con un Escorpión pegado y otro junto al spawn de una Serpiente con un Lobo a dos
                // baldosas, y se tomó 159 pociones ahí. A lo que tiene pegado se le devuelve el golpe: mirar y pegar, como en
                // `CazarAsync`, sin llamarlo, que con la vida baja vuelve a `AlejarseAsync`.
                if ((EstaAcorralado(_huidas.SinMoverse, _huidas.QueNoLoSacaron) || NoSeLoSacaDeEncima(_huidas.DeLaMismaCriatura))
                    && TieneFuelleParaPelear(sentidos)
                    && sentidos.MasCercano(f => LoEstaAtacando(_entorno.Datos.Npc(f), 1) && !NoLaLastima(f)) is { Pasos: <= 1 } pegado)
                {
                    var (px, py) = sentidos.Posicion;
                    byte haciaElQuePega = SentidosDelBot.RumboHacia(pegado.Quien.X - px, pegado.Quien.Y - py);
                    if (!transporte.Mandar(Paquete(ClientPacketId.ChangeHeading, w => w.WriteByte(haciaElQuePega)))) return;
                    if (!transporte.Mandar(Paquete(ClientPacketId.attack, _ => { }))) return;
                    await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                    continue;
                }
                // LA SERIE: cuántas veces seguidas huyó de ESTA criatura. La cortan el cambio de amenaza y el reloj, y NADA más:
                // el respiro que deja huir es parte de la persecución.
                _huidas.Huyo(amenaza.Quien.CharIndex, Environment.TickCount64, VentanaDeHuidasMs);

                var antesDeHuir = sentidos.Posicion;
                if (!await AlejarseAsync(transporte, sentidos, amenaza.Quien, cerrada, ct)) return;
                _huidas.SinMoverse = sentidos.Posicion == antesDeHuir ? _huidas.SinMoverse + 1 : 0;
                // Y SI SE MOVIÓ PERO LA SIGUE TENIENDO CERCA, tampoco le sirvió: una criatura camina lo mismo que una persona,
                // así que a la que te persigue no se le gana la carrera. Se cuenta aparte del rincón porque son dos problemas
                // distintos con la misma salida: dejar de correr. Ver `LaHuidaNoSirvio` para por qué el tope es el radio de susto
                // y no la baldosa de al lado.
                var despues = sentidos.Donde(amenaza.Quien.CharIndex);
                _huidas.QueNoLoSacaron = LaHuidaNoSirvio(despues is { } sigueAhi
                        ? Math.Abs(sigueAhi.X - sentidos.Posicion.X) + Math.Abs(sigueAhi.Y - sentidos.Posicion.Y) : -1)
                    ? _huidas.QueNoLoSacaron + 1 : 0;
                continue;
            }
            _huidas.SalioDelPeligro();

            // LO QUE YA LE PEGA SE LO SACA DE ENCIMA, aunque haya algo peligroso a la vista, la vida no esté para empezar una pelea o
            // no le den ganas: esas reglas son para ELEGIR a quién atacar, no para dejarse picar. Medido en `normal1`: un Minero de
            // nivel 1 con 16 de vida se tomó 67 pociones seguidas en la llegada de la cuna sin cazar ni huir, y terminó la media hora
            // sin matar nada.
            if (TieneFuelleParaPelear(sentidos)
                && sentidos.MasCercano(f => EsPresa(f, vidaTope, nivelAhora, conPociones, curaVeneno) && LoEstaAtacando(_entorno.Datos.Npc(f), 1)) is { Pasos: <= 1 } picando)
            {
                if (!await CazarAsync(transporte, sentidos, picando.Quien.CharIndex, cerrada, ct)) return;
                continue;
            }

            if (Equiparse(transporte, sentidos)) { await Task.Delay(_rng.Next(400, 900), ct); continue; }
            if (await CurarVenenoAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await SanarseAsync(transporte, sentidos, cerrada, ct)) continue;
            // AL LLEGAR AL ÚLTIMO NIVEL DE NOVATO SE SALE DE COMPRAS YA: ver `EmpiezaAAbastecerse`.
            if (EmpiezaAAbastecerse(_nivelParaLoNovato, sentidos.Nivel)) _proximaCompra = 0;
            _nivelParaLoNovato = sentidos.Nivel;
            if (RepartirPuntos(transporte, sentidos)) { await Task.Delay(_rng.Next(900, 2_500), ct); continue; }
            if (await AprenderHechizosAsync(transporte, sentidos, ct)) continue;
            if (await LevantarBotinAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await IrDeComprasAsync(transporte, sentidos, cerrada, ct)) continue;
            if (await MeditarAsync(transporte, sentidos, ct)) continue;
            if (await FabricarAsync(transporte, sentidos, cerrada, ct)) continue;
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
            // Y LA QUE NO SE ALCANZA NO SE ELIGE DE NUEVO POR UN RATO (ver `NoSeLlegaA`).
            var salvo = SiguenSinCamino(_sinCamino, Environment.TickCount64);
            var presa = sentidos.MasCercano(ficha => EsPresa(ficha, vidaMaxima, nivelAhora, conPociones, curaVeneno) && PagaOro(ficha), salvo);
            // LO QUE PAGA SE VA A BUSCAR MÁS LEJOS. El radio del temperamento decide si vale la pena moverse por
            // cualquier cosa; por lo que da plata, cualquiera camina unas baldosas más. Medido con la sonda: una
            // Clériga tranquila —radio 6— con un escorpión a la vista a nueve pasos, pegándole al murciélago de al
            // lado, que no suelta nada.
            if (presa is not { } rica || rica.Pasos > RadioDeCaza)
                presa = sentidos.MasCercano(ficha => EsPresa(ficha, vidaMaxima, nivelAhora, conPociones, curaVeneno), salvo);
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
                // Y AL SUBIR DE NIVEL SE REVISA EL COTO AUNQUE TENGA PRESA A LA VISTA. La paciencia de
                // `BuscarDondeCazarAsync` es para "no veo nada", y en la cuna siempre hay algo que cazar: nunca se
                // agotaba, así que el bot no se preguntaba jamás si eso todavía le rendía. Subir de nivel es
                // justamente cuando una persona mira alrededor y decide si se queda.
                if (HayQueRevisarElCoto(_nivelDelCoto, nivelAhora))
                {
                    _nivelDelCoto = nivelAhora;
                    _ultimaPresaQuePaga = 0;
                    if (await BuscarDondeCazarAsync(transporte, sentidos, vidaMaxima, nivelAhora, true, cerrada, ct)) continue;
                }
                if (PagaOro(p.Quien.FichaNpc)) _ultimaPresaQuePaga = Environment.TickCount64;
                else if (await BuscarDondeCazarAsync(transporte, sentidos, vidaMaxima, nivelAhora, true, cerrada, ct)) continue;
                if (!await CazarAsync(transporte, sentidos, p.Quien.CharIndex, cerrada, ct)) return;
                continue;
            }

            if (MirarALaGente(transporte, sentidos)) { await Task.Delay(_rng.Next(1_200, 3_500), ct); continue; }

            // ACÁ NO HAY NADA QUE CAZAR, ASÍ QUE SE MUDA. Un paseo que pisa una puerta cambia de mapa, y el
            // bot toma el mapa nuevo como su zona; si cae en un pueblo —que no tiene criaturas— se queda
            // paseando ahí para siempre. Medido: cuatro de ocho bots parados en Ullathorpe y en el 457 con la
            // experiencia congelada durante una hora entera, mientras los que se quedaron en la cuna subían
            // de nivel. Un jugador que se cansa de dar vueltas por el pueblo agarra y sale a cazar.
            if (await BuscarDondeCazarAsync(transporte, sentidos, vidaMaxima, nivelAhora,
                                            VeUnaPresaQueIriaABuscar(presa?.Pasos, alcance),
                                            cerrada, ct)) continue;

            if (!await PasearAsync(transporte, sentidos, cerrada, ct, presa?.Quien)) return;
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
        EsPeligrosoParaElBot(_entorno.Datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno, _miBando,
                             _entorno.Datos, _entorno.Ini);

    /// <summary>
    /// <see cref="EsPeligrosoPara"/> y, además, EL GUARDIA DEL OTRO BANDO (regla de la casa `GuardiasAtacan`): no es hostil,
    /// así que la regla de las criaturas no lo ve, y a un bot que entra a una ciudad enemiga lo mata. Es peligro como
    /// cualquier otro: se lo esquiva, se huye de él y su ciudad no sirve de coto (<see cref="MapaSeguroPara"/> hace lo
    /// mismo con el refugio, el mercado y el templo).
    /// </summary>
    public static bool EsPeligrosoParaElBot(NpcInfo? n, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno,
                                            byte bando, GameData datos, ServerIni? ini) =>
        EsPeligrosoPara(n, vidaMaxima, nivel, conPociones, curaVeneno) || NpcSystem.GuardiaVaPor(n, bando, nivel, datos, ini);

    private bool EsGuardiaQueVaPorEl(short ficha, int nivel) =>
        NpcSystem.GuardiaVaPor(_entorno.Datos.Npc(ficha), _miBando, nivel, _entorno.Datos, _entorno.Ini);

    /// <summary>¿Nace en ese mapa un guardia que va por alguien de este bando y nivel? Las ciudades del otro bando.</summary>
    public static bool HayGuardiaQueVaPor(int mapa, byte bando, int nivel, GameData datos, ServerIni? ini) =>
        datos.MapSpawns.TryGetValue(mapa, out var nacen)
        && nacen.Any(sp => NpcSystem.GuardiaVaPor(datos.Npc((short)sp.NpcIndex), bando, nivel, datos, ini));

    private bool HayGuardiaQueLoAtaca(int mapa) => HayGuardiaQueVaPor(mapa, _miBando, _miNivel, _entorno.Datos, _entorno.Ini);

    /// <summary>¿Asusta esta criatura a quien no tiene pociones y anda con la vida bajo el umbral de la poción? Toda la que pega.</summary>
    public static bool AsustaConLaVidaEnRojo(NpcInfo? n, bool conPociones, double salud) => !conPociones && salud < TomaPocion && n is { Hostile: true, MaxHit: > 0 };

    /// <summary>
    /// CÓMO HUYE de lo que asusta. Con pociones, alejándose, salvo contra lo que lo mata de un golpe. Sin pociones: con la vida en rojo,
    /// el sacerdote si está a mano; contra lo que lo mata de un golpe, o en rojo sin sacerdote, la runa, sólo fuera de un mapa pk y si
    /// no la acaba de usar. La runa va antes que el sacerdote contra un golpe que mata: el sacerdote cura hasta la vida máxima y ese
    /// golpe la alcanza. Sin runa lista y en rojo, el sacerdote igual, que sube la vida por encima del golpe más flojo; si no, alejándose.
    ///
    /// <para>CON POCIONES TAMBIÉN, CONTRA UN GOLPE QUE MATA: la poción cura lo que ya pegó, no el golpe que viene. Medido en `normal3`:
    /// bots de nivel 1 con pociones y 16 y 18 de vida murieron huyendo del Lobo con la vida llena, y en `normal1` otros dos igual.</para>
    /// </summary>
    public static Huida ComoHuye(bool conPociones, double salud, bool unGolpeLoMata, bool sacerdoteAMano, bool mapaSinPk, bool runaLista)
    {
        if (conPociones && !unGolpeLoMata) return Huida.Alejarse;
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
        // CON POCIONES QUEDA UN HUECO, Y ESTAS DOS REGLAS NO LO TAPAN: lo que no es presa pero tampoco asusta no se sale
        // a buscar ni se esquiva de antemano. Medido contra la data: son 3 criaturas de 111 a nivel 1 —Escorpión, Sierpe,
        // Sertlac—, 5 a nivel 10 —Zombie, Esqueleto Guerrero y los Fuegos elementales, que caen por la experiencia y no
        // por el golpe— y 8 a nivel 20.
        //
        // Taparlo acá es una decisión de balance, no un arreglo: ampliar `EsPeligrosoPara` le hace perder presas
        // legítimas y ampliar `EsPresaPara` lo manda a peleas largas contra cosas como el Zombie, que pega 30. Las dos
        // rompen un test que documenta la decisión contraria. DECIDIDO POR EL AUTOR (14/09): estas dos quedan como están.
        // PERO EL HUECO MATA, y se atiende aparte (ver `EsDelHuecoPara`, decidido por el autor el 29/09).
        return !conPociones && n.MaxHit > 0 && !EsPresaPara(n, vidaMaxima, nivel, conPociones, curaVeneno);
    }

    /// <summary>
    /// ¿ES DEL HUECO? Lo hostil que pega y que para él no es presa ni peligro (<see cref="EsPresaPara"/>,
    /// <see cref="EsPeligrosoPara"/>): no lo sale a buscar ni lo esquiva de antemano. Pero pega, y camina hacia el que ve.
    ///
    /// <para>Medido en `ropa1` (28/09, 18 bots desde el nivel 1): tres de las cinco muertes fueron del hueco, contra los Zombies
    /// del 13 (golpe de 25 a 30, 320 de vida) y con pociones encima: un Carpintero de 122 de vida, dos veces, y un Mago de 107.
    /// Con el Zombie pegándole no se le iba ni le devolvía el golpe: seguía con lo suyo tomando pociones —una de las veces,
    /// cuarenta y cinco— y se moría igual.</para>
    ///
    /// <para>DECIDIDO POR EL AUTOR (29/09): en un mapa inseguro, lo del hueco que ya le pega lo hace huir
    /// (<see cref="LoDelHuecoEsLaAmenaza"/>) y, si no se lo saca de encima, irse del mapa (<see cref="SeVaDelMapaPor"/>), al vecino
    /// donde nace menos de lo que lo puede matar (<see cref="CuantasLoPuedenMatar"/>); en uno seguro, nada cambia. Lo que se elige
    /// de antemano —dónde cazar, por dónde viajar— queda como estaba:
    /// los de `ropa1` no eligieron el 13 para cazar, lo cruzaban de viaje a Ullathorpe y no tenían otro camino; y para un bot de
    /// 55 a 89 de vida el Lobo también es del hueco, así que esquivarlo de antemano le cerraba los mapas de Lobos y la cuna.</para>
    /// </summary>
    public static bool EsDelHuecoPara(NpcInfo? n, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno) =>
        n is { Hostile: true, MaxHit: > 0 } && !EsPresaPara(n, vidaMaxima, nivel, conPociones, curaVeneno)
        && !EsPeligrosoPara(n, vidaMaxima, nivel, conPociones, curaVeneno);

    /// <summary>La regla de <see cref="EsDelHuecoPara"/> para este bot.</summary>
    private bool EsDelHueco(short ficha, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno) =>
        EsDelHuecoPara(_entorno.Datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno);

    /// <summary>
    /// ¿LO DEL HUECO ES LA AMENAZA DE LA QUE HUYE? Sólo en un mapa inseguro, cuando ya le pega (<see cref="LoEstaAtacando"/>) y
    /// está más cerca que lo peligroso. En uno seguro lo del hueco se trata como siempre: ni se huye ni se pelea.
    ///
    /// <para>Medido en `hueco3` (29/09), cuando también huía de él en un mapa seguro: un Paladín de 79 de vida pasó cuarenta
    /// minutos entre el nivel 9 y el 10 en la cuna, con setenta y un episodios del Lobo —del hueco de 55 a 89 de vida—, huyendo y
    /// devolviéndole el golpe en vez de cazar. En la cuna los Lobos del hueco no habían matado a nadie (`ropa1`); los que matan
    /// son los Zombies del 13, que es inseguro. DECIDIDO POR EL AUTOR (29/09).</para>
    /// </summary>
    public static bool LoDelHuecoEsLaAmenaza(bool mapaInseguro, NpcInfo? delHueco, int pasos, int? pasosDelPeligro) =>
        mapaInseguro && LoEstaAtacando(delHueco, pasos) && (pasosDelPeligro is not { } otro || pasos < otro);

    /// <summary>
    /// CUÁNTAS DE LAS QUE NACEN EN ESE MAPA LO PUEDEN MATAR: lo peligroso (<see cref="EsPeligrosoParaElBot"/>) y lo del hueco
    /// (<see cref="EsDelHuecoPara"/>). Es con lo que se elige adónde salir huyendo de un mapa (<see cref="VecinosDelMenosPeligroso"/>):
    /// escapando de un Zombie no se sale a otro mapa de Zombies.
    /// </summary>
    public static int CuantasLoPuedenMatar(GameData datos, ServerIni? ini, byte bando, int mapa, int vida, int nivel, bool conPociones,
                                           bool curaVeneno) =>
        datos.MapSpawns.TryGetValue(mapa, out var nacen)
            ? nacen.Select(sp => datos.Npc((short)sp.NpcIndex))
                   .Count(n => EsPeligrosoParaElBot(n, vida, nivel, conPociones, curaVeneno, bando, datos, ini)
                               || EsDelHuecoPara(n, vida, nivel, conPociones, curaVeneno))
            : 0;

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
        //
        // Y EL QUE PELEA A GOLPES NO ENTRA ACÁ, que parece el caso simétrico y no lo es. Lo que las otras dos
        // ramas rompen es un CÍRCULO: sin la skill mínima el servidor no deja castear, y sin castear la skill no
        // sube nunca. Armas no tiene mínimo — con la skill en cero el acierto ya es
        // `clamp(50 + (0 − evasión) × 0,4; 10; 90)`, o sea 45% contra el Murciélago — así que el bot pega, acierta
        // y la entrena solo. Medido contra los .chr de una corrida: `RaiseChancePercent` es 100, cada acierto
        // suma un punto, y el uso lleva Armas al tope del nivel en seis golpes a nivel 1 y en cincuenta a nivel
        // 10 — menos de un minuto. Los puntos libres, en cambio, quedan POR DEBAJO de ese tope a partir del nivel
        // 5 (23 contra 25 a nivel 10): darle Armas en vez del azar cambia el acierto en UN punto porcentual a
        // nivel 1 y en nada después. Se probó y se sacó.
        byte suya = SkillPropia(sentidos);
        byte propia = suya != 0 ? suya
                    : sentidos.UsaMana ? (byte)GameLimits.SkillMagia
                    : (byte)0;
        // Y EL QUE CASTEA Y FABRICA —el Druida— pone primero en Magia lo que pide su hechizo de nacimiento: con los
        // puntos en la Alquimia desde el principio no casteaba, que es el mismo defecto de arriba por el otro lado.
        // Después, a la de fabricar; la Magia sigue subiendo sola con cada hechizo que tira.
        if (SuFabricacion is not null && sentidos.UsaMana
            && LeFaltaMagiaParaCastear(sentidos.Skill(GameLimits.SkillMagia), MagiaDelHechizoDeNacimiento))
            propia = (byte)GameLimits.SkillMagia;
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
        if (salio)
        {
            sentidos.GastarPuntos(gastados);
            // LO QUE LE RECHAZARON SE VUELVE A PROBAR. Ver `VuelveAProbarLosHechizos`.
            if (propia > 0 && VuelveAProbarLosHechizos(propia)) _noLoPuedeLanzar.Clear();
        }
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

        bool faltanPociones = sentidos.Suma(PocionPropia) < PocionesQueQuiere;
        // LA COMIDA Y LA BEBIDA SE REPONEN ANTES DE QUE SE ACABEN. El kit de novato trae cien de cada una y el bot
        // nunca compraba más: después de horas de juego se le terminaban, el hambre y la sed llegaban a cero, y
        // con cualquiera de las dos en cero el servidor deja de regenerar. Sin energía `CazarAsync` se aleja en
        // cuanto empieza, así que no juntaba oro para comer: un espiral cerrado. Medido: un Gladiador de nivel 7
        // con la mochila sin una manzana ni un agua, la energía en 20 de 130, y cada caza durando un segundo.
        // EL ANTÍDOTO ES UNA NECESIDAD PROPIA, y sin esto el bot no lo conseguía NUNCA. `Sirve` elige al
        // mercader por la necesidad, y el único que vende antídoto —el Alquimista— sólo tiene pociones y dos
        // herramientas: ni comida ni bebida. Así que sólo lo elegía yendo por pociones, y eso no pasa hasta
        // nivel 15 porque el kit de novato trae mil quinientas. Medido: treinta mostradores en treinta y cinco
        // minutos y ninguno vendía antídoto.
        bool faltaAntidoto = sentidos.Suma(AntidotoPropio) < AntidotosQueQuiere;
        bool faltaComida = sentidos.Suma(ComidaPropia) < ComidaMinima;
        bool faltaBebida = sentidos.Suma(BebidaPropia) < BebidaMinima;
        bool hayQueVender = sentidos.EnLaMochila(obj => EsBotin(sentidos, obj)) is not null;
        short laQueFalta = HerramientaQueLeFalta(sentidos);
        bool faltaHerramienta = laQueFalta != 0;
        // EL ARMA PARA EL CAMINO: ver `LeFaltaElArmaDelCamino`.
        bool faltaArma = LeFaltaElArmaDelCamino(sentidos.Nivel, LoMejorQueTiene(sentidos, EsArma));
        // Y LA ROPA DEL CAMINO: ver `LeFaltaLaRopaDelCamino`.
        bool faltaRopa = LeFaltaLaRopaDelCamino(sentidos.Nivel, TieneRopaPropia(sentidos));

        // EL PRECIO SALE DE LA DATA, no de un número a mano: hoy las tres herramientas valen 200, y el día que
        // la data cambie esto cambia solo.
        int cuestaLaHerramienta = laQueFalta > 0
            ? (_entorno.Datos.Obj(laQueFalta)?.Value ?? int.MaxValue) : int.MaxValue;

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
        // La de FABRICAR no pide lugar: se fabrica parado donde sea (el servidor no mira el mapa).
        bool vaPorLaHerramienta = faltaHerramienta && sentidos.Oro >= cuestaLaHerramienta
                                  && (laQueFalta != HerramientaDeSuOficio || HayDondeTrabajarSeguro(sentidos));
        // CON MENOS DE LO QUE CUESTA UN AGUA EN CAJA NO SE SALE A COMPRAR, sólo a vender: el viaje es para volver
        // con las manos vacías. Medido: un bot con una y dos monedas, tres viajes seguidos al mostrador de comidas
        // sin llevarse nada.
        bool enApuro = sentidos.Oro < MonedasDeApuro;
        bool vaPorPociones = faltanPociones && !enApuro;
        // Y CON LO QUE SOBRA, EQUIPO MEJOR. Es el único gasto que no es de supervivencia, así que va con el
        // sobrante y sólo cuando hay bastante como para que el viaje valga la pena.
        bool vaPorEquipo = !enApuro && PlataParaElEquipo(sentidos) >= OroParaMirarEquipo;
        // Y EL QUE CASTEA APRENDE HECHIZOS NUEVOS, que es su forma de mejorar: el arma no le sirve de mucho.
        bool vaPorHechizo = !enApuro && sentidos.UsaMana && PlataParaElEquipo(sentidos) >= OroParaMirarHechizos;
        // No manda al bot lejos: replicando `DondeHay` contra los mapas, el Alquimista es el mercader
        // alcanzable MÁS CERCANO desde la cuna (un salto) y desde el mapa 1 y el 208 (dos).
        bool vaPorAntidoto = !enApuro && ValeSalirPorElAntidoto(faltaAntidoto, sentidos.Oro, CuestaElAntidoto);
        bool vaPorComida = faltaComida && !enApuro;
        bool vaPorBebida = faltaBebida && !enApuro;
        // Y EL ARMA, SI HAY QUIEN LE VENDA UNA QUE SIRVA: ver `PrimeroElArma`. Alcanza con el MAPA, como con el pergamino
        // (ver `HayDondeComprarElPergamino`): los herreros están adentro de edificios.
        double sobraParaElArma = PlataParaElEquipo(sentidos);
        bool VendeElArma(int ficha) =>
            _entorno.Datos.Npc(ficha) is { StockItems: { Count: > 0 } suStock }
            && suStock.Any(s => ArmaQueLeSirve(sentidos, (short)s.ObjIndex, sobraParaElArma));
        bool vaPorElArma = faltaArma && !enApuro;
        bool hayArmaAlAlcance = vaPorElArma && ArmaAlAlcance(sentidos, VendeElArma, MapaAtiende);
        bool VendeRopa(int ficha) =>
            _entorno.Datos.Npc(ficha) is { StockItems: { Count: > 0 } suStock }
            && suStock.Any(s => RopaQueLeSirve(sentidos, (short)s.ObjIndex, sobraParaElArma));
        bool hayRopaAlAlcance = faltaRopa && !enApuro && RopaAlAlcance(sentidos, VendeRopa, MapaAtiende);
        if (!hayQueVender && !vaPorLaHerramienta && !vaPorPociones && !vaPorComida && !vaPorBebida && !vaPorEquipo
            && !vaPorHechizo && !vaPorAntidoto && !hayArmaAlAlcance && !hayRopaAlAlcance) return false;

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
        short quiero = vaPorLaHerramienta && !vaPorComida && !vaPorBebida ? laQueFalta : (short)0;
        bool soloLaRopa = PrimeroLaRopa(hayRopaAlAlcance, vaPorComida, vaPorBebida, vaPorLaHerramienta);
        bool soloElArma = PrimeroElArma(vaPorElArma, hayArmaAlAlcance, vaPorComida, vaPorBebida, vaPorLaHerramienta,
                                        vaPorLaRopa: hayRopaAlAlcance);
        // Y SI VA A VENDER, QUE SEA A ALGUIEN QUE LE COMPRE. Cada mercader acepta UN tipo de objeto y rechaza
        // el resto en silencio (`TipoItems`, VB6 Comercio.bas:123), así que caer en el gremio equivocado es un
        // viaje entero para volver con lo mismo: el pollo se lo compra el de comidas, no el herrero. Eso lo
        // sabe cualquiera que jugó una tarde, y sale de la data.
        // SI NO LE FALTA NADA ESENCIAL, EL VIAJE ES AL QUE VENDE LO QUE BUSCA. `Sirve` acepta al mercader más
        // cercano que atienda CUALQUIERA de las necesidades, y el de comida siempre está más cerca —en la cuna hay
        // uno y herrero no hay ninguno—, así que al herrero no se llegaba nunca. Medido en `acelerada1`: una
        // Pescadora de nivel 11 con 4755 monedas seguía con el Martillo de Piedra del kit (golpe 5) mientras el
        // Herrero General de su mapa vendía una Espada Larga (golpe 8) a 350.
        bool soloGustos = SoloLeFaltanGustos(faltaComida, faltaBebida, faltanPociones, faltaHerramienta, faltaRopa: hayRopaAlAlcance);
        // Y EL MAPA TIENE QUE ATENDER: que su mercader le venda a la facción del bot, con la MISMA regla que el server
        // (`ElMercaderLeVende`: la del Server.ini, que por defecto es la del original — un ciudadano sólo compra donde el
        // `battleMode` es 2 o 3). Y sin guardias que vayan por él: la ciudad del otro bando atiende, pero ahí lo matan.
        bool MapaAtiende(int mapa) => ElMercaderLeVende(_entorno.Ini, _entorno.Datos.MapBattleMode(mapa), _miBando) && !HayGuardiaQueLoAtaca(mapa);
        // Una sola vez: `Sirve` corre por cada mercader del mapa y esto recorre la mochila entera.
        double sobraParaEquipo = PlataParaElEquipo(sentidos);

        // EL QUE CASTEA VA PRIMERO POR EL PERGAMINO. `Sirve` aceptaba al mercader que atendiera CUALQUIERA de los dos
        // gustos, y gana siempre el más cercano —el herrero—, así que a los siete que venden pergaminos no se llegaba
        // nunca: ningún caster de ninguna corrida pasó del hechizo con el que nace. Para un mago el hechizo ES su arma.
        // Medido en `seguir4`: Fabia, maga con quince mil monedas y `hechizo=True`, viajando al Herrero Rinkel.
        bool VendePergamino(int ficha) =>
            _entorno.Datos.Npc(ficha) is { StockItems: { Count: > 0 } suStock }
            && suStock.Any(s => ValeElViajePorElPergamino(sentidos, (short)s.ObjIndex, sobraParaEquipo));
        bool hayUnoAlAlcance = vaPorHechizo && soloGustos && HayDondeComprarElPergamino(
            _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, VendePergamino, MapaAtiende,
                                    Pisable(sentidos)) is not null,
            _entorno.Mapas.MapaMasCercano(sentidos.Mapa, sentidos.Posicion,
                m => MapaAtiende(m) && _entorno.Datos.MapSpawns.TryGetValue(m, out var deEse)
                     && deEse.Any(q => VendePergamino(q.NpcIndex)),
                Pisable(sentidos)) is not null);
        bool soloElPergamino = PrimeroElPergamino(vaPorHechizo, soloGustos, hayUnoAlAlcance);

        // Y VENDER NO ALCANZA PARA ELEGIRLO SI LE FALTA LO ESENCIAL: ver `VenderAlcanzaParaElegirlo`. Una sola
        // vez, igual que `sobraParaEquipo`: `Sirve` corre por cada mercader del mapa.
        bool venderAlcanza = VenderAlcanzaParaElegirlo(faltaComida || faltaBebida, sentidos.Oro,
                                                       ReservaPara(sentidos, comida: true, bebida: true));

        bool Sirve(int ficha)
        {
            if (_entorno.Datos.Npc(ficha) is not { StockItems: { Count: > 0 } stock } mercader) return false;
            if (quiero != 0) return stock.Any(s => s.ObjIndex == quiero);
            if (soloLaRopa) return VendeRopa(ficha);
            if (soloElArma) return VendeElArma(ficha);
            if (soloElPergamino) return VendePergamino(ficha);
            if (soloGustos)
                return (vaPorEquipo && stock.Any(s => ValeElViajePorEso(sentidos, (short)s.ObjIndex, sobraParaEquipo)))
                    || (vaPorHechizo && stock.Any(s => ValeElViajePorElPergamino(sentidos, (short)s.ObjIndex, sobraParaEquipo)))
                    || (hayQueVender && sentidos.EnLaMochila(obj => LeVendeA(sentidos, mercader.TipoItems, obj)) is not null);
            if (primeroLaBebida) return stock.Any(s => EsBebida((short)s.ObjIndex));
            if (primeroLaComida) return stock.Any(s => EsComida((short)s.ObjIndex));
            // CADA NECESIDAD, A QUIEN LA ATIENDE. Antes, sin nada que vender, servía cualquier mercader, y así se
            // podía viajar a buscar pociones hasta el herrero. Ahora el que va por comida busca a quien la vende,
            // y lo mismo la bebida y las pociones. Lo que hace falta para vivir, primero.
            if (vaPorComida && stock.Any(s => EsComida((short)s.ObjIndex))) return true;
            if (vaPorBebida && stock.Any(s => EsBebida((short)s.ObjIndex))) return true;
            if (vaPorPociones && stock.Any(s => EsPocionDeVida((short)s.ObjIndex))) return true;
            if (vaPorAntidoto && stock.Any(s => EsPocionQueCuraElVeneno((short)s.ObjIndex))) return true;
            if (hayRopaAlAlcance && VendeRopa(ficha)) return true;
            if (hayArmaAlAlcance && VendeElArma(ficha)) return true;
            if (vaPorEquipo && stock.Any(s => ValeElViajePorEso(sentidos, (short)s.ObjIndex, sobraParaEquipo))) return true;
            if (vaPorHechizo && stock.Any(s => ValeElViajePorElPergamino(sentidos, (short)s.ObjIndex, sobraParaEquipo))) return true;
            return venderAlcanza
                   && hayQueVender && sentidos.EnLaMochila(obj => LeVendeA(sentidos, mercader.TipoItems, obj)) is not null;
        }

        // Y EL MAPA TIENE QUE ATENDER. Un mercader no le vende a un ciudadano donde el `battleMode` del mapa no
        // es 2 ni 3 (VB6 Protocol.bas:5205, portado igual), y TODOS los personajes nacen ciudadanos. Sin este
        // filtro el viaje terminaba siempre en el mapa vecino —que tiene los tres vendedores de herramientas y
        // battleMode 1— donde nadie lo atendía. De los 863 mapas del mundo, 55 tienen battleMode 2 o 3 y 23 de
        // ésos tienen mercader: destinos sobran.
        var tienda = MapaAtiende(sentidos.Mapa) ? sentidos.MasCercano(ficha => Sirve(ficha)) : null;
        // NO HAY NINGUNO A LA VISTA: se viaja. Se intenta DOS veces porque el primer destino se elige sin
        // conocer el mapa de llegada —desde otro mapa no se puede saber por dónde se va a entrar— y muchos
        // mercaderes están adentro de un edificio al que no se llega caminando: medido, en el mapa donde nacen
        // los bots NINGUNO de los dos es alcanzable, y en el de al lado lo son tres de once. Ya en el mapa, la
        // segunda búsqueda sí exige camino y elige uno al que se llega.
        bool abrioUnaPuerta = false, llegoAlPueblo = false;
        for (int intento = 0; intento < 3 && (tienda is null || tienda.Value.Pasos > RadioDeCompras); intento++)
        {
            if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, Sirve, MapaAtiende, Pisable(sentidos)) is not { } lugar)
            {
                // SIN TIENDA A LA QUE SE LLEGUE CAMINANDO, LA RUNA. Hay mapas a los que se entra y de los que no se
                // sale a pie: medido, desde el mapa 62 no hay a pie ningún vendedor de piquetes ni de hachas, y la
                // única salida hacia la cuna está en un cuarto cerrado. Después de un par de intentos, a casa, igual
                // que con el coto; el contador es otro porque cazar bien no arregla no poder comprar.
                // Sólo si hay algo que COMPRAR: nadie vuelve a casa con la runa para ir a vender.
                // ANTES DE DARSE POR VENCIDO, LA PUERTA. Un mercader al que no se llega caminando casi siempre está
                // adentro de un edificio, y el bot no abría una sola puerta en todo el juego: seis de los ocho
                // vendedores de pergaminos del mapa 20 están detrás de sus once puertas cerradas, todas sin llave.
                // Es lo que hace cualquiera que no encuentra un negocio abierto: probar la puerta de al lado.
                // Y SÓLO DONDE ATIENDEN: ver `ValeBuscarLaPuertaAca`. Buscar la puerta en un mapa donde nadie le
                // vende es abrir el ropero más cercano, y era lo que pasaba en las ocho vueltas medidas.
                bool hayQuienLeVendaAca = _entorno.Datos.MapSpawns.TryGetValue(sentidos.Mapa, out var paraLaPuerta)
                                          && paraLaPuerta.Any(q => Sirve(q.NpcIndex));
                if (TocaProbarLosCaminosCaros(intento, abrioUnaPuerta, llegoAlPueblo)
                    && ValeBuscarLaPuertaAca(MapaAtiende(sentidos.Mapa), hayQuienLeVendaAca)
                    && await AbrirUnaPuertaAsync(transporte, sentidos, Sirve, cerrada, ct)) { abrioUnaPuerta = true; continue; }

                // Y SI YA ESTÁ EN EL MAPA DEL MERCADER, SE CAMINA HACIA ÉL AUNQUE NO SE LLEGUE: el servidor sólo
                // manda los objetos del suelo que entran en la vista, así que la puerta del edificio recién aparece
                // de cerca. Ver `ValeAcercarseAunqueNoLlegue`.
                if (TocaProbarLosCaminosCaros(intento, abrioUnaPuerta, llegoAlPueblo)
                    && _entorno.Datos.MapSpawns.TryGetValue(sentidos.Mapa, out var losDeAca)
                    && losDeAca.FirstOrDefault(q => Sirve(q.NpcIndex)) is { NpcIndex: > 0 } elMercader
                    && ValeAcercarseAunqueNoLlegue(MapaAtiende(sentidos.Mapa), false)
                    && await IrHastaAsync(transporte, sentidos, sentidos.Mapa, (byte)elMercader.X, (byte)elMercader.Y,
                                          cerrada, ct, ParaQueLaPuertaEntreEnVista)
                    && await AbrirUnaPuertaAsync(transporte, sentidos, Sirve, cerrada, ct)) continue;

                // Y SI LA PUERTA NO ESTÁ ACÁ, SE VIAJA AL PUEBLO IGUAL. Buscar la puerta donde el bot está parado
                // sólo sirve si ya llegó: en el bosque no hay ninguna. Con el mercader adentro de un edificio,
                // `DondeHay` nunca devuelve nada y el viaje no salía — pero el MAPA sí se encuentra. Ver
                // `ValeViajarAlPueblo`.
                // Y SI A CINCO MAPAS NO HAY NINGUNO, MÁS LEJOS: ver `PuebloConMercader`.
                var conMercader = PuebloConMercader(_entorno.Mapas, sentidos.Mapa, sentidos.Posicion,
                    m => MapaAtiende(m) && _entorno.Datos.MapSpawns.TryGetValue(m, out var sp) && sp.Any(q => Sirve(q.NpcIndex)),
                    Pisable(sentidos), PasaSinPeligro(sentidos, NingunMapa));
                if (TocaProbarLosCaminosCaros(intento, abrioUnaPuerta, llegoAlPueblo) && conMercader is { } pueblo
                    && ValeViajarAlPueblo(false, pueblo.Mapa != sentidos.Mapa)
                    && await IrHastaAsync(transporte, sentidos, pueblo.Mapa, pueblo.X, pueblo.Y, cerrada, ct, ToleranciaDelCoto,
                                          topeDeMapas: Math.Max(Navegante.TopeDeMapas, pueblo.Saltos)))
                { llegoAlPueblo = true; continue; }
                bool vaAComprar = HayAlgoQueComprar(vaPorLaHerramienta, vaPorComida, vaPorBebida, vaPorPociones,
                                                    vaPorAntidoto,
                                                    vaPorEquipo || hayArmaAlAlcance, vaPorHechizo);
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
        // Y LO MISMO CON EL ARMA DEL CAMINO, si hay quien se la venda: ver `LeFaltaElArmaDelCamino`. El nivel 15 dura poco, y con
        // el descanso largo después de la comida el viaje al herrero llegaba ya en el 16. Medido en `oficios22`: los tres bots que
        // pasaron el 15 fueron del coto a la cuna por la comida y de vuelta al coto, y ninguno al herrero.
        bool sigueFaltando = sentidos.Suma(PuntosDeComida) < ComidaMinima || sentidos.Suma(PuntosDeBebida) < BebidaMinima
                             || (hayArmaAlAlcance && LeFaltaElArmaDelCamino(sentidos.Nivel, LoMejorQueTiene(sentidos, EsArma)))
                             || (hayRopaAlAlcance && LeFaltaLaRopaDelCamino(sentidos.Nivel, TieneRopaPropia(sentidos)));
        switch (DescansoTrasElViaje(compro, sigueFaltando))
        {
            case DescansoDeCompras.Ninguno: _descansoLargo = false; _proximaCompra = 0; break;
            case DescansoDeCompras.Largo: PostergarCompra(exito: true); break;
            default: PostergarCompra(); break;
        }
        return true;
    }

    /// <summary>Cuánto se espera para la próxima compra después de un viaje. Ver <see cref="DescansoTrasElViaje"/>.</summary>
    public enum DescansoDeCompras { Ninguno, Corto, Largo }

    /// <summary>
    /// ¿CUÁNTO DESCANSA DESPUÉS DE UN VIAJE DE COMPRAS? El que se llevó todo lo que fue a buscar, el descanso largo; el que no
    /// compró nada, el corto (<see cref="EnfriamientoFalloMinS"/> a <see cref="EnfriamientoFalloMaxS"/> segundos), que es para
    /// no reintentar en cada vuelta un viaje que falla. Y EL QUE COMPRÓ PERO TODAVÍA LE FALTA ALGO, NINGUNO: sale ya por lo que
    /// falta. Iba al corto, como si hubiera fallado.
    ///
    /// <para>Medido en `oficios23`: Ciro llegó al nivel 15 al lado de la cuna, con nueve mil monedas, y ahí se abastece para el
    /// camino —la comida, la bebida y el arma, porque en el 16 el servidor le saca el kit de novato—. Compró la comida, esperó,
    /// compró la bebida, esperó, y cuando le tocaba el arma ya era 16: la cuna, y con ella el paso a Rinkel, le quedó cerrada, y
    /// siguió con el martillo de novato hasta el 20. Con la experiencia a 50 el nivel 15 le duró cuatro minutos, y las dos
    /// esperas se comieron casi la mitad. Cada viaje que sigue así compró algo, así que no puede quedar dando vueltas: cuando
    /// no compra nada, vuelve el descanso corto.</para>
    /// </summary>
    public static DescansoDeCompras DescansoTrasElViaje(bool compro, bool sigueFaltando) =>
        !compro ? DescansoDeCompras.Corto : sigueFaltando ? DescansoDeCompras.Ninguno : DescansoDeCompras.Largo;

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
        (comida ? ReservaParaLoQueFalta(CostoPorPunto(esBebida: false), sentidos.Suma(ComidaPropia), ComidaMinima) : 0)
        + (bebida ? ReservaParaLoQueFalta(CostoPorPunto(esBebida: true), sentidos.Suma(BebidaPropia), BebidaMinima) : 0);

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
    /// <summary>
    /// ¿VALE SALIR A COMPRAR ANTÍDOTO? Si le falta y le alcanza. Lo segundo no es un detalle: el archivo ya
    /// tropezó con eso en la herramienta — que faltara alcanzaba para salir de compras aunque tuviera CERO
    /// monedas, y como no la podía pagar nunca, volvía con las manos vacías y salía de nuevo.
    ///
    /// <para>Un <paramref name="cuesta"/> en cero quiere decir que NADIE lo vende: entonces tampoco se sale.</para>
    /// </summary>
    public static bool ValeSalirPorElAntidoto(bool falta, int oro, int cuesta) => falta && oro >= cuesta && cuesta > 0;

    /// <summary>Lo que cuesta el antídoto más barato que alguien venda, o 0 si no lo vende nadie. Sale de la
    /// DATA y no de un número a mano, igual que el precio de la herramienta.</summary>
    public static int PrecioDelAntidotoEn(GameData datos)
    {
        int mejor = int.MaxValue;
        foreach (var npc in datos.Npcs.Values)
            if (npc.StockItems is { } stock)
                foreach (var (obj, _) in stock)
                    if (datos.Obj((short)obj) is { Value: > 0 } o
                        && CuraElVeneno(o, datos.Spell((short)o.LanzaHechizo)))
                        mejor = Math.Min(mejor, o.Value);
        return mejor == int.MaxValue ? 0 : mejor;
    }

    private int _cuestaElAntidoto = -1;

    /// <inheritdoc cref="PrecioDelAntidotoEn"/>
    private int CuestaElAntidoto =>
        _cuestaElAntidoto >= 0 ? _cuestaElAntidoto : _cuestaElAntidoto = PrecioDelAntidotoEn(_entorno.Datos);

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

        // LA HERRAMIENTA PRIMERO: es lo que habilita el oficio, y sin oficio no hay con qué comprar lo demás. Las dos, la
        // de juntar y la de fabricar, si las vende el mismo mostrador: el Alquimista tiene las Tijeras y la Olla.
        foreach (short herramienta in new[] { HerramientaDeSuOficio, HerramientaDeFabricar })
        {
            if (herramienta == 0 || sentidos.CuantoTiene(obj => obj == herramienta) > 0
                || sentidos.EnLaTienda(obj => obj == herramienta) is not { } h) continue;
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(h.Ranura).WriteInteger(1)));
            await Task.Delay(_rng.Next(600, 1_400), ct);
            compro |= sentidos.CuantoTiene(obj => obj == herramienta) > 0;
        }

        // Lo esencial primero: sin comer ni tomar no se recupera la energía, y sin energía no se pelea. Las pociones
        // sobran —el kit de novato trae más de mil—, así que van al final con lo que quede.
        // Sin una gota encima, con la sed en cero y el hambre aguantando, la plata es para el agua: la comida no
        // se compra aunque esté enfrente. Y al revés con el hambre.
        if (!(SinNadaQueTomar(sentidos) && !sentidos.ConHambre))
            compro |= await ReponerAsync(transporte, sentidos, ComidaPropia, ComidaQueSeLleva(sentidos.Nivel), ct,
                                         ReservaPara(sentidos, comida: false, bebida: true));
        if (!(SinNadaQueComer(sentidos) && !sentidos.ConSed))
            compro |= await ReponerAsync(transporte, sentidos, BebidaPropia, BebidaQueSeLleva(sentidos.Nivel), ct,
                                         ReservaPara(sentidos, comida: true, bebida: false));
        compro |= await ReponerAsync(transporte, sentidos, PocionPropia, PocionesQueQuiere, ct, ReservaPara(sentidos, comida: true, bebida: true));
        // EL ANTIDOTO, que cuesta DIEZ monedas y es la diferencia entre seguir cazando y perder la vida de a
        // poco. Se compra acá, donde el bot ya está parado, y no se viaja por él: lo vende el Alquimista, que
        // tiene 21 puestos —uno en la cuna y otro en el 62, que es donde los bots cazan— mientras que criaturas
        // venenosas hay 709 repartidas en 93 mapas. Ver `CuraElVeneno`.
        compro |= await ReponerAsync(transporte, sentidos, AntidotoPropio, AntidotosQueQuiere, ct,
                                     ReservaPara(sentidos, comida: true, bebida: true));
        // LA ROPA DEL CAMINO, ANTES QUE EL ARMA: ver `LeFaltaLaRopaDelCamino`.
        if (LeFaltaLaRopaDelCamino(sentidos.Nivel, TieneRopaPropia(sentidos)))
            compro |= await ComprarLaRopaDelCaminoAsync(transporte, sentidos, ct);
        // EL ARMA DEL CAMINO, ANTES QUE LOS GUSTOS: ver `LeFaltaElArmaDelCamino`.
        if (LeFaltaElArmaDelCamino(sentidos.Nivel, LoMejorQueTiene(sentidos, EsArma)))
            compro |= await ComprarElArmaDelCaminoAsync(transporte, sentidos, ct);
        // EL QUE CASTEA GASTA PRIMERO EN EL PERGAMINO: ver <see cref="ElHechizoAntesQueElArma"/>. Las dos compras
        // salen de la MISMA bolsa, así que el orden decide cuál se queda sin plata.
        if (ElHechizoAntesQueElArma(sentidos.UsaMana))
        {
            compro |= await ComprarPergaminoAsync(transporte, sentidos, ct);
            compro |= await MejorarElEquipoAsync(transporte, sentidos, ct);
        }
        else
        {
            compro |= await MejorarElEquipoAsync(transporte, sentidos, ct);
            compro |= await ComprarPergaminoAsync(transporte, sentidos, ct);
        }
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

    /// <summary>La herramienta con la que JUNTA su oficio, o 0 si no junta con herramienta. Los números son los del
    /// <c>obj.dat</c>, y son los que el servidor mira para saber qué oficio se está ejerciendo: el oficio lo
    /// declara la HERRAMIENTA EQUIPADA, no la skill que diga el cliente.
    ///
    /// <para>LOS QUE FABRICAN JUNTAN LO SUYO CON LA MISMA HERRAMIENTA QUE EL QUE LO VENDE: el Carpintero tala como el
    /// Leñador y el Druida corta raíces con las Tijeras (las dos sobre un árbol, PacketDispatcher `OficioDe`). El Sastre
    /// no junta con herramienta: sus pieles las suelta el Lobo que caza (RandomDrop 414 al 100 %).</para></summary>
    private short HerramientaDeSuOficio => _clase switch
    {
        ClasePescador => CanaDePesca,
        ClaseLenador or ClaseCarpintero => HachaDeLenador,
        ClaseMinero => PiqueteDeMinero,
        ClaseDruida => Tijeras,
        _ => 0,
    };

    private const byte ClasePescador = 11, ClaseLenador = 13, ClaseMinero = 14;
    private const byte ClaseDruida = 7, ClaseCarpintero = 15, ClaseSastre = 16;
    private const short CanaDePesca = 881, HachaDeLenador = 127, PiqueteDeMinero = 187, Tijeras = 885;

    /// <summary>Lo que saca su oficio PARA VENDER, con los mismos números que usa el servidor para saber qué da cada
    /// trabajo (el pez de la caña, la leña y el hierro). El que fabrica no vende lo que junta: ver <see cref="InsumoDeSuOficio"/>.</summary>
    private short ProductoDeSuOficio => _clase switch
    {
        ClasePescador => Pargo,
        ClaseLenador => Lena,
        ClaseMinero => Hierro,
        _ => 0,
    };

    private const short Pargo = 139, Lena = 58, Hierro = 192;

    /// <summary>Lo que junta para fabricar, y que por eso NO es botín aunque haya aparecido en la sesión: la leña del
    /// Carpintero, la raíz del Druida y la piel de lobo del Sastre (los números del servidor: <c>GameLimits.WoodObj</c>,
    /// <c>RootObj</c> y <c>SkinWolfObj</c>).</summary>
    private short InsumoDeSuOficio => _clase switch
    {
        ClaseCarpintero => Lena,
        ClaseDruida => (short)GameLimits.RootObj,
        ClaseSastre => (short)GameLimits.SkinWolfObj,
        _ => 0,
    };

    /// <summary>
    /// CÓMO FABRICA ESTA CLASE, o null si no fabrica. Los cuatro datos salen del handler del servidor, y son los que él
    /// mira: el paquete, las recetas que acepta, la herramienta que tiene que estar PUESTA (el Costurero y la Olla; la
    /// carpintería no pide ninguna, fiel al original) y la skill contra la que compara cada receta.
    ///
    /// <para>El Herrero no está: sus recetas piden lingotes, que salen de fundir lo que se mina, y ni el Herrero ni el
    /// Minero son clases de bot (ver <see cref="Reparto"/>).</para>
    /// </summary>
    private (ClientPacketId Paquete, IReadOnlySet<int> Recetas, Oficios.Fabricacion Regla)? SuFabricacion => _clase switch
    {
        ClaseCarpintero => (ClientPacketId.CraftCarpenter, _entorno.Datos.CarpenterItems, Oficios.Carpinteria),
        ClaseDruida => (ClientPacketId.Craftalquimia, _entorno.Datos.AlchemyItems, Oficios.Alquimia),
        ClaseSastre => (ClientPacketId.CraftSastre, _entorno.Datos.TailorItems, Oficios.Sastreria),
        _ => null,
    };

    /// <summary>La herramienta con la que fabrica, o 0 si no pide ninguna.</summary>
    private short HerramientaDeFabricar => (short)(SuFabricacion?.Regla.Herramienta ?? 0);

    /// <summary>
    /// LA HERRAMIENTA QUE LE FALTA, o 0: primero la de juntar y después la de fabricar. Ninguna clase nace con ellas —el
    /// Pescador, el Leñador y el Minero reciben todos el mismo martillo de piedra— así que hay que ir a comprarlas, igual
    /// que un jugador nuevo.
    /// </summary>
    private short HerramientaQueLeFalta(SentidosDelBot sentidos)
    {
        short juntar = HerramientaDeSuOficio, fabricar = HerramientaDeFabricar;
        if (juntar > 0 && sentidos.CuantoTiene(obj => obj == juntar) == 0) return juntar;
        if (fabricar > 0 && sentidos.CuantoTiene(obj => obj == fabricar) == 0) return fabricar;
        return 0;
    }

    /// <summary>¿Es una de las herramientas de su oficio? Ésas no se venden aunque las haya comprado en la sesión.</summary>
    private bool EsHerramientaDeSuOficio(short obj) => obj != 0 && (obj == HerramientaDeSuOficio || obj == HerramientaDeFabricar);

    /// <summary>Qué cuenta como lugar de trabajo para su oficio: null para el agua (la pesca mira el terreno), o qué
    /// objetos del mapa sirven (los árboles para el Leñador, los yacimientos para el Minero).</summary>
    private Func<int, bool>? RecursoDeSuOficio()
    {
        byte skill = SkillDeSuOficio;
        if (skill == GameLimits.SkillPesca) return null;
        // La tala y la botánica trabajan las dos sobre un ÁRBOL (el hacha y las tijeras, PacketDispatcher `IniciarFaena`).
        bool arbol = skill is GameLimits.SkillTalar or GameLimits.SkillBotanica;
        return obj => _entorno.Datos.Obj(obj) is { } o && (arbol ? o.IsTree : o.IsDeposit);
    }

    private bool MapaSeguro(int mapa) => MapaSeguroPara(mapa, _miBando, _miNivel, _entorno.Datos, _entorno.Ini);

    /// <summary>Un mapa sin pk y sin guardias que vayan por él: donde se desconecta, se refugia, pesca y se sienta a fabricar.</summary>
    public static bool MapaSeguroPara(int mapa, byte bando, int nivel, GameData datos, ServerIni? ini) =>
        !datos.IsPkMap(mapa) && !HayGuardiaQueVaPor(mapa, bando, nivel, datos, ini);

    /// <summary>
    /// ¿SE PUEDE EJERCER SU OFICIO EN ESE MAPA? El agua, en un mapa sin pk, como siempre. El ÁRBOL, al revés: sólo en un
    /// mapa PK, porque el original no deja talar ni juntar raíces en zona segura (Acciones.bas: "No puedes talar en zona
    /// segura"), y ahí sólo donde no nace nada que lo pueda matar — la misma regla con la que caza, que el autor aprobó
    /// para trabajar. Los bots talaban en la cuna, que es segura, por un clic de oficio que pagaba sin mirar la baldosa
    /// ni el mapa: el servidor ya no lo deja.
    ///
    /// <para>ES UNO SOLO para las tres preguntas —si hay dónde, adónde viajar y si se trabaja acá—: con filtros
    /// distintos el bot viaja a un lugar donde después no puede trabajar (memoria `filtro-del-viaje-y-de-la-compra`).</para>
    /// </summary>
    private Func<int, bool> MapaParaSuFaena(SentidosDelBot sentidos)
    {
        if (SkillDeSuOficio == GameLimits.SkillPesca) return MapaSeguro;
        int vida = sentidos.VidaMaxima, nivel = sentidos.Nivel;
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        Func<int, bool> esPeligroso = f => EsPeligroso((short)f, vida, nivel, conPociones, curaVeneno);
        return m => SeTrabajaElArbolAhi(_entorno.Datos.IsPkMap(m),
            _entorno.Datos.MapSpawns.TryGetValue(m, out var nacen) ? nacen.Select(sp => sp.NpcIndex) : Enumerable.Empty<int>(),
            esPeligroso);
    }

    /// <summary>La regla de <see cref="MapaParaSuFaena"/> para el ÁRBOL, sobre el mapa solo: pk, y sin nada que le
    /// quede grande naciendo ahí.</summary>
    public static bool SeTrabajaElArbolAhi(bool mapaPk, IEnumerable<int> loQueNace, Func<int, bool> esPeligroso) =>
        mapaPk && SeCazaAhi(true, loQueNace, esPeligroso);

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

    /// <summary>
    /// ¿SE PASA POR ESE MAPA CAMINO A OTRO? Si deja entrar y no nace ahí nada peligroso para él; el destino, siempre que deje
    /// entrar. <see cref="IrHastaAsync"/> busca primero el camino por mapas así, y si no hay, va por el de siempre.
    ///
    /// <para>Medido en `oficios7`: una Cazadora de nivel 7 murió en el 4, a dos baldosas de donde nace uno de sus tres Señores
    /// de las sombras (pega de 60 a 75), yendo del 32 al 23: el camino de siempre cruza el 4, y por el 29 se llega igual. Y en
    /// `oficios5` el Sastre murió cruzando el 10 (la Aparición) camino al 242, donde no hay otro camino a cinco mapas.</para>
    /// </summary>
    public static bool PasaPorAhi(int mapa, int mapaDestino, bool dejaEntrar, bool naceAlgoPeligroso) =>
        dejaEntrar && (mapa == mapaDestino || !naceAlgoPeligroso);

    /// <summary>
    /// POR DÓNDE SE BUSCA ADÓNDE IR A TRABAJAR O A CAZAR: por donde cazaría (<see cref="SeCruzaBuscando"/>). Con
    /// <see cref="Pisable"/> la búsqueda encontraba lugares seguros a los que sólo se llega cruzando uno que no lo es, y el viaje,
    /// sin camino sin peligro, iba por el de siempre. Medido en `tienda4`: una Pescadora de nivel 14 pescaba en el 101 y eligió
    /// cazar en el 551, al que sólo se llega por el 17, donde nacen cinco Arañas gigantes (nivel 24, pegan hasta 115); murió
    /// cruzándolo. El viaje prueba este mismo filtro antes que el de siempre (<see cref="IrHastaAsync"/>): un solo filtro para
    /// decidir y para viajar (memoria `filtro-del-viaje-y-de-la-compra`).
    /// </summary>
    private Func<int, bool> CruzaSinPeligro(SentidosDelBot sentidos) =>
        SeCruzaBuscando(_entorno.Datos, _entorno.Ini, _miBando, sentidos.Nivel, sentidos.VidaMaxima,
                        sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear, sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0);

    /// <summary>
    /// ¿SE CRUZA ESE MAPA BUSCANDO DÓNDE CAZAR O TRABAJAR? Si deja entrar, no nacen ahí guardias que vayan por él y ahí cazaría
    /// (<see cref="SeCazaAhi"/>): un mapa inseguro donde nace algo que lo mata no se cruza, y uno seguro sí, como se caza en él.
    ///
    /// <para>Con el filtro del viaje (<see cref="PasaPorAhi"/>: ningún mapa donde nazca algo peligroso) la cuna no pasaba para
    /// un bot de poca vida, por sus Lobos, y el que la dejaba no volvía. Medido en `oficios15`: un Druida y un Clérigo de nivel 3
    /// a 5 dejaron la cuna llena por el 58, que tiene cuatro presas, y se quedaron ahí cuarenta minutos sin pasar del nivel 5:
    /// desde el 58 la búsqueda llegaba a doce mapas, y con la cuna a sesenta y seis.</para>
    ///
    /// <para>NI UNO, SEGURO O NO, DONDE NACE UNA CRIATURA HOSTIL QUE LO MATA DE UN GOLPE (<see cref="UnGolpeLoMata"/> contra su
    /// vida máxima): de eso la runa no lo saca. Medido en `oficios18` y `oficios20`: dos casters de nivel 7 salían del pueblo del
    /// 20 a cazar Escorpiones al 208, donde nace el Señor de las sombras (pega de 60 a 75 y tenían unos 70 de vida), y murieron
    /// peleando con él en nivel 8. El 208 mató bots desde la primera corrida (nueve en `oficios6`). En un mapa inseguro no cambia
    /// nada: lo que mata de un golpe ya es peligroso. Lo que no es hostil no cuenta: los Guerreros y Asesinos de Ullathorpe pegan
    /// de 260 a 350, y sin esa condición el 1 —el paso del 58 a la cuna— se cerraba. Y donde ya está no se mira: el Lobo de la
    /// cuna pega 18 y un nivel 1 nace con 16 a 19 de vida, así que el que la deja no vuelve hasta el nivel 2.</para>
    /// </summary>
    public static Func<int, bool> SeCruzaBuscando(GameData datos, ServerIni? ini, byte bando, int nivel, int vida, bool conPociones,
                                                  bool curaVeneno)
    {
        Func<int, bool> esPeligroso = f => EsPeligrosoParaElBot(datos.Npc((short)f), vida, nivel, conPociones, curaVeneno, bando, datos, ini);
        return m =>
        {
            var nacen = datos.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>();
            return PuedeEntrar(nivel, datos.MapZone(m)) && !HayGuardiaQueVaPor(m, bando, nivel, datos, ini)
                   && SeCazaAhi(datos.IsPkMap(m), nacen, esPeligroso)
                   && !nacen.Any(f => datos.Npc((short)f) is { Hostile: true } n && UnGolpeLoMata(n, vida));
        };
    }

    /// <summary><see cref="CruzaSinPeligro"/> camino a un mapa: el destino, siempre que deje entrar, como en <see cref="PasaPorAhi"/>.</summary>
    private Func<int, bool> CruzaSinPeligroHacia(SentidosDelBot sentidos, int mapaDestino)
    {
        var cruza = CruzaSinPeligro(sentidos);
        var pisable = Pisable(sentidos);
        return m => m == mapaDestino ? pisable(m) : cruza(m);
    }

    /// <summary><see cref="PasaPorAhi"/> para este bot, con su vida, su nivel y lo que lleva encima.</summary>
    private Func<int, bool> PasaSinPeligro(SentidosDelBot sentidos, int mapaDestino)
    {
        var pisable = Pisable(sentidos);
        int vida = sentidos.VidaMaxima, nivel = sentidos.Nivel;
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        return m => PasaPorAhi(m, mapaDestino, pisable(m),
            _entorno.Datos.MapSpawns.TryGetValue(m, out var nacen)
            && nacen.Any(sp => EsPeligroso((short)sp.NpcIndex, vida, nivel, conPociones, curaVeneno)));
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
            && _entorno.Mapas.DondeSeTrabaja(sentidos.Mapa, agua, RecursoDeSuOficio(), sentidos.Posicion, MapaParaSuFaena(sentidos), CruzaSinPeligro(sentidos)) is not null;
        _trabajoSeguroMapa = sentidos.Mapa;
        _trabajoSeguroHasta = Environment.TickCount64 + 5 * 60_000;
        return _trabajoSeguro;
    }

    private int _trabajoSeguroMapa = -1;
    private long _trabajoSeguroHasta;
    private bool _trabajoSeguro;

    /// <summary>¿Hay quien le venda el arma del camino, cerca o LEJOS? Ver <see cref="HayArmaAlAlcance"/>. Se recalcula al
    /// cambiar de mapa o cada cinco minutos: la búsqueda larga recorre hasta doce mapas, y esto se pregunta en cada vuelta.</summary>
    private bool ArmaAlAlcance(SentidosDelBot sentidos, Func<int, bool> vendeElArma, Func<int, bool> mapaAtiende)
    {
        long ahora = Environment.TickCount64;
        if (sentidos.Mapa == _armaAlAlcanceMapa && ahora < _armaAlAlcanceHasta) return _armaAlAlcance;
        _armaAlAlcanceMapa = sentidos.Mapa;
        _armaAlAlcanceHasta = ahora + 5 * 60_000;
        return _armaAlAlcance = HayArmaAlAlcance(_entorno.Mapas, _entorno.Datos, sentidos.Mapa, sentidos.Posicion,
            vendeElArma, mapaAtiende, Pisable(sentidos), PasaSinPeligro(sentidos, NingunMapa));
    }

    private int _armaAlAlcanceMapa = -1;
    private long _armaAlAlcanceHasta;
    private bool _armaAlAlcance;

    /// <summary>¿Hay quien le venda la ropa del camino, cerca o lejos? Como <see cref="ArmaAlAlcance"/>, con su propia memoria.</summary>
    private bool RopaAlAlcance(SentidosDelBot sentidos, Func<int, bool> vendeRopa, Func<int, bool> mapaAtiende)
    {
        long ahora = Environment.TickCount64;
        if (sentidos.Mapa == _ropaAlAlcanceMapa && ahora < _ropaAlAlcanceHasta) return _ropaAlAlcance;
        _ropaAlAlcanceMapa = sentidos.Mapa;
        _ropaAlAlcanceHasta = ahora + 5 * 60_000;
        return _ropaAlAlcance = HayArmaAlAlcance(_entorno.Mapas, _entorno.Datos, sentidos.Mapa, sentidos.Posicion,
            vendeRopa, mapaAtiende, Pisable(sentidos), PasaSinPeligro(sentidos, NingunMapa));
    }

    private int _ropaAlAlcanceMapa = -1;
    private long _ropaAlAlcanceHasta;
    private bool _ropaAlAlcance;

    /// <summary>¿Le falta alguna herramienta de su oficio? Ver <see cref="HerramientaQueLeFalta"/>.</summary>
    private bool LeFaltaLaHerramienta(SentidosDelBot sentidos) => HerramientaQueLeFalta(sentidos) != 0;

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
        if ((_traidoDeCasa.Contains(obj) && obj != ProductoDeSuOficio && !enElPozo) || EsHerramientaDeSuOficio(obj)) return false;
        // NI LO QUE JUNTA PARA FABRICAR: la leña del Carpintero o la piel del Sastre aparecen durante la sesión como
        // cualquier botín, y vendiéndolas no fabricaría nunca.
        if (obj == InsumoDeSuOficio && obj != 0) return false;
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
        // Y LO MEJOR QUE TIENE DE CADA CLASE DE EQUIPO TAMPOCO: ver `EsLoMejorQueTieneDeEsaClase`. Sin esto el bot
        // vendía la armadura que acababa de comprar —o la que se le desequipó al morir— y la recompraba en el mismo
        // segundo, perdiendo dos tercios del valor en cada vuelta.
        if (o is not null && LoPuedeUsar(o, sentidos.Nivel)
            && QueSePone.Any(esDeEso => esDeEso(o)
                             && EsLoMejorQueTieneDeEsaClase(ValorDeCombate(o), LoMejorQueTiene(sentidos, esDeEso))))
            return false;
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

    /// <summary>
    /// ¿ESTÁ POR DEJAR DE SER NOVATO? Desde <see cref="NivelesDePreparacion"/> niveles antes del último de novato ya cuenta
    /// como que sí.
    ///
    /// <para>Al pasar <see cref="GameLimits.NewbieMaxLevel"/> el servidor le saca DE UNA todo lo de novato
    /// (<c>QuitarEquipoNovato</c>, prendido en el `Server.ini` que se distribuye), y de los ocho ítems del kit de
    /// nacimiento SIETE son de novato: el mapa, las pociones rojas y las azules, las manzanas, las botellas, el
    /// martillo y el báculo. El que no se preparó se queda sin comer, sin tomar, sin con qué curarse y sin arma en la
    /// misma subida de nivel. Un jugador lo ve venir y va de compras antes.</para>
    ///
    /// <para>Y ANTES QUIERE DECIR ANTES DEL ÚLTIMO NIVEL, no en él. Medido en `hueco2` (29/09, con las tasas en 50): dos bots
    /// subieron al 16 al minuto y medio y a los dos minutos de llegar al 15, sin haber comprado ropa, comida ni bebida; se
    /// quedaron sin energía y los mató un Lobo. En las cuatro corridas de ese día el 15 duró de mediana diez minutos y el más
    /// corto uno; el 13 y el 14, cinco y cuatro de mediana. Con esos dos niveles el viaje al pueblo entra.</para>
    /// </summary>
    public static bool PorDejarDeSerNovato(int nivel) => nivel >= GameLimits.NewbieMaxLevel - NivelesDePreparacion;

    /// <summary>Cuántos niveles antes del último de novato empieza a prepararse para dejar la cuna (ver <see cref="PorDejarDeSerNovato"/>).</summary>
    public const int NivelesDePreparacion = 2;

    /// <summary>
    /// ¿LE FALTA EL ARMA PARA EL CAMINO? Está por dejar de ser novato (<see cref="PorDejarDeSerNovato"/>) y no tiene un arma
    /// propia: la de novato ya no cuenta (<see cref="CuentaComoPropio"/>), y con <c>QuitarEquipoNovato</c> el servidor se la
    /// saca en la subida. Entonces el arma es una necesidad, como la comida para el camino, y no un gusto.
    ///
    /// <para>Pedido del autor (2026-09-27): que se compre un arma al dejar de ser novato. Medido en `oficios20` y `oficios21`:
    /// de los bots que pasaron el nivel 15 uno solo se había comprado un arma; los demás seguían con el Martillo de Piedra, el
    /// Báculo o la Daga de novato (golpe 5, 4 y 2) y de once mil a setenta mil monedas encima. El arma iba con el equipo —lo que
    /// sobra, cuando no falta nada más—, y en el 15 lo primero que falta es la comida y la bebida del camino: el viaje al
    /// herrero llegaba ya en el 16, cuando desde el campo ningún pueblo que le venda se alcanza sin cruzar peligro.</para>
    /// </summary>
    public static bool LeFaltaElArmaDelCamino(int nivel, int mejorArmaPropia) => PorDejarDeSerNovato(nivel) && mejorArmaPropia <= 0;

    /// <summary>
    /// ¿ACABA DE EMPEZAR A PREPARARSE PARA DEJAR DE SER NOVATO (<see cref="PorDejarDeSerNovato"/>)? Entonces el descanso de la
    /// compra anterior —hasta quince minutos, si fue un éxito— no vale: se abastece para el camino (la comida, la bebida, la
    /// ropa y el arma), y los últimos niveles de novato duran poco.
    /// </summary>
    public static bool EmpiezaAAbastecerse(int nivelAntes, int nivelAhora) =>
        !PorDejarDeSerNovato(nivelAntes) && PorDejarDeSerNovato(nivelAhora);

    /// <summary>
    /// ¿EL VIAJE VA POR EL ARMA DEL CAMINO? Cuando le falta, hay quien le venda una que pueda usar y pagar, y ya tiene la
    /// comida, la bebida y la herramienta: se busca con la panza llena, como la herramienta, y antes que las pociones y los
    /// gustos, porque los últimos niveles de novato duran poco. Sin <paramref name="hayUnaAlAlcance"/>, el que no puede usar ninguna de las que se
    /// venden cerca dejaría de comprar todo lo demás.
    /// </summary>
    public static bool PrimeroElArma(bool vaPorElArma, bool hayUnaAlAlcance, bool vaPorComida, bool vaPorBebida,
                                     bool vaPorLaHerramienta, bool vaPorLaRopa = false) =>
        vaPorElArma && hayUnaAlAlcance && !vaPorComida && !vaPorBebida && !vaPorLaHerramienta && !vaPorLaRopa;

    /// <summary>
    /// ¿LE FALTA LA ROPA DEL CAMINO? Está por dejar de ser novato (<see cref="PorDejarDeSerNovato"/>) o ya dejó, y no tiene ropa
    /// de cuerpo propia —la de novato no cuenta, ver <see cref="CuentaComoPropio"/>—. Con <c>QuitarEquipoNovato</c>, prendido en el
    /// <c>Server.ini</c> que se distribuye, el servidor se la saca en el 16, y el DESNUDO NO RECUPERA ENERGÍA
    /// (<see cref="Survival.RecuperaEnergia"/>): sin energía no pelea, y lo que lo ataca lo mata parado. Va con lo esencial, antes
    /// que el arma (<see cref="PrimeroElArma"/>), y sigue valiendo después del 16 para el que quedó desnudo.
    ///
    /// <para>Medido en `salir1` (28/09, 18 bots desde el nivel 1, con el Server.ini de ejemplo): tres de los primeros bots que
    /// pasaron el 16 murieron enseguida con la energía en cero, desnudos —dos de ellos con la comida, la bebida y el arma ya
    /// compradas—. Las corridas anteriores no lo veían: el Server.ini del arnés no traía la clave.</para>
    /// </summary>
    public static bool LeFaltaLaRopaDelCamino(int nivel, bool tieneRopaPropia) => PorDejarDeSerNovato(nivel) && !tieneRopaPropia;

    /// <summary>¿El viaje va por la ropa del camino? Cuando hay quien se la venda y ya tiene la comida, la bebida y la herramienta:
    /// es lo que sigue en el orden de lo esencial, antes que el arma.</summary>
    public static bool PrimeroLaRopa(bool hayUnaAlAlcance, bool vaPorComida, bool vaPorBebida, bool vaPorLaHerramienta) =>
        hayUnaAlAlcance && !vaPorComida && !vaPorBebida && !vaPorLaHerramienta;

    /// <summary>¿Tiene ropa de cuerpo propia, puesta o en la mochila, que se pueda poner? La de novato no cuenta desde el último
    /// nivel de novato (<see cref="CuentaComoPropio"/>).</summary>
    private bool TieneRopaPropia(SentidosDelBot sentidos)
    {
        foreach (byte ranura in sentidos.RanurasOcupadas())
        {
            short obj = sentidos.QueHayEn(ranura);
            if (_entorno.Datos.Obj(obj) is { IsArmor: true } o && LoPuedeUsar(o, sentidos.Nivel) && CuentaComoPropio(obj))
                return true;
        }
        return false;
    }

    /// <summary>
    /// ¿Eso cuenta COMO PROPIO para decidir qué comprar? Lo de novato deja de contar cuando está por perderlo.
    ///
    /// <para>Es sólo para COMPRAR. Para usarlo sigue contando: mientras las tenga, esas pociones se las toma y esas
    /// manzanas se las come — tirarlas sería peor que perderlas.</para>
    /// </summary>
    private bool CuentaComoPropio(short obj) =>
        !PorDejarDeSerNovato(_nivelParaLoNovato) || _entorno.Datos.Obj(obj) is not { IsNewbie: true };

    /// <summary>El nivel que mira <see cref="CuentaComoPropio"/>; se refresca en cada vuelta del ciclo.</summary>
    private int _nivelParaLoNovato;

    /// <summary>Lo mismo que los contadores de al lado, pero mirando SÓLO lo propio: con esto se decide qué comprar.</summary>
    private int PocionPropia(short obj) => CuentaComoPropio(obj) && EsPocionDeVida(obj) ? 1 : 0;

    /// <inheritdoc cref="PocionPropia"/>
    private int AntidotoPropio(short obj) => CuentaComoPropio(obj) && EsPocionQueCuraElVeneno(obj) ? 1 : 0;

    /// <inheritdoc cref="PocionPropia"/>
    private int ComidaPropia(short obj) => CuentaComoPropio(obj) ? PuntosDeComida(obj) : 0;

    /// <inheritdoc cref="PocionPropia"/>
    private int BebidaPropia(short obj) => CuentaComoPropio(obj) ? PuntosDeBebida(obj) : 0;
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

        bool llego = false;
        short queHabia = 0, hayAhora = 0;
        for (int intentos = 0; intentos < RadioDeBotin * 2 && !cerrada.IsCompleted; intentos++)
        {
            var (x, y) = sentidos.Posicion;
            int dx = botin.Donde.X - x, dy = botin.Donde.Y - y;
            if (dx == 0 && dy == 0)
            {
                llego = true;
                queHabia = sentidos.EnElPiso(botin.Donde);
                transporte.Mandar(Paquete(ClientPacketId.PickUp, _ => { }));
                await Task.Delay(_rng.Next(400, 900), ct);
                hayAhora = sentidos.EnElPiso(botin.Donde);
                break;
            }
            if (await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct) is var r && r != Paso.Dado)
                return r != Paso.SinConexion;
        }
        if (BaldosaQueNoSirve(llego, queHabia, hayAhora))
            _nadaQueHacerAca.Add((sentidos.Mapa, botin.Donde.X, botin.Donde.Y));
        return true;
    }

    /// <summary>
    /// ¿QUEDA LA BALDOSA ANOTADA COMO QUE NO SIRVE? Si se llegó, cuando agacharse no levantó nada: la mochila llena, o algo
    /// que el servidor no deja levantar. <b>Y SI NO SE LLEGÓ, SIEMPRE.</b>
    ///
    /// <para>El paso hacia el botín es codicioso, así que cualquier pared en el medio deja la baldosa inalcanzable. Sin
    /// anotarla, la vuelta siguiente elige el MISMO botín —es el más cercano— y vuelve a gastar los mismos
    /// <see cref="RadioDeBotin"/> por dos pasos sin llegar, para siempre. Medido con la sonda del ciclo: levantar botín se
    /// llevó el 63 % del tiempo de ocho bots en veinticinco minutos, con el tiempo por vuelta clavado por bot —3,9 s los de
    /// ritmo bajo, 5,9 s los de ritmo alto—, que es la firma de una cuenta de pasos fija y no de una distancia que varía. Un
    /// Gladiador terminó la corrida en nivel 1, con cero de experiencia y cero de oro, después de 198 vueltas.</para>
    /// </summary>
    public static bool BaldosaQueNoSirve(bool llego, short habiaAntes, short hayAhora) => !llego || habiaAntes == hayAhora;

    /// <summary>
    /// ¿Es algo que se pueda levantar? El servidor manda como "objeto del piso" lo tirado Y LAS PUERTAS
    /// (ModAreas: el decorado fijo no viaja, pero las puertas sí, para que se sepa si están abiertas).
    ///
    /// <para>Sin este filtro el bot trataba a cada puerta del pueblo como botín: caminaba hasta ella, se
    /// agachaba, no levantaba nada, y volvía a empezar. Medido: ocho bots con oro en CERO después de nueve
    /// minutos, porque ninguno llegó nunca a un botín de verdad.</para>
    /// </summary>
    private bool SePuedeLevantar(short obj) => obj > 0 && SeLevantaDelPiso(_entorno.Datos.Obj(obj));

    /// <summary>
    /// La regla de <see cref="SePuedeLevantar"/> sobre el objeto solo, que es la del servidor al levantar: `Agarrable` distinto de 1
    /// (<see cref="ObjInfo.IsPickable"/>, VB6 InvUsuario.bas:619). El decorado fijo también viaja como objeto del piso, y no sólo
    /// las puertas: los Teleports de la cuna están encima de las salidas. Medido en `normal2`: una Pescadora de nivel 1 pasó
    /// veinticuatro de treinta minutos sin matar nada y terminó al lado del Teleport de (66,74) de la cuna, que es la salida al
    /// 208. `LevantarBotinAsync` camina hacia lo que ve a levantar y sólo anota la baldosa inútil al agacharse encima: con el paso
    /// a la salida sin entrar, devolvía que hizo algo en cada vuelta.
    /// </summary>
    public static bool SeLevantaDelPiso(ObjInfo? o) => o is not null && !o.IsDoor && o.IsPickable;

    /// <summary>Baldosas donde ya se agachó y no levantó nada.</summary>
    private readonly HashSet<(short Mapa, byte X, byte Y)> _nadaQueHacerAca = new();

    /// <summary>
    /// TRABAJAR EL OFICIO: pescar, talar o minar. Devuelve si se ocupó de esto.
    ///
    /// <para>La secuencia es la del cliente: se equipa la herramienta —el servidor decide QUÉ oficio se está
    /// ejerciendo por la herramienta que llevás puesta, no por lo que diga el paquete—, se manda
    /// <c>Work</c> con la skill, el servidor contesta pidiendo el objetivo, y se marca la baldosa con
    /// <c>WorkLeftClick</c>. Ese clic ARRANCA la faena, igual que el doble clic de un jugador, y el servidor junta de a
    /// una vuelta por intervalo mientras el bot se quede ahí; caminar la corta. Los clics que siguen la mantienen
    /// prendida si algo la cortó (la energía, por ejemplo), pero ya no cobran por su cuenta.</para>
    ///
    /// <para>Sólo lo hacen las tres clases que tienen oficio, y sólo si ya consiguieron la herramienta —que
    /// ninguna trae de nacimiento y hay que ir a comprar—. Es lo que hace que un Pescador se distinga de un
    /// Guerrero mirándolo, en vez de ser el mismo bot con otro nombre de clase.</para>
    /// </summary>
    private async Task<bool> TrabajarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada,
                                           CancellationToken ct)
    {
        short herramienta = HerramientaDeSuOficio;
        // Para juntar alcanza con la herramienta de juntar: al Druida sin la Olla todavía le sirven las raíces.
        if (herramienta == 0 || sentidos.CuantoTiene(obj => obj == herramienta) == 0) return false;
        if (Environment.TickCount64 < _proximaFaena) return false;
        // EL QUE JUNTA PARA FABRICAR NO JUNTA DE MÁS: un acierto de tala da de diez a treinta leñas y la cuchara gasta dos, así
        // que sin tope el Carpintero se pasaría la vida talando. Con la reserva hecha, lo que falta es fabricar —o subir de
        // nivel para destrabar la receta siguiente—, y mientras tanto sale a cazar como cualquiera.
        if (InsumoDeSuOficio != 0 && sentidos.CuantoTiene(obj => obj == InsumoDeSuOficio) >= InsumoQueGuarda) return false;
        if (sentidos.Fuelle < FuelleMinimo) return false;   // trabajar cuesta energía

        byte skill = SkillDeSuOficio;
        bool agua = skill == GameLimits.SkillPesca;
        Func<int, bool>? recurso = RecursoDeSuOficio();

        // ¿Hay dónde trabajar acá cerca? Si no, se viaja hasta donde haya.
        // Y SÓLO DONDE NO SE MATA A LA GENTE: nada de mapas pk. Medido: un Minero de nivel 5 al que se mandaba a la mina
        // del mapa 33, a cinco mapas, cruzando zonas pk con gladiadores que pegan 282 y trece lobos, murió al menos dos
        // veces en veinte minutos y perdió los dos piquetes, que se caen al morir. Todas las minas del mundo están en
        // mapas pk; el agua y los árboles de la cuna, no.
        var sirve = MapaParaSuFaena(sentidos);
        var punto = sirve(sentidos.Mapa)
            ? _entorno.Mapas.DondeTrabajar(sentidos.Mapa, sentidos.Posicion, RadioDeFaena, agua, recurso) : null;
        if (punto is null)
        {
            if (_entorno.Mapas.DondeSeTrabaja(sentidos.Mapa, agua, recurso, sentidos.Posicion, sirve, CruzaSinPeligro(sentidos)) is not { } lejos) { PostergarFaena(); return false; }
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
        //
        // Y AL ÁRBOL O AL YACIMIENTO SE LLEGA POR EL CAMINO, no de a un paso en línea recta. Medido en `oficios2`: el Druida,
        // recién comprada la tijera, eligió un árbol del mapa 20 a seis baldosas en línea recta, con una pared de cuatro filas
        // en el medio —por el camino son cuarenta y un pasos—, y los pasos contra la pared lo dejaron donde estaba. Fue su
        // único intento de la corrida.
        if (!agua)
        {
            if (!await IrHastaAsync(transporte, sentidos, sentidos.Mapa, punto.Value.Donde.X, punto.Value.Donde.Y, cerrada, ct,
                                    DistanciaParaFaenar)) { PostergarFaena(); return true; }
        }
        else
            for (int i = 0; i < RadioDeFaena * 2 && !cerrada.IsCompleted; i++)
            {
                var (x, y) = sentidos.Posicion;
                int dx = punto.Value.Donde.X - x, dy = punto.Value.Donde.Y - y;
                if (Math.Abs(dx) <= AlcanceHechizoX && Math.Abs(dy) <= AlcanceHechizoY) break;
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

    /// <summary>
    /// FABRICAR LO SUYO con lo que juntó: el Carpintero con la leña, el Druida con las raíces y la Olla, el Sastre con las
    /// pieles y el Costurero. Devuelve si hizo algo.
    ///
    /// <para>Cada vez, lo más difícil que ya sabe hacer (<see cref="QueFabricar"/>), y de a una por paquete: la energía y
    /// la subida de la skill se cobran por PAQUETE y no por unidad (VB6 CarpinteroConstruirItem), así que de a una es lo
    /// que más sube el oficio por material. El servidor rechaza en silencio (sin <c>Else</c> en el original) lo que no
    /// llega, así que la skill sale de lo que él mandó (<see cref="SentidosDelBot.Skill"/>) y lo que salió se mira en
    /// la mochila.</para>
    ///
    /// <para>En un mapa seguro y sin nada hostil cerca, como cualquiera que se sienta a coser, y con la energía que sobra:
    /// es la misma que hace falta para pelear. Si tiene qué fabricar y está en un mapa inseguro, va a uno seguro
    /// (<see cref="DondeSentarseAFabricar"/>).</para>
    /// </summary>
    private async Task<bool> FabricarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (SuFabricacion is not { } fab) return false;
        if (Environment.TickCount64 < _proximaFabricacion) return false;
        if (HostilCerca(sentidos)) return false;
        if (sentidos.Fuelle < FuelleParaFabricar || sentidos.EnergiaEnPuntos < fab.Regla.Energia) return false;
        int skill = sentidos.Skill(fab.Regla.Skill);
        if (skill < 0) return false;   // todavía no le llegaron sus skills
        Func<int, int> tiene = obj => sentidos.CuantoTiene(o => o == obj);
        if (QueFabricar(_entorno.Datos, fab.Recetas, fab.Regla, skill, tiene) is not { } primera) { PostergarFabricacion(); return false; }
        // FABRICA PARA APRENDER, O PARA LO QUE LE FALTA. Con la skill en el tope de su nivel otra cuchara no le enseña nada
        // y le come la energía de pelear; el Druida igual se hace el antídoto que le falta.
        bool leFalta = EsPocionQueCuraElVeneno(primera) && sentidos.Suma(AntidotoPropio) < AntidotosQueQuiere;
        if (!ValeFabricar(skill, SkillProgression.CapForLevel(sentidos.Nivel), leFalta)) { PostergarFabricacion(); return false; }

        short herramienta = (short)fab.Regla.Herramienta;
        if (herramienta != 0 && tiene(herramienta) == 0) return false;

        // EN UN MAPA INSEGURO SE VA A FABRICAR A UNO SEGURO, como el que vuelve al pueblo a coser. Medido en `oficios3`: el
        // Sastre cazó Lobos en el 19 y el 24, los dos pk, juntó 49 pieles y no fabricó nada en toda la corrida: no volvió
        // a un mapa seguro, y fabricar sólo se hacía en el mapa donde estaba parado.
        if (!MapaSeguro(sentidos.Mapa))
        {
            if (DondeSentarseAFabricar(_entorno.Mapas, sentidos.Mapa, sentidos.Posicion, MapaSeguro, Pisable(sentidos)) is not { } lugar)
            { PostergarFabricacion(); return false; }
            if (!await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, ToleranciaDelCoto))
                PostergarFabricacion();
            return true;   // llegó: la vuelta que viene fabrica, ya en el mapa seguro
        }

        // LA HERRAMIENTA VA PUESTA (el Costurero, la Olla): equiparla es un interruptor, así que sólo se manda si la
        // tiene guardada. El Druida la cambia por las Tijeras para juntar, y acá vuelve a ponerse la Olla.
        if (herramienta != 0 && sentidos.EnLaMochila(obj => obj == herramienta) is { } guardada)
        {
            transporte.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(guardada.Ranura)));
            await Task.Delay(_rng.Next(400, 900), ct);
        }

        for (int vez = 0; vez < FabricacionesPorTanda; vez++)
        {
            if (Urge(sentidos)) break;
            // SIN ENERGÍA, SE ESPERA A QUE VUELVA: sentado se recupera en segundos (`RecStamina`, cada 2 s), y cortar la tanda
            // ahí dejaba con una o dos hechas al que había viajado a fabricar. Cada Vestimenta cuesta 50.
            if (!await EsperarEnergiaAsync(sentidos, fab.Regla.Energia, ct)) break;
            // Se vuelve a elegir cada vez: la skill sube con cada una y los materiales bajan.
            int ahora = sentidos.Skill(fab.Regla.Skill);
            if (QueFabricar(_entorno.Datos, fab.Recetas, fab.Regla, ahora, tiene) is not { } receta) break;
            // Y SE LEVANTA CUANDO YA NO APRENDE: medido en `oficios1`, la primera de la tanda llevó al Carpintero al tope del
            // nivel y las otras cinco se fabricaron igual, gastando leña y energía por nada.
            bool leSirve = EsPocionQueCuraElVeneno(receta) && sentidos.Suma(AntidotoPropio) < AntidotosQueQuiere;
            if (!ValeFabricar(ahora, SkillProgression.CapForLevel(sentidos.Nivel), leSirve)) break;
            int antes = tiene(receta);
            if (!transporte.Mandar(Paquete(fab.Paquete, w => w.WriteInteger(receta).WriteInteger((short)1)))) return false;
            await Task.Delay(_rng.Next(FabricarMinMs, FabricarMaxMs), ct);
            if (tiene(receta) <= antes) break;   // no salió, y el servidor no dice por qué
        }
        PostergarFabricacion();
        return true;
    }

    /// <summary>
    /// QUÉ FABRICAR: de las recetas de su oficio, la de mínimo más alto que le alcanza la skill y para la que tiene los
    /// materiales de UNA unidad; entre dos que piden lo mismo, la que gasta menos. Null si ninguna. Es lo que hace
    /// cualquiera que sube un oficio, y la regla es la del servidor (<see cref="Oficios.Fabricacion"/>), no una copia.
    ///
    /// <para>Una receta sin ningún material queda afuera: el servidor la daría gratis, y eso es una rareza de la data,
    /// no un oficio.</para>
    /// </summary>
    public static short? QueFabricar(GameData datos, IReadOnlySet<int> recetas, Oficios.Fabricacion regla, int skill,
                                     Func<int, int> cuantoTiene)
    {
        short? mejor = null;
        int mejorPide = -1, mejorGasta = int.MaxValue;
        foreach (int item in recetas.OrderBy(x => x))
        {
            if (datos.Obj(item) is not { } o) continue;
            int pide = regla.Pide(o);
            if (pide > skill) continue;
            var materiales = regla.Materiales(o).Where(m => m.PorUnidad > 0).ToArray();
            if (materiales.Length == 0 || materiales.Any(m => cuantoTiene(m.Obj) < m.PorUnidad)) continue;
            int gasta = materiales.Sum(m => m.PorUnidad);
            if (pide < mejorPide || (pide == mejorPide && gasta >= mejorGasta)) continue;
            mejor = (short)item; mejorPide = pide; mejorGasta = gasta;
        }
        return mejor;
    }

    /// <summary>¿Vale sentarse a fabricar? Mientras la skill pueda subir —el tope es por nivel, <see cref="SkillProgression"/>—
    /// o si lo que sale le hace falta.</summary>
    public static bool ValeFabricar(int skill, int topeDelNivel, bool leHaceFalta) => skill < topeDelNivel || leHaceFalta;

    /// <summary>
    /// LO QUE LE FALTA PARA FABRICAR: los materiales de las recetas que ya sabe hacer, cuando no le alcanza para ninguna. Vacío si
    /// puede fabricar algo o si todavía no sabe hacer nada.
    /// </summary>
    public static IReadOnlySet<int> MaterialesQueLeFaltan(GameData datos, IReadOnlySet<int> recetas, Oficios.Fabricacion regla,
                                                         int skill, Func<int, int> cuantoTiene)
    {
        var falta = new HashSet<int>();
        if (QueFabricar(datos, recetas, regla, skill, cuantoTiene) is not null) return falta;
        foreach (int item in recetas)
        {
            if (datos.Obj(item) is not { } o || regla.Pide(o) > skill) continue;
            foreach (var m in regla.Materiales(o))
                if (m.PorUnidad > 0 && cuantoTiene(m.Obj) < m.PorUnidad) falta.Add(m.Obj);
        }
        return falta;
    }

    /// <summary>¿Suelta esa criatura alguno de esos objetos? El Lobo, la piel del Sastre (<c>RandomDrop 414</c> al 100 %).</summary>
    public static bool SueltaAlgoDe(NpcInfo? criatura, IReadOnlySet<int> objetos) =>
        objetos.Count > 0 && criatura?.Drops is { } lo && lo.Any(d => d.ProbTirar > 0 && objetos.Contains(d.ObjIndex));

    /// <summary>
    /// EL COTO PARA SU OFICIO: el mapa con más criaturas de las que <paramref name="sirve"/> a <see cref="SaltosHastaElCoto"/> mapas o
    /// menos, y si no hay, el más cercano donde nazca alguna. SÓLO donde se caza sin peligro (<paramref name="seCazaAhi"/>): por un
    /// material no se arriesga lo que sí se arriesga por no tener dónde cazar. null si no hay ninguno.
    ///
    /// <para>Por cantidad y no por lo que rinde: lo que se busca es juntar, y por lo que rinde todos los Lobos empatan.</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? CotoParaSuOficio(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                           Func<int, bool> sirve, Func<int, bool>? pisable,
                                                                           Func<int, bool> seCazaAhi)
    {
        if (mapas.CotoMasPoblado(mapa, desde, sirve, SaltosHastaElCoto, pisable, seCazaAhi) is { } poblado)
            return (poblado.Mapa, poblado.X, poblado.Y, poblado.Saltos);
        return mapas.DondeHay(mapa, desde, sirve, seCazaAhi, pisable);
    }

    /// <summary>
    /// LO QUE SALE A CAZAR PARA SU OFICIO: los materiales que le faltan para fabricar lo que ya sabe (<see cref="MaterialesQueLeFaltan"/>).
    /// Vacío si no fabrica, si todavía no tiene la herramienta —primero la plata para comprarla— o si en su nivel ya no aprende nada.
    ///
    /// <para>Sólo mueve al Sastre: la piel de lobo la suelta el Lobo, y la leña y la raíz no las suelta nadie, se juntan. Medido en
    /// `oficios2`: el Sastre cazaba donde cazaba cualquiera y en tres cuartos de hora juntó UNA piel de las seis que pide la
    /// primera receta, con el Costurero comprado desde el nivel 9.</para>
    /// </summary>
    private IReadOnlySet<int> LoQueSaleACazarParaSuOficio(SentidosDelBot sentidos)
    {
        if (SuFabricacion is not { } fab) return NadaQueCazar;
        if (fab.Regla.Herramienta != 0 && sentidos.CuantoTiene(o => o == fab.Regla.Herramienta) == 0) return NadaQueCazar;
        int skill = sentidos.Skill(fab.Regla.Skill);
        if (skill < 0 || !ValeFabricar(skill, SkillProgression.CapForLevel(sentidos.Nivel), leHaceFalta: false)) return NadaQueCazar;
        return MaterialesQueLeFaltan(_entorno.Datos, fab.Recetas, fab.Regla, skill, obj => sentidos.CuantoTiene(o => o == obj));
    }

    private static readonly IReadOnlySet<int> NadaQueCazar = new HashSet<int>();

    /// <summary>
    /// DÓNDE SENTARSE A FABRICAR estando en un mapa inseguro: el más cercano de los que <paramref name="sirve"/> —los seguros—, con la
    /// baldosa por la que se entra. null si no hay ninguno a <see cref="Navegante.TopeDeMapas"/> saltos.
    ///
    /// <para>Con criaturas o sin ellas: fabricar ya pide que no haya nada hostil cerca. Pedir un pueblo sin ninguna dejaba al Sastre
    /// sin dónde desde el 19, donde cazaba: el río del 98 corta el camino al 20, y a cinco saltos los únicos seguros son el mapa
    /// 1 y la cuna, que tienen.</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y)? DondeSentarseAFabricar(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                     Func<int, bool> sirve, Func<int, bool>? pisable)
    {
        foreach (var (otro, _, entrada, _) in mapas.MapasAlcanzables(mapa, desde, Navegante.TopeDeMapas, pisable))
            if (sirve(otro)) return (otro, entrada.X, entrada.Y);
        return null;
    }

    /// <summary>Espera, sentado, a tener la energía de una fabricación más. Devuelve si la tiene; corta si algo urge o se
    /// acerca algo hostil.</summary>
    private async Task<bool> EsperarEnergiaAsync(SentidosDelBot sentidos, int energia, CancellationToken ct)
    {
        for (int i = 0; i < EsperasDeEnergia && sentidos.EnergiaEnPuntos < energia; i++)
        {
            if (Urge(sentidos) || HostilCerca(sentidos)) return false;
            await Task.Delay(EsperaDeEnergiaMs, ct);
        }
        return sentidos.EnergiaEnPuntos >= energia;
    }

    private const int EsperasDeEnergia = 15, EsperaDeEnergiaMs = 2_000;

    private void PostergarFabricacion() =>
        _proximaFabricacion = Environment.TickCount64 + _rng.Next(EnfriamientoFaenaMinS, EnfriamientoFaenaMaxS + 1) * 1_000L;

    private long _proximaFabricacion;

    /// <summary>Cuántas fabrica de una sentada, cuánto tarda cada una (lo que se tarda en apretar "construir" otra vez) y
    /// con cuánta energía se sienta a fabricar: la mitad larga, porque la otra mitad es para pelear.</summary>
    private const int FabricacionesPorTanda = 6, FabricarMinMs = 900, FabricarMaxMs = 2_200;
    private const double FuelleParaFabricar = 0.6;

    /// <summary>Cuánto de lo que junta para fabricar guarda antes de dejar de juntar.</summary>
    private const int InsumoQueGuarda = 100;

    private long _proximaFaena;

    /// <summary>La skill del oficio de esta clase.</summary>
    private byte SkillDeSuOficio => _clase switch
    {
        ClasePescador => (byte)GameLimits.SkillPesca,
        ClaseLenador or ClaseCarpintero => (byte)GameLimits.SkillTalar,
        ClaseMinero => (byte)GameLimits.SkillMineria,
        ClaseDruida => (byte)GameLimits.SkillBotanica,
        _ => 0,
    };

    /// <summary>
    /// DÓNDE VAN LOS PUNTOS DE NIVEL: la de su oficio, y el que fabrica, a la skill de FABRICAR sólo hasta la mínima de su
    /// primera receta. Es el mismo círculo que el de la magia (ver <see cref="RepartirPuntos"/>): cada receta pide una
    /// skill mínima —la cuchara 5, la Poción Violeta y las Vestimentas 10— y el servidor la rechaza en silencio si no
    /// llega, así que sin puntos no fabrica y sin fabricar la skill no sube.
    ///
    /// <para>PASADA LA MÍNIMA, LA SUBE FABRICANDO —un punto por cada una, hasta el tope del nivel— y los puntos van a la de
    /// juntar, que es de donde sale el material. Medido en `oficios1`: con todos los puntos a Carpintería, un Carpintero
    /// de nivel 12 tenía la skill en 30, que es justo el tope del nivel, y no fabricaba nunca: ya no le enseñaba nada, y
    /// en esta data lo fabricado vale menos que lo que gasta (seis pieles se venden a más que unas Vestimentas).</para>
    /// </summary>
    private byte SkillPropia(SentidosDelBot sentidos) =>
        SuFabricacion is { } f && LeFaltaParaFabricar(sentidos.Skill(f.Regla.Skill), MinimaParaFabricar(f.Recetas, f.Regla))
            ? (byte)f.Regla.Skill : SkillDeSuOficio;

    /// <summary>¿Le falta skill para su primera receta? Una skill que todavía no sabe (-1) cuenta como que falta.</summary>
    public static bool LeFaltaParaFabricar(int skill, int minima) => skill < minima;

    /// <summary>La skill que pide la receta más fácil de ese oficio (entre las que gastan algo): la cuchara, la Violeta o las
    /// Vestimentas. Se calcula una vez: la data no cambia con el servidor andando.</summary>
    private int MinimaParaFabricar(IReadOnlySet<int> recetas, Oficios.Fabricacion regla) =>
        _minimaParaFabricar ??= MinimaDeLasRecetas(_entorno.Datos, recetas, regla);

    private int? _minimaParaFabricar;

    /// <summary><see cref="MinimaParaFabricar"/> sobre la data. <c>int.MaxValue</c> si no hay ninguna receta.</summary>
    public static int MinimaDeLasRecetas(GameData datos, IReadOnlySet<int> recetas, Oficios.Fabricacion regla) =>
        recetas.Select(i => datos.Obj(i)).Where(o => o is not null && regla.Materiales(o).Any(m => m.PorUnidad > 0))
               .Select(o => regla.Pide(o!)).DefaultIfEmpty(int.MaxValue).Min();

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
        // Salvo en la ciudad del otro bando: ahí revive entre los guardias que lo mataron.
        if (!HayGuardiaQueLoAtaca(sentidos.Mapa) && sentidos.MasCercano(EsSacerdote) is { } c && AlAlcanceDelAltar(sentidos, c.Quien))
        {
            await Task.Delay(RebanadaDePausaMs, ct);
            return true;
        }

        // Y SI LO VE PERO LEJOS, TAMPOCO SE VA DERECHO. Acá se daba un paso codicioso hacia él, y el paso
        // codicioso se traba con cualquier pared en el medio: medido, un cadáver a ocho baldosas del sacerdote
        // en el mapa de la cuna se pasó cuatro minutos oscilando entre dos baldosas. Peor todavía, esa rama
        // devolvía "hice algo", así que nunca se llegaba a la salida de volver al hogar. Se va por el
        // navegante, igual que al mercader, que aprendió lo mismo.

        if (_entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => EsSacerdote((short)f), m => !HayGuardiaQueLoAtaca(m),
                                    Pisable(sentidos)) is { } templo)
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
    /// DÓNDE CAZAR: el coto que paga más cercano, salvo que tenga pocas contra el más poblado a <see cref="SaltosHastaElCoto"/> mapas o
    /// menos; ahí, el poblado. Si los dos caen en el mismo mapa, lo que paga. <paramref name="porElOro"/> dice cuál eligió, que es lo
    /// que decide entre qué spawns se reparte al llegar.
    ///
    /// <para>Lo que paga primero salió de bots de nivel 5 a 7 que no ganaban una moneda. Para uno recién creado era al revés: medido en
    /// `sano3`, siete de ocho se mudaron de la cuna al mapa 62 en el primer minuto y pasaron ahí la media hora repartiéndose cuatro
    /// Águilas, y entre los ocho cazaron cuatro criaturas. En la cuna un nivel 1 tiene 38 presas y ninguna paga; recién en nivel 3
    /// pagan los Escorpiones, y ahí la cuna misma es el coto que paga.</para>
    ///
    /// <para>Y NO SE CAZA DONDE VIVE LO QUE LO MATA Y NO HAY RUNA: ver <see cref="SeCazaAhi"/>. Los mapas que no pasan
    /// <paramref name="seCazaAhi"/> no son coto, tampoco el propio; si con ese filtro no queda ninguno, se elige como
    /// siempre: mejor cazar con cuidado que quedarse sin dónde, la misma regla de <see cref="LejosDelPeligro"/>.</para>
    ///
    /// <para>SALVO LO QUE <paramref name="nunca"/> DESCARTA, que no entra ni como repuesto: el mapa de los guardias que van por él.
    /// Medido en `pelea_b2`: un Guerrero cortó en (28,40) la salida del 806 —cuatro guardias republicanos— y desde ahí la
    /// búsqueda sin peligro no llega a ninguno (los cuatro vecinos tienen algo que lo mata), así que el repuesto eligió cazar en
    /// el mismo 806, hacia donde nacen los guardias, y el Clérigo lo mató. Desde cualquier baldosa del 806 elegía el 806.</para>
    ///
    /// <para>Y ENTRE LOS QUE RINDEN IGUAL, EL SUYO: <paramref name="desempate"/> ordena los empates de lo que rinde (ver
    /// <see cref="CuantoLeGusta"/>). Sin él gana siempre el más cercano, y todos los que salen del mismo lugar van al mismo
    /// coto.</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? ElegirCoto(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                     Func<int, bool> esPresa, Func<int, bool> paga,
                                                                     Func<int, int> rinde,
                                                                     Func<int, bool>? pisable, out bool porElOro,
                                                                     out int porLoQueRinde, Func<int, bool>? seCazaAhi = null,
                                                                     Func<int, int>? desempate = null, Func<int, bool>? nunca = null)
    {
        if (seCazaAhi is not null
            && ElegirCotoEntre(mapas, mapa, desde, esPresa, paga, rinde, pisable, seCazaAhi, desempate,
                               out porElOro, out porLoQueRinde) is { } sinPeligro)
            return sinPeligro;
        return ElegirCotoEntre(mapas, mapa, desde, esPresa, paga, rinde, pisable, nunca is null ? null : m => !nunca(m), desempate,
                               out porElOro, out porLoQueRinde);
    }

    /// <summary><see cref="ElegirCoto"/> entre los mapas que cumplen <paramref name="sirve"/> (todos, si es null).</summary>
    private static (int Mapa, byte X, byte Y, int Saltos)? ElegirCotoEntre(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                           Func<int, bool> esPresa, Func<int, bool> paga,
                                                                           Func<int, int> rinde, Func<int, bool>? pisable,
                                                                           Func<int, bool>? sirve, Func<int, int>? desempate,
                                                                           out bool porElOro, out int porLoQueRinde)
    {
        porLoQueRinde = 0;
        // LO QUE RINDE MANDA CUANDO LA DIFERENCIA ES GRANDE, y va primero: ni la cantidad ni el oro sirven para salir
        // de la cuna. Preguntado a la data, lo más gordo que un bot se anima a cazar en la cuna es el Lobo —115 de
        // experiencia— y ahí se queda para siempre, mientras a dos mapas hay Esqueletos Guerreros de 270 desde el
        // nivel 13 y Orcos de 505 desde el 20. Con 53 presas, la cuna gana siempre por cantidad.
        // Donde está parado no rinde nada si ahí no se caza: con cualquier coto que sirva, se va.
        var acaRinde = sirve is null || sirve(mapa) ? mapas.LoQueMasRinde(mapa, desde, rinde) : null;
        if (mapas.CotoQueMasRinde(mapa, desde, rinde, SaltosHastaElCoto, pisable, sirve, desempate) is { } gordo && gordo.Mapa != mapa
            && ValeMudarsePorLoQueRinde(acaRinde?.Rinde ?? 0, gordo.Rinde))
        {
            porElOro = false;
            porLoQueRinde = gordo.Rinde;
            return (gordo.Mapa, gordo.X, gordo.Y, gordo.Saltos);
        }

        Func<int, bool> conOro = f => esPresa(f) && paga(f);
        var quePaga = mapas.DondeHay(mapa, desde, conOro, sirve, pisable);
        var poblado = mapas.CotoMasPoblado(mapa, desde, esPresa, SaltosHastaElCoto, pisable, sirve);
        porElOro = true;
        if (quePaga is { } p && (poblado is not { } r || r.Mapa == p.Mapa
                                  || ValeMudarsePorElOro(mapas.CuantasSeAlcanzan(p.Mapa, (p.X, p.Y), conOro), r.Cuantas)))
            return p;
        porElOro = false;
        if (poblado is { } q) return (q.Mapa, q.X, q.Y, q.Saltos);
        return mapas.DondeHay(mapa, desde, esPresa, sirve, pisable);
    }

    /// <summary>
    /// ¿SE CAZA EN ESE MAPA? No si es inseguro y nace ahí algo peligroso para él. En un mapa inseguro la runa no saca a
    /// nadie vivo, y de lo que persigue no se escapa caminando: pelea o muere.
    ///
    /// <para>Medido en las nueve corridas de campo del 15 al 17/09: quince muertes, y TRECE en dos mapas inseguros con un
    /// bicho así — diez del Basilisco del 202 (golpe de hasta 150, contra bots de nivel 9 a 17), dos
    /// del Señor de las sombras del 4 y un Lobo en el mismo 202. Los bots iban al 202 por sus trece Lobos, que pagan. En
    /// una de esas muertes un Leñador de nivel 17 se tomó diez pociones de a treinta mientras le entraban golpes de 44, 49
    /// y 95.</para>
    ///
    /// <para>En un mapa seguro no se descarta nada: ahí la runa lo saca, y la cuna, que es donde viven los nivel 1, tiene
    /// Lobos que para ellos son peligrosos.</para>
    /// </summary>
    public static bool SeCazaAhi(bool mapaInseguro, IEnumerable<int> loQueNace, Func<int, bool> esPeligroso) =>
        !mapaInseguro || !loQueNace.Any(esPeligroso);

    /// <summary>
    /// ¿ESTÁ LLENO EL LUGAR? Cuando se le acabó la paciencia sin nada que cazar a la vista y ve a <see cref="PersonasQueLlenan"/> o más
    /// personas, el mapa donde está se deja por <see cref="LlenoMs"/>, como el que llega a donde cazan muchos y se va a otro lado: el
    /// coto sale de <see cref="CotoSinVolverAlLleno"/>.
    ///
    /// <para>Medido en `oficios3` y `oficios4` (18 bots desde nivel 1): tres cuartos del tiempo de los bots en la cuna, casi siempre
    /// con diez a dieciocho juntos, y el 208 —el otro mapa de novatos— casi vacío. Sin nadie a la vista fue lo más grande del reparto
    /// del tiempo: entre un cuarto y un tercio del de todos. La cuna se elegía por cuántas criaturas nacen ahí, no por cuántos las
    /// cazan, y reaparecen a los treinta segundos y lejos de los jugadores.</para>
    /// </summary>
    public static bool EstaLleno(int personasALaVista) => personasALaVista >= PersonasQueLlenan;

    /// <summary>
    /// A DÓNDE IRSE DE UN LUGAR LLENO: <see cref="ElegirCoto"/> entre los mapas que <paramref name="sirve"/>
    /// (<see cref="SirveParaIrseDelLleno"/>), sin caer a elegir como siempre: null si no hay ninguno, y el bot se queda.
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? OtroCotoSinPeligro(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                             Func<int, bool> esPresa, Func<int, bool> paga,
                                                                             Func<int, int> rinde, Func<int, bool>? pisable,
                                                                             Func<int, bool> sirve, Func<int, int>? desempate,
                                                                             out bool porElOro, out int porLoQueRinde) =>
        ElegirCotoEntre(mapas, mapa, desde, esPresa, paga, rinde, pisable, sirve, desempate, out porElOro, out porLoQueRinde);

    /// <summary>
    /// ¿SIRVE PARA IRSE AHÍ DE UN LUGAR LLENO? Si no está lleno también y no nace ahí NADA peligroso para él, sea el mapa seguro o
    /// no: por irse de un lugar lleno no se va a donde lo matan.
    ///
    /// <para>Medido en `oficios6`, con la regla del lugar lleno sin esto: nueve de las catorce muertes en el 208, el otro mapa de
    /// novatos, del Señor de las sombras (pega de 60 a 75 y no camina), a bots de nivel 4 a 11. El coto del 208 era siempre la
    /// baldosa (45,40), a cinco pasos de donde nace, y el 208 es seguro: <see cref="SeCazaAhi"/> no descarta nada en un mapa
    /// seguro, porque la cuna del nivel 1 tiene Lobos que para él son peligrosos.</para>
    /// </summary>
    public static bool SirveParaIrseDelLleno(bool tambienLleno, IEnumerable<int> loQueNace, Func<int, bool> esPeligroso) =>
        !tambienLleno && !loQueNace.Any(esPeligroso);

    /// <summary>¿Sigue lleno ese mapa? Hasta que pasa el rato desde que lo vio lleno.</summary>
    public static bool SigueLleno(IReadOnlyDictionary<int, long> llenoHasta, int mapa, long ahora) =>
        llenoHasta.TryGetValue(mapa, out long hasta) && ahora < hasta;

    /// <summary>
    /// EL COTO, CON LOS LUGARES LLENOS A LA VISTA. Parado en uno lleno, el otro coto sin peligro (<see cref="OtroCotoSinPeligro"/>);
    /// si no hay, el de siempre (<see cref="ElegirCoto"/>). Y si el de siempre cae en OTRO mapa que todavía está lleno para él, ahí no
    /// se vuelve mientras dura: el otro coto sin peligro, o null y se queda donde está.
    ///
    /// <para>Medido en `oficios9`: de 141 veces que un bot dejó un lugar lleno, 60 lo volvió a elegir en la búsqueda siguiente. El
    /// Sastre fue y vino seis veces entre la cuna y el 58 antes de llegar a nivel 5: en el 58 hay cuatro presas y en la cuna
    /// treinta y ocho, y el coto de siempre elige por cuántas hay.</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? CotoSinVolverAlLleno(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                               Func<int, bool> esPresa, Func<int, bool> paga,
                                                                               Func<int, int> rinde, Func<int, bool>? pisable,
                                                                               Func<int, bool> seCazaAhi, Func<int, bool> sigueLleno,
                                                                               Func<int, bool> sirveParaIrseDelLleno,
                                                                               Func<int, int>? desempate,
                                                                               out bool porElOro, out int porLoQueRinde,
                                                                               Func<int, bool>? nunca = null)
    {
        if (sigueLleno(mapa)
            && OtroCotoSinPeligro(mapas, mapa, desde, esPresa, paga, rinde, pisable, sirveParaIrseDelLleno, desempate,
                                  out porElOro, out porLoQueRinde) is { } otro)
            return otro;
        var elegido = ElegirCoto(mapas, mapa, desde, esPresa, paga, rinde, pisable, out porElOro, out porLoQueRinde, seCazaAhi, desempate,
                                 nunca);
        if (elegido is { } e && e.Mapa != mapa && sigueLleno(e.Mapa))
            return OtroCotoSinPeligro(mapas, mapa, desde, esPresa, paga, rinde, pisable, sirveParaIrseDelLleno, desempate,
                                      out porElOro, out porLoQueRinde);
        return elegido;
    }

    private readonly Dictionary<int, long> _llenoHasta = new();

    /// <summary>
    /// ¿NO VUELVE A CRUZAR ESE MAPA? No lo cruza de viaje —salvo que sea el destino— si hace menos de un rato se fue de ahí
    /// huyendo de algo que no podía lastimar o de lo del hueco (<see cref="SeVaDelMapaPor"/>).
    ///
    /// <para>La huida ya dejaba ese mapa fuera de los cotos (<see cref="SigueLleno"/>), pero el VIAJE no lo miraba: medido en
    /// `hueco2` (29/09), una Ladrona de nivel 8 se fue del 19 por un Lobo al que no lastimaba, salió al 98 por el borde, eligió
    /// cazar en el 27, y el camino al 27 volvía a entrar al 19 por la misma baldosa, al lado del mismo Lobo. Treinta y cinco
    /// minutos de ida y vuelta, una por segundo: 520 viajes. Es el filtro del viaje que no mira lo mismo que la acción.</para>
    /// </summary>
    public static bool NoVuelveACruzar(IReadOnlyDictionary<int, long> huyoDe, int mapa, int mapaDestino, long ahora) =>
        mapa != mapaDestino && huyoDe.TryGetValue(mapa, out long hasta) && ahora < hasta;

    /// <summary>Los mapas de los que se fue huyendo, hasta cuándo no los cruza de viaje (ver <see cref="NoVuelveACruzar"/>).</summary>
    private readonly Dictionary<int, long> _huyoDe = new();
    private const int PersonasQueLlenan = 3, LlenoMs = 5 * 60_000;

    /// <summary>
    /// CUÁNTO LE GUSTA ESE MAPA A ESTE BOT, para desempatar entre cotos que rinden lo mismo. Sale de la semilla del nombre,
    /// así que es siempre el mismo para el mismo bot —cada uno tiene "su" lugar— y distinto entre bots.
    ///
    /// <para>Medido en `tarde2`: cinco de los ocho bots, de nivel 14 a 19, cazando en el mismo mapa (807), veintidós presas
    /// para cinco, peleando el 8 % del tiempo; mientras tanto Fabia, sola en el 261, peleaba el 28 % y subió tres niveles
    /// en catorce minutos. Los cinco salían de comprar en el 815, y a dos saltos de ahí lo que más rinde —la Rata Gigante,
    /// 220— empata en varios mapas: ganaba siempre el más cercano.</para>
    /// </summary>
    public static int CuantoLeGusta(int propio, int mapa)
    {
        uint h = unchecked((uint)propio * 0x9E3779B1u ^ (uint)mapa * 0x85EBCA77u);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        return (int)(h & int.MaxValue);
    }

    /// <summary>
    /// DE LOS SPAWNS DONDE CAZAR, LOS QUE NO TIENEN ALGO PELIGROSO NACIENDO A LA VISTA (<see cref="AlAlcanceDeSuVista"/>). Si no queda
    /// ninguno, todos: mejor ir a cazar que quedarse sin coto.
    /// </summary>
    public static List<(byte X, byte Y)> LejosDelPeligro(IReadOnlyList<(byte X, byte Y)> presas, IReadOnlyCollection<(byte X, byte Y)> peligros)
    {
        var lejos = presas.Where(t => !peligros.Any(o => AlAlcanceDeSuVista(t, o))).ToList();
        return lejos.Count > 0 ? lejos : presas.ToList();
    }

    /// <summary>
    /// EL DESTINO EN OTRO MAPA, LEJOS DEL PELIGRO: el spawn que eligió el navegante si no tiene nada peligroso naciendo a la vista; si
    /// lo tiene, el más cercano a él que no lo tenga y al que se llega desde ahí (la misma zona del mapa). Si no hay ninguno, el del
    /// navegante.
    ///
    /// <para>Para otro mapa el navegante da el spawn de presa más cercano a la entrada, sin mirar qué nace al lado, y el reparto de
    /// <see cref="LejosDelPeligro"/> recién corría al llegar. Medido en `oficios6`: el coto del 208 era siempre la baldosa (45,40), a
    /// cinco pasos de donde nace el Señor de las sombras (pega de 60 a 75), y fueron ahí tanto los que dejaban la cuna llena como
    /// los que iban por el oro: nueve de catorce muertes.</para>
    /// </summary>
    public static (byte X, byte Y) DestinoLejosDelPeligro(Navegante mapas, int mapa, (byte X, byte Y) elegido,
                                                          IReadOnlyList<(byte X, byte Y)> presas,
                                                          IReadOnlyCollection<(byte X, byte Y)> peligros)
    {
        if (!peligros.Any(o => AlAlcanceDeSuVista(elegido, o))) return elegido;
        (byte X, byte Y)? mejor = null;
        foreach (var t in presas.OrderBy(t => Math.Abs(t.X - elegido.X) + Math.Abs(t.Y - elegido.Y)))
        {
            if (peligros.Any(o => AlAlcanceDeSuVista(t, o))) continue;
            if (mapas.HayComoLlegar(mapa, elegido, t, ToleranciaDelCoto) is null) continue;
            mejor = t;
            break;
        }
        return mejor ?? elegido;
    }

    /// <summary>¿Vale ir por lo que paga? Si tiene al menos una que paga por cada <see cref="PresasPorCadaUnaQuePaga"/> presas del
    /// coto más poblado.</summary>
    public static bool ValeMudarsePorElOro(int quePagan, int presasDelPoblado) => quePagan * PresasPorCadaUnaQuePaga >= presasDelPoblado;

    /// <summary>
    /// ¿VALE MUDARSE POR LO QUE RINDE? Cuando el bicho más gordo que se anima a cazar allá da varias veces lo que el
    /// más gordo de acá. La comparación es contra lo que TIENE, no contra un número fijo: en la cuna un nivel 1 caza
    /// Escorpiones de 38 y lo mejor a dos mapas son Serpientes de 25, así que se queda —y hace bien—; el mismo bot en
    /// nivel 13 sigue con el Lobo de 115 teniendo Esqueletos Guerreros de 270 al lado, y ahí se muda.
    /// </summary>
    public static bool ValeMudarsePorLoQueRinde(int rindeAca, int rindeAlla) => rindeAca <= 0 ? rindeAlla > 0 : rindeAlla >= rindeAca * VecesQueTieneQueRendir;

    private const int SaltosHastaElCoto = 2, PresasPorCadaUnaQuePaga = 4, VecesQueTieneQueRendir = 2;

    /// <summary>
    /// Cuando hace rato que no aparece una presa, viajar a un mapa donde haya. Devuelve si se ocupó de esto.
    ///
    /// <para>La PACIENCIA es lo que hace que esto no sea un bot que vive viajando: recién se mueve después de
    /// varios minutos sin ver nada a lo que animarse, que es más o menos lo que aguanta una persona dando
    /// vueltas al pedo. Y el destino sale del navegante con el MISMO criterio con el que elige presa, así que
    /// no viaja a un mapa lleno de cosas que le quedan grandes.</para>
    /// </summary>
    private async Task<bool> BuscarDondeCazarAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                                   int vidaMaxima, int nivel, bool veAlgunaPresa,
                                                   Task cerrada, CancellationToken ct)
    {
        // SIN ENERGÍA PARA PELEAR TAMPOCO SE SALE A BUSCAR DÓNDE CAZAR: se llega y no se puede pegar. Medido: con
        // GolpeTolerable en 3, las diez muertes de una corrida de quince minutos fueron de los dos bots con la energía en
        // cero (ocho y dos), y la sonda ya los había visto así saliendo a buscar coto. Para ellos, con el valor 3 el lobo
        // pasa a ser presa, y el lobo paga cincuenta: los cotos que se elegían eran de lobos.
        if (!TieneFuelleParaPelear(sentidos)) return false;
        // Y LA PACIENCIA NO CORRE DONDE NO NACE NADA: ver <see cref="AcaPuedeAparecerAlgo"/>.
        if (AcaPuedeAparecerAlgo(sentidos.Mapa)
            && EsperaParaBuscarCoto(veAlgunaPresa, Environment.TickCount64 - _ultimaPresaQuePaga)) return false;
        _ultimaPresaQuePaga = Environment.TickCount64;   // se haya podido o no: no se reintenta en el paso siguiente

        // PRIMERO UN COTO QUE PAGUE, y recién si no hay ninguno al alcance, cualquiera. El coto se elegía por
        // "hay al menos una presa", sin mirar si soltaba oro — y así la única serpiente del mapa 1 alcanzaba para
        // mandar ahí a cinco bots, que se la repartieron durante media hora sin ganar una moneda. Pero no a cualquier
        // precio: ver `ElegirCoto`.
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        Func<int, bool> esPresa = f => EsPresa((short)f, vidaMaxima, nivel, conPociones, curaVeneno);
        Func<int, bool> esPeligroso = f => EsPeligroso((short)f, vidaMaxima, nivel, conPociones, curaVeneno);
        Func<int, IEnumerable<int>> loQueNace = m => _entorno.Datos.MapSpawns.TryGetValue(m, out var nacen)
            ? nacen.Select(sp => sp.NpcIndex) : Enumerable.Empty<int>();
        // EN EL MAPA DE LOS GUARDIAS QUE VAN POR ÉL NO SE CAZA, tampoco en una ciudad del otro bando que no sea pk: ver `ElegirCoto`.
        Func<int, bool> seCazaAhi = m => SeCazaAhi(_entorno.Datos.IsPkMap(m), loQueNace(m), esPeligroso) && !HayGuardiaQueLoAtaca(m);
        // EL LUGAR LLENO SE DEJA POR UN RATO: ver `EstaLleno`.
        long ahora = Environment.TickCount64;
        if (!veAlgunaPresa && EstaLleno(sentidos.PersonasALaVista())) _llenoHasta[sentidos.Mapa] = ahora + LlenoMs;
        // EL SASTRE VA A DONDE ESTÁN LOS LOBOS cuando le faltan pieles: ver `LoQueSaleACazarParaSuOficio`.
        var paraSuOficio = LoQueSaleACazarParaSuOficio(sentidos);
        Func<int, bool> esPresaDelOficio = f => esPresa(f) && SueltaAlgoDe(_entorno.Datos.Npc(f), paraSuOficio);
        var hallado = paraSuOficio.Count > 0
            ? CotoParaSuOficio(_entorno.Mapas, sentidos.Mapa, sentidos.Posicion, esPresaDelOficio, CruzaSinPeligro(sentidos), seCazaAhi)
            : null;
        bool porElOro = false;
        int porLoQueRinde = 0;
        Func<int, bool> sirve = esPresaDelOficio;
        Func<int, int> rinde = f => esPresa(f) ? (int)(_entorno.Datos.Npc(f)?.GiveExp ?? 0) : 0;
        if (hallado is null)
        {
            hallado = CotoSinVolverAlLleno(_entorno.Mapas, sentidos.Mapa, sentidos.Posicion, esPresa, f => PagaOro((short)f), rinde,
                                           CruzaSinPeligro(sentidos), seCazaAhi, m => SigueLleno(_llenoHasta, m, ahora),
                                           m => SirveParaIrseDelLleno(SigueLleno(_llenoHasta, m, ahora), loQueNace(m), esPeligroso),
                                           m => CuantoLeGusta(_propio, m), out porElOro, out porLoQueRinde, HayGuardiaQueLoAtaca);
            sirve = porElOro ? (f => esPresa(f) && PagaOro((short)f)) : esPresa;
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
        if (_entorno.Datos.MapSpawns.TryGetValue(coto.Mapa, out var spawns))
        {
            // NI A LA VISTA DE DONDE NACE ALGO PELIGROSO, que ahí vuelve a aparecer y camina hacia el que ve. Medido en `normal3`: de
            // las siete muertes de bots de nivel 1, dos fueron a la vista de donde nace el Lobo del medio de la cuna.
            var peligros = spawns.Where(sp => EsPeligroso((short)sp.NpcIndex, vidaMaxima, nivel, conPociones, curaVeneno))
                                 .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
            var presas = spawns.Where(sp => sirve(sp.NpcIndex)).Select(sp => ((byte)sp.X, (byte)sp.Y)).Distinct().ToList();
            if (coto.Mapa == sentidos.Mapa)
            {
                var opciones = LejosDelPeligro(presas, peligros)
                                     .Where(t => _entorno.Mapas.HayComoLlegar(coto.Mapa, sentidos.Posicion, (t.X, t.Y),
                                                                              ToleranciaDelCoto) is not null)
                                     .ToList();
                if (opciones.Count > 0) destino = opciones[_rng.Next(opciones.Count)];
            }
            else
                // Y EN OTRO MAPA TAMBIÉN: ver `DestinoLejosDelPeligro`.
                destino = DestinoLejosDelPeligro(_entorno.Mapas, coto.Mapa, destino, presas, peligros);
        }

        // "HAY PRESAS EN ESTE MAPA" NO ES "HAY PRESAS DONDE ESTOY PARADO". Acá se devolvía false cuando el
        // coto caía en el mapa propio, dando por hecho que entonces ya estaba bien ubicado — y no: el paseo
        // no se aleja más de `RadioDeCasa` de donde entró, así que un bot que aparece en una punta de un mapa
        // de cien por cien se queda en su burbuja de veinte baldosas para siempre. Medido con el perfilador:
        // cuatro bots con `pasear` al 100% del tiempo y "sin presa a la vista" en TODAS las vueltas, en mapas
        // que sí tienen criaturas. Se camina hasta donde aparecen, esté en este mapa o en otro.
        // NO SE PASA DE LARGO UNA PRESA POR LLEGAR AL COTO. El viaje al coto cruza medio mapa y el bot lo caminaba entero mirando
        // el piso: lo único que lo cortaba era la vida. Con las criaturas reapareciendo desparramadas —que es lo que hace el
        // original— lo que hay en el camino vale tanto como lo que hay en el destino, y nadie pasa al lado de un bicho que salió a
        // matar. Medido en `verdad2`: ocho bots caminando entre veinte y treinta y cuatro baldosas por minuto y setenta y dos
        // criaturas entre todos en quince minutos, con dos bots que no mataron ninguna.
        // Y DEL LUGAR LLENO SE SALE SIN FRENAR: ver `FrenaEnElCamino`.
        _cortoElViaje = false;
        if (!await IrHastaAsync(transporte, sentidos, coto.Mapa, destino.X, destino.Y, cerrada, ct, ToleranciaDelCoto,
                                cortaSi: () => FrenaEnElCamino(_llenoHasta, sentidos.Mapa, coto.Mapa, Environment.TickCount64)
                                               && HayAlgoQueCazarCerca(sentidos, vidaMaxima, nivel, conPociones, curaVeneno, porLoQueRinde)))
        {
            // El que frenó porque vio algo no está encerrado: se ocupó de esto, y la vuelta siguiente elige presa.
            if (_cortoElViaje) { _cortoElViaje = false; return true; }
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

    /// <summary>
    /// ¿VALE FRENAR EL VIAJE POR ESTO? Si el viaje es porque el destino RINDE MÁS, sólo por algo que rinda parecido:
    /// el que se muda porque acá el bicho más gordo da 115 y allá 270 no puede pararse a matar los de 115, porque
    /// entonces no llega nunca. Medido en `seguir3`: Ciro eligió `m195 -> m202` cuatro veces —niveles 13, 14 y 15— y
    /// se quedó veinte minutos en m195, que siempre tiene un lobo a la vista.
    ///
    /// <para>Con <paramref name="rindeElDestino"/> en cero —el viaje no es por lo que rinde— vale cualquier presa,
    /// que es la conducta de siempre: nadie pasa al lado de un bicho que salió a matar.</para>
    /// </summary>
    public static bool ValeFrenarElViaje(int rindeLoDelCamino, int rindeElDestino) =>
        rindeElDestino <= 0 || rindeLoDelCamino * VecesQueTieneQueRendir >= rindeElDestino;

    /// <summary>
    /// ¿HAY ALGO QUE CAZAR ACÁ MISMO, como para dejar un viaje por la mitad? Mira lo que tiene a la vista con el radio de su
    /// temperamento —el paseante no frena por cualquier cosa—, pide vida y energía para pelear, y se sigue de largo si lo que hay
    /// cerca es peligroso: frenar al lado de un Lobo para pegarle a un Murciélago es la forma más rápida de morirse.
    /// </summary>
    public static bool HayAlgoQueCazarCerca(SentidosDelBot sentidos, GameData datos, int vidaMaxima, int nivel,
                                            bool conPociones, bool curaVeneno, int radioSuyo, int rindeElDestino = 0,
                                            Func<short, bool>? tambienPeligroso = null)
    {
        var presa = sentidos.MasCercano(ficha => EsPresaPara(datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno)
                                                 && ValeFrenarElViaje((int)(datos.Npc(ficha)?.GiveExp ?? 0), rindeElDestino));
        var peligro = sentidos.MasCercano(ficha => EsPeligrosoPara(datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno)
                                                   || tambienPeligroso?.Invoke(ficha) == true);
        // Lo que paga se persigue desde más lejos, igual que al elegir presa.
        int alcance = presa is { } p && datos.Npc(p.Quien.FichaNpc) is { GiveGld: > 0 } ? RadioDeCaza : radioSuyo;
        bool peligroALaVista = peligro is { } temible && AlAlcanceDeSuVista(sentidos.Posicion, (temible.Quien.X, temible.Quien.Y));
        return CortaElViajePorLaPresa(presa?.Pasos ?? -1, alcance, sentidos.Salud, TieneFuelleParaPelear(sentidos), peligroALaVista);
    }

    /// <summary>La misma pregunta con lo que ya sabe este bot, y avisando por <see cref="_cortoElViaje"/> que la parada fue por
    /// esto y no por una traba.</summary>
    private bool HayAlgoQueCazarCerca(SentidosDelBot sentidos, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno,
                                      int rindeElDestino = 0)
    {
        _cortoElViaje = HayAlgoQueCazarCerca(sentidos, _entorno.Datos, vidaMaxima, nivel, conPociones, curaVeneno,
                                             RadioDeCazaSuyo, rindeElDestino, f => EsGuardiaQueVaPorEl(f, nivel));
        return _cortoElViaje;
    }

    /// <summary>Cuándo vale más lo que hay a la vista que seguir hasta el destino: una presa al alcance, con vida y energía para
    /// pelear y sin nada peligroso a la vista. <paramref name="pasosDeLaPresa"/> negativo = no hay ninguna.</summary>
    public static bool CortaElViajePorLaPresa(int pasosDeLaPresa, int alcance, double salud, bool conFuelle,
                                              bool peligroALaVista) =>
        pasosDeLaPresa >= 0 && pasosDeLaPresa <= alcance && salud >= VuelveAPelear && conFuelle && !peligroALaVista;

    /// <summary>
    /// ¿FRENA EN EL CAMINO AL COTO POR LO QUE HAYA A MANO? No mientras sigue adentro del lugar lleno del que se está yendo
    /// (<see cref="EstaLleno"/>): ahí lo que aparece se lo llevan los otros, y frenar es quedarse. Ya afuera, o con el coto en el
    /// mismo mapa, como siempre.
    ///
    /// <para>Medido en `early1` (29/09, 18 bots desde nivel 1): Petra eligió irse de la cuna al 35 nueve veces en seis minutos,
    /// con nivel 1, sin salir nunca de la cuna ni subir de nivel; Quim, trece veces en once minutos con nivel 8, y un rato antes,
    /// en el 35, había subido del 5 al 8 en medio minuto.</para>
    /// </summary>
    public static bool FrenaEnElCamino(IReadOnlyDictionary<int, long> llenoHasta, int mapaAhora, int mapaDelCoto, long ahora) =>
        mapaAhora == mapaDelCoto || !SigueLleno(llenoHasta, mapaAhora, ahora);

    private bool _cortoElViaje;

    /// <summary>Cuánto aguanta sin ver nada que cazar antes de mudarse.</summary>
    /// <summary>El nivel con el que eligió el coto donde está; cuando cambia, vuelve a mirar.</summary>
    private int _nivelDelCoto;

    /// <summary>
    /// ¿HAY QUE VOLVER A MIRAR DÓNDE ESTÁ CAZANDO? Cuando subió de nivel desde que eligió este coto. La paciencia de
    /// <see cref="BuscarDondeCazarAsync"/> es para "no veo nada" y en la cuna siempre hay algo, así que no se agotaba
    /// nunca: el bot no se preguntaba jamás si eso todavía le rendía. Un cero de entrada también cuenta como cambio,
    /// que es el bot recién entrado al mundo.
    /// </summary>
    public static bool HayQueRevisarElCoto(int nivelDelCoto, int nivelAhora) => nivelDelCoto != nivelAhora;

    /// <summary>
    /// ¿VE UNA PRESA A LA QUE IRÍA? Para la paciencia de mudarse de coto, lo que cuenta no es ver algo cazable sino ver algo
    /// que vaya a cazar. La vista alcanza un área de bloques enteros alrededor, y el temperamento decide cuántas baldosas
    /// camina por una pelea (<see cref="RadioDeCazaSuyo"/>: 12 el cazador, 6 el tranquilo, 4 el quieto). Una presa más lejos
    /// que eso no se pelea NUNCA.
    ///
    /// <para>Contándola igual, el bot tomaba la paciencia larga —tres minutos— y se quedaba paseando alrededor de algo que no
    /// iba a atacar. Medido con la sonda del ciclo: `pasear` con una presa lejos a la vista fue el 49,4 % del tiempo de ocho
    /// bots en veinticinco minutos, la rama más grande de lejos, contra un 3,6 % de pelear. Sin presa que valga, la paciencia
    /// corta de treinta segundos lo manda al coto, y el viaje al coto es el que lo deja parado donde las criaturas nacen.</para>
    /// </summary>
    public static bool VeUnaPresaQueIriaABuscar(int? pasosHastaLaPresa, int alcance) =>
        pasosHastaLaPresa is { } pasos && pasos <= alcance;

    /// <summary>¿Puede aparecer algo para cazar en el mapa donde está parado?</summary>
    private bool AcaPuedeAparecerAlgo(int mapa) =>
        _entorno.Datos.MapSpawns.TryGetValue(mapa, out var nacen)
        && AlgunoEsHostil(nacen, f => _entorno.Datos.Npc((short)f) is { Hostile: true });

    /// <summary>
    /// ¿NACE ALGO HOSTIL EN ESE MAPA? Si no nace una sola criatura, esperar no sirve: por mucho tiempo que se le dé no va a
    /// aparecer nada. Es el caso del pueblo, donde el bot cae cada vez que va de compras — y donde la paciencia de treinta
    /// segundos lo dejaba paseando entre mercaderes.
    ///
    /// <para>Medido con la sonda del ciclo, abriendo el «no veo presa» por lo que el bot tiene a la vista: el 13,9 % del
    /// tiempo de ocho bots se iba en vueltas donde lo único visible eran NPCs no hostiles. La mudanza ya existía —es la
    /// rama de abajo del ciclo—; lo que sobraba era la espera antes de decidirla.</para>
    /// </summary>
    public static bool AlgunoEsHostil(IEnumerable<MapNpcSpawn> nacen, Func<int, bool> esHostil) =>
        nacen.Any(s => esHostil(s.NpcIndex));

    /// <summary>
    /// ¿ESPERA ANTES DE VOLVER A BUSCAR COTO? Depende de si VE alguna presa, que son dos situaciones distintas
    /// y estaban con la misma paciencia.
    ///
    /// <para>"Veo presas pero ninguna me rinde" puede esperar: el bot igual está cazando y juntando algo. "No
    /// veo NINGUNA" no, porque lo único que hace mientras tanto es pasear al azar. Medido con una sonda que
    /// cuenta el primer motivo por el que no pelea, en 1355 vueltas de ocho bots: <b>63 % de las vueltas sin una
    /// sola presa a la vista</b> —y la cuna tiene 53 hostiles—, contra 9 % peleando. Con tres minutos de espera,
    /// el que se alejó del coto tarda eso en enterarse.</para>
    /// </summary>
    public static bool EsperaParaBuscarCoto(bool veAlgunaPresa, long desdeLaUltimaQuePaga) =>
        desdeLaUltimaQuePaga < (veAlgunaPresa ? PacienciaSinPresaMs : PacienciaSinNingunaMs);

    private const int PacienciaSinPresaMs = 3 * 60_000;

    /// <summary>Lo que espera para volver a mirar dónde cazar cuando NO ve una sola presa. Corto, pero no cero:
    /// buscar coto recorre varios mapas y el bot no tiene por qué hacerlo en cada vuelta del ciclo.</summary>
    private const int PacienciaSinNingunaMs = 30_000;

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
    /// <summary>
    /// LA CURA MÁS FUERTE QUE PUEDE PAGAR con el maná que tiene, o null si no le alcanza para ninguna. Entre varias,
    /// la que más levanta: el que sabe Curar Heridas Leves y Graves tira la Grave mientras le dé el maná.
    /// </summary>
    /// <summary>
    /// ¿EL SERVIDOR NO SE LO DEJÓ? Se pide un casteo y en la vuelta siguiente se mira la vida: si NO subió, el
    /// servidor lo rechazó y ese hechizo no se vuelve a elegir.
    ///
    /// <para>Hace falta porque el bot sólo miraba el maná y el servidor pide además la skill de Magia — y el
    /// rechazo <b>no cobra el maná</b>, así que el hechizo se podía repetir para siempre. Peor: como
    /// <c>CurarseConMagia</c> devolvía true por haber MANDADO los paquetes, <c>CuidarseAsync</c> cortaba ahí y la
    /// poción no se tomaba nunca. El bot se quedaba casteando mientras lo mataban, con la mochila llena.</para>
    ///
    /// <para>Se mira el EFECTO y no el requisito, que es el mismo camino que <c>_noSeLoPone</c> usa para el equipo:
    /// así cubre cualquier motivo de rechazo y no hace falta que el bot lleve la cuenta de sus skills.</para>
    /// </summary>
    public static bool ElServidorNoSeLoDejo(double vidaAntes, double vidaAhora) => vidaAntes >= vidaAhora;

    /// <summary>¿Vuelve a probar los hechizos que le rechazaron? Sí cuando lo que subió es Magia, que es
    /// justamente el requisito que se lo estaba impidiendo: <c>RepartirPuntos</c> le mete ahí la mitad de los
    /// puntos libres si el personaje castea, así que el rechazo de ayer deja de valer.</summary>
    public static bool VuelveAProbarLosHechizos(byte skillQueSubio) => skillQueSubio == GameLimits.SkillMagia;

    /// <summary>¿Le falta Magia para castear el hechizo con el que nació? Una skill que todavía no sabe (-1) cuenta como
    /// que falta: es el personaje recién creado, que tiene todo en cero.</summary>
    public static bool LeFaltaMagiaParaCastear(int magia, int minima) => magia < minima;

    /// <summary>El hechizo con el que nace todo el que castea (VB6 <c>UserHechizos(1) = 2</c>, TCP.bas:541).</summary>
    private const short HechizoDeNacimiento = 2;

    /// <summary>La Magia que pide ese hechizo, de la data.</summary>
    private int MagiaDelHechizoDeNacimiento => _entorno.Datos.Spell(HechizoDeNacimiento)?.MinSkill ?? 0;

    public static (byte Ranura, int Cura)? MejorCuraQuePuedePagar(IReadOnlyList<(byte Ranura, int Cura, int Mana)> curas, int mana)
    {
        (byte Ranura, int Cura)? mejor = null;
        foreach (var c in curas)
            if (c.Mana <= mana && (mejor is null || c.Cura > mejor.Value.Cura)) mejor = (c.Ranura, c.Cura);
        return mejor;
    }

    /// <summary>
    /// CURARSE CON MAGIA. Devuelve si mandó el casteo.
    ///
    /// <para>Va ANTES de la poción: el maná vuelve solo y las pociones se compran, así que el que sabe curarse guarda
    /// las pociones para cuando se queda seco. Es lo primero que hace cualquiera que juega un caster, y el bot no lo
    /// hacía: <see cref="LanzarAlgo"/> filtra los hechizos de daño y para curarse sólo tenía la poción y el sacerdote.</para>
    ///
    /// <para>Se clickea SU PROPIA CABEZA, la baldosa de arriba de sus pies, como un jugador que se hace clic encima:
    /// el servidor elige el blanco como el original, mirando PRIMERO la baldosa de abajo del clic (LookatTile,
    /// GameLogic.bas:733), y así lo encuentra a él. Clickear los pies elegiría al que esté parado justo al sur. Son
    /// los mismos dos paquetes que manda el cliente.</para>
    /// </summary>
    /// <summary>La baldosa de la CABEZA de un personaje parado en <paramref name="y"/>: la de arriba de sus pies. Es la
    /// que se clickea para elegirlo, porque el servidor mira primero la baldosa de abajo del clic (ver
    /// <see cref="CurarseConMagia"/>).</summary>
    public static byte ClicEnLaCabeza(byte y) => (byte)Math.Max(1, y - 1);

    /// <summary>
    /// ¿EL CLIC EN LA CABEZA LO ELIGE? Sólo si cae adentro del área jugable: el servidor elige blanco desde la fila
    /// <see cref="GameLimits.MinYBorder"/> (<c>InMapBounds</c>, GameLogic.bas:225) y un clic más arriba DESELECCIONA. Al que está
    /// parado en esa primera fila no se lo elige clickeando su cabeza, y el hechizo que va sin blanco se corta sin cobrar.
    /// </summary>
    public static bool ElClicLoElige(byte y) => ClicEnLaCabeza(y) >= GameLimits.MinYBorder;

    private bool CurarseConMagia(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        // Parado en la primera fila su cabeza queda afuera (`ElClicLoElige`): la cura no sale y, por no subirle la vida, el
        // hechizo quedaría anotado como rechazado. Ahí, la poción.
        if (!sentidos.UsaMana || !ElClicLoElige(sentidos.Posicion.Y)) return false;
        RevisarElUltimoCasteo(sentidos);
        var curas = new List<(byte Ranura, int Cura, int Mana)>();
        foreach (var (ranura, numero) in sentidos.Hechizos())
            if (!_noLoPuedeLanzar.Contains(numero)
                && _entorno.Datos.Spell(numero) is { IsHeal: true, MaxHp: > 0 } h) curas.Add((ranura, h.MaxHp, h.ManaCost));
        if (MejorCuraQuePuedePagar(curas, sentidos.ManaEnPuntos) is not { } cura) return false;

        // LA VIDA SE LEE ANTES DE PEDIR, y lo pedido se recuerda DESPUÉS de que los dos paquetes salieron: si el
        // envío falla no hubo casteo, y anotarlo igual marcaría como rechazado un hechizo que el servidor ni vio.
        short hechizo = sentidos.HechizoEn(cura.Ranura);
        double vidaAntes = sentidos.Salud;
        var (x, y) = sentidos.Posicion;
        byte cabeza = ClicEnLaCabeza(y);
        if (!transporte.Mandar(Paquete(ClientPacketId.CastSpell, w => w.WriteByte(cura.Ranura)))
            || !transporte.Mandar(Paquete(ClientPacketId.WorkLeftClick,
                   w => w.WriteByte(x).WriteByte(cabeza).WriteByte((byte)GameLimits.SkillMagia)))) return false;
        _curaPedida = (hechizo, vidaAntes);
        _ultimoCasteo = (hechizo, Environment.TickCount64);
        return true;
    }

    private async Task<bool> CuidarseAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (HoraDeTomarPocion(sentidos.Salud, sentidos.CuantoTiene(EsPocionDeVida))
            && (CurarseConMagia(transporte, sentidos)
                || Usar(transporte, sentidos, o => o is { IsPotion: true, SubType: 3 })))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);

            // ¿EL SERVIDOR LE DEJÓ EL HECHIZO? Se mira ACÁ y no en la vuelta siguiente porque acá la medición es
            // inmediata: pasó el delay del casteo y nada más. Dejarla para después abre una ventana entre esta baja
            // de vida y la próxima, y en el medio un hechizo que curó bien quedaría anotado por lo que le pegaron.
            // Ver `ElServidorNoSeLoDejo`.
            if (_curaPedida is { } pedida)
            {
                if (ElServidorNoSeLoDejo(pedida.Vida, sentidos.Salud)) _noLoPuedeLanzar.Add(pedida.Hechizo);
                _curaPedida = null;
            }
            return true;
        }
        if (TieneQueComer(sentidos) && Usar(transporte, sentidos, o => o.IsFood))
        {
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            return true;
        }
        if (TieneQueTomar(sentidos) && Usar(transporte, sentidos, o => o.IsDrink))
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

        // PRIMERO, SACARSE EL ARMA CON LA QUE NO SE PEGA: con el arco puesto el servidor contesta 127 a cada golpe.
        if (ArmaQueNoPegaPuesta(sentidos, _entorno.Datos) is { } arco) return PedirQueSeSaque(transporte, arco);
        foreach (var esDeEso in QueSePone)
            if (MejorParaPoner(sentidos, esDeEso) is { } ranura) return PedirQueSePonga(transporte, sentidos, ranura);

        // LOS NUDILLOS, SÓLO PARA EL QUE NO TIENE ARMA. El servidor saca uno al ponerse el otro (VB6
        // InvUsuario.bas:991-997 y :1051-1058), así que con los dos en la mochila el bot se pasaría la vida cambiando.
        // Medido con la sonda: un Gladiador sin arma, con los Nudillos de Hierro en la mochila sin poner.
        if (!sentidos.LlevaPuesto(obj => _entorno.Datos.Obj(obj) is { } puesto && (EsArma(puesto) || puesto.IsKnuckles))
            && MejorParaPoner(sentidos, EsArma) is null
            && MejorParaPoner(sentidos, o => o.IsKnuckles) is { } nudillos)
            return PedirQueSePonga(transporte, sentidos, nudillos);
        return false;
    }

    /// <summary>
    /// ¿ES UN ARMA QUE EL BOT SE PONE PARA PELEAR? Las de cuerpo a cuerpo: ni el arco ni las arrojadizas.
    ///
    /// <para>El bot pelea cuerpo a cuerpo (y castea), no dispara. Con un arma de PROYECTIL puesta el servidor no
    /// deja pegar: contesta el 127 a cada golpe, igual que el original (Protocol.bas:1700-1706). El arco llegó a
    /// contarse como arma porque el port lo dejaba pegar con su golpe de arma, y el Cazador —que nace con un Arco
    /// Simple y nada más— peleaba así; ahora eso no existe, y con el arco puesto pelearía peor que a mano limpia.
    /// Se lo saca (ver <see cref="ArmaQueNoPegaPuesta"/>) y pelea a mano hasta conseguir una de verdad.</para>
    /// </summary>
    public static bool EsArma(ObjInfo o) => o.IsWeapon && o.Proyectil == 0;

    /// <summary>La ranura de un arma de PROYECTIL que lleve puesta —con la que el servidor no lo deja pegar—, o
    /// null. Es lo primero que se saca al equiparse.</summary>
    public static byte? ArmaQueNoPegaPuesta(SentidosDelBot sentidos, GameData datos)
    {
        foreach (byte ranura in sentidos.RanurasOcupadas())
            if (sentidos.EstaPuesto(ranura) && datos.Obj(sentidos.QueHayEn(ranura)) is { IsWeapon: true, Proyectil: > 0 })
                return ranura;
        return null;
    }

    /// <summary>La ranura con lo mejor de eso que tiene sin poner y que puede usar, o null.</summary>
    /// <summary>
    /// ¿CUÁL DE ESAS SE PONE? La mejor de la mochila, y SÓLO si supera a la que ya lleva puesta.
    ///
    /// <para>Antes alcanzaba con llevar algo de esa clase para no volver a mirar la mochila, así que el bot se ponía lo
    /// primero que conseguía y no se lo cambiaba nunca. Medido en `seguir1`: Ciro, de nivel 13, con el Escudo de Hierro
    /// +2 —defensa 8, comprado por 3774 monedas— guardado, y el Escudo de Tortuga —defensa 2, del botín— puesto. Toda la
    /// cadena de juntar plata para comprarse equipo mejor terminaba en la mochila.</para>
    ///
    /// <para>Empatada NO se cambia: el servidor saca la vieja al poner la nueva, así que con dos iguales el bot se
    /// pasaría la vida cambiándoselas.</para>
    /// </summary>
    public static byte? QueSePoneDeEso(IReadOnlyList<(byte Ranura, int Valor, bool Puesto)> deEsas)
    {
        if (deEsas.Count == 0) return null;
        int puesto = deEsas.Where(x => x.Puesto).Select(x => x.Valor).DefaultIfEmpty(0).Max();
        var mejor = deEsas.Where(x => !x.Puesto).OrderByDescending(x => x.Valor).ToList();
        if (mejor.Count == 0) return null;
        // CON NADA PUESTO SE PONE LO QUE HAYA, aunque no defienda nada. Exigir que sea MEJOR que lo puesto deja
        // desnudo al que sólo tiene ropa de defensa cero: `0 > 0` es falso. Y desde que el desnudo no recupera
        // energía (<see cref="Survival.RecuperaEnergia"/>) eso no es cosmético, es un bloqueo — sin energía no
        // trabaja ni pelea, y la energía sólo se repone al subir de nivel.
        //
        // Medido en `tala1`: de los ocho bots, el ÚNICO desnudo era el único con la energía en cero (0 de 572),
        // con las Vestimentas Tribales de defensa 0 en la mochila; los otros siete, con una prenda puesta, la
        // tenían llena. La sonda de la faena lo dijo así: "sin energia: 0 % < 20 %".
        bool nadaPuesto = !deEsas.Any(x => x.Puesto);
        return nadaPuesto || mejor[0].Valor > puesto ? mejor[0].Ranura : null;
    }

    /// <summary>Las de esa clase que tiene, con lo que valen y si las lleva puestas; las que el servidor no le deja
    /// poner quedan afuera, salvo que ya las tenga encima.</summary>
    private byte? MejorParaPoner(SentidosDelBot sentidos, Func<ObjInfo, bool> esDeEso)
    {
        var deEsas = new List<(byte Ranura, int Valor, bool Puesto)>();
        foreach (byte ranura in sentidos.RanurasOcupadas())
        {
            short obj = sentidos.QueHayEn(ranura);
            if (_entorno.Datos.Obj(obj) is not { } o || !esDeEso(o)) continue;
            bool puesto = sentidos.EstaPuesto(ranura);
            if (!puesto && (_noSeLoPone.Contains(obj) || !LoPuedeUsar(o, sentidos.Nivel))) continue;
            deEsas.Add((ranura, ValorDeCombate(o), puesto));
        }
        return QueSePoneDeEso(deEsas);
    }

    /// <summary>Sacarse lo de esa ranura: equipar lo que ya está puesto lo saca. No se anota como pedido, porque
    /// el control de "no se lo pudo poner" lo marcaría como imposible de ponerse.</summary>
    private bool PedirQueSeSaque(TransporteDeBot transporte, byte ranura)
    {
        _proximoEquipo = Environment.TickCount64 + EsperaEquipoMs;
        return transporte.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(ranura)));
    }

    private bool PedirQueSePonga(TransporteDeBot transporte, SentidosDelBot sentidos, byte ranura)
    {
        _equipoPedido = (ranura, sentidos.QueHayEn(ranura));
        _proximoEquipo = Environment.TickCount64 + EsperaEquipoMs;
        return transporte.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(ranura)));
    }

    /// <summary>Lo que vale en combate: el golpe si pega, la defensa si se viste.</summary>
    public static int ValorDeCombate(ObjInfo o) => o.IsWeapon || o.IsKnuckles ? o.MaxHit : o.MaxDef;

    /// <summary>
    /// ¿LO PUEDE USAR? La clase, LA RAZA, EL GÉNERO y el nivel: todo lo que el servidor va a mirar cuando se lo
    /// quiera poner. Llama a las MISMAS funciones del handler (<see cref="Equipment.MiraElGenero"/> y
    /// <see cref="Equipment.PuedeUsarloSuGenero"/>) y no a una copia, porque dos copias de la misma regla se
    /// desincronizan solas — y entonces el bot paga algo que después no se puede poner.
    ///
    /// <para>La raza faltaba, y es media biblioteca: <c>RazasProhibidas</c> del obj.dat viene de Imperium, que ordena
    /// las razas distinto, y <see cref="ObjInfo.RaceForbidden"/> ya hace la traducción. Sin eso el bot compraba ropa
    /// que no se puede poner y se quedaba con la que tenía. Medido en `seguir3`: Ciro, enano, con la Armadura de
    /// Cazador (defensa 24) guardada —lleva <c>RazasProhibidas=2,5</c>, enano y gnomo— y defensa 3 encima.</para>
    /// </summary>
    public static bool PuedeUsarlo(ObjInfo? o, byte clase, byte raza, byte genero, short nivel) =>
        o is not null && !o.ClassForbidden(clase) && !o.RaceForbidden(raza) && o.MinLevel <= nivel
        && Equipment.PuedeUsarloSuGenero(Equipment.GeneroDeLaPrenda(o), genero);

    /// <inheritdoc cref="PuedeUsarlo"/>
    private bool LoPuedeUsar(ObjInfo o, short nivel) => PuedeUsarlo(o, _clase, _raza, _genero, nivel);

    /// <summary>Lo mejor de eso que YA tiene, puesto o guardado: contra esto se compara la vitrina.</summary>
    /// <summary>
    /// ¿ES LO MEJOR QUE TIENE DE ESA CLASE DE EQUIPO? Lo que lo es NO se vende: el bot lo ofrece en el mostrador
    /// y un segundo después lo vuelve a comprar, porque sigue siendo lo mejor de la vitrina.
    ///
    /// <para>Medido en el log de economía del juego, cuatro pares del mismo objeto con cero o un segundo de
    /// diferencia: una armadura vendida a 6.000 y recomprada a 16.072, otra a 6.000 y 15.930, y un casco dos veces
    /// a 1.666 y 4.202. <b>25.074 monedas</b> quemadas en una corrida. El mercader paga <c>Valor/3</c> y cobra el
    /// valor entero, así que cada vuelta se come dos tercios de la pieza.</para>
    ///
    /// <para>Pasa sobre todo después de morir, porque <c>UserDie</c> desequipa todo: la pieza queda en la mochila
    /// sin poner y el bucle de venta la toma por botín. Lo PEOR sí se vende —eso es botín de verdad, lo que sueltan
    /// las criaturas— así que la mochila se sigue vaciando.</para>
    /// </summary>
    public static bool EsLoMejorQueTieneDeEsaClase(int valorDeCombate, int mejorDeEsaClase) =>
        valorDeCombate > 0 && valorDeCombate >= mejorDeEsaClase;

    private int LoMejorQueTiene(SentidosDelBot sentidos, Func<ObjInfo, bool> esDeEso)
    {
        int mejor = 0;
        foreach (byte ranura in sentidos.RanurasOcupadas())
            if (_entorno.Datos.Obj(sentidos.QueHayEn(ranura)) is { } o && esDeEso(o) && LoPuedeUsar(o, sentidos.Nivel)
                && CuentaComoPropio(sentidos.QueHayEn(ranura)))
                mejor = Math.Max(mejor, ValorDeCombate(o));
        return mejor;
    }

    /// <summary>¿Eso de la vitrina lo mejora en algo? Es lo que decide si vale la pena el viaje al mercader.</summary>
    /// <summary>
    /// ¿LE ALCANZA LA PLATA? El precio de lista es el TECHO de lo que le van a cobrar: el mercader cobra
    /// <c>Valor / (1 + Comerciar/100)</c> (ver <see cref="Comercio.PrecioDeVitrina"/>), o sea el valor o menos. Desde
    /// lejos el bot no conoce su propio regateo, así que mira el techo: como mucho deja pasar algo que con el descuento
    /// habría podido pagar, y eso es preferible al viaje al pedo.
    ///
    /// <para>Lo que no cuesta nada SÍ se puede pagar: hay objetos de valor cero en las vitrinas.</para>
    /// </summary>
    public static bool LoPuedePagar(int precioDeLista, double plata) => precioDeLista <= plata;

    /// <summary>¿Eso de la vitrina lo mejora Y lo puede pagar? Las dos cosas, porque es lo que decide el VIAJE: sin el
    /// precio, el bot viajaba a ver algo que no le alcanzaba, volvía con las manos vacías y salía de nuevo — y como
    /// mientras viaja no caza, la plata no le crecía nunca. Medido en `seguir1`: Ciro, nivel 13 con 2445 monedas, doce
    /// minutos entre la cuna y el Herrero Rinkel —Armadura de Cuero a 3000— sin matar una criatura.</summary>
    private bool ValeElViajePorEso(SentidosDelBot sentidos, short obj, double plata) =>
        MejoraLoQueTiene(sentidos, obj) && LoPuedePagar(_entorno.Datos.Obj(obj)?.Value ?? int.MaxValue, plata);

    /// <summary>¿Es un arma que se puede poner, que puede pagar y que el servidor no le rechazó? La del camino: ver
    /// <see cref="LeFaltaElArmaDelCamino"/>.</summary>
    private bool ArmaQueLeSirve(SentidosDelBot sentidos, short obj, double plata) =>
        _entorno.Datos.Obj(obj) is { } o && EsArma(o) && LoPuedeUsar(o, sentidos.Nivel) && !_noSeLoPone.Contains(obj)
        && LoPuedePagar(o.Value, plata);

    /// <summary>COMPRARSE LA ROPA DEL CAMINO: de las de la vitrina que le sirven y puede pagar, la que más defiende, y si ninguna
    /// defiende, cualquiera. Ponérsela es cosa de <see cref="Equiparse"/>, que al desnudo le pone lo que haya.</summary>
    private async Task<bool> ComprarLaRopaDelCaminoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        double plata = PlataParaElEquipo(sentidos);
        if (plata <= 0 || !sentidos.Comerciando) return false;
        int Abriga(short obj) => RopaQueLeSirve(sentidos, obj, plata) && _entorno.Datos.Obj(obj) is { } o ? Math.Max(1, ValorDeCombate(o)) : 0;
        if (sentidos.LoMejorDeLaVitrina(Abriga, plata) is not { } oferta) return false;
        int teniaAntes = sentidos.CuantoTiene(obj => obj == oferta.Obj);
        transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
        await Task.Delay(_rng.Next(600, 1_400), ct);
        return sentidos.CuantoTiene(obj => obj == oferta.Obj) > teniaAntes;
    }

    /// <summary>¿Es ropa de cuerpo que no es de novato, se la puede poner, el servidor no se la rechazó y la puede pagar? La del
    /// camino: ver <see cref="LeFaltaLaRopaDelCamino"/>. Defienda o no: la que defiende cero también lo viste.</summary>
    private bool RopaQueLeSirve(SentidosDelBot sentidos, short obj, double plata) =>
        _entorno.Datos.Obj(obj) is { IsArmor: true, IsNewbie: false } o && LoPuedeUsar(o, sentidos.Nivel)
        && !_noSeLoPone.Contains(obj) && LoPuedePagar(o.Value, plata);

    /// <summary>COMPRARSE EL ARMA DEL CAMINO: de las de la vitrina que le sirven y puede pagar, la que más pega. Ponérsela es
    /// cosa de <see cref="Equiparse"/>.</summary>
    private async Task<bool> ComprarElArmaDelCaminoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        double plata = PlataParaElEquipo(sentidos);
        if (plata <= 0 || !sentidos.Comerciando) return false;
        int Pega(short obj) => ArmaQueLeSirve(sentidos, obj, plata) && _entorno.Datos.Obj(obj) is { } o ? Math.Max(1, ValorDeCombate(o)) : 0;
        if (sentidos.LoMejorDeLaVitrina(Pega, plata) is not { } oferta) return false;
        int teniaAntes = sentidos.CuantoTiene(obj => obj == oferta.Obj);
        transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
        await Task.Delay(_rng.Next(600, 1_400), ct);
        return sentidos.CuantoTiene(obj => obj == oferta.Obj) > teniaAntes;
    }

    /// <summary>¿Eso de la vitrina lo mejora en algo? Es lo que decide si vale la pena el viaje al mercader.</summary>
    private bool MejoraLoQueTiene(SentidosDelBot sentidos, short obj)
    {
        if (_entorno.Datos.Obj(obj) is not { } o || !LoPuedeUsar(o, sentidos.Nivel)) return false;
        foreach (var esDeEso in QueSePone)
            if (esDeEso(o) && ValorDeCombate(o) > LoMejorQueTiene(sentidos, esDeEso)) return true;
        return false;
    }

    /// <summary>
    /// COMPRARSE ALGO MEJOR. El que junta plata jugando no la deja quieta: se compra el arma o la armadura que lo
    /// hace más fuerte, y por eso a la hora de juego no está igual que cuando empezó.
    ///
    /// <para>Va DESPUÉS de lo esencial y con lo que sobra —la comida, la bebida y las pociones se reservan aparte—,
    /// y se lleva UNA pieza por visita. Ponérsela es cosa de <see cref="Equiparse"/>, en la vuelta siguiente.</para>
    /// </summary>
    private async Task<bool> MejorarElEquipoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        double plata = PlataParaElEquipo(sentidos);
        if (plata <= 0 || !sentidos.Comerciando) return false;
        foreach (var esDeEso in QueSePone)
        {
            int tengo = LoMejorQueTiene(sentidos, esDeEso);
            int Vale(short obj) =>
                _entorno.Datos.Obj(obj) is { } o && esDeEso(o) && LoPuedeUsar(o, sentidos.Nivel) && ValorDeCombate(o) > tengo
                    ? ValorDeCombate(o) : 0;
            if (sentidos.LoMejorDeLaVitrina(Vale, plata) is not { } oferta) continue;
            int teniaAntes = sentidos.CuantoTiene(obj => obj == oferta.Obj);
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
            await Task.Delay(_rng.Next(600, 1_400), ct);
            if (sentidos.CuantoTiene(obj => obj == oferta.Obj) > teniaAntes) return true;
        }
        return false;
    }

    /// <summary>
    /// ¿GASTA PRIMERO EN EL PERGAMINO Y DESPUÉS EN EL EQUIPO? El que castea, sí. Para él el hechizo ES el arma, y las
    /// dos compras salen de la MISMA bolsa (<see cref="PlataParaElEquipo"/>), así que el que va primero es el que se la
    /// gasta.
    ///
    /// <para>El VIAJE al vendedor de pergaminos ya estaba priorizado, pero al llegar al mercader la compra del
    /// pergamino corría última: el caster llegaba con plata, la dejaba en un arma que casi no usa y se volvía sin
    /// hechizo. Medido con una sonda sobre la compuerta del viaje: un Clérigo con 3.550 monedas decidió viajar por el
    /// pergamino ONCE veces en media hora y terminó con el hechizo con el que nació; y en una corrida de cincuenta
    /// minutos un Mago llegó a nivel 16 con Magia 42 y 7.115 monedas sin aprender ninguno, cuando le entraban cuatro.</para>
    ///
    /// <para>Al que no castea no le cambia nada: no compra pergaminos.</para>
    /// </summary>
    public static bool ElHechizoAntesQueElArma(bool castea) => castea;

    /// <summary>
    /// COMPRARSE UN HECHIZO: de los pergaminos de la vitrina que le sirven y puede pagar, el del hechizo que más pega.
    /// </summary>
    private async Task<bool> ComprarPergaminoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        double plata = PlataParaElEquipo(sentidos);
        if (plata <= 0 || !sentidos.UsaMana || !sentidos.Comerciando) return false;
        int Pega(short obj) => LeSirveElPergamino(sentidos, obj) && QueEnsena(obj) is { } h ? Math.Max(1, h.MaxHp) : 0;
        if (sentidos.LoMejorDeLaVitrina(Pega, plata) is not { } oferta) return false;
        int teniaAntes = sentidos.CuantoTiene(obj => obj == oferta.Obj);
        transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
        await Task.Delay(_rng.Next(600, 1_400), ct);
        return sentidos.CuantoTiene(obj => obj == oferta.Obj) > teniaAntes;
    }

    /// <summary>
    /// Lo que puede gastar en lo que no es de supervivencia: lo que sobra después de asegurarse la comida, la bebida,
    /// LA HERRAMIENTA DEL OFICIO y un colchón.
    ///
    /// <para>La herramienta se reserva aparte porque es lo único que le da un ingreso propio, y se compra en la
    /// tienda que la venda: sin esta reserva, un Minero con lo justo se gastaba las monedas en un arma en el
    /// herrero y se quedaba sin piquete, que cuesta doscientas y tarda una hora de caza en juntarse.</para>
    /// </summary>
    private int PlataParaElEquipo(SentidosDelBot sentidos) =>
        PlataQueSobra(sentidos.Oro, ReservaPara(sentidos, comida: true, bebida: true), CuestaLaHerramienta(sentidos), MonedasDeApuro);

    /// <summary>Lo que queda para gustos después de lo que no se puede dejar de comprar.</summary>
    public static int PlataQueSobra(int oro, int reservaDeLaPanza, int cuestaLaHerramienta, int colchon) =>
        Math.Max(0, oro - reservaDeLaPanza - cuestaLaHerramienta - colchon);

    /// <summary>Lo que cuesta la herramienta de su oficio si todavía le falta; cero si ya la tiene o no tiene oficio.</summary>
    private int CuestaLaHerramienta(SentidosDelBot sentidos) =>
        HerramientaQueLeFalta(sentidos) is var falta && falta > 0 ? _entorno.Datos.Obj(falta)?.Value ?? 0 : 0;

    /// <summary>
    /// ¿LO ÚNICO QUE LE FALTA SON GUSTOS? O sea: nada de comer, nada de tomar, nada con qué curarse y la herramienta
    /// en la mochila. Cuando es así, el viaje al mercader se hace por el equipo o por un pergamino, y entonces sirve
    /// SÓLO quien venda eso: si no, gana siempre el mercader más cercano —el de comida— y al herrero no se llega nunca.
    /// </summary>
    public static bool SoloLeFaltanGustos(bool faltaComida, bool faltaBebida, bool faltanPociones, bool faltaHerramienta,
                                          bool faltaRopa = false) =>
        !faltaComida && !faltaBebida && !faltanPociones && !faltaHerramienta && !faltaRopa;

    /// <summary>
    /// ¿ES UNA PUERTA QUE EL BOT PUEDE ABRIR? Cerrada, sin llave, y con una versión abierta a la que cambiar.
    ///
    /// <para>Las tres condiciones salen del handler del servidor (<c>AccionParaPuerta</c>, portado de VB6): una
    /// puerta CON llave sólo se abre con el ítem <c>otLlaves</c> de su misma clave —que este slice no modela— y sin
    /// <c>IndexAbierta</c> no hay a qué objeto cambiarla. <c>Closed</c> vale 1 cuando está cerrada (sale de la clave
    /// <c>abierta</c> del .dat, invertida).</para>
    ///
    /// <para>En el mundo hay 494 puertas y 381 no tienen llave. El bot no abría ninguna, así que TODO lo que vive
    /// adentro de un edificio le era inalcanzable: medido, los ocho vendedores de pergaminos del mapa 20 y varios
    /// de los que venden las herramientas de oficio.</para>
    /// </summary>
    public static bool PuertaQueSeAbre(ObjInfo? o) => o is { IsDoor: true, Closed: 1, Llave: 0, IndexAbierta: > 0 };

    /// <summary>
    /// ¿ESTÁ LO BASTANTE CERCA PARA ABRIRLA? La distancia EUCLÍDEA del original: <c>If Not (Distance(...) > 2)</c>
    /// (Matematicas.bas:55), o sea <c>dx² + dy² &lt;= 4</c>. No es el Chebyshev de los gates de NPC: con la euclídea
    /// la diagonal de dos —distancia 2,83— NO alcanza, y el servidor contesta "Estás muy lejos".
    /// </summary>
    public static bool AlAlcanceDeLaPuerta(int dx, int dy) => dx * dx + dy * dy <= 4;

    /// <summary>
    /// ¿YA ESTÁ ABIERTA? El servidor cambia el objeto del piso por su versión abierta, así que la baldosa deja de
    /// tener el que el mapa trae puesto.
    ///
    /// <para>Hace falta porque el doble clic es un INTERRUPTOR: <c>AccionParaPuerta</c> abre la cerrada y cierra
    /// la abierta, y las candidatas salen de <c>MapObjects</c>, que es el mapa antes de que nadie tocara nada —
    /// una puerta ya abierta sigue figurando cerrada ahí. Medido en campo en el mapa 184: uno abrió la de (24,45)
    /// y el que venía atrás por lo mismo la cerró.</para>
    ///
    /// <para>El cero es "no sé": el suelo sólo le llega cuando entra en el área, y no saberlo no es motivo para
    /// no probar — la verificación de después dice si pasó algo.</para>
    /// </summary>
    public static bool YaEstaAbierta(short enElPiso, short laQueTraeElMapa) =>
        enElPiso != 0 && enElPiso != laQueTraeElMapa;

    /// <summary>
    /// LAS PUERTAS QUE EL MAPA TIENE, de la más cercana a la más lejana. NO las que están a la vista.
    ///
    /// <para><b>Por qué no alcanza con mirar.</b> El servidor sólo manda los objetos del suelo que caen en el
    /// área vecina (<see cref="World.InView"/>: bloques de nueve baldosas, ±1), y la puerta del edificio del
    /// mercader casi nunca cae ahí. Mientras las candidatas salían de <c>TodoLoDelPiso</c>, abrir la correcta era
    /// una lotería que dependía de hacia qué mostrador lo hubiera llevado la rama de acercarse: medido sobre el
    /// mapa 20, parado junto a la ficha 63 la puerta que sirve —la de (50,86)— le queda a 18 pasos y FUERA de la
    /// vista, y parado junto a la 47 le queda a 5 y la abre. En campo salió 2 de 4: dos veces
    /// "destraba=NINGUNA" en un mapa donde sí había con qué.</para>
    ///
    /// <para>Una puerta es DATA, igual que la posición del mercader: está en <c>MapObjects</c>, y el navegante ya
    /// razona sobre ese mapa estático para saber cuál destraba (<see cref="Navegante.AbrirlaDaCamino"/>). O sea que
    /// el bot puede IR A BUSCARLA en vez de esperar a verla, que es lo que hace cualquiera que sabe dónde queda el
    /// negocio.</para>
    ///
    /// <para>Y sin tope de distancia: <see cref="RadioDeCompras"/> sobre lo que estaba a la vista casi no
    /// filtraba —el área vecina ya está cerca—, y sobre las puertas del mapa filtraría justo las que sirven: las
    /// dos del mapa 20 están a 54 y 56 pasos de la baldosa por la que se entra. Cruzar el pueblo hasta la puerta
    /// correcta es barato al lado de los mapas que ya cruzó para llegar.</para>
    /// </summary>
    public static IReadOnlyList<((byte X, byte Y) Donde, short Obj, int Pasos)> PuertasQueElMapaTiene(
        IReadOnlyList<MapObjectPlacement>? objetos, (byte X, byte Y) desde, Func<int, bool> esPuerta)
    {
        if (objetos is null) return Array.Empty<((byte X, byte Y), short, int)>();
        var todas = new List<((byte X, byte Y) Donde, short Obj, int Pasos)>();
        foreach (var o in objetos)
            if (esPuerta(o.ObjIndex))
                todas.Add((((byte)o.X, (byte)o.Y), (short)o.ObjIndex,
                           Math.Abs(o.X - desde.X) + Math.Abs(o.Y - desde.Y)));
        // De la más cercana a la más lejana: `LaPuertaQueConviene` se queda con la primera que destraba, y
        // preguntar por la de al lado antes que por la del otro extremo del mapa ahorra la búsqueda en anchura.
        todas.Sort((a, b) => a.Pasos.CompareTo(b.Pasos));
        return todas;
    }

    /// <summary>
    /// ABRIR LA PUERTA QUE DA CAMINO a un mercader de este mapa. Devuelve si la abrió.
    ///
    /// <para>Se acerca por el navegante —una puerta suele estar en una pared, y el paso codicioso se traba— y manda
    /// el mismo doble clic que el cliente. Que se haya abierto se verifica mirando el piso: el servidor cambia el
    /// objeto por su versión abierta, así que si la baldosa sigue con el mismo objeto no pasó nada.</para>
    /// </summary>
    private async Task<bool> AbrirUnaPuertaAsync(TransporteDeBot transporte, SentidosDelBot sentidos,
                                                 Func<int, bool> sirve, Task cerrada, CancellationToken ct)
    {
        var candidatas = PuertasQueElMapaTiene(_entorno.Datos.MapObjects.GetValueOrDefault(sentidos.Mapa),
                                               sentidos.Posicion,
                                               obj => PuertaQueSeAbre(_entorno.Datos.Obj(obj)));
        if (LaPuertaQueConviene(candidatas,
                donde => _entorno.Mapas.AbrirlaDaCamino(sentidos.Mapa, sentidos.Posicion, donde, sirve))
            is not { } puerta) return false;

        var (x, y) = sentidos.Posicion;
        if (!AlAlcanceDeLaPuerta(x - puerta.Donde.X, y - puerta.Donde.Y)
            && !await IrHastaAsync(transporte, sentidos, sentidos.Mapa, puerta.Donde.X, puerta.Donde.Y, cerrada, ct, 1))
            return false;

        // Y SI OTRO YA LA ABRIÓ, no se toca: ver `YaEstaAbierta`. Devuelve que sí porque para el que llama lo que
        // importa es que el camino quedó abierto, y con eso vuelve a buscar al mercader.
        if (YaEstaAbierta(sentidos.EnElPiso(puerta.Donde), puerta.Obj)) return true;

        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(puerta.Donde.X).WriteByte(puerta.Donde.Y)));
        await Task.Delay(_rng.Next(400, 900), ct);
        return sentidos.EnElPiso(puerta.Donde) != puerta.Obj;
    }

    /// <summary>
    /// ¿VALE VIAJAR AL PUEBLO IGUAL? Cuando no hay ningún mercader al que se LLEGUE caminando pero sí existe el
    /// MAPA que lo tiene, y ese mapa no es en el que ya está.
    ///
    /// <para>Los mercaderes de ciudad viven adentro de edificios, así que desde afuera "no se llega a ninguno" es
    /// lo normal, no la excepción: medido con el navegante, los ocho vendedores de pergaminos del mapa 20 dan
    /// "sin camino" desde la puerta del mapa. El bot se quedaba en el bosque buscando una puerta que ahí no existe
    /// —`puertas1`, once veces "NINGUNA a la vista"— porque nunca viajaba. Una vez adentro del pueblo la puerta sí
    /// está a la vista y <see cref="AbrirUnaPuertaAsync"/> la abre.</para>
    ///
    /// <para>La segunda condición evita el viaje a ninguna parte: si el mapa que tiene al mercader es el propio, ya
    /// está ahí y lo que falta es la puerta, no el viaje.</para>
    /// </summary>
    public static bool ValeViajarAlPueblo(bool hayDondeLlegar, bool estaEnOtroMapa) => !hayDondeLlegar && estaEnOtroMapa;

    /// <summary>
    /// EL PUEBLO CON MERCADER: el más cercano de los que <paramref name="tieneMercader"/>, y si a cinco mapas no hay ninguno, el
    /// más cercano hasta <see cref="SaltosHastaElPuebloLejano"/> al que se llega sólo por mapas que <paramref name="cruzaSinPeligro"/>
    /// (donde no nace nada que lo mate). Uno de nivel alto cruza medio mundo para ir a comprar, pero no por donde lo matan: el viaje
    /// largo no tiene la ruta de repuesto del corto (<see cref="IrHastaAsync"/>).
    ///
    /// <para>Medido en `oficios14`: la Sastre de nivel 18 cazaba en los Campos Abiertos del 25 con 89 mil monedas, sin comida,
    /// bebida ni pociones propias y sin el Costurero, y cada intento de compra terminaba en "ningún pueblo" (`tienda1`). Pasado
    /// el nivel 15 la cuna no deja entrar, y era el atajo: desde el 22, el 24 y el 25 el primer mapa que atiende con comida queda
    /// a 10 a 12 saltos, y Rinkel (el Costurero) a 8 a 10. La runa no lo arregla: al dejar de ser novato el hogar pasa a Nix,
    /// donde los mercaderes no le venden a un ciudadano.</para>
    ///
    /// <para>Y medido en `tienda2`, con el viaje largo por cualquier camino: tres bots de nivel 16 a 19 muertos en el Desierto de
    /// Rinkel (el 15 y el 16, Arañas gigantes que pegan hasta 115), que queda de camino a Rinkel; desde el mismo arranque y con el
    /// código anterior, `tienda1` no tuvo ninguna.</para>
    /// </summary>
    public static (int Mapa, byte X, byte Y, int Saltos)? PuebloConMercader(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                           Func<int, bool> tieneMercader, Func<int, bool>? pisable,
                                                                           Func<int, bool> cruzaSinPeligro) =>
        mapas.MapaMasCercano(mapa, desde, tieneMercader, pisable)
        ?? mapas.MapaMasCercano(mapa, desde, tieneMercader, cruzaSinPeligro, SaltosHastaElPuebloLejano);

    /// <summary>
    /// ¿HAY DÓNDE COMPRAR EL ARMA DEL CAMINO? Un herrero que le venda una que pueda usar y pagar, al que se llegue caminando, o un
    /// pueblo que atienda con uno —alcanza con el MAPA: los herreros están adentro de edificios—, buscado como la compra de todo
    /// lo demás (<see cref="PuebloConMercader"/>): a cinco mapas, y si ahí no hay, hasta <see cref="SaltosHastaElPuebloLejano"/>
    /// por mapas donde no nace nada que lo mate.
    ///
    /// <para>Medido en `oficios23`: Ciro llegó a nivel 20 con 52 mil monedas y el martillo de novato, cazando en el 29 (al lado de
    /// Nix), y no salió nunca a comprar. Las ciudades viejas no le venden a un ciudadano, y desde el 29 el herrero más cercano que
    /// sí —Rinkel— queda a cuatro mapas PASANDO POR EL DUNGEON NEWBIE, donde pasado el nivel 15 no se entra; caminando por fuera,
    /// a ONCE, y el camino cruza el Desierto de Rinkel. El arma se buscaba sólo a cinco mapas, así que no salía nunca. Ahora va
    /// cuando puede cruzar el desierto: con 260 de vida las Arañas Gigantes lo matan y espera; con 400, va.</para>
    /// </summary>
    public static bool HayArmaAlAlcance(Navegante mapas, GameData datos, int mapa, (byte X, byte Y) desde,
                                        Func<int, bool> vendeElArma, Func<int, bool> mapaAtiende,
                                        Func<int, bool> pisable, Func<int, bool> cruzaSinPeligro) =>
        mapas.DondeHay(mapa, desde, vendeElArma, mapaAtiende, pisable) is not null
        || PuebloConMercader(mapas, mapa, desde,
               m => mapaAtiende(m) && datos.MapSpawns.TryGetValue(m, out var conHerrero) && conHerrero.Any(q => vendeElArma(q.NpcIndex)),
               pisable, cruzaSinPeligro) is not null;

    /// <summary>Un número de mapa que no existe: con <see cref="PasaSinPeligro"/>, ningún mapa es "el destino" y todos tienen
    /// que cruzarse sin peligro.</summary>
    private const int NingunMapa = -1;

    /// <summary>Hasta cuántos mapas se va a buscar un pueblo con mercader cuando a cinco no hay ninguno.</summary>
    public const int SaltosHastaElPuebloLejano = 12;

    /// <summary>
    /// ¿HAY ALGO QUE COMPRAR? Es lo que habilita volver a casa con la runa cuando no hay ninguna tienda al alcance.
    ///
    /// <para>El EQUIPO y los PERGAMINOS cuentan, y antes no: con eso un bot que ya tenía lo esencial cubierto y salía
    /// a cazar lejos se quedaba en el bosque para siempre —no encontraba mercader, no volvía— y la plata se le
    /// acumulaba sin gastar. Medido en `puertas3`: tres bots en m202 y m807 con "mapa con mercader=NINGUNO" una y
    /// otra vez; y en `seguir7`, Fabia con 19.839 monedas sin comprar un solo pergamino.</para>
    ///
    /// <para>Lo que sigue sin contar es VENDER: nadie usa una runa para volver a casa a descargar el botín.</para>
    /// </summary>
    public static bool HayAlgoQueComprar(bool herramienta, bool comida, bool bebida, bool pociones,
                                         bool equipo, bool hechizo, bool antidoto) =>
        herramienta || comida || bebida || pociones || equipo || hechizo || antidoto;

    /// <summary>
    /// ¿ALCANZA CON QUE EL MERCADER LE COMPRE para que el viaje termine en él? No mientras le falte comida o
    /// bebida y tenga con qué pagarlas: ahí el viaje es a quien se las VENDA.
    ///
    /// <para>Botín para vender hay casi siempre, así que esa última condición de <c>Sirve</c> acepta al mercader
    /// más cercano SEA CUAL SEA lo que venda — y como ahí se compra lo que ese tenga y la necesidad sigue abierta,
    /// el viaje siguiente termina en el mismo mostrador. Medido con los bots pasando el nivel 15, cuando el kit de
    /// novato deja de contarles: cuatro visitas seguidas con <c>comida=0 bebida=420</c> y veinte mil monedas
    /// encima, mientras el vendedor de comida estaba a los mismos saltos que el tabernero al que sí llegó. Es el
    /// mismo defecto que este archivo ya documenta para el antídoto, del otro lado de la misma función.</para>
    ///
    /// <para><b>Y el guardia del espiral:</b> sin plata para pagarlo, vender SIGUE alcanzando. Si no, el que se
    /// quedó sin nada viajaría a un mostrador que no le compra el botín, volvería con las manos vacías y no
    /// podría comprar nunca — el mismo lazo cerrado que el archivo documenta para la herramienta.</para>
    /// </summary>
    public static bool VenderAlcanzaParaElegirlo(bool faltaComidaOBebida, double oro, int cuestaLoQueFalta) =>
        !faltaComidaOBebida || oro < cuestaLoQueFalta;

    /// <summary>
    /// ¿VALE CAMINAR HACIA EL MERCADER AUNQUE NO SE LLEGUE A ÉL? Cuando ya está en su mapa y no lo alcanza.
    ///
    /// <para>Es el último eslabón de las puertas. El servidor sólo manda los objetos del suelo que ENTRAN EN LA
    /// VISTA del jugador (<c>EnviarSueloQueEntraEnVista</c> → <c>GroundInView</c>), o sea unas ocho baldosas; la
    /// puerta del edificio del mercader está a veinte. El bot la estaba ESPERANDO en vez de ir a buscarla, y por eso
    /// la sonda decía "NINGUNA a la vista" incluso ya dentro del pueblo.</para>
    ///
    /// <para>Acercarse ya NO es lo que mete la puerta en la lista —eso lo resuelve
    /// <see cref="PuertasQueElMapaTiene"/>, que la saca de la data—, pero sigue haciendo falta: al mostrador hay
    /// que llegar igual, y desde más cerca el camino que el navegante calcula es más corto.</para>
    ///
    /// <para>La posición del mercader no hace falta verla: sale de <c>MapSpawns</c>, que es data. Se camina hasta lo
    /// más cerca que el navegante consiga, la puerta entra en vista y <see cref="AbrirUnaPuertaAsync"/> la abre.</para>
    /// </summary>
    public static bool ValeAcercarseAunqueNoLlegue(bool enElMapaDelMercader, bool loAlcanza) => enElMapaDelMercader && !loAlcanza;

    /// <summary>
    /// ¿VALE BUSCAR LA PUERTA ACÁ? Sólo donde el mapa ATIENDE y hay alguien que le venda: una puerta es un camino
    /// hacia un mercader de ESE mapa, y sin mercader que atienda no hay nada detrás de ninguna puerta.
    ///
    /// <para>Sin esto el bot abría la puerta más cercana en cualquier mapa. Medido contra la data con el navegante
    /// replicado: de los ocho mapas donde entró a este camino en `puertas7`, ATIENDEN CERO — cinco no tienen un solo
    /// mercader, y el m34 tiene once con <c>battleMode=1</c>, que no le venden a un ciudadano (VB6 Protocol.bas:5205).
    /// En ese m34 la puerta que abrió sumó treinta baldosas y NINGÚN mercader: de los once ya alcanzaba nueve, y las
    /// dos que lo habrían destrabado estaban a unas cincuenta y cinco baldosas, fuera de
    /// <see cref="RadioDeCompras"/>.</para>
    ///
    /// <para>Es el mismo defecto que ya apareció tres veces: el filtro de la acción de acá mira menos cosas que la
    /// decisión de a dónde ir, así que el bot se ocupa y no progresa.</para>
    /// </summary>
    public static bool ValeBuscarLaPuertaAca(bool elMapaAtiende, bool hayQuienLeVendaAca) => elMapaAtiende && hayQuienLeVendaAca;

    /// <summary>
    /// CUÁL DE LAS PUERTAS A LA VISTA CONVIENE ABRIR: la más cercana ENTRE LAS QUE DESTRABAN el camino a un
    /// mercader. Ninguna si ninguna destraba: abrir por abrir gasta el viaje y la puerta no se vuelve a cerrar.
    ///
    /// <para>Las candidatas vienen ya ordenadas por cercanía, así que la primera que sirve es la más barata.
    /// Medido contra la data: de los 84 mercaderes encerrados de los mapas que atienden, abrir UNA puerta libera
    /// 36. En el mapa 20 la puerta correcta libera SEIS vendedores de pergaminos; la que abría en campo, en el
    /// mapa 34, no liberaba a ninguno.</para>
    /// </summary>
    public static ((byte X, byte Y) Donde, short Obj, int Pasos)? LaPuertaQueConviene(
        IReadOnlyList<((byte X, byte Y) Donde, short Obj, int Pasos)> candidatas,
        Func<(byte X, byte Y), bool> destraba)
    {
        var elegida = candidatas.FirstOrDefault(p => destraba(p.Donde));
        return elegida.Obj == 0 ? null : elegida;
    }

    /// <summary>Lo lejos que el bot se conforma con quedar del mostrador cuando NO llega: lo que el navegante
    /// consiga. Es generoso a propósito — con esto sólo se busca que la puerta entre en vista.</summary>
    private const int ParaQueLaPuertaEntreEnVista = 12;

    /// <summary>
    /// ¿TOCA PROBAR LOS CAMINOS CAROS? —abrir una puerta, caminar hasta el mercader, viajar al pueblo—. En el primer
    /// intento, en el siguiente a HABER ABIERTO UNA PUERTA y en el siguiente a HABER LLEGADO AL PUEBLO.
    ///
    /// <para>Abrir una puerta cambia el mundo: el servidor desbloquea sus dos baldosas (<c>SetTileBlocked</c>) y el
    /// navegante las lee en vivo, así que un camino que no existía ahora sí. Sin esto el bot abría la puerta y en la
    /// misma vuelta se daba por vencido sin volver a mirar — medido en `puertas7`, dos líneas seguidas:
    /// <c>abierta=True</c> y acto seguido <c>viaje al pueblo: intento=1 | mapa con mercader=NINGUNO</c>.</para>
    ///
    /// <para>LLEGAR AL PUEBLO ES LO MISMO: la puerta y el mercader se buscan desde donde está parado, y ahora está en
    /// otro mapa. El viaje se comía el primer intento, así que el bot que venía de afuera llegaba a la entrada del
    /// pueblo y ya no tenía permitido buscar la puerta: se volvía sin entrar a ninguna tienda. Medido en `tarde2`: un
    /// Clérigo con diez mil monedas yendo por el pergamino de la cuna a la entrada de Rinkel —(74,54), desde donde
    /// sólo se llega a pie a dos vendedores con pergaminos de un millón— y de vuelta a la cuna, una y otra vez. Lo
    /// que se medía el 14/09 no lo mostraba porque el escenario ponía a los bots ADENTRO del pueblo.</para>
    /// </summary>
    public static bool TocaProbarLosCaminosCaros(int intento, bool abrioUnaPuerta, bool llegoAlPueblo) =>
        intento == 0 || abrioUnaPuerta || llegoAlPueblo;

    /// <summary>Desde acá vale la pena pasar por el mercader a mirar equipo.</summary>
    private const int OroParaMirarEquipo = 100;

    private static readonly Func<ObjInfo, bool>[] QueSePone =
    {
        EsArma, o => o.IsArmor, o => o.IsShield, o => o.IsHelmet,
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

        if (Usar(transporte, sentidos, o => CuraElVeneno(o, _entorno.Datos.Spell((short)o.LanzaHechizo))))
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

    /// <summary>
    /// A cuántos mapas de distancia se va a buscar un sacerdote cuando no queda ninguna poción: hasta donde llega
    /// el navegante, que es el mismo alcance que ya usan <c>CurarVenenoAsync</c> y <c>BuscarSacerdoteAsync</c>.
    ///
    /// <para>Estaba en 2 y eso dejaba al bot sin salida en medio mundo. Medido sobre los 282 mapas con criaturas
    /// hostiles: con dos saltos el sacerdote se alcanza desde 142 (50,4%) y con cinco desde 275 (97,5%). Y la otra
    /// salida —la runa a casa— no sirve en mapa pk, que es lo que casi todo mapa de caza es: <b>139 mapas (49,3%)
    /// quedaban sin NINGUNA vía</b>, con el bot esperando ahí hasta que algo lo terminara. Con el alcance del
    /// navegante eso baja a 6 mapas (2,1%).</para>
    /// </summary>
    private const int SaltosHastaElSacerdote = Navegante.TopeDeMapas;

    /// <summary>¿El bot busca al sacerdote tan lejos como su navegante sabe viajar? Si se queda corto, hay mapas
    /// donde no tiene cómo curarse y tampoco puede usar la runa.</summary>
    public static bool BuscaAlSacerdoteHastaDondeLlega => SaltosHastaElSacerdote >= Navegante.TopeDeMapas;
    private const int EsperaSanarFallidoMinMs = 20_000, EsperaSanarFallidoMaxMs = 40_000;
    private long _proximaSanacion;
    private int _sanacionesSinCamino;
    private long _proximaRunaDeHuida;
    private long _proximoPedidoAlSacerdote;

    /// <summary>
    /// ¿NO SE LA PUEDE SACAR DE ENCIMA? Tres huidas de LA MISMA criatura adentro de la misma ventana de tiempo
    /// (<see cref="VentanaDeHuidasMs"/>) y correr deja de ser una estrategia: hay que pelear o irse con la runa.
    ///
    /// <para>Es la tercera forma de estar acorralado. Las otras dos miran UNA huida —no se pudo mover, o quedó pegada— y esta
    /// mira la SERIE. Medido en `verdad5`: al huir, el Lobo queda a cuatro, cinco, seis o siete baldosas, o sea que huir SÍ lo
    /// despega; el problema es que vuelve a alcanzarlo a los pocos segundos. Con los contadores por distancia el bot encadenó
    /// treinta y siete huidas seguidas y la rama de pelear no entró ni una vez.</para>
    /// </summary>
    public static bool NoSeLoSacaDeEncima(int huidasDeLaMismaCriatura) =>
        huidasDeLaMismaCriatura >= HuidasParaAcorralado;

    /// <summary>
    /// ¿SE VA DEL MAPA POR ESTA AMENAZA? Cuando no se la saca de encima (<see cref="NoSeLoSacaDeEncima"/>) y es algo que no puede
    /// lastimar —en cualquier mapa— o algo del hueco (<see cref="EsDelHuecoPara"/>) en un mapa inseguro. En uno seguro lo del hueco
    /// ni siquiera es la amenaza (<see cref="LoDelHuecoEsLaAmenaza"/>), así que tampoco lo saca del mapa.
    ///
    /// <para>Medido en `hueco1` (29/09), cuando lo del hueco también lo sacaba de un mapa seguro: de 53 salidas que llegaron a otro
    /// mapa, 23 volvieron al mismo antes de cinco minutos —a comprar, a curarse el veneno— y casi todas eran de la cuna, por el Lobo
    /// (del hueco para 55 a 89 de vida). Y hubo salidas trabadas: un bot intentó irse seis veces seguidas desde la misma baldosa.
    /// En la cuna los Lobos del hueco no habían matado a nadie (`ropa1`); los que matan son los Zombies del 13, que es inseguro.
    /// DECIDIDO POR EL AUTOR (29/09).</para>
    /// </summary>
    public static bool SeVaDelMapaPor(bool noLaLastima, bool esDelHueco, bool mapaInseguro, int huidasDeLaMismaCriatura) =>
        (noLaLastima || (esDelHueco && mapaInseguro)) && NoSeLoSacaDeEncima(huidasDeLaMismaCriatura);

    /// <summary>
    /// ¿LA HUIDA NO SIRVIÓ? Si después de alejarse la amenaza sigue adentro del radio de susto, sí: es la misma distancia que
    /// vuelve a disparar la huida en la vuelta siguiente, así que el bot ya está condenado a repetirla.
    ///
    /// <para>Acá decía "a una baldosa" y por eso el contador nunca llegaba a tres: <see cref="AlejarseAsync"/> camina de tres a
    /// seis pasos y la criatura se mueve una vez por segundo, así que casi siempre queda a dos o tres —lejos para este contador,
    /// cerca para volver a huir—. Medido en `verdad4`, con la regla ya puesta: 44 huidas, 37 de un mismo bot con el Lobo encima,
    /// racha de 28 seguidas, y la rama de pelear no se activó NI UNA VEZ.</para>
    ///
    /// <para><paramref name="pasosDeLaAmenazaDespues"/> negativo = ya no se la ve, que es la única huida que sirvió.</para>
    /// </summary>
    public static bool LaHuidaNoSirvio(int pasosDeLaAmenazaDespues) =>
        pasosDeLaAmenazaDespues >= 0 && pasosDeLaAmenazaDespues <= RadioDeSusto;

    /// <summary>
    /// ¿ESTÁ ACORRALADO? Son DOS formas de estarlo, y las dos terminan igual: huir no lo saca del problema.
    ///
    /// <para>La primera son <see cref="HuidasParaAcorralado"/> huidas seguidas SIN MOVERSE de la baldosa: un rincón, o alguien
    /// tapando la única salida.</para>
    ///
    /// <para>La segunda son huidas en las que SÍ se movió y la criatura le quedó pegada igual, porque camina al mismo paso que
    /// él. Medido en `verdad3`: de 120 huidas de la corrida, 91 fueron de un mismo bot con un Lobo encima —84 veces a una
    /// baldosa— y la racha más larga fue de TREINTA Y SEIS huidas seguidas, con mil cuatrocientas pociones en la mochila. El
    /// contador viejo no lo veía nunca: se resetea apenas el bot da un paso, y el bot daba un paso siempre.</para>
    /// </summary>
    public static bool EstaAcorralado(int huidasSinMoverse, int huidasQueNoLoSacaron = 0) =>
        huidasSinMoverse >= HuidasParaAcorralado || huidasQueNoLoSacaron >= HuidasParaAcorralado;

    /// <summary>¿Lo está atacando? Una criatura hostil que pega, pegada en cruz, que es desde donde se pega en el juego.</summary>
    public static bool LoEstaAtacando(NpcInfo? n, int pasos) => pasos <= 1 && n is { Hostile: true, MaxHit: > 0 };

    private const int HuidasParaAcorralado = 3;
    private readonly ContadoresDeHuida _huidas = new();

    /// <summary>
    /// LOS CONTADORES DE HUIDA, juntos porque se olvidan de maneras DISTINTAS, y esa diferencia es justo lo que hace falta.
    ///
    /// <para><see cref="SinMoverse"/> y <see cref="QueNoLoSacaron"/> miran UNA huida —no se pudo mover, o la criatura le quedó
    /// adentro del radio de susto— y dejan de valer apenas se termina el peligro. <see cref="DeLaMismaCriatura"/> mira la SERIE,
    /// y a esa la corta el TIEMPO: al que te vuelve a alcanzar no se le gana la carrera.</para>
    ///
    /// <para>Los cinco campos estaban sueltos en el bot y se olvidaban todos juntos. Como huir SÍ despega a la criatura —queda a
    /// cuatro, cinco, seis baldosas—, entre alcance y alcance el bot sale del peligro y ahí se borraba también la serie: medido
    /// en `verdad6`, 53 huidas, la rama de pelear sin entrar NI UNA VEZ y el contador sin pasar de uno.</para>
    /// </summary>
    public sealed class ContadoresDeHuida
    {
        /// <summary>Huidas seguidas en las que no se movió de la baldosa: el rincón.</summary>
        public int SinMoverse;

        /// <summary>Huidas seguidas que lo dejaron con la amenaza adentro del radio de susto.</summary>
        public int QueNoLoSacaron;

        /// <summary>Huidas de LA MISMA criatura adentro de la ventana: la serie.</summary>
        public int DeLaMismaCriatura;

        private short _deQuien;
        private long _hastaCuando;

        /// <summary>Una huida más: sigue la serie si es la misma criatura y todavía está adentro de la ventana.</summary>
        public void Huyo(short deQuien, long ahora, int ventanaMs)
        {
            DeLaMismaCriatura = deQuien == _deQuien && ahora < _hastaCuando ? DeLaMismaCriatura + 1 : 1;
            _deQuien = deQuien;
            _hastaCuando = ahora + ventanaMs;
        }

        /// <summary>
        /// SE TERMINÓ EL PELIGRO: se olvidan los dos contadores del rincón, que son de esa huida y de ninguna otra. La serie no
        /// se toca acá — el respiro entre alcance y alcance es parte de la persecución, no el final.
        /// </summary>
        public void SalioDelPeligro()
        {
            SinMoverse = 0;
            QueNoLoSacaron = 0;
        }
    }

    /// <summary>Cuánto dura la serie: dos huidas separadas por más de esto son dos sustos distintos, no una persecución.</summary>
    private const int VentanaDeHuidasMs = 60_000;
    private const int EsperaEntreRunasDeHuidaMs = 30_000;

    /// <summary>El SubTipo de poción que LANZA UN HECHIZO sobre quien la toma (VB6 InvUsuario.bas:1636).
    /// No es "la que cura el veneno": de las ocho que hay, sólo las dos Violetas lanzan `Curar Veneno`.</summary>
    private const int PocionQueLanzaUnHechizo = 5;
    private const int EsperaCuraMinMs = 60_000, EsperaCuraMaxMs = 120_000;
    private long _proximaCura;

    /// <summary>Búsquedas de sacerdote seguidas sin ninguno al que se llegue caminando.</summary>
    private int _curasSinCamino;

    /// <summary>
    /// ¿ESTA PELEA NO SE GANA? Con <see cref="GolpesParaJuzgar"/> golpes resueltos, si lo que le sacó no alcanza para matarla en
    /// <see cref="GolpesQueSeBanca"/> contando la vida de su ficha. En cero o negativo no la mata nunca. El tope es para lo que en
    /// la práctica no se mata, no para lo lento: lo que se deja se anota, se esquiva y puede sacarlo del mapa, y en `pelea_b3` un
    /// Leñador que con cincuenta dejó a un Esqueleto guerrero al que mataba en unos cincuenta y dos golpes salió del mapa donde tala.
    ///
    /// <para>El original le resta la defensa de la criatura al golpe SIN PISO (SistemaCombate.bas:465): el golpe que no la pasa la
    /// CURA. Medido: un Ladrón de nivel 9 con la Daga de novato (golpe de 10 a 13) contra los Goblins del mapa 12 (defensa 15) se
    /// pasó 40 de 45 minutos peleando en `oficios11`, una sola pelea de 19; y una Ladrona de nivel 12 (golpe hasta 17), 6 minutos
    /// contra un Esqueleto guerrero (defensa 19) en `oficios14`. A lo que ya le pegaba se lo "sacaba de encima" mientras le
    /// duraran las pociones.</para>
    /// </summary>
    public static bool NoLaVaAMatar(int golpes, int dano, int vidaDeLaCriatura) =>
        golpes >= GolpesParaJuzgar
        && (dano <= 0 || (long)vidaDeLaCriatura * golpes > (long)dano * GolpesQueSeBanca);

    private const int GolpesParaJuzgar = 10, GolpesQueSeBanca = 100;

    /// <summary>
    /// ¿DEJA DE LANZARLE? Después de <see cref="LanzadosEnVanoParaPegar"/> hechizos seguidos que no le cobraron el maná. El
    /// servidor corta sin cobrar todo lo que no sale —sin blanco, fuera de rango, la cadencia—, así que el maná que no baja es
    /// el hechizo que no salió. <c>LanzarAlgo</c> devuelve true por haber MANDADO los paquetes y arriba hay un <c>continue</c>:
    /// sin esto el bot no llegaba nunca a pegar con el arma. Visto en `pelea_a`: un Nigromante con 79 de maná en (13,8) del
    /// mapa 19, la fila de al lado del borde, siete minutos con el maná clavado y tomando pociones mientras lo mordía un Lobo.
    /// </summary>
    public static bool DejaDeLanzarle(int lanzadosEnVano) => lanzadosEnVano >= LanzadosEnVanoParaPegar;

    private const int LanzadosEnVanoParaPegar = 3;

    /// <summary>
    /// LAS CRIATURAS QUE NO PUEDE LASTIMAR (<see cref="NoLaVaAMatar"/>), con el arma y el nivel con que lo intentó: con otra arma o
    /// en otro nivel el golpe cambia y se vuelve a probar. Lo anotado no es presa (<see cref="EsPresa"/>) y se esquiva.
    /// </summary>
    private readonly Dictionary<short, (short Arma, int Nivel)> _noLaLastima = new();

    /// <summary>Con qué pelea ahora: el arma en la mano y el nivel. Se toma en cada vuelta del ciclo.</summary>
    private (short Arma, int Nivel) _conQuePelea;

    private bool NoLaLastima(short ficha) => _noLaLastima.TryGetValue(ficha, out var con) && con == _conQuePelea;

    /// <summary>El arma o los nudillos que tiene puestos, o 0.</summary>
    private short ArmaEnLaMano(SentidosDelBot s)
    {
        foreach (byte ranura in s.RanurasOcupadas())
            if (s.EstaPuesto(ranura) && _entorno.Datos.Obj(s.QueHayEn(ranura)) is { } o && (o.IsWeapon || o.IsKnuckles)) return o.Index;
        return 0;
    }

    /// <summary>Ir hasta la presa y pegarle hasta que muera, se pierda de vista, o convenga irse. Devuelve
    /// false sólo si se cayó la conexión.</summary>
    private async Task<bool> CazarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, short presa,
                                        Task cerrada, CancellationToken ct)
    {
        int pasosGastados = 0;
        bool rodeando = false;
        // LO QUE LE VA SACANDO, para no quedarse peleando con lo que no puede matar (`NoLaVaAMatar`). Lo de antes no cuenta.
        sentidos.TomarLoQuePego();
        int golpes = 0, sacado = 0;
        // Y LOS HECHIZOS QUE NO SALEN, para pasar al arma (`DejaDeLanzarle`).
        int lanzadosEnVano = 0;
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
            int manaAntes = sentidos.ManaEnPuntos;
            if (Math.Abs(dx) <= AlcanceHechizoX && Math.Abs(dy) <= AlcanceHechizoY && !DejaDeLanzarle(lanzadosEnVano)
                && LanzarAlgo(transporte, sentidos, quien))
            {
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                lanzadosEnVano = sentidos.ManaEnPuntos < manaAntes ? 0 : lanzadosEnVano + 1;
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
                var (g, d) = sentidos.TomarLoQuePego();
                golpes += g;
                sacado += d;
                if (NoLaVaAMatar(golpes, sacado, _entorno.Datos.Npc(quien.FichaNpc)?.MaxHp ?? 0))
                {
                    _conQuePelea = (ArmaEnLaMano(sentidos), sentidos.Nivel);
                    _noLaLastima[quien.FichaNpc] = _conQuePelea;
                    return await AlejarseAsync(transporte, sentidos, quien, cerrada, ct);
                }
                continue;
            }

            if (pasos > RadioDeCaza || ++pasosGastados > PacienciaPersiguiendo) return true;   // se aleja o no se llega

            if (PasoDeCaza(_entorno.Mapas, sentidos.Mapa, (x, y), (quien.X, quien.Y), _pasoTrabado, rodeando,
                           sentidos.BaldosasOcupadas()) is not { } acercarse)
            { NoSeLlegaA(presa); return true; }
            rodeando = acercarse.Rodeando;
            if (await UnPasoHaciaAsync(transporte, sentidos, acercarse.Paso.X - x, acercarse.Paso.Y - y, ct) is var r && r != Paso.Dado)
                return r != Paso.SinConexion;
        }
        return !cerrada.IsCompleted;
    }

    /// <summary>
    /// CÓMO SE ACERCA A LA PRESA: la baldosa del próximo paso, y si de acá en más va rodeando. Mientras el paso codicioso avance se
    /// usa ése, que es como camina cualquiera; apenas choca contra algo se pasa al camino del navegante y ya no se vuelve, porque lo
    /// que frenó un paso es una pared y va a seguir estando. <c>null</c> = ni rodeando hay camino.
    ///
    /// <para>El paso codicioso solo prueba el otro eje, así que con algo grande en el medio el bot rebota contra la misma pared hasta
    /// gastar la paciencia, y la vuelta siguiente elige LA MISMA presa y empieza de nuevo. Medido en `verdad1`: un bot quince minutos
    /// entre (49,67) y (53,67) de la cuna, eligiendo un Murciélago a once baldosas en línea recta —dieciséis por camino, con el
    /// edificio de la tienda de comidas en el medio— y terminó la corrida en nivel 1 con seis criaturas.</para>
    /// </summary>
    public static ((byte X, byte Y) Paso, bool Rodeando)? PasoDeCaza(Navegante mapas, int mapa, (byte X, byte Y) desde,
                                                                     (byte X, byte Y) presa, bool choco, bool rodeando,
                                                                     IReadOnlySet<(byte X, byte Y)>? ocupadas = null)
    {
        if (!choco && !rodeando)
        {
            var (dx, dy) = SentidosDelBot.Paso(SentidosDelBot.RumboHacia(presa.X - desde.X, presa.Y - desde.Y));
            return (((byte)(desde.X + dx), (byte)(desde.Y + dy)), false);
        }
        return mapas.HayComoLlegar(mapa, desde, presa, 1, ocupadas) is { Count: > 0 } camino ? (camino[0], true) : null;
    }

    /// <summary>
    /// LA PRESA A LA QUE NO HAY CAMINO SE DESCARTA UN RATO: si no, sigue siendo la más cercana —<see cref="SentidosDelBot.MasCercano"/>
    /// mide en Manhattan, que no sabe de paredes— y se vuelve a elegir en la vuelta siguiente.
    ///
    /// <para><b>Y son VARIAS.</b> Guardando una sola, con dos criaturas inalcanzables a la vista el bot hace
    /// ping-pong: elige A, no hay camino, la anota; elige B, no hay camino, la anota —y pisa la de A—, así que
    /// vuelve a elegir A. Medido en campo con una sonda por escalón: un Clérigo empezó <b>508 peleas en cuarenta
    /// minutos y 487 terminaron en "sin camino"</b>, repartidas entre DOS criaturas, 244 y 243 veces. Otro bot,
    /// en el mismo mapa y la misma corrida, tuvo una sola de ésas en 67 peleas.</para>
    /// </summary>
    private void NoSeLlegaA(short presa)
    {
        long ahora = Environment.TickCount64;
        // Lo vencido se saca acá: es el único lugar por donde se agrega, y sin esto la lista junta todas las
        // criaturas de la sesión.
        foreach (var vencida in _sinCamino.Where(p => p.Value <= ahora).Select(p => p.Key).ToList())
            _sinCamino.Remove(vencida);
        _sinCamino[presa] = ahora + SinCaminoMs;
    }

    /// <summary>De lo anotado en <see cref="NoSeLlegaA"/>, lo que TODAVÍA está descartado.</summary>
    public static IReadOnlySet<short> SiguenSinCamino(IReadOnlyDictionary<short, long> anotadas, long ahora)
    {
        var vigentes = new HashSet<short>();
        foreach (var (quien, hasta) in anotadas)
            if (ahora < hasta) vigentes.Add(quien);
        return vigentes;
    }

    private readonly Dictionary<short, long> _sinCamino = new();
    private const int SinCaminoMs = 60_000;

    /// <summary>
    /// Tira el hechizo de daño más fuerte que pueda pagar contra ese objetivo. false = no sabe magia, no le
    /// alcanza el maná, o no tiene ninguno de ataque.
    ///
    /// <para>El casteo son DOS paquetes, igual que en el cliente: <c>CastSpell</c> elige la ranura y el
    /// servidor contesta pidiendo el objetivo, y el <c>WorkLeftClick</c> sobre la baldosa lo marca. Mandar
    /// sólo el primero deja el hechizo elegido y nada más — que es exactamente lo que le pasa a un jugador
    /// que aprieta el hechizo y no clickea.</para>
    /// </summary>
    /// <summary>
    /// ¿VALE LA PENA ESE PERGAMINO? Enseña un hechizo que HACE DAÑO o que CURA, y que todavía no sabe.
    ///
    /// <para>Un caster nace con uno solo —el original le da <c>UserHechizos(1) = 2</c> en el alta— y sin comprar
    /// pergaminos se queda con ése para siempre, pegando con el báculo. En la data hay ochenta y cuatro, de
    /// doscientas a tres mil monedas, y siete mercaderes que los venden.</para>
    ///
    /// <para>LA CURA TAMBIÉN: sólo se miraban los de daño, así que el bot mago no aprendía nunca a curarse —lo
    /// primero que compra cualquiera que juega un caster— y se le iban las pociones. Curar Heridas Leves sale 500.
    /// Ver <see cref="CurarseConMagia"/>, que es lo que las usa.</para>
    /// </summary>
    public static bool ValeLaPenaElPergamino(SpellInfo? hechizo, IReadOnlyList<(byte Ranura, short Hechizo)> yaSabe) =>
        hechizo is { SubeHp: 1 or 2 } && !yaSabe.Any(x => x.Hechizo == hechizo.Index);

    /// <summary>El hechizo que enseña ese objeto si es un pergamino, o null.</summary>
    private SpellInfo? QueEnsena(short obj) =>
        _entorno.Datos.Obj(obj) is { IsPergamino: true } o ? _entorno.Datos.Spell((short)o.HechizoIndex) : null;

    /// <summary>¿Ese pergamino de la vitrina le sirve a este bot?</summary>
    private bool LeSirveElPergamino(SentidosDelBot sentidos, short obj) =>
        sentidos.UsaMana && ValeLaPenaElPergamino(QueEnsena(obj), sentidos.Hechizos());

    /// <inheritdoc cref="ValeElViajePorEso"/>
    private bool ValeElViajePorElPergamino(SentidosDelBot sentidos, short obj, double plata) =>
        LeSirveElPergamino(sentidos, obj) && LoPuedePagar(_entorno.Datos.Obj(obj)?.Value ?? int.MaxValue, plata);

    /// <summary>
    /// APRENDER LO QUE COMPRÓ: usar el pergamino que tenga en la mochila. Devuelve si mandó algo.
    ///
    /// <para>Si el servidor no se lo deja aprender —sin maná, sin lugar en el libro— no se insiste con ese: se anota
    /// y se prueba con otro, igual que con el equipo que no se puede poner.</para>
    /// </summary>
    private async Task<bool> AprenderHechizosAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!sentidos.UsaMana) return false;
        foreach (byte ranura in sentidos.RanurasOcupadas())
        {
            short obj = sentidos.QueHayEn(ranura);
            if (_noLoAprende.Contains(obj) || !LeSirveElPergamino(sentidos, obj)) continue;
            int sabiaAntes = sentidos.Hechizos().Count;
            if (!transporte.Mandar(Paquete(ClientPacketId.UseItem, w => w.WriteByte(ranura)))) return false;
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
            if (sentidos.Hechizos().Count == sabiaAntes) _noLoAprende.Add(obj);
            return true;
        }
        return false;
    }

    private readonly HashSet<short> _noLoAprende = new();

    /// <summary>Los hechizos que el servidor no le dejó lanzar: se anotan mirando si la vida subió, y no se
    /// vuelven a elegir. El mismo camino que <c>_noSeLoPone</c> para el equipo.</summary>
    private readonly HashSet<short> _noLoPuedeLanzar = new();
    private (short Hechizo, double Vida)? _curaPedida;

    /// <summary>Desde acá vale la pena pasar por el mercader a mirar pergaminos: el más barato de la data vale 200.</summary>
    /// <summary>
    /// ¿EL VIAJE VA SÓLO POR EL PERGAMINO? Cuando el bot castea, NO LE FALTA NADA ESENCIAL, y hay un vendedor al
    /// alcance que tenga uno que le sirva y pueda pagar.
    ///
    /// <para>Las otras dos condiciones no son adorno. Sin <paramref name="soloLeFaltanGustos"/> un caster con hambre
    /// o sin pociones viajaría igual al de pergaminos, que es la regla de <see cref="SoloLeFaltanGustos"/> al revés.
    /// Y sin <paramref name="hayUnoAlAlcance"/>, un caster lejos de cualquier vendedor de pergaminos dejaría de
    /// comprar comida, bebida y equipo — peor que no aprender un hechizo.</para>
    /// </summary>
    public static bool PrimeroElPergamino(bool vaPorHechizo, bool soloLeFaltanGustos, bool hayUnoAlAlcance) =>
        vaPorHechizo && soloLeFaltanGustos && hayUnoAlAlcance;

    /// <summary>
    /// ¿HAY DÓNDE COMPRAR EL PERGAMINO? Alcanza con que exista el MAPA: los vendedores están adentro de
    /// edificios —los del mapa 20, detrás de sus once puertas— y preguntar sólo por los que se alcanzan
    /// CAMINANDO daba siempre que no, así que el bot nunca priorizaba el viaje y se quedaba con el mercader
    /// más cercano, que no vende pergaminos.
    ///
    /// <para>Es la misma decisión que ya se tomó para el viaje al pueblo: con el mercader adentro de un
    /// edificio <c>DondeHay</c> no devuelve nada, pero el mapa sí se encuentra — y una vez ahí el bot prueba
    /// la puerta.</para>
    ///
    /// <para>Medido en una corrida de 45 minutos: el caster salió de compras 35 veces, 7 de ellas queriendo un
    /// hechizo, llegó a 10 mostradores y en NINGUNO había un pergamino que le sirviera; terminó con 17.775
    /// monedas y el único hechizo con el que nació. Contra la data: de los 22 mercaderes a los que llega
    /// caminando desde la cuna, cero venden pergaminos; mapas que atienden y los venden, dos.</para>
    /// </summary>
    public static bool HayDondeComprarElPergamino(bool seLlegaCaminando, bool hayUnMapaQueLosVende) =>
        seLlegaCaminando || hayUnMapaQueLosVende;

    /// <summary>¿El mercader de un mapa con este <c>battleMode</c> le vende a un bot de esta facción? La regla del server
    /// (<see cref="ServerIni.MercaderLeVende"/>); sin ini, la del original.</summary>
    public static bool ElMercaderLeVende(ServerIni? ini, int battleMode, byte faccion) =>
        (ini ?? ComercioDelOriginal).MercaderLeVende(battleMode, faccion);

    private static readonly ServerIni ComercioDelOriginal = new();

    private const int OroParaMirarHechizos = 250;

    /// <summary>
    /// ¿LO PUEDE LANZAR AHORA? El servidor pide maná Y ENERGÍA (<c>p.Stamina &lt; spell.StaRequerido</c>,
    /// PacketDispatcher.cs:10299). El bot miraba sólo el maná, así que elegía hechizos que le rebotaban — y el
    /// rechazo no gasta maná, con lo cual volvía a elegir el mismo en la vuelta siguiente.
    /// </summary>
    public static bool LoPuedeLanzarAhora(SpellInfo? h, int mana, int energia) =>
        h is not null && mana >= h.ManaCost && energia >= h.StaRequerido;

    /// <summary>
    /// ¿SE LO RECHAZÓ POR FALTA DE MAGIA? El servidor pide la skill de Magia antes que nada
    /// (<c>Skills[SkillMagia] &lt; spell.MinSkill</c> → <c>LocaleMsg 221</c>, PacketDispatcher.cs:10298) y ese
    /// <c>return</c> corta antes de cobrar, así que el casteo rechazado no gasta nada y el bot elegía el mismo
    /// hechizo para siempre. Peor: <c>LanzarAlgo</c> devuelve true por haber MANDADO los paquetes y arriba hay
    /// un <c>continue</c>, con lo cual tampoco llegaba a pegar con el arma. No hacía nada.
    ///
    /// <para>Medido: de los 23 pergaminos de daño que venden los mercaderes, el bot ordena por daño y elige el
    /// Misil Mágico (daño 35, maná 40) apenas lo tiene, y ése pide <b>30 de Magia</b>. El bot junta 5 al nacer
    /// y 2 por nivel, o sea que recién a nivel 16 lo puede lanzar — el pergamino, en cambio, lo compra mucho
    /// antes.</para>
    ///
    /// <para>Se compara CUÁNDO: un rechazo posterior al pedido es de ese pedido. Sin mirar el momento, un
    /// rechazo viejo condenaría al hechizo que se pide después.</para>
    /// </summary>
    public static bool SeLoRechazoPorMagia(long cuandoLoPidio, long ultimoRechazo) => cuandoLoPidio > 0 && ultimoRechazo >= cuandoLoPidio;

    /// <summary>
    /// ¿ALGUNA VEZ VA A PODER LANZARLO? Es lo que decide si vale la pena COMPRAR el pergamino: hay hechizos que
    /// piden más energía que el techo del juego, así que no los puede lanzar nadie, nunca.
    ///
    /// <para>Son dos de los ciento trece, y son justo los dos que encabezan el ranking del bot porque ordena por
    /// daño: el 30 «Te Violo» (energía 1000, daño 10000) y el 31 «Curación» (energía 1000). El techo es
    /// <see cref="Leveling.MaxStaCap"/> = 999.</para>
    /// </summary>
    public static bool AlgunaVezLoVaAPoderLanzar(SpellInfo? h) =>
        h is not null && h.StaRequerido <= Leveling.MaxStaCap;

    /// <summary>
    /// EL HECHIZO DE DAÑO QUE CONVIENE: el de más daño entre los que puede pagar de maná y energía,
    /// salteando los que el servidor ya le rechazó por falta de Magia. Ver <see cref="SeLoRechazoPorMagia"/>.
    /// </summary>
    public static byte? MejorHechizoDeDano(
        IReadOnlyList<(byte Ranura, short Numero, int Dano, int Mana, int Sta)> hechizos,
        int mana, int energia, IReadOnlySet<short> anotados)
    {
        byte? mejor = null;
        int suDano = -1;
        foreach (var h in hechizos)
        {
            if (anotados.Contains(h.Numero)) continue;
            if (mana < h.Mana || energia < h.Sta) continue;
            if (h.Dano > suDano) { suDano = h.Dano; mejor = h.Ranura; }
        }
        return mejor;
    }

    /// <summary>¿El servidor rechazó el último casteo por falta de Magia? Entonces ese hechizo se anota y no se
    /// vuelve a elegir, ni para pegar ni para curarse. Ver <see cref="SeLoRechazoPorMagia"/>.</summary>
    private void RevisarElUltimoCasteo(SentidosDelBot sentidos)
    {
        if (_ultimoCasteo is not { } pedido) return;
        if (SeLoRechazoPorMagia(pedido.Cuando, sentidos.UltimoRechazoDeMagia)) _noLoPuedeLanzar.Add(pedido.Hechizo);
        _ultimoCasteo = null;
    }

    private (short Hechizo, long Cuando)? _ultimoCasteo;

    private bool LanzarAlgo(TransporteDeBot transporte, SentidosDelBot sentidos, SentidosDelBot.Visible a)
    {
        if (!sentidos.UsaMana || !ElClicLoElige(a.Y)) return false;
        RevisarElUltimoCasteo(sentidos);
        var candidatos = new List<(byte Ranura, short Numero, int Dano, int Mana, int Sta)>();
        foreach (var (ranura, numero) in sentidos.Hechizos())
        {
            var h = _entorno.Datos.Spell(numero);
            if (h is null || h.SubeHp != 2) continue;            // SubeHp 2 = hace daño; 1 = cura
            candidatos.Add((ranura, numero, h.MaxHp, h.ManaCost, h.StaRequerido));
        }
        if (MejorHechizoDeDano(candidatos, sentidos.ManaEnPuntos, sentidos.EnergiaEnPuntos, _noLoPuedeLanzar)
                is not { } ranuraElegida) return false;

        // Lo pedido se recuerda DESPUÉS de que los dos paquetes salieron: ver `CurarseConMagia`.
        short hechizo = sentidos.HechizoEn(ranuraElegida);
        if (!transporte.Mandar(Paquete(ClientPacketId.CastSpell, w => w.WriteByte(ranuraElegida)))
            || !transporte.Mandar(Paquete(ClientPacketId.WorkLeftClick,
                   w => w.WriteByte(a.X).WriteByte(ClicEnLaCabeza(a.Y)).WriteByte((byte)GameLimits.SkillMagia)))) return false;
        _ultimoCasteo = (hechizo, Environment.TickCount64);
        return true;
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

    private const int ReintentoDeSalidaDelGuardiaMs = 30_000;
    private long _proximaSalidaDelGuardia;
    /// <summary>Cuándo puede volver a probar irse del mapa por lo que no puede lastimar o por lo del hueco, si la salida anterior
    /// no lo movió: la misma espera que con el guardia.</summary>
    private long _proximaSalidaPorLaAmenaza;

    /// <summary>
    /// SALIR DEL MAPA hacia el más cercano donde no nace ningún guardia que vaya por él: la huida de un guardia del otro bando, y
    /// lo primero que hace el que aparece en su mapa (ver dónde se llama). Devuelve si hizo algo —salió o por lo menos se movió
    /// hacia la salida—; si no se pudo mover, false, y queda la huida de siempre.
    ///
    /// <para>Se probó elegir la salida más lejos de donde nacen los guardias (`tienda6`, 26/09) y no sirve así: en la Cordillera
    /// del Norte elegía siempre la del norte, también desde al lado del Mercenario, que queda al sur, o sea cruzando el mapa entre
    /// los cuatro. Lo que importa es por dónde pasa el CAMINO, no dónde queda la puerta; mientras eso no esté, la más cercana.</para>
    /// </summary>
    private async Task<bool> SalirDelMapaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        int aca = sentidos.Mapa;
        var antes = sentidos.Posicion;
        var pisable = Pisable(sentidos);
        int vida = sentidos.VidaMaxima, nivel = sentidos.Nivel;
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        long ahora = Environment.TickCount64;
        foreach (int vecino in VecinosDelMenosPeligroso(_entorno.Datos, aca, m => pisable(m) && !HayGuardiaQueLoAtaca(m),
                     m => CuantasLoPuedenMatar(_entorno.Datos, _entorno.Ini, _miBando, m, vida, nivel, conPociones, curaVeneno)))
        {
            // LA SALIDA QUE RECIÉN NO LO SACÓ SE SALTEA: ver `SePruebaEsaSalida`.
            if (!SePruebaEsaSalida(_salidasQueNoAndan, aca, vecino, ahora)) continue;
            if (_entorno.Mapas.MapaMasCercano(aca, antes, m => m == vecino, pisable) is not { } afuera) continue;
            await IrHastaAsync(transporte, sentidos, afuera.Mapa, afuera.X, afuera.Y, cerrada, ct, ToleranciaDelCoto, escapando: true);
            if (sentidos.Mapa == aca) _salidasQueNoAndan[(aca, vecino)] = Environment.TickCount64 + SalidaQueNoAndaMs;
            return sentidos.Mapa != aca || sentidos.Posicion != antes;
        }
        return false;
    }

    /// <summary>
    /// ¿SE PRUEBA SALIR POR AHÍ? No, si hace menos de <see cref="SalidaQueNoAndaMs"/> esa misma salida no lo sacó del mapa: la
    /// próxima vez prueba por el vecino que sigue (<see cref="VecinosDelMenosPeligroso"/>).
    ///
    /// <para>Medido en `hueco2` y `hueco3` (29/09): escapando de lo del hueco, los bots salían siempre por el vecino más cercano sin
    /// peligro, y en el 13 y en el 12 ese es la esquina (9,7), con salidas a los dos costados y un Zombie o un Goblin tapándola. El
    /// viaje no los movía de la baldosa, y el intento siguiente elegía la misma esquina: dos de esas muertes fueron ahí.</para>
    /// </summary>
    public static bool SePruebaEsaSalida(IReadOnlyDictionary<(int Aca, int Vecino), long> queNoAndan, int aca, int vecino, long ahora) =>
        !queNoAndan.TryGetValue((aca, vecino), out long hasta) || ahora >= hasta;

    /// <summary>Las salidas que hace poco no lo sacaron del mapa, hasta cuándo se saltean (ver <see cref="SePruebaEsaSalida"/>).</summary>
    private readonly Dictionary<(int Aca, int Vecino), long> _salidasQueNoAndan = new();
    private const int SalidaQueNoAndaMs = 5 * 60_000;

    /// <summary>
    /// ADÓNDE SE SALE: los mapas de al lado que <paramref name="sirve"/>, del que tiene MENOS de lo que lo puede matar al que más.
    /// El primero que se encontraba podía ser el peor: medido en `tienda7`, un Guerrero de nivel 18 salió de la Cordillera del
    /// Norte (806) al Río Daglor (798), donde nacen cuatro Sanguijuelas gigantes (nivel 31, pegan hasta 180), y una lo mató; por
    /// el Bosque mortecino (805) había un solo Turbador y murciélagos.
    /// </summary>
    public static IEnumerable<int> VecinosDelMenosPeligroso(GameData datos, int mapa, Func<int, bool> sirve, Func<int, int> peligros) =>
        datos.MapExits.Where(e => e.Key.Map == mapa).Select(e => e.Value.DestMap).Distinct()
             .Where(m => m != mapa && sirve(m))
             .OrderBy(peligros);

    /// <summary>
    /// POR QUÉ CAMINO SE VA A OTRO MAPA. Vivo: por donde no lo matan, si hay por donde (<see cref="PasaPorAhi"/>); si no, por donde
    /// cazaría, que es por donde se buscó el coto (<see cref="SeCruzaBuscando"/>); y si no, por el de siempre, salvo en un viaje largo,
    /// que va sólo por esos dos (<see cref="PuebloConMercader"/>). Sin el del medio, un coto que se alcanza cruzando un mapa seguro se
    /// iba a buscar por el de siempre, que puede cruzar uno inseguro que lo mata. De muerto, por el de siempre, que al fantasma no
    /// lo atacan.
    ///
    /// <para>De muerto no se viajaba a ningún lado: escrito como <c>deMuerto ? null : sinPeligro ?? (...)</c>, el <c>??</c> se
    /// agrupa antes que el <c>?:</c>, y el "de muerto, ninguno" se tragaba también el camino de siempre. Un cadáver con el sacerdote
    /// en otro mapa no llegaba nunca (<see cref="BuscarSacerdoteAsync"/>).</para>
    /// </summary>
    public static T? CaminoDelViaje<T>(bool deMuerto, bool viajeLargo, Func<T?> sinPeligro, Func<T?> porDondeCazaria,
                                       Func<T?> deSiempre) where T : struct =>
        deMuerto ? deSiempre() : sinPeligro() ?? porDondeCazaria() ?? (viajeLargo ? null : deSiempre());

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
                                          bool deMuerto = false, Func<bool>? cortaSi = null,
                                          int topeDeMapas = Navegante.TopeDeMapas, bool escapando = false)
    {
        // LO QUE TAPÓ UN PASO SE RODEA. El mapa sólo sabe de paredes y agua: una criatura quieta o un personaje
        // parado en un pasillo no están ahí, así que recalcular devolvía el mismo camino y el bot chocaba contra lo
        // mismo hasta gastar todos los tramos. Medido: tres bots de la cuna sin llegar nunca a la comida, uno
        // doce veces contra la misma baldosa y dos atascados uno detrás del otro en el mismo pasillo.
        var trabas = new HashSet<(byte X, byte Y)>();
        // UN TRAMO POR MAPA DE MÁS en un viaje largo (ver `PuebloConMercader`): cada puerta que se cruza gasta uno.
        int tramosDelViaje = TramosPorViaje + Math.Max(0, topeDeMapas - Navegante.TopeDeMapas);
        for (int tramos = 0; tramos < tramosDelViaje && !ct.IsCancellationRequested && !cerrada.IsCompleted; tramos++)
        {
            // De muerto se viaja igual: es la única forma de llegar al sacerdote.
            if (!sentidos.Ubicado || (!deMuerto && !sentidos.Vivo)) return false;
            int mapa = sentidos.Mapa;
            var desde = sentidos.Posicion;

            // El destino puede estar en otro mapa: en ese caso el tramo termina en la puerta.
            bool esLaMeta = mapa == mapaDestino;
            (byte X, byte Y) meta;
            // Y NI POR UN MAPA DEL QUE RECIÉN SE FUE HUYENDO, en ninguno de los tres escalones: ver `NoVuelveACruzar`. Escapando o de
            // muerto, sí.
            long ahoraDelTramo = Environment.TickCount64;
            Func<int, bool> sinHuidas = m => escapando || deMuerto || !NoVuelveACruzar(_huyoDe, m, mapaDestino, ahoraDelTramo);
            var pasa = PasaSinPeligro(sentidos, mapaDestino);
            var cruza = CruzaSinPeligroHacia(sentidos, mapaDestino);
            var pisable = Pisable(sentidos);
            if (esLaMeta) meta = (destinoX, destinoY);
            // POR DÓNDE: ver `CaminoDelViaje`.
            else if (CaminoDelViaje(deMuerto, topeDeMapas > Navegante.TopeDeMapas,
                         () => _entorno.Mapas.PuertaHacia(mapa, mapaDestino, desde, (destinoX, destinoY),
                                                          m => pasa(m) && sinHuidas(m), topeDeMapas),
                         () => _entorno.Mapas.PuertaHacia(mapa, mapaDestino, desde, (destinoX, destinoY),
                                                          m => cruza(m) && sinHuidas(m), topeDeMapas),
                         () => _entorno.Mapas.PuertaHacia(mapa, mapaDestino, desde, (destinoX, destinoY), m => pisable(m) && sinHuidas(m),
                                                          topeDeMapas)) is { } salida)
                meta = salida.Puerta;
            else return false;   // desde acá no se llega

            // PARADO ENCIMA DE LA PUERTA: ver `RumboParaUsarLaPuerta`.
            if (!esLaMeta && meta == desde)
            {
                byte rumbo = RumboParaUsarLaPuerta(_entorno.Datos.MapExits, mapa, desde, (x, y) => _entorno.Datos.IsBlockedTile(mapa, x, y));
                if (rumbo == 0) return false;
                var (px, py) = SentidosDelBot.Paso(rumbo);
                if (await UnPasoHaciaAsync(transporte, sentidos, px, py, ct, deMuerto, puedeSalir: true) != Paso.Dado) return false;
                continue;
            }

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
                // NI EL VIAJE SE ARRIMA A LO QUE LO MATA (ver `LoArrimaAlPeligro`): esa baldosa pasa a ser una traba y el camino se
                // recalcula rodeándola. De muerto da igual, que al fantasma no lo atacan.
                if (!deMuerto && LoArrimaAlPeligro((x, y), baldosa, PeligrosALaVista(sentidos))) { trabas.Add(baldosa); break; }
                var como = await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct, deMuerto, puedeSalir: true);
                // CURARSE EN EL CAMINO EN VEZ DE VOLVERSE. Un golpe que bajaba la vida del umbral de la poción
                // abortaba el viaje entero aunque el bot tuviera mil pociones encima, y el intento siguiente
                // arrancaba otra vez desde donde estuviera. Mientras no esté en peligro de verdad se toma la poción y
                // se sigue caminando; con la vida bajo `SeRetira` el viaje se corta igual que antes.
                if (como == Paso.Urgencia && !deMuerto && sentidos.Vivo && (sentidos.Salud >= SeRetira || escapando)
                    && await CuidarseAsync(transporte, sentidos, ct))
                    como = Paso.Dado;
                // ESCAPANDO, EL VIAJE NO SE CORTA POR LA VIDA: se toma lo que haya en el camino y se sigue. Pararse a curarse es
                // quedarse quieto delante de lo que pega (ver `SalirDelMapaAsync`). Medido en `tienda5`: un Guerrero que salía del
                // mapa de cuatro guardias cortaba el viaje en cada paso con la vida baja, y el Clérigo lo mató a seis baldosas.
                if (como == Paso.Urgencia && escapando && !deMuerto && sentidos.Vivo) como = Paso.Dado;
                // SIN POCIONES Y SIN NADA HOSTIL CERCA, EL VIAJE SIGUE. Cortarlo sirve para tomar una poción o para escapar de
                // lo que pega; sin pociones y sin nadie que ataque, pararse no cura nada. Medido en `largo2`: una Guerrera con 5
                // de 58 de vida, sin pociones, sin comida ni bebida y con una moneda cortaba cada minuto, en el primer paso, el
                // viaje a vender la Runa que la sacaba del pozo.
                if (como == Paso.Urgencia && SigueCaminandoConUrgencia(sentidos.Vivo, deMuerto,
                        sentidos.CuantoTiene(EsPocionDeVida), HostilCerca(sentidos)))
                    como = Paso.Dado;
                if (como != Paso.Dado) return false;   // sin conexión, o algo más urgente que el viaje
                if (cortaSi?.Invoke() == true) return false;   // apareció algo que vale más que seguir viajando
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
                                              CancellationToken ct, bool deMuerto = false, bool puedeSalir = false)
    {
        byte porElOtroEje = dx != 0 && dy != 0
            ? SentidosDelBot.RumboHacia(Math.Abs(dx) > Math.Abs(dy) ? 0 : dx, Math.Abs(dx) > Math.Abs(dy) ? dy : 0)
            : (byte)0;
        byte rumbo = _pasoTrabado && porElOtroEje != 0 ? porElOtroEje : SentidosDelBot.RumboHacia(dx, dy);
        // UN PASO SUELTO NO SACA DEL MAPA (ver `PasoSaleDelMapa`): si el rumbo pisa una salida se prueba el otro eje, y si
        // tampoco se puede, el paso no se da y cuenta como trabado. Sólo el viaje sale a propósito.
        if (!puedeSalir && PasoSaleDelMapa(_entorno.Datos.MapExits, sentidos.Mapa, sentidos.Posicion, rumbo))
        {
            if (porElOtroEje != 0 && porElOtroEje != rumbo
                && !PasoSaleDelMapa(_entorno.Datos.MapExits, sentidos.Mapa, sentidos.Posicion, porElOtroEje))
                rumbo = porElOtroEje;
            else
            {
                _pasoTrabado = true;
                await Task.Delay(UnPaso(), ct);
                return Urge(sentidos, deMuerto) ? Paso.Urgencia : Paso.Dado;
            }
        }
        var antes = sentidos.Posicion;
        if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return Paso.SinConexion;
        sentidos.PredecirPaso(rumbo);
        await Task.Delay(UnPaso(), ct);
        // El servidor corrige con un PosUpdate cuando el paso se rechaza, así que quedar donde estábamos
        // significa que hay algo adelante.
        _pasoTrabado = sentidos.Posicion == antes;
        return Urge(sentidos, deMuerto) ? Paso.Urgencia : Paso.Dado;
    }

    /// <summary>
    /// ¿EL PASO EN ESE RUMBO PISA UNA SALIDA DEL MAPA? Pisarla lo saca del mapa, y eso se hace a propósito, viajando
    /// (<see cref="IrHastaAsync"/>): no paseando ni persiguiendo una presa.
    ///
    /// <para>Medido en `oficios6`: un Paladín y una Nigromante de nivel 12 cazaban en el borde oeste del 187 —toda la columna
    /// x = 9 es la salida al 186— y aparecieron en la entrada del 186, al lado del Ent (pega de 40 a 90 y no camina): murieron
    /// los dos, sin haber elegido nunca ir ahí. El camino del navegante ya esquivaba las salidas; el paseo y el paso suelto,
    /// no.</para>
    /// </summary>
    public static bool PasoSaleDelMapa(IReadOnlyDictionary<(int Map, int X, int Y), (int DestMap, int DestX, int DestY)> salidas,
                                       int mapa, (byte X, byte Y) desde, byte rumbo)
    {
        var (dx, dy) = SentidosDelBot.Paso(rumbo);
        return salidas.ContainsKey((mapa, desde.X + dx, desde.Y + dy));
    }

    /// <summary>
    /// PARADO ENCIMA DE LA PUERTA: la salida se dispara al PISARLA, no por estar encima. El rumbo del paso para usarla: a una vecina que
    /// también sale al mismo mapa, y si no hay, a una vecina que se pisa sin salir, desde donde el tramo siguiente vuelve a la puerta.
    /// 0 si no es una puerta o no hay ninguna vecina así.
    ///
    /// <para>Medido en `oficios10`: una Cazadora y el Minero pasaron más de media hora parados en la esquina (9,7) del 11 y del 12, que
    /// es una salida con otra salida a cada costado. El que llega a (10,8) desde el mapa del oeste con esa baldosa ocupada cae en la
    /// legal más cercana, y el anillo del original (ClosestLegalPos) empieza por esa esquina. Desde ahí el camino hasta la puerta daba
    /// cero pasos y el viaje se daba por fallido, una y otra vez.</para>
    /// </summary>
    public static byte RumboParaUsarLaPuerta(IReadOnlyDictionary<(int Map, int X, int Y), (int DestMap, int DestX, int DestY)> salidas,
                                             int mapa, (byte X, byte Y) puerta, Func<int, int, bool> bloqueada)
    {
        if (!salidas.TryGetValue((mapa, puerta.X, puerta.Y), out var aca)) return 0;
        byte sinSalir = 0;
        for (byte rumbo = 1; rumbo <= 4; rumbo++)
        {
            var (dx, dy) = SentidosDelBot.Paso(rumbo);
            int x = puerta.X + dx, y = puerta.Y + dy;
            if (x < GameLimits.MinXBorder || x > GameLimits.MaxXBorder || y < GameLimits.MinYBorder || y > GameLimits.MaxYBorder) continue;
            if (salidas.TryGetValue((mapa, x, y), out var alla))
            {
                if (alla.DestMap == aca.DestMap) return rumbo;
            }
            else if (sinSalir == 0 && !bloqueada(x, y)) sinSalir = rumbo;
        }
        return sinSalir;
    }

    /// <summary>
    /// ¿ESE PASO LO ARRIMA A LO QUE LO MATA? Si lo deja a <see cref="RadioDeSusto"/> pasos o menos de algo peligroso que tiene a la
    /// vista —la distancia a la que huye— sin alejarlo del más cercano: nadie camina solo hasta donde después tiene que escaparse.
    /// Alejarse, aunque todavía quede cerca, vale siempre.
    ///
    /// <para>Medido en `oficios8`: un Carpintero de nivel 7 a 10 murió tres veces en el 208, dos del Señor de las sombras (pega de
    /// 60 a 75 y no camina) y una de un Lobo, a menos de siete pasos de donde nace el Señor. Con algo peligroso a la vista el bot
    /// no pelea y pasea (`pasear: peligro cerca`, dieciséis veces), y el paseo daba varios pasos seguidos hacia la presa que veía
    /// sin volver a mirar el peligro.</para>
    /// </summary>
    public static bool LoArrimaAlPeligro((byte X, byte Y) desde, (byte X, byte Y) hasta, IReadOnlyCollection<(byte X, byte Y)> peligros)
    {
        if (peligros.Count == 0) return false;
        int despues = peligros.Min(p => Math.Abs(p.X - hasta.X) + Math.Abs(p.Y - hasta.Y));
        int antes = peligros.Min(p => Math.Abs(p.X - desde.X) + Math.Abs(p.Y - desde.Y));
        return despues <= RadioDeSusto && despues <= antes;
    }

    /// <summary>Dónde está, de lo que tiene a la vista, lo peligroso para él.</summary>
    private List<(byte X, byte Y)> PeligrosALaVista(SentidosDelBot sentidos)
    {
        int vida = sentidos.VidaMaxima, nivel = sentidos.Nivel;
        bool conPociones = sentidos.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool curaVeneno = sentidos.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        return sentidos.DondeEstan(f => EsPeligroso(f, vida, nivel, conPociones, curaVeneno));
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
                                         CancellationToken ct, SentidosDelBot.Visible? haciaDonde = null)
    {
        // Al cambiar de mapa, este lugar pasa a ser su zona: las coordenadas de la anterior no significan nada.
        var (x, y) = sentidos.Posicion;
        if (sentidos.CambioDeMapa() || _casa is null) _casa = (x, y);

        int dx = _casa.Value.X - x, dy = _casa.Value.Y - y;
        bool lejos = Math.Abs(dx) + Math.Abs(dy) > RadioDeCasa;
        // HACIA LO QUE VE, NO AL AZAR. Ver <see cref="ElPaseoVaHaciaLaPresa"/>. Volver a la zona manda:
        // sin ese freno el bot se iría siguiendo lo que aparezca y dejaría de tener un lugar.
        (int X, int Y) hacia = lejos ? (dx, dy)
                             : ElPaseoVaHaciaLaPresa(lejos, haciaDonde is not null) && haciaDonde is { } presaALaVista
                                 ? (presaALaVista.X - x, presaALaVista.Y - y)
                                 : (0, 0);
        byte rumbo = hacia != (0, 0) ? SentidosDelBot.RumboHacia(hacia.X, hacia.Y) : (byte)_rng.Next(1, 5);   // 1=N, 2=E, 3=S, 4=O
        // NO SE EMPUJA CONTRA LO QUE TAPA EL PASO: ver `RodeoDelPaseo`.
        if (_pasoTapado is { } tapado && tapado.Mapa == sentidos.Mapa && tapado.Desde == (x, y) && tapado.Rumbo == rumbo)
            rumbo = RodeoDelPaseo(rumbo, hacia.X, hacia.Y, _rng.Next(2));

        int pasos = _rng.Next(PasosMin, PasosMax);
        for (int i = 0; i < pasos && !cerrada.IsCompleted; i++)
        {
            if (PasoSaleDelMapa(_entorno.Datos.MapExits, sentidos.Mapa, sentidos.Posicion, rumbo)) break;   // ver `PasoSaleDelMapa`
            var (px, py) = SentidosDelBot.Paso(rumbo);
            var aca = sentidos.Posicion;
            if (LoArrimaAlPeligro(aca, ((byte)(aca.X + px), (byte)(aca.Y + py)), PeligrosALaVista(sentidos))) break;   // ver `LoArrimaAlPeligro`
            if (!transporte.Mandar(Paquete(ClientPacketId.Walk, w => w.WriteByte(rumbo)))) return false;
            sentidos.PredecirPaso(rumbo);
            await Task.Delay(UnPaso(), ct);
            // El servidor corrige con un PosUpdate el paso que no se dio: quedar donde estaba es que algo tapa esa baldosa.
            if (sentidos.Posicion == aca)
            {
                _pasoTapado = (sentidos.Mapa, aca, rumbo);
                rumbo = RodeoDelPaseo(rumbo, hacia.X, hacia.Y, _rng.Next(2));
            }
            if (sentidos.Salud < TomaPocion) return true;   // se corta el paseo: hay algo más urgente
        }
        return true;
    }

    /// <summary>
    /// EL RODEO DEL PASEO: el rumbo que prueba cuando el paso en <paramref name="tapado"/> no se dio. El otro eje hacia donde va
    /// (<paramref name="dx"/>, <paramref name="dy"/>), si lo hay; si no, de costado, para el lado que dice <paramref name="moneda"/>
    /// (0 o 1). Nunca el mismo rumbo.
    ///
    /// <para>Medido en `oficios13`: un Cazador de nivel 8 pasó media hora en la misma baldosa del 12 tomándose 668 pociones, con un
    /// Goblin al lado que le pegaba y que para él no es presa (no se le pelea) ni peligro (no se huye). El paseo iba hacia una presa
    /// lejana siempre por el mismo rumbo, y el paso rebotaba contra lo que tapaba la baldosa. Reproducido desde el estado guardado
    /// (`clavado1`), la sonda mostró el paso que no se da: un Esqueleto parado en la baldosa de enfrente.</para>
    /// </summary>
    public static byte RodeoDelPaseo(byte tapado, int dx, int dy, int moneda)
    {
        var (px, py) = SentidosDelBot.Paso(tapado);
        if (px != 0 && dy != 0) return SentidosDelBot.RumboHacia(0, dy);
        if (py != 0 && dx != 0) return SentidosDelBot.RumboHacia(dx, 0);
        int lado = moneda == 0 ? -1 : 1;
        return px != 0 ? SentidosDelBot.RumboHacia(0, lado) : SentidosDelBot.RumboHacia(lado, 0);
    }

    /// <summary>El último paso del paseo que no se dio: en qué mapa, desde dónde y hacia dónde.</summary>
    private (int Mapa, (byte X, byte Y) Desde, byte Rumbo)? _pasoTapado;

    /// <summary>
    /// ¿EL PASEO VA HACIA LA PRESA? Si hay una a la vista y no está lejos de su zona, sí; si no, al azar, como siempre.
    ///
    /// <para>El radio del temperamento (<see cref="RadioDeCazaSuyo"/>) decide si INTERRUMPE lo que está haciendo por una
    /// pelea, no si alguna vez va a caminar para ese lado. Con el paseo al azar, una presa fuera de ese radio dejaba al bot
    /// dando vueltas en círculos al lado de algo que no iba a atacar: medido con la sonda del ciclo, esa sola situación era
    /// ~20 % del tiempo de ocho bots, la rama más grande, incluso después de arreglar la paciencia del coto.</para>
    ///
    /// <para>No lo vuelve una persecución: sigue siendo el paseo —unos pocos pasos y una pausa— y la vuelta siguiente vuelve
    /// a decidir. Si en el camino la presa entra en su radio, la agarra la compuerta de pelea. Y volver a la zona manda por
    /// encima: sin ese freno el bot se iría detrás de lo que apareciera y dejaría de tener un lugar.</para>
    /// </summary>
    public static bool ElPaseoVaHaciaLaPresa(bool lejosDeSuZona, bool veUnaPresa) => !lejosDeSuZona && veUnaPresa;

    /// <summary>Dónde considera que es su zona. Se fija al entrar y se muda con él si cambia de mapa.</summary>
    private (byte X, byte Y)? _casa;

    /// <summary>¿Le conviene pelearse con esa criatura? Ataca sólo lo que ataca y se puede atacar —así no se
    /// mete con un ciudadano ni con un guardia, que lo dejarían criminal— y sólo si el golpe del bicho es
    /// chico contra su vida.</summary>
    private bool EsPresa(short ficha, int vidaMaxima, int nivel, bool conPociones, bool curaVeneno) =>
        EsPresaPara(_entorno.Datos.Npc(ficha), vidaMaxima, nivel, conPociones, curaVeneno) && !NoLaLastima(ficha);

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

    /// <summary>
    /// ¿ESTA POCIÓN CURA EL VENENO? No alcanza con el SubTipo: el 5 es "lanza un hechizo sobre quien la
    /// toma", y hay OCHO pociones así. Sólo las dos Violetas lanzan el hechizo <c>Curar Veneno</c>; las otras
    /// seis lanzan Metamorfosis, Remover parálisis, Desencantar —dos—, Invisibilidad y Sanar.
    ///
    /// <para>Con el SubTipo a secas el bot envenenado se tomaba la de invisibilidad y llamaba a
    /// <c>OlvidarVeneno</c>: creía haberse curado mientras el veneno le seguía sacando vida. Y peor, porque
    /// tener antidoto es lo que decide si una criatura venenosa es presa o peligro: salía a cazar serpientes
    /// creyendo que estaba cubierto.</para>
    /// </summary>
    public static bool CuraElVeneno(ObjInfo? o, SpellInfo? loQueLanza) =>
        o is { IsPotion: true, SubType: PocionQueLanzaUnHechizo } && loQueLanza is { IsCurePoison: true };

    private bool EsPocionQueCuraElVeneno(short obj) =>
        _entorno.Datos.Obj(obj) is { } o && CuraElVeneno(o, _entorno.Datos.Spell((short)o.LanzaHechizo));

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
    /// <summary>
    /// EL REPARTO DE CLASES. Un dado por bot deja clases en cero: sembrando un <c>Random</c> con el hash del
    /// nombre, la lista de cuarenta y ocho nombres —que es el techo, no hay más— da Gladiador seis veces y
    /// LEÑADOR, CARPINTERO y SASTRE NINGUNA. Y el oficio del Leñador está entero en el bot (el hacha, la leña, la
    /// skill de talar, los árboles de <see cref="QueCuentaComoTrabajo"/>) con sus tests en verde: escrito, probado
    /// y sin correr nunca en el juego.
    ///
    /// <para>El orden no es casual: los primeros ocho —que es con cuantos se arranca— cubren los dos oficios de juntar
    /// (el Leñador y el Pescador), el Druida, que junta raíces y prepara pociones, dos clases de maná más y tres de
    /// combate, así que una corrida corta ya ejercita casi todos los caminos que el bot sabe recorrer. El Carpintero y
    /// el Sastre quedaron al final de cuando el bot todavía no fabricaba.</para>
    ///
    /// <para>SIN MINERO NI HERRERO (decisión del autor, 19/09): el oficio del Minero no tiene cómo andar. Todas las minas
    /// están en mapas pk, y según la data la única sin nada peligroso adentro a la que se llega a pie, las Minas Dorck,
    /// queda detrás del Bosque Dorck (Jabalíes que pegan hasta 130 y Arañas gigantes hasta 115), peligroso para el bot
    /// hasta nivel 36; las demás, peor. El Herrero forja con los lingotes que salen de lo que se mina. Los personajes
    /// que ya existían siguen con su clase.</para>
    ///
    /// <para>Da la vuelta: con dieciséis bots hay uno de cada clase y con treinta y dos, dos.</para>
    /// </summary>
    public static readonly IReadOnlyList<byte> Reparto = new byte[] { 13, 2, 3, 11, 1, 8, 10, 7, 17, 4, 18, 5, 9, 6, 15, 16 };

    /// <summary>Qué clase le toca al bot número <paramref name="indice"/> del poblado.</summary>
    public static byte ClaseQueLeToca(int indice) => Reparto[indice % Reparto.Count];

    private (byte Raza, byte Genero, byte Clase, byte Hogar, short Cabeza) Aspecto()
    {
        int semilla = 17;
        foreach (char c in Nombre) semilla = unchecked(semilla * 31 + c);
        var r = new Random(semilla);
        byte genero = (byte)r.Next(1, 3);
        byte raza = (byte)r.Next(1, 7);            // 1..6: las que tienen tabla de cabezas (Equipment.HeadRange)
        // LA CLASE SALE DEL REPARTO, no del dado. Un nombre que no está en el poblado —no pasa hoy— sortea entre las del reparto.
        int suLugar = PobladoDeBots.LugarEnElPoblado(Nombre);
        byte clase = suLugar >= 0 ? ClaseQueLeToca(suLugar) : Reparto[r.Next(Reparto.Count)];
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
