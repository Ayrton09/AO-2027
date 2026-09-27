using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

/// <summary>
/// El <c>Server.ini</c> del original: la única perilla que tiene quien HOSTEA el servidor.
///
/// Hasta acá el port no lo leía — el puerto y los límites eran flags de línea de comandos, los rates vivían
/// sólo en el panel (y se perdían al reiniciar), y <b>no había forma de ser GM sin editar un .chr a mano</b>,
/// porque el único personaje con privilegios estaba cableado en el seed del fixture. Eso dejaba inservibles
/// los ~70 comandos GM ya portados: nadie podía usarlos.
///
/// El formato es el del VB6 (<c>FileIO.bas loadAdministrativeUsers</c>, TCP.bas:1941, y el propio
/// <c>Server.ini</c> del install): sección <c>[INIT]</c> con los ajustes, y las listas de staff declaradas
/// con un contador en <c>[INIT]</c> más una sección por rango:
///
/// <code>
/// [INIT]
/// Dioses=2
/// [Dioses]
/// Dios1=PEPE
/// Dios2=JUANA
/// </code>
///
/// Los nombres se comparan en MAYÚSCULAS y admiten un prefijo <c>*</c> o <c>+</c> que el original descarta
/// (lo usaba para marcar cuentas; acá se ignora igual).
/// </summary>
public sealed class ServerIni
{
    /// <summary>Rango de staff, con el bit de <c>PlayerType</c> que le corresponde (Declares.bas:103).
    /// <b>El orden es el de AUTORIDAD, de mayor a menor</b>, y de eso depende el desempate cuando un nombre
    /// aparece en dos listas: gana el primero, como la cadena de <c>ElseIf</c> de <c>UserDarPrivilegioLevel</c>.
    /// El bit NO sirve para ordenar — el de RoleMaster (32) es más grande que el de Admin (16).</summary>
    private static readonly (string Contador, string Seccion, string Clave, byte Priv)[] Rangos =
    {
        ("Admines",      "Admines",      "Admin",     16),  // PlayerType.Admin
        ("Dioses",       "Dioses",       "Dios",       8),  // PlayerType.Dios
        ("SemiDioses",   "SemiDioses",   "SemiDios",   4),  // PlayerType.SemiDios
        ("Consejeros",   "Consejeros",   "Consejero",  2),  // PlayerType.Consejero
        ("RolesMasters", "RolesMasters", "RM",        32),  // PlayerType.RoleMaster
    };

    // nombre en MAYÚSCULAS -> bit de privilegio. Un nombre en dos listas se queda con el rango MÁS ALTO,
    // que es lo que hace el original con su cadena de ElseIf (Admin gana sobre Dios, Dios sobre SemiDios…).
    private readonly Dictionary<string, byte> _staff = new(StringComparer.OrdinalIgnoreCase);

    public int StartPort { get; private set; } = 7666;
    public int MaxUsers { get; private set; }              // 0 = sin tope
    public bool AllowMultiLogins { get; private set; } = true;
    public int LimiteConexionesPorIp { get; private set; } // 0 = sin tope
    // ANTICHEAT: sanción escalonada por cuenta (ver PacketDispatcher.RegistrarTrampa). 0 = ese escalón apagado.
    public int TrampasParaEchar { get; private set; } = 3;
    public int TrampasParaBanear { get; private set; } = 6;
    // REGISTRO DE ECONOMÍA: los movimientos de oro de este monto o más van a data/logs/economia-*.log.
    public int EconomiaUmbralOro { get; private set; } = 1000;
    // PERSONAJES EN EL MUNDO POR IP (el original: tope fijo de 10 CONEXIONES por IP y AllowMultiLogins todo o
    // nada). 0 = sin tope. Con --fixture vale 0 salvo que el ini lo escriba (como las cadencias).
    public int MaxPersonajesPorIp { get; private set; } = 10;   // 10, como las conexiones del original: en una LAN juegan varios
    /// <summary>Cuentas nuevas que una misma IP puede CREAR cada diez minutos. Es nuestro: el rate-limit de
    /// login cuenta fallos y crear una cuenta con un nombre libre no falla nunca. 0 = sin cupo.</summary>
    public int CuentasNuevasPorIp { get; private set; } = 3;
    // Largo mínimo de una contraseña nueva (crear cuenta y cambiar clave). El original no exige nada.
    public int ClaveMinima { get; private set; } = 6;
    public IReadOnlyCollection<string> InitEscritas { get; private set; } = Array.Empty<string>();
    /// <summary>¿El ini escribió esta clave de <c>[INIT]</c>? Para que el fixture no apague un tope que un flow pidió.</summary>
    public bool InitEscrita(string clave) => InitEscritas.Contains(clave, StringComparer.OrdinalIgnoreCase);
    // TECHO DE CADENCIA por conexión (ver Net/LimiteDeCadencia.cs). El original no tiene control de flood;
    // hace falta porque el cliente es una página web y cualquiera puede mandar por el WebSocket lo que quiera,
    // a la velocidad que quiera. `RafagaDePaquetes` es cuánto pico se perdona de una; `MaxPaquetesPorSegundo`
    // es el ritmo sostenido. 0 = sin límite (se sigue MIDIENDO el pico, que es lo que permite calibrarlo).
    //
    // EL 150 ESTÁ MEDIDO, no elegido a ojo. Con el techo apagado se le tiró el tráfico más apurado que hay
    // —el walker caminando y los 132 flows, que mandan una acción cada 60 ms sin pensar— y sobre 81 sesiones
    // dio: mediana 2 pkt/s, p90 15, p99 30, MÁXIMO 30. El techo queda 5× arriba de eso.
    //
    // El margen es grande a propósito, porque los dos errores no cuestan lo mismo. Si queda corto, echa
    // jugadores de verdad en medio de una pelea y el que hostea no tiene cómo enterarse de por qué; y hay un
    // caso que los flows no ejercitan: con Shift+flecha el auto-repeat del teclado manda ~31 ChangeHeading por
    // segundo él solo, más caminar y clics. Si queda holgado, el que inunda igual baja de los ~1000 pkt/s que
    // saca un `for` en la consola del navegador a 150, el spam de chat tiene SU PROPIO techo mucho más bajo, y
    // la cola de salida acotada le pone tope a la amplificación. Errar para el lado holgado es más barato.
    public int MaxPaquetesPorSegundo { get; private set; } = 150;
    public int RafagaDePaquetes { get; private set; } = 200;
    // ANTI-SPAM DEL JUEGO, que es otra cosa que el techo de paquetes: acá el que se pasa es un jugador, así
    // que la acción se descarta y se le avisa — no se lo desconecta. En MILISEGUNDOS ENTRE ACCIONES, que es
    // como lo piensa el que hostea. `Ruidosas` = hablar, susurrar, clan, /GM (les llega a otros).
    // `Consultas` = los comandos que sólo le contestan a quien pregunta. 0 = sin límite.
    public int MsEntreMensajes { get; private set; } = 700;
    public int RafagaDeMensajes { get; private set; } = 5;
    public int MsEntreConsultas { get; private set; } = 300;
    public int RafagaDeConsultas { get; private set; } = 10;
    public bool PuedeCrearPersonajes { get; private set; } = true;
    public bool ServerSoloGms { get; private set; }
    public int IdleLimitMinutos { get; private set; }      // 0 = sin desconexión por inactividad
    /// <summary>
    /// LA TASA DEL SERVER: cuántas veces la experiencia (<c>Exp</c>) y el oro (<c>Oro</c>) que da cada criatura.
    /// 1 = lo de la data; 5 = cinco veces. Es la del original: <c>.GiveEXP = GiveEXP * Expc</c> al cargar la
    /// criatura (MODULO_NPCs.bas:996 y :1004), un multiplicador directo y no un porcentaje — el install venía
    /// con 200, o sea un server de ×200.
    ///
    /// <para>No es el EVENTO del GM (<c>/EXP</c>, <c>/ORO</c> y el panel): ése vive en <c>AdminConfig</c> y
    /// multiplica encima. Estaban en el mismo campo, y un <c>/EXP</c> sobre un server de ×10 lo tomaba por un
    /// evento en curso y lo bajaba a ×1.</para>
    /// </summary>
    public int Exp { get; private set; } = 1;
    public int Oro { get; private set; } = 1;

    /// <summary>
    /// REGLA DE LA CASA — <c>EventoAlRematar</c> (default <b>0</b>, como el original). Con 1, el evento de experiencia del
    /// GM (<c>/EXP</c>, el panel) y el medio extra del donador multiplican también el RESTO que cobra el que mata a la
    /// criatura: la parte de su experiencia que no se cobró golpe a golpe.
    ///
    /// <para>En el original el resto se suma pelado (MODULO_NPCs.bas:128-142) y el evento va sólo en el goteo de cada
    /// golpe (SistemaCombate.bas:2025). Pesa sobre todo en los que castean: el hechizo resta la vida ANTES de gotear
    /// (modHechizos.bas), así que el que mata no gotea nada y todo lo de ese hechizo sale del resto. Con un evento x50, un
    /// Mago que mata de un hechizo cobra x1 y un Guerrero que mata con la espada, x50. Pedido del autor (2026-09-27): como
    /// el original, con la opción de prenderlo.</para>
    /// </summary>
    public bool EventoAlRematar { get; private set; }

    /// <summary>
    /// <c>OroPorMonton</c> (default <b>10000</b>, el <c>MAX_INVENTORY_OBJS</c> del original): cuánto oro entra en cada montón que se
    /// tira al piso. Tirar más se reparte en varios montones por las baldosas libres de alrededor, como el original
    /// (TirarOro, InvUsuario.bas:212). Pedido del tester, 2026-09-27. Va de 1 a 32767, lo que entra en una pila del piso.
    /// </summary>
    public int OroPorMonton { get; private set; } = 10_000;

    /// <summary>
    /// REGLA DE LA CASA — <c>AlcanceDelHechizo</c> (default <b>1</b>, PRENDIDA): al tocar Lanzar, el cliente web marca hasta
    /// dónde llega el hechizo mientras se apunta. Es el rectángulo en el que el server acepta el clic —ocho baldosas a los
    /// costados y seis arriba y abajo, la caja de visión del original (Protocol.bas:2646)—, igual para todos los hechizos. El
    /// cliente original mostraba justo esa caja; el web muestra más, y sin la marca no se sabe hasta dónde se puede tirar.
    /// Pedido del autor, 2026-09-27. Con 0 no se marca. Le llega al cliente en <c>coverao-config.json</c>.
    /// </summary>
    public bool AlcanceDelHechizo { get; private set; } = true;

    /// <summary>
    /// REGLA DE LA CASA — <c>DestinoDeLosPortales</c> (default <b>1</b>, PRENDIDA): cada portal dice a dónde lleva, con un cartel
    /// arriba y en el texto que sale al clickearlo ("Teleport a Nix"). En el original los 67 portales del mundo se llaman
    /// "Teleport" a secas y el destino se descubre pisándolo. Pedido del tester, 2026-09-27. Lo dibuja el cliente web con la
    /// salida de la baldosa; le llega en <c>coverao-config.json</c>. Con 0, como el original.
    /// </summary>
    public bool DestinoDeLosPortales { get; private set; } = true;

    /// <summary>
    /// REGLA DE LA CASA — <c>VidaMinimaDeJefe</c> (default <b>50000</b>): desde cuánta vida máxima una criatura hostil que da
    /// experiencia es un JEFE, y el cliente web muestra su vida en una barra grande arriba de la pantalla mientras está a la
    /// vista. Con 50.000 son 16: los dragones grandes y el Rey Dragón, Garveloth, los gólems, los yetis, el Guardián, el
    /// Profeta Arcano, el Vampiro Transformado y el Grinch. Pedido del tester, 2026-09-27. Con 0 no hay jefes ni barra, y el
    /// server no manda el paquete (<c>VidaDelJefe</c>, que el original no tiene).
    /// </summary>
    public int VidaMinimaDeJefe { get; private set; } = 50_000;

    /// <summary>El techo de <see cref="Exp"/> y <see cref="Oro"/>: el <c>Integer</c> de VB6 donde el original
    /// guarda la tasa (<c>Expc</c>, Declares.bas:36). Más grande, allá el server no arrancaba.</summary>
    public const int TasaMaxima = short.MaxValue;

    /// <summary>
    /// EL AVISO PARA EL Server.ini DE ANTES. Hasta el 2026-09-20 las tasas se leían como porcentaje y la
    /// plantilla traía <c>Exp=100</c> = normal. Al actualizar, el armador CONSERVA el ini del host, así que ese
    /// 100 pasa a ser cien veces sin que nadie lo haya decidido. Los porcentajes de antes eran múltiplos de 100:
    /// ése es el caso que se avisa. Un host que de verdad quiere ×100 lo ve una vez por arranque y sigue.
    /// Devuelve null si no hay nada que avisar.
    /// </summary>
    public string? AvisoDeTasaVieja()
    {
        static bool Sospechosa(int tasa) => tasa >= 100 && tasa % 100 == 0;
        if (!Sospechosa(Exp) && !Sospechosa(Oro)) return null;
        return $"OJO: la experiencia va x{Exp} y el oro x{Oro}. Hasta el 20/09 este número era un porcentaje " +
               "(100 = normal). Si tu Server.ini es de antes, poné Exp=1 y Oro=1 para lo normal.";
    }
    public string? Ruta { get; private set; }              // de dónde se leyó (null = defaults)

    /// <summary>
    /// Las claves de <c>[INIT]</c> que el servidor sabe leer. La lista está a mano y un test la cruza contra
    /// las llamadas reales de este archivo, así que no puede quedarse vieja sin que algo se ponga rojo.
    ///
    /// <para>Existe por un problema concreto de ACTUALIZAR: el host conserva su <c>Server.ini</c> entre
    /// versiones —el armador no lo pisa, a propósito— así que las opciones nuevas nunca aparecen en su
    /// archivo. Sin este aviso, una versión puede agregar una regla que cambia el juego y el que hostea no se
    /// entera de que existe ni de con qué valor está corriendo.</para>
    /// </summary>
    public static readonly string[] ClavesConocidas =
    {
        "AlcanceDelHechizo", "AllowMultiLogins", "AtacarAliadoVuelveCriminal", "BackupsAConservar", "Bots", "Centinela", "ClaveMinima", "CodigoFuente", "EconomiaUmbralOro",
        "ComercioEnCiudadesViejas", "ComercioEnElResto", "ComercioEnLindos", "ComercioEnRinkel",
        "CuraAlSubirNivel", "DropGlobal", "EventoAlRematar", "Exp", "FamiliarBusca", "OroPorMonton",
        "GolpeParalizanteDado", "GolpeParalizanteDadoGladiadorBardo",
        "GolpeParalizanteVueltas", "GolpeParalizanteVueltasPvP", "HipocamposEnMarAbierto",
        "GuardiasAtacan", "GuardiasAtacanNewbies", "GuardiasDelCaosAtacanA", "GuardiasImperialesAtacanA",
        "GuardiasPersiguen", "GuardiasRepublicanosAtacanA",
        "IdleLimit", "LimiteConexionesPorIp", "LogDias", "MaxPaquetesPorSegundo", "TrampasParaBanear", "TrampasParaEchar",
"CuentasNuevasPorIp", "DestinoDeLosPortales", "MaxPersonajesPorIp", "MaxUsers", "MetamorfosisDelDruida", "MinutosBackup", "MinutosDeClima", "MinutosDelDia",
        "MinutosGuardarUsuarios", "MsEntreConsultas", "MsEntreMensajes", "NavegacionParaCruzar", "Oro",
        "MinutosDeFogata", "OrigenesPermitidos", "OroAlSubirNivel", "PaginaPublica", "PasesDeRinkel", "PersistirSuelo",
        "PorcentajeLluvia",
        "PuedeCrearPersonajes", "PuertoWebSocketPublico", "QuitarEquipoNovato", "RafagaDeConsultas",
        "RafagaDeMensajes", "RafagaDePaquetes", "ServerSoloGMs", "StartPort", "Version", "VidaMinimaDeJefe",
    };

    /// <summary>Las opciones que el servidor sabe leer y que el <c>Server.ini</c> del host NO menciona: están
    /// corriendo con su valor por defecto. Vacía si no se leyó ningún archivo (ahí TODO es default y decirlo
    /// no aporta).</summary>
    public IReadOnlyList<string> ClavesAusentes { get; private set; } = Array.Empty<string>();

    /// <summary>De dónde bajar el código fuente de ESTE servidor. Se le publica al cliente y el jugador lo ve
    /// en el menú.
    ///
    /// No es decorativo: AO2026 es AGPL-3.0 y su cláusula de red obliga a ofrecerle el código a quien
    /// interactúa con el servidor por la red — o sea, a cada persona que entra a jugar. Un host que sólo
    /// levanta el ejecutable no tiene forma de cumplir eso salvo que el juego lo muestre, y no se le puede
    /// pedir que arme una página aparte. Poniendo una URL acá queda cumplido.</summary>
    public string CodigoFuente { get; private set; } = "";

    /// <summary>La versión de cliente que este servidor acepta (<c>[INIT] Version</c>, VB6 <c>ULTIMAVERSION</c>,
    /// FileIO.bas:1467). El cliente manda la suya en tres bytes al entrar y el original la compara como texto:
    /// <c>VersionOK = (Ver = ULTIMAVERSION)</c> (Admin.bas:99). Si no coincide, cartel de "juego desactualizado"
    /// y a la calle.
    ///
    /// <para><b>Vacía = sin control</b>, que es lo que pasa si el host no escribe la clave. El original no
    /// tiene esa salida —allá el ini SIEMPRE la trae—, y acá hace falta: el gate es una comparación literal, así
    /// que prenderlo con un valor que no sea exactamente el del cliente desplegado deja a TODO el mundo afuera.
    /// Que lo encienda quien sabe qué versión repartió.</para></summary>
    public string VersionRequerida { get; private set; } = "";

    /// <summary>Puerto del WebSocket que se le ANUNCIA al cliente web, cuando no es el mismo que el servidor
    /// abrió de verdad. 0 = anunciar el real, que es lo normal.
    ///
    /// Existe por los hostings que REMAPEAN puertos. La página no trae la dirección cableada: le pide al
    /// servidor un <c>coverao-config.json</c> para enterarse por dónde hablar, y hasta ahora ahí iba el
    /// puerto INTERNO. Si el proveedor te da el 25000 por afuera y adentro el servidor está en el 7667, el
    /// jugador baja la página bien y después el socket va a un puerto que no está abierto: se ve como
    /// "carga pero no conecta", y nada lo explica.
    ///
    /// El del cliente web no necesita gemelo: ése el jugador lo escribe en la URL, así que si está
    /// remapeado escribe el de afuera y listo. El del WebSocket no lo escribe nadie — lo descubre el
    /// cliente— y por eso es el único que hay que poder corregir.</summary>
    public int PuertoWebSocketPublico { get; private set; }

    /// <summary>Autosave y backup vienen PRENDIDOS aunque no haya <c>Server.ini</c>, y hay que escribir un
    /// <c>0</c> para apagarlos. Antes el default de los dos era 0 —o sea apagados— y eso convertía al archivo
    /// de configuración en un requisito escondido: el servidor arranca perfectamente sin él (está documentado
    /// como opcional), pero en ese caso corría sin ninguna red. Un host que borra el ini, lo renombra mal, o
    /// levanta la imagen de Docker configurando sólo por variables de entorno, se quedaba sin backups y sin
    /// autosave <b>en silencio</b>: el banner decía <c>backup=off</c>, que se lee como un estado normal.
    ///
    /// Los valores son los mismos que trae el <c>Server.ini.ejemplo</c> del paquete, a propósito: si el
    /// default del código y el del archivo no coinciden, el servidor se comporta distinto según si el archivo
    /// está o no, que es la clase de diferencia que nadie sospecha cuando algo sale mal.</summary>
    private const int DefaultAutosaveMin = 15;

    /// <summary>Una copia cada MEDIA HORA. Numero redondo y sin ataduras: se evaluó atarlo al día de juego
    /// (24 min) para tener "una foto por amanecer", y se descartó porque acoplarlo haría que cambiar el largo
    /// del día cambie en silencio cada cuánto se copia el servidor — un host que pone días de 3 minutos para
    /// probar algo se comería un backup cada 3 minutos sin haberlo pedido. Un knob, un efecto.</summary>
    private const int DefaultBackupMin = 30;

    /// <summary>48 copias cada media hora son UN DÍA REAL de historia hacia atrás, que es la misma ventana que
    /// había antes (24 copias cada hora). Subir la frecuencia sin tocar esto la habría recortado a la mitad,
    /// y de eso nadie se entera hasta que necesita una copia vieja.
    ///
    /// El disco no es el límite: una copia con 10 personajes pesa 45 KB y un <c>.chr</c> real 2,6 KB, así que
    /// un servidor con MIL jugadores gasta unos 190 MB en las 48 copias.</summary>
    private const int DefaultBackupsAConservar = 48;

    // Operación (autosave, backups y log). El autosave existía sólo como perilla del panel y se perdía al
    // reiniciar; los otros dos no existían. Son lo que hace que el servidor sobreviva a un apagón sin que el
    // host tenga que acordarse de nada.
    public int AutosaveMinutos { get; private set; }       // VB6 MinutosGuardarUsuarios. 0 = desactivado
    public int BackupMinutos { get; private set; }         // cada cuánto copiar el almacén. 0 = desactivado
    public int BackupsAConservar { get; private set; } = DefaultBackupsAConservar;
    public int LogDias { get; private set; } = 7;          // días de log en disco. 0 = no escribir archivo

    // Relojes de COMBATE (VB6 [INTERVALOS]). Los defaults son los del Server.ini del install, no inventados.
    // 0 = desactivado, igual que el resto. Ver Game/CombatTiming.cs: los dos cruzados son ATAJOS, no gates.
    public int IntervaloCastear { get; private set; } = 1000;      // IntervaloLanzaHechizo
    public int IntervaloMagiaGolpe { get; private set; } = 400;    // pegar después de castear
    public int IntervaloGolpeMagia { get; private set; } = 400;    // castear después de pegar
    public int IntervaloGolpeUsar { get; private set; } = 400;     // usar un objeto después de pegar
    // NUESTRO, no del original: cuánto esperar para usar un objeto después de CASTEAR. En el VB6 sólo el
    // golpe cuerpo a cuerpo sella ese reloj; un mago podía tomar la poción en el mismo instante que tiraba
    // el hechizo, que es lo que aprovecha el "auto-pot". 0 = como el original.
    public int IntervaloHechizoUsar { get; private set; } = 0;
    public int IntervaloAtacar { get; private set; } = 1000;        // IntervaloUserPuedeAtacar
    /// <summary><c>[INTERVALOS] IntervaloTrabajo</c> (700 en el install) — la cadencia de TRABAJAR, y el
    /// original la usa en dos lados con el MISMO contador: cada vuelta de la faena continua
    /// (<c>DoTrabajar</c>, Trabajo.bas:2475) y cada intento de ROBAR (Protocol.bas:2959).
    ///
    /// <para>La clave estaba en el archivo y no se leía: el host podía escribirla y no pasaba nada. El robo,
    /// además, se medía con la cadencia de GOLPE, que es otra perilla — dejarlas cruzadas significa que quien
    /// pone el golpe en 0 para un evento abre el robo en ráfaga sin querer.</para></summary>
    public int IntervaloTrabajo { get; private set; } = 700;

    /// <summary><c>[TIMERS] IntervaloPuedeSerAtacado</c> (5000 en el install) — LA GRACIA CONTRA LAS CRIATURAS
    /// al entrar al mundo y en cada teletransporte. Mientras dura, la IA no te ve: no te elige de blanco, no te
    /// persigue y no te tira magia (VB6 <c>IntervaloPermiteSerAtacado</c>, modNuevoTimer.bas:316, leído en los
    /// seis bucles de selección de blanco de AI_NPC.bas).
    ///
    /// <para>Se arma en dos lugares y nada más: al conectar (<c>ConnectUser</c>, TCP.bas:1199) y en
    /// <c>WarpUserChar</c> (Modulo_UsUaRiOs.bas:1660), o sea en TODO cambio de posición hecho por el server —
    /// una salida de mapa, un <c>/TELEP</c>, la cárcel, el hogar—. Caminar no la renueva.</para>
    ///
    /// <para>Este port no la tenía: aparecer en medio de un nido de hostiles era comerse el primer golpe en el
    /// acto. Es de las que sólo se notan jugando.</para></summary>
    public int IntervaloPuedeSerAtacado { get; private set; } = 5000;

    /// <summary>
    /// <c>[INTERVALOS] IntervaloFlechasCazadores</c> (1100 en el install) — la cadencia del ARCO, y la única
    /// del original que el port no tenía.
    ///
    /// <para>Es la más fuerte de todas y además es TRANSVERSAL: disparar sella el reloj, y mientras esté
    /// caliente el original no deja <b>ni pegar ni castear</b>. Sus cuatro call sites lo dicen —
    /// <c>Protocol.bas:2661</c> (arrojadizas) y <c>:2760</c> (arco) lo SELLAN; <c>:2936</c> (el clic del
    /// hechizo) y <c>SistemaCombate.bas:975</c> (<c>UsuarioAtaca</c>) sólo lo CONSULTAN.</para>
    ///
    /// <para>Sin ella el camino del disparo no tenía <b>ninguna</b> compuerta de cadencia: los otros dos
    /// chequeos de ese call site (<c>PermiteAtacar</c> y <c>PermiteLanzarSpell</c>) van con
    /// <c>Actualizar = False</c>, o sea que ninguno sella nada — el que sella es éste. Un cliente modificado
    /// disparaba tan rápido como se lo permitiera el techo de paquetes, y el arquero quedaba muy por encima
    /// de lo que es en el original.</para>
    /// </summary>
    public int IntervaloArco { get; private set; } = 1100;

    /// <summary>Cuánto le dura a un jugador la PERTENENCIA de una criatura que golpeó
    /// (<c>[TIMERS] IntervaloOwnedNpc</c>, 18.000 ms en el install; VB6 <c>IntervaloPerdioNpc</c>,
    /// modNuevoTimer.bas:351). Pasado ese rato sin volver a pegarle, la criatura queda libre y otro se la
    /// puede apropiar.
    ///
    /// <para>Es de <c>[TIMERS]</c> y no de <c>[INTERVALOS]</c> — el original tiene las dos secciones y ésta
    /// era la única de TIMERS que hacía falta leer.</para></summary>
    public int IntervaloNpcApropiado { get; private set; } = 18_000;

    /// <summary>
    /// CUÁNTO TARDA EN VOLVER UNA CRIATURA, en segundos, de la sección <c>[RESPAWN]</c>. Tres escalones por
    /// lo que vale la criatura, porque una rata y el Dragón Legendario no pueden volver a la misma velocidad.
    ///
    /// <para><b>ES DECISIÓN DEL AUTOR, no fidelidad.</b> Las dos referencias reaparecen AL INSTANTE: el VB6
    /// llama a <c>ReSpawnNpc</c> dentro de <c>MuereNpc</c> sin ninguna espera (y su campo <c>RespawnTime</c>
    /// del .dat es data muerta — una línea suelta pisa el <c>if</c> que lo leía, MODULO_NPCs.bas:1089), y
    /// aoweb devuelve 0 para todo salvo los dragones, que esperan una hora. Lo pidió el tester y el autor
    /// eligió estos números.</para>
    ///
    /// <para>Va en <c>[RESPAWN]</c>, sección PROPIA, y no en <c>[TIMERS]</c>: el original no tiene ninguna
    /// clave de esto —reaparece al instante— así que es una regla de la casa, como <c>[NIVELMINIMO]</c>.
    /// Meterla entre las claves del install haría creer que sale de ahí.</para>
    ///
    /// <para>El corte lo marca la data: de las 124 hostiles la mediana da 3.200 de experiencia, y después
    /// del cuarto puesto (Grinch 3.900.000, Rey Dragón 2.200.000, Dragón Legendario 1.750.000, Garveloth
    /// 1.250.000) cae 2,3 veces de golpe. Por eso el umbral de jefe va en el millón.</para>
    /// </summary>
    public int RespawnSegundos { get; private set; } = 30;
    /// <summary>Los que dan mucha experiencia (por defecto, desde 100.000): 5 minutos.</summary>
    public int RespawnSegundosGordo { get; private set; } = 300;
    /// <summary>Los cuatro jefes del mundo (por defecto, desde 1.000.000): media hora.</summary>
    public int RespawnSegundosJefe { get; private set; } = 1800;
    /// <summary>Desde qué experiencia una criatura cuenta como gorda.</summary>
    public long RespawnExpGordo { get; private set; } = 100_000;
    /// <summary>Desde qué experiencia cuenta como jefe.</summary>
    public long RespawnExpJefe { get; private set; } = 1_000_000;

    /// <summary>Los segundos que le tocan a una criatura que da <paramref name="experiencia"/>.</summary>
    public int SegundosDeRespawn(long experiencia) =>
        experiencia >= RespawnExpJefe ? RespawnSegundosJefe
        : experiencia >= RespawnExpGordo ? RespawnSegundosGordo
        : RespawnSegundos;

    /// <summary>Las claves de <c>[INTERVALOS]</c> que el archivo trae ESCRITAS, distinguidas de las que están
    /// tomando su valor por defecto.
    ///
    /// <para>Existe por una sola razón, y es de verificación: los relojes de combate se fuerzan a 0 con
    /// <c>--fixture</c> —si no, los flows que pegan o disparan en ráfaga se caerían por cadencia— y eso deja a
    /// las compuertas <b>sin nadie que las ejercite de punta a punta</b>. Justo a las de anti-cheat, que son
    /// las que más importa que estén bien cableadas y no sólo bien escritas.</para>
    ///
    /// <para>Con esto, un flow que quiera probar una cadencia la pone en su <c>&lt;flow&gt;_flow.ini</c> y gana
    /// sobre el 0 del fixture. Los flows que no la mencionan —que hoy son todos— no cambian en nada.</para></summary>
    public IReadOnlyCollection<string> IntervalosEscritos { get; private set; } = Array.Empty<string>();

    /// <summary>¿El ini escribió esta clave de <c>[INTERVALOS]</c>? Sin distinción de mayúsculas, como el
    /// <c>clsIniManager</c> del original.</summary>
    public bool IntervaloEscrito(string clave) =>
        IntervalosEscritos.Contains(clave, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>[INTERVALOS] IntervaloParalizado</c> — cuánto dura una parálisis, en VUELTAS del game-loop de 40 ms
    /// (500 = 20 s, que es lo que trae el Server.ini del install).
    ///
    /// <para>Es la del HECHIZO Paralizar y la de las criaturas que paralizan. NO es la del golpe que paraliza
    /// —el del gladiador y el bardo a mano limpia—, que el original clava en 150 vueltas (6 s) dentro de
    /// <c>GolpeParalizaUsuario</c> (SistemaCombate.bas:2289) y no sale de ninguna clave. Ni la de paralizar a
    /// una CRIATURA, que usa el <c>IntervaloParalizadoReducido</c> de Admin.bas:58, cableado en 37 (1,5 s).</para>
    ///
    /// <para>Estaba cableada en dos lugares del port, así que quien hostea no la podía tocar aunque el
    /// original le dé la perilla.</para>
    /// </summary>
    public int IntervaloParalizado { get; private set; } = 500;

    /// <summary><c>[INTERVALOS] IntervaloCerrarConexion</c> (5 en el install), en SEGUNDOS: lo que dura la cuenta
    /// de salida de un usuario común vivo en un mapa inseguro, sea por /SALIR o porque se le cortó la conexión
    /// (VB6 FileIO.bas:1512, leído en Cerrar_Usuario y CancelExit). Con 0 la salida espera sólo hasta el próximo
    /// segundo del reloj, como en el original.</summary>
    public int IntervaloCerrarConexion { get; private set; } = 5;

    // ── EL GOLPE QUE PARALIZA, a pedido del tester (informe del 2026-09-02, punto 2) ────────────────────────
    //
    // Las cuatro perillas de abajo son NUESTRAS: el original no tiene claves para esto, lo lleva cableado en
    // `GolpeParalizaUsuario` y `GolpeParalizaNPC` (SistemaCombate.bas:2254 y 2204). Los DEFAULTS son
    // exactamente esos números, así que un Server.ini que no las mencione se comporta igual que el original.
    //
    // Van en [INIT] con el resto de las reglas de la casa —no en [INTERVALOS], que es una sección del
    // original— para que se vea de un vistazo qué es fiel y qué agregamos.
    //
    // Y OJO CON EL NOMBRE: dicen VUELTAS porque eso miden, vueltas de ESTE server, de 40 ms. Para la de JUGADOR son
    // las mismas que las del original, que cuenta en su game-loop de 40 ms. Para la de CRIATURA no: ese contador lo
    // baja el `TIMER_AI` del original, que corre cada 380 ms (frmMain.frm:170-171), asi que sus 30 vueltas son 295
    // ticks de acá — ver `GameLimits.TicksDeUnContadorDeCriatura`. Escribir 30 pensando en segundos deja una
    // parálisis de veinte minutos.

    /// <summary><c>[INIT] GolpeParalizanteVueltas</c> — cuánto queda quieta una CRIATURA a la que un golpe a
    /// mano limpia paralizó, en vueltas de 40 ms de este server. Default 295 = 11,4 s: son las 30 vueltas del
    /// <c>Contadores.Paralisis = 30</c> del original (SistemaCombate.bas:2235) medidas en SU reloj, el TIMER_AI de 380 ms
    /// (<see cref="GameLimits.TicksDeUnContadorDeCriatura"/>). Acá decía 30 leyendo esas vueltas como si fueran del
    /// game-loop, y la criatura arrancaba a los 1,2 s.</summary>
    public int GolpeParalizanteVueltas { get; private set; } = 295;

    /// <summary><c>[INIT] GolpeParalizanteVueltasPvP</c> — lo mismo contra un JUGADOR. Default 150 = 6 s
    /// (SistemaCombate.bas:2289), y acá sí son vueltas de 40 ms en las dos puntas. A la CRIATURA el original la deja
    /// quieta casi el doble (11,4 s), aunque su número sea más chico: son relojes distintos.</summary>
    public int GolpeParalizanteVueltasPvP { get; private set; } = 150;

    /// <summary><c>[INIT] GolpeParalizanteDado</c> — contra qué se tira. La probabilidad del golpe es
    /// <c>Fuerza de Wrestling × 0,2 + nivel × 0,66</c> sobre un dado de 1 a este número, así que <b>bajarlo lo
    /// hace más frecuente</b>. Default 500, el <c>RandomNumber(1, 500)</c> del original.
    ///
    /// <para>La fórmula de la probabilidad NO se expone, y a propósito: es la misma que la de DESARMAR
    /// (<see cref="Game.Combat.ProbabilidadDesarmar"/>), así que tocarle los coeficientes cambiaría las dos
    /// cosas de una y nadie lo notaría. El dado es de este efecto solo.</para></summary>
    public int GolpeParalizanteDado { get; private set; } = 500;

    /// <summary><c>[INIT] GolpeParalizanteDadoGladiadorBardo</c> — el dado de las dos clases que pelean a mano
    /// limpia. Default 300, o sea que les sale ~1,7 veces más seguido que al resto (SistemaCombate.bas:2229).
    /// La clase cambia la PROBABILIDAD, nunca la duración.</summary>
    public int GolpeParalizanteDadoGladiadorBardo { get; private set; } = 300;

    // Relojes del mundo (día/noche y clima). Se declaran en MINUTOS y no en vueltas del game-loop porque
    // quien hostea no tiene por qué saber que el loop corre a 40 ms; la conversión la hace AplicarRelojes.
    // Ninguno de los dos relojes existe en el original —el mundo VB6 es siempre de día y la lluvia sólo se
    // conmuta con /LLUVIA—, así que estos valores son decisión de producto y por eso son configurables.
    public int MinutosDelDia { get; private set; } = 24;      // día ENTERO: amanecer + día + atardecer + noche
    public int MinutosDeClima { get; private set; } = 18;     // ciclo entero de clima (llueve una fracción)
    public int PorcentajeLluvia { get; private set; } = 18;   // qué parte del ciclo llueve

    // ── REGLAS DE LA CASA ────────────────────────────────────────────────────────────────────────────
    // Tres cosas que el tester pidió y que el ORIGINAL NO HACE. No son arreglos: cambian el balance del juego,
    // así que la decisión no es del port — es de quien hostea. Por eso vienen APAGADAS: con el ini por defecto
    // el server se comporta exactamente como el VB6, y prenderlas es una línea.
    //
    // Están implementadas y probadas igual, para que decidir cueste cambiar un número y no escribir código.

    /// <summary>Oro que se regala AL SUBIR DE NIVEL, por nivel (informe del tester, 2.12). El original da
    /// CERO: `SubirNivel` (Modulo_UsUaRiOs.bas:387) reparte vida, maná, energía, golpe y skillpoints, y no
    /// toca el oro. 0 = como el original.</summary>
    public int OroAlSubirNivel { get; private set; }

    /// <summary>¿Corre el CENTINELA anti-macro? (reporte del tester, 2026-08-24). El módulo del original está
    /// completo pero DESENCHUFADO: sus dos llamadas están comentadas en la fuente (frmMain.frm:749 y
    /// mMainLoop.bas:21), así que en el install no aparece nunca. false = como el original. Prendido, un NPC
    /// se le planta al que está trabajando y le pide una clave; el que no contesta en dos minutos se come
    /// quince de cárcel. Un Dios también lo conmuta en vivo con /CENTINELAACTIVADO.</summary>
    public bool Centinela { get; private set; }

    /// <summary>¿El familiar SALE A BUSCAR enemigo solo? (reporte del tester, 2026-08-24). El original no: la
    /// IA de mascota es <c>TipoAI.SigueAmo</c> (AI_NPC.bas:736) —te sigue y se mueve al azar— y sólo pelea
    /// contra lo que ya tenga de objetivo, que se lo pone el dueño atacando o el bicho atacándolo. false =
    /// como el original. Prendido, el familiar se va contra el hostil más cercano a su dueño, y el bicho que
    /// recibe el golpe se queda peleando con él. <b>Cambia el balance del combate</b>: un domador con familiar
    /// deja de tener que abrir cada pelea.</summary>
    public bool FamiliarBusca { get; private set; }

    /// <summary>¿Subir de nivel CURA del todo? (informe del tester, 2.13). El original sube los MÁXIMOS y deja
    /// los mínimos donde estaban: se sube de nivel herido y se sigue herido. false = como el original.</summary>
    public bool CuraAlSubirNivel { get; private set; }

    /// <summary>¿El equipo de novato desaparece al dejar de ser newbie? (informe del tester, 2.2b). El
    /// original no tiene ningún `QuitarNewbieObj`: los 25 objetos `(Newbies)` se quedan para siempre.
    /// false = como el original.</summary>
    public bool QuitarEquipoNovato { get; private set; }

    /// <summary>
    /// NIVEL MÍNIMO PARA ENTRAR A CADA MAPA, de la sección <c>[NIVELMINIMO]</c> del <c>Server.ini</c>: una
    /// línea <c>mapa=nivel</c>. Vacío = la regla no corre, que es el default del servidor.
    ///
    /// <para>Es REGLA DE LA CASA: el original tiene un campo <c>Restringir</c> por mapa, lo carga y deja que
    /// un GM lo edite, pero NADIE lo lee para negar la entrada. Lo pidió el tester (punto 5 de su informe) y
    /// el autor lo aprobó <b>sólo con mínimo, sin tope máximo</b>.</para>
    ///
    /// <para>Va por MAPA y no por cueva porque una cueva son varios mapas —el Dungeon Cristal es el 230, el
    /// 231 y el 232—, y porque así el host la toca sin tocar código.</para>
    /// </summary>
    public IReadOnlyDictionary<int, int> NivelMinimoPorMapa { get; private set; }
        = new Dictionary<int, int>();

    /// <summary>
    /// ¿LO DEJA ENTRAR? Sin mínimo para ese mapa, sí; con mínimo, sólo desde ese nivel. El GM entra siempre,
    /// igual que en el gate de la zona NEWBIE del original.
    /// </summary>
    public bool DejaEntrarAlMapa(int mapa, int nivel, bool esGm) =>
        esGm || !NivelMinimoPorMapa.TryGetValue(mapa, out int minimo) || minimo <= 0 || nivel >= minimo;

    /// <summary>¿Lo que queda tirado en el suelo sobrevive a un reinicio? (informe del tester, 2.27). El
    /// original tiene el suelo SÓLO en RAM —`MapData().ObjInfo`, que se repuebla del .csm en cada arranque—,
    /// así que un reinicio se lleva puesto todo lo que la gente dejó o se le cayó al morir. false = como el
    /// original.
    ///
    /// El tester lo reportó como si el drop estuviera roto (*"me mató una araña, se me cayó todo, cuando volví
    /// desapareció"*) y no lo estaba: se estaba reiniciando el server entre medio. Es fiel y es una pérdida
    /// real igual — en una beta el server se reinicia para actualizarlo, y cada reinicio le borra a la gente
    /// su mochila. Por eso está acá y no cableado: quien hostea decide.</summary>
    public bool PersistirSuelo { get; private set; }

    /// <summary>
    /// REGLA DE LA CASA — <c>MinutosDeFogata</c> (default 0 = como el original). Cuántos minutos dura prendida
    /// una fogata antes de apagarse sola.
    ///
    /// <para>En el original la intención está ESCRITA y el código no. <c>AccionParaRamita</c> (Acciones.bas:572)
    /// mete la posición de la fogata que se acaba de prender en una colección llamada <c>TrashCollector</c>, con
    /// el comentario <i>"Las fogatas prendidas se deben eliminar"</i> justo arriba — y esa colección aparece
    /// exactamente DOS veces en todo el servidor: esa línea y su declaración (Declares.bas:63). Nadie la vacía
    /// nunca. O sea que el recolector se declaró y jamás se escribió, y la fogata queda prendida para siempre.
    /// Por eso el default es CERO: lo fiel es que no se apague.</para>
    ///
    /// <para>Lo reportó el tester: hizo una fogata para subir Supervivencia, volvió a conectarse un buen rato
    /// después y seguía ahí. La plantilla que se distribuye viene con cinco minutos.</para>
    /// </summary>
    public int MinutosDeFogata { get; private set; }

    /// <summary>
    /// REGLA DE LA CASA — <c>PasesDeRinkel</c> (default 0 = como el install). Hace usables los dos pases que
    /// vende el pirata de Rinkel.
    ///
    /// <para>En la data del install esos dos pases (595 "a Nueva Esperanza" y 1053 "a Tiama") traen
    /// <c>Desde=20</c>, la ciudad de Rinkel; pero el único transportador del lugar está en el mapa 99, el
    /// puerto. Como usar un pase exige estar en su mapa de origen Y al lado de un transportador
    /// (InvUsuario.bas:1404), esos dos no se pueden usar en ningún lado — el pirata te los cobra y nunca te
    /// los honra. Es así también en el VB6: es un bug de la data, no del port. Los otros nueve
    /// transportadores sí coinciden con el <c>Desde</c> de sus pases.</para>
    ///
    /// <para>Va como regla de la casa y apagada por defecto por la misma razón que las otras: prendido es
    /// una decisión nuestra, apagado es el install. El proyecto ya deja a propósito otros bugs de la data
    /// sin tocar (las pistas 3-1 de los mapas Arena, el 896.png huérfano), y arreglar éste cableado dejaba
    /// dos bugs iguales con políticas opuestas. Prendida, el server también diverge del oráculo VB6 para
    /// esa acción — que es exactamente lo que una regla de la casa declara.</para>
    /// </summary>
    public bool PasesDeRinkel { get; private set; }

    /// <summary>
    /// REGLA DE LA CASA — <c>HipocamposEnMarAbierto</c> (default 0 = como el install). Con la RED de pesca en
    /// un mapa de mar abierto se saca <b>Hipocampo</b> (obj 900) en vez de los cuatro peces de siempre.
    ///
    /// <para><b>Esto no existe en el original, y el hipocampo tampoco se puede conseguir ahí.</b> El objeto
    /// está definido en el <c>obj.dat</c> —comida, restaura 100 de hambre— pero NADA lo produce: la caña da
    /// siempre Pargo (<c>DoPescar</c>, Trabajo.bas:1315), la red sortea entre Pargo, Lisa, Pez Espada y
    /// Merluza (<c>DoPescarRed</c> con <c>PecesPosibles</c>, :1429-1432), ninguna criatura lo tira, ningún
    /// mercader lo vende, no está colocado en ninguno de los 863 mapas y el código del servidor VB6 no lo
    /// nombra ni una vez. Es data muerta del install.</para>
    ///
    /// <para>Se agrega porque el tester lo pidió: en Imperium se salía a mar abierto con la red y ahí se
    /// pescaba sólo hipocampo.</para>
    ///
    /// <para><b>ES LA ÚNICA REGLA DE LA CASA QUE SALE PRENDIDA</b>, por decisión del autor: los cuatro peces
    /// de la red ya se usan y el hipocampo no lo pescaba nadie, así que prefiere que el juego lo entregue.
    /// Las otras siete salen apagadas para que "sin Server.ini" signifique "como el original", y ésta es la
    /// excepción a esa regla — está anotada como tal en <c>ReglasDeLaCasaTests</c>. Quien quiera el
    /// comportamiento del install pone <c>HipocamposEnMarAbierto=0</c>.</para>
    ///
    /// <para>Prendida, el server DIVERGE del oráculo VB6 en esa acción. No lo rompe hoy porque el barrido
    /// pesca en el mapa del fixture y no en mar abierto, pero es la clase de divergencia que una regla de la
    /// casa declara a propósito.</para>
    ///
    /// <para>Qué cuenta como mar abierto lo decide el NOMBRE del mapa (403 se llaman "Océano abierto"), no un
    /// número cableado: ver <see cref="GameData.EsOceanoAbierto"/>. La CAÑA no cambia — en Imperium era la
    /// red.</para>
    /// </summary>
    public bool HipocamposEnMarAbierto { get; private set; } = true;

    /// <summary>
    /// REGLA DE LA CASA — <c>NavegacionParaCruzar</c> (default <b>80</b>, prendida; 0 = como el original). Los puntos
    /// de Navegación que pide un pasaje que CRUZA EL AGUA a otra tierra (<see cref="GameData.MismaTierra"/>). Entre dos
    /// puertos de la misma tierra no pide nada.
    ///
    /// <para>El original no la pide: el <c>obj.dat</c> marca con <c>NecesitaNave=1</c> quince de los que cruzan y ningún
    /// código lee la clave; la geografía los incluye a todos. La reportó el tester, que
    /// recordaba los 80 puntos, y el autor la dejó prendida para todo pasaje que cruce el agua (2026-09-23). Al que
    /// no llega le sale el aviso del barco (LocaleMsg 477), y al staff no lo gatea, como al barco
    /// (<c>PuedeUsarSkill</c>).</para>
    /// </summary>
    public int NavegacionParaCruzar { get; private set; } = 80;

    /// <summary>
    /// <c>PaginaPublica</c> (default <b>1</b>, prendida). Publica <c>estado.html</c> en el mismo puerto del
    /// cliente web: quiénes están conectados y la tabla de posiciones.
    ///
    /// <para>No es una regla de la casa —no cambia una sola mecánica del juego, y por eso no está en esa
    /// cuenta—: es una VENTANA a lo que ya pasa. Existe porque con pocos jugadores lo que decide si alguien
    /// entra es saber si hay alguien adentro, y <c>/ONLINE</c> sólo se lee estando ya conectado.</para>
    ///
    /// <para>Sale nombre, nivel, clase y clan: lo mismo que cualquiera ve parado al lado tuyo en el juego.
    /// <b>No sale el mapa ni la posición de nadie</b>, que sería regalarle el mapa a quien quiera cazar
    /// gente, ni cuentas ni IPs. En 0 la ruta directamente no existe (404).</para>
    /// </summary>
    public bool PaginaPublica { get; private set; } = true;

    /// <summary>
    /// <c>OrigenesPermitidos</c> — dominios EXTRA desde los que se acepta abrir el WebSocket del juego,
    /// separados por coma. Vacío (el default) = sólo el del propio servidor.
    ///
    /// <para>Existe para el único caso legítimo que el chequeo de <c>Origin</c> rompería: servir el cliente
    /// desde otro dominio que el servidor de juego. Sin esta llave, ese host vería a sus jugadores rebotados
    /// con un 403 y sin idea de por qué.</para>
    ///
    /// <para>Lo que el chequeo evita está en <c>WebSocketGameServer.OrigenAceptable</c>: que una página
    /// cualquiera abra sockets desde el navegador de un jugador, le gaste el cupo de conexiones y le haga
    /// bloquear su propia IP con intentos de login fallidos.</para></summary>
    public string[] OrigenesPermitidos { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// REGLA DE LA CASA — <c>DropGlobal</c> (default <b>10000</b>, o sea PRENDIDA). Es <b>una de cada cuántas</b>
    /// criaturas muertas suelta, además de su botín propio, UN ítem de la tabla de <c>DropGlobal.dat</c>.
    /// <c>DropGlobal=10000</c> = una cada diez mil; <c>DropGlobal=0</c> lo apaga.
    ///
    /// <para>La tabla existe en el install —26 entradas activas, todas accesorios de los buenos: el Brazalete
    /// del Ogro +30, el Anillo de Defensa Mágica +40, las tres Orbes, y dos Pendientes marcados como veinte
    /// veces más raros— y <b>ningún código del VB6 la abre</b>. La rama del panel que la recargaría es
    /// <c>MsgBox "Drop Global"</c>. Ver <see cref="Data.DropGlobalItem"/> para el detalle y para el comentario
    /// donde el autor dejó escrito el diseño que no llegó a existir.</para>
    ///
    /// <para><b>Por qué "uno cada N" y no un porcentaje.</b> Los números del archivo no se pueden usar como
    /// probabilidades: suman 482 %. Se usan como pesos —qué sale cuando sale— y la frecuencia la decide esta
    /// clave, que es la única parte que el original no dejó escrita. La unidad es "uno cada N" porque
    /// cualquier porcentaje se queda sin resolución: acá el valor útil está en el orden de los diez mil, y en
    /// por mil el mínimo (1, o sea uno cada mil) ya es demasiado.</para>
    ///
    /// <para><b>De dónde salen los 10.000</b>, con la data del propio juego: <c>Niveles.dat</c> y el
    /// <c>GiveEXP</c> de las 118 criaturas que dan experiencia dicen que llegar a nivel 30 son ~1.900 muertes,
    /// a nivel 40 ~12.900 y a nivel 50 ~81.000. Con este valor un personaje encuentra alrededor de UNO en todo
    /// su camino hasta el 40. Con "uno cada mil" encontraría trece, que para un ítem de tope es una fuente y
    /// no un hallazgo.</para>
    ///
    /// <para><b>Sale PRENDIDA por decisión del autor</b> (2026-08-31): la tabla está escrita en el install y
    /// son accesorios que de otro modo no salen de ningún lado.</para>
    ///
    /// <para>Prendida, el server DIVERGE del oráculo VB6 al morir una criatura. No lo rompe hoy porque el
    /// barrido manda UN golpe por acción y nunca llega a matar nada, así que <c>DropNpcLoot</c> no corre
    /// ahí — pero es la clase de divergencia que una regla de la casa declara a propósito.</para>
    /// </summary>
    public int DropGlobal { get; private set; } = 10000;

    /// <summary>
    /// REGLA DE LA CASA — <c>MetamorfosisDelDruida</c> (default <b>-1</b>, o sea PRENDIDA y PERMANENTE).
    /// <b>-1</b> = no vence nunca, <b>0</b> = apagada, <b>N</b> = dura N segundos (hasta 3600).
    ///
    /// <para>Los once hechizos de metamorfosis (#38-50: Murciélago, Lobo, Jabalí, Tigre, Oso Pardo, Tortuga,
    /// Araña, Demonio, Árbol, Gólem y Dragón) existen en el <c>Hechizos.dat</c> del install con todo:
    /// cuerpo, cabeza, FX, sonido, maná, energía, skill mínima, bonos al golpe y a la defensa, y un
    /// <c>asusta</c> de 1 a 10. Y sus pergaminos se VENDEN, de 35.000 a 2.200.000 de oro. <b>Lanzarlos no
    /// hace nada</b>: el servidor VB6 lee la clave <c>metamorfosis</c> al cargar el archivo y no la usa en
    /// ninguna línea. Ver <see cref="Data.SpellInfo.EsMetamorfosis"/>.</para>
    ///
    /// <para>Prendida, la transformación hace las tres cosas que promete la ficha: el cuerpo, los bonos, y que
    /// <b>las criaturas más débiles que la forma dejen de atacar</b> — ver
    /// <see cref="Game.Player.AsustaAUnaCriaturaDeNivel"/>. Esa tercera es la que hace valer al Árbol, que
    /// cuesta 900 de maná y pega +6: no sirve para pelear, sirve para pasar desapercibido.</para>
    ///
    /// <para><b>Por qué la duración es la clave y no un número cableado.</b> Es el único dato que el original
    /// no dejó escrito en ningún lado: la ficha del hechizo dice "efecto no permanente" y ahí se termina. No
    /// hay campo de duración, ni constante, ni contador. Cablearlo sería inventar un número y esconderlo;
    /// ponerlo acá lo deja a la vista de quien hostea, que es de quien es la decisión. Todo lo demás —cuánto
    /// pega cada forma, cuánto cuesta, qué skill pide— sale de la data del install sin tocar nada.</para>
    ///
    /// <para><b>Por qué el default es permanente.</b> El 2026-08-31 salió en 120 s como decisión de balance,
    /// anclada en que el efecto más largo que sostiene el original son los 40 s de una invocación. El
    /// 2026-09-08 el autor trajo el dato que faltaba: <b>en el AO clásico la metamorfosis del druida no
    /// vence</b> —se toma la forma y se conserva hasta desencantar o morir (manual del druida de Imperium
    /// Classic, foro de FenixAO)—, así que el número dejó de ser una elección nuestra y pasó a ser el
    /// comportamiento del juego. Los segundos siguen aceptándose para quien quiera acortarla; el
    /// <c>[INTERVALOS]</c> del original, con sus 34 entradas, sigue sin tener ninguna de esto, y el contador
    /// <c>Mimetismo</c> de <c>UserCounters</c> se pone en 0 al entrar y nadie lo vuelve a tocar.</para>
    ///
    /// <para><b>Permanente es dentro de la sesión.</b> El estado no se persiste (ver
    /// <see cref="Game.Player.MetamorfosisSinVencimiento"/>): morir y desconectarse devuelven al druida a su
    /// forma, y lanzar otra transformación pisa la anterior sin acumular bonos. Con la clave en permanente,
    /// esos tres son los únicos finales — el install no trae ningún hechizo que la saque, porque el
    /// <c>desencantar</c> del <c>Hechizos.dat</c> (#22) es otra clave que el VB6 lee y no usa.</para>
    ///
    /// <para><b>Sale PRENDIDA por decisión del autor.</b> Prendida, el server DIVERGE del oráculo VB6 al
    /// lanzar esos once hechizos: donde el original no hace nada ni avisa, el nuestro transforma. El barrido no los castea —un personaje recién creado no los tiene—, así que no lo rompe.</para>
    /// </summary>
    public int MetamorfosisDelDruida { get; private set; } = -1;

    /// <summary>¿La regla está prendida? Cualquier valor que no sea 0, o sea permanente o con reloj. Existe
    /// para que ningún call site vuelva a preguntar <c>&gt; 0</c>, que con el -1 diría que está apagada.</summary>
    public bool HayMetamorfosis => MetamorfosisDelDruida != 0;

    /// <summary>¿La transformación no vence con el reloj? Es el default y el comportamiento del AO clásico.</summary>
    public bool MetamorfosisPermanente => MetamorfosisDelDruida < 0;

    /// <summary>
    /// REGLA DE LA CASA — <c>GuardiasAtacan</c> (default <b>1</b>, o sea PRENDIDA). Los guardias de las ciudades
    /// atacan a los de las facciones enemigas de su bando.
    ///
    /// <para><b>En el original no atacan.</b> Los 246 guardias del install (<c>NpcType=2</c>, GuardiasCity) no
    /// corren IA: <c>NPCAI</c> sale en su primera línea para ellos (AI_NPC.bas:700) con el comentario <i>"Mermas
    /// temporal para que los bichos nose muevan"</i>, y lo temporal quedó. Ni pelean ni se mueven. La rutina de
    /// guardias existe (<c>GuardiasAI</c>, AI_NPC.bas:65) pero nadie la llama, y distingue sólo a los renegados
    /// del resto. La data, en cambio, está hecha para pelear: los guardias por clase traen nivel 50, 40.000 de
    /// vida, golpes de 250 a 280 y hechizos.</para>
    ///
    /// <para>El bando del guardia es el <c>Status</c> de su ficha: 1 imperial (122 guardias: Ullathorpe, Nix,
    /// Banderbill, Arghûl…), 2 republicano (91: Lindos, Suramei, Illiandor…) y 4 del Caos (33: Orac, Ruinas
    /// Caóticas, Kerthon…). A quién ataca cada bando lo eligen las tres listas de abajo.</para>
    ///
    /// <para><b>Sale PRENDIDA por decisión del autor</b> (2026-09-23), a pedido del tester. Con 0 los guardias
    /// vuelven a ser los del original.</para>
    /// </summary>
    public bool GuardiasAtacan { get; private set; } = true;

    /// <summary>
    /// REGLA DE LA CASA — <c>AtacarAliadoVuelveCriminal</c> (default <b>1</b>, PRENDIDA). Atacar a un ciudadano o a
    /// un soldado sin guerra de clanes ni facción enemiga de por medio te vuelve criminal y te echa del ejército,
    /// con el aviso "Has atacado a un aliado. Ahora eres CRIMINAL.".
    ///
    /// <para>En el fuente del original no está: <c>UsuarioAtacadoPorUsuario</c> (SistemaCombate.bas:1442) no toca
    /// el estado de nadie, y renegado sólo se vuelve el que deja una facción. <b>Sale PRENDIDA por decisión del
    /// autor</b> (2026-09-25), que la recuerda del juego. Con 0 atacar no cambia el estado.</para>
    /// </summary>
    public bool AtacarAliadoVuelveCriminal { get; private set; } = true;

    /// <summary><c>GuardiasPersiguen</c> (default <b>1</b>): el guardia que ve a un enemigo sale de su puesto a
    /// buscarlo y no lo suelta mientras siga en su ciudad; cuando se va (o muere, o se hace invisible) vuelve a su
    /// puesto. Con 0 se quedan quietos: pegan al que pasa al lado y los que tienen hechizos le tiran al que ven.
    /// Los que en su ficha son estáticos —la Torre Imperial, los dos "Guardia Imperial" y los dos "Guardia
    /// Republicano"— no caminan nunca.</summary>
    public bool GuardiasPersiguen { get; private set; } = true;

    /// <summary><c>GuardiasAtacanNewbies</c> (default <b>0</b>): con 0 los guardias no le hacen nada a un newbie
    /// (hasta nivel <see cref="GameLimits.NewbieMaxLevel"/>) aunque sea del bando enemigo. Todo personaje nace
    /// Ciudadano (o Republicano si nace en Illiandor), así que sin esto el recién llegado que entra a la ciudad del
    /// otro bando se come un golpe de 280 y muere.</summary>
    public bool GuardiasAtacanNewbies { get; private set; }

    /// <summary>A quién ataca cada bando de guardias: el <c>Faccion.Status</c> de los jugadores (1 Renegado,
    /// 2 Ciudadano, 3 Republicano, 4 Caos, 5 Armada, 6 Milicia), por el <c>Status</c> del guardia (1, 2 o 4).
    /// En el ini van por nombre: <c>GuardiasImperialesAtacanA</c>, <c>GuardiasRepublicanosAtacanA</c> y
    /// <c>GuardiasDelCaosAtacanA</c>. Los defaults son los del tester: cada bando ataca a todos menos a los suyos.</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<byte>> EnemigosDeLosGuardias { get; private set; } = new Dictionary<int, IReadOnlySet<byte>>
    {
        [GuardiaImperial] = LeerEnemigos(ImperialesAtacanPorDefecto, null),
        [GuardiaRepublicano] = LeerEnemigos(RepublicanosAtacanPorDefecto, null),
        [GuardiaDelCaos] = LeerEnemigos(DelCaosAtacanPorDefecto, null),
    };

    // Los defaults van escritos como los escribiría el host: cada bando, a todos menos a los suyos.
    private const string ImperialesAtacanPorDefecto = "Renegado, Republicano, Caos, Milicia";
    private const string RepublicanosAtacanPorDefecto = "Renegado, Ciudadano, Caos, Armada";
    private const string DelCaosAtacanPorDefecto = "Renegado, Ciudadano, Republicano, Armada, Milicia";

    /// <summary>El <c>Status</c> del NPCs.dat que marca el bando de un guardia.</summary>
    public const int GuardiaImperial = 1, GuardiaRepublicano = 2, GuardiaDelCaos = 4;

    /// <summary>¿Este guardia ataca a alguien de esta facción?</summary>
    public bool GuardiaAtacaA(int bandoDelGuardia, byte faccionDelJugador) =>
        EnemigosDeLosGuardias.TryGetValue(bandoDelGuardia, out var enemigos) && enemigos.Contains(faccionDelJugador);

    /// <summary>
    /// OPCIONAL — A QUIÉN LE VENDE EL MERCADER, según la zona (pedido del autor, 2026-09-27). En el original es fija
    /// (<see cref="Player.MercaderMeAtiende"/>, VB6 Protocol.bas:5203-5220 y Acciones.bas:180-197) y la decide el
    /// <c>battle_mode</c> del mapa:
    /// <list type="bullet">
    /// <item><b>Rinkel</b> (battle_mode 3: Rinkel, Nueva Esperanza, el Dungeon de Newbies): a todos.</item>
    /// <item><b>Lindos</b> (battle_mode 2): a ciudadanos, republicanos y sus ejércitos; ni al renegado ni al Caos.</item>
    /// <item><b>Las ciudades viejas</b> (battle_mode 1: Ullathorpe, Nix, Banderbill, Arghâl…): a nadie.</item>
    /// <item><b>El resto</b> (el campo y los mapas sueltos): a nadie.</item>
    /// </list>
    /// Ésos son los defaults, así que sin tocar nada el comercio es el del original. En el ini van por nombre de facción, como
    /// las de los guardias: <c>ComercioEnRinkel</c>, <c>ComercioEnLindos</c>, <c>ComercioEnCiudadesViejas</c> y
    /// <c>ComercioEnElResto</c>. Escrita y vacía, ahí no se le vende a nadie. Al staff se le vende siempre (lo decide el
    /// llamador), y los bots miran la misma regla para saber dónde comprar.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlySet<byte>> ComercioPorZona { get; private set; } = new Dictionary<int, IReadOnlySet<byte>>
    {
        [ZonaRinkel] = LeerEnemigos(ComercioEnRinkelPorDefecto, null),
        [ZonaLindos] = LeerEnemigos(ComercioEnLindosPorDefecto, null),
        [ZonaCiudadesViejas] = LeerEnemigos("", null),
        [ZonaResto] = LeerEnemigos("", null),
    };

    private const string ComercioEnRinkelPorDefecto = "Renegado, Ciudadano, Republicano, Caos, Armada, Milicia";
    private const string ComercioEnLindosPorDefecto = "Ciudadano, Republicano, Armada, Milicia";

    /// <summary>Las zonas del comercio: el <c>battle_mode</c> del mapa, con el 0 y el 4 juntos en "el resto".</summary>
    public const int ZonaResto = 0, ZonaCiudadesViejas = 1, ZonaLindos = 2, ZonaRinkel = 3;

    /// <summary>La zona del comercio de un mapa con este <c>battle_mode</c>.</summary>
    public static int ZonaDelComercio(int battleMode) => battleMode is ZonaCiudadesViejas or ZonaLindos or ZonaRinkel ? battleMode : ZonaResto;

    /// <summary>¿El mercader de un mapa con este <c>battle_mode</c> le vende a alguien de esta facción?</summary>
    public bool MercaderLeVende(int battleMode, byte faccion) =>
        ComercioPorZona.TryGetValue(ZonaDelComercio(battleMode), out var aQuien) && aQuien.Contains(faccion);

    /// <summary>Los nombres que no se reconocieron en las listas de facciones —las de los guardias y las del comercio—. El
    /// arranque los avisa: un "Renegao" mal escrito dejaría a los renegados a salvo sin que nadie se entere.</summary>
    public IReadOnlyList<string> FaccionesDesconocidas { get; private set; } = Array.Empty<string>();

    /// <summary>La facción por su nombre, sin mirar mayúsculas ni acentos y en singular o plural.</summary>
    private static byte? FaccionPorNombre(string nombre)
    {
        string n = new string(nombre.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        return n switch
        {
            "renegado" or "renegados" => 1,
            "ciudadano" or "ciudadanos" => 2,
            "republicano" or "republicanos" => 3,
            "caos" => 4,
            "armada" or "armadas" => 5,
            "milicia" or "milicias" => 6,
            _ => null,
        };
    }

    /// <summary>Una lista de facciones separadas por coma. Escrita y vacía, ese bando no ataca a nadie. Un
    /// comentario pegado al valor (<c>; …</c>) se corta, como en <c>[NIVELMINIMO]</c>.</summary>
    private static IReadOnlySet<byte> LeerEnemigos(string lista, List<string>? desconocidas)
    {
        int corte = lista.IndexOfAny(new[] { ';', '\'', '#' });
        if (corte >= 0) lista = lista[..corte];
        var enemigos = new HashSet<byte>();
        foreach (var nombre in lista.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FaccionPorNombre(nombre) is { } faccion) enemigos.Add(faccion);
            else desconocidas?.Add(nombre);
        }
        return enemigos;
    }

    /// <summary>
    /// CUÁNTOS BOTS puebla este servidor (default <b>0</b>, o sea ninguno).
    ///
    /// <para>Un bot es una cuenta y un personaje que juegan solos: entran, caminan, y vuelven a entrar si se
    /// caen. No es un NPC — habla el protocolo por una sesión, como cualquier jugador, así que pasa por el
    /// mismo login, los mismos intervalos y el mismo anticheat, y no puede hacer nada que un jugador no pueda
    /// hacer (ver <see cref="Net.TransporteDeBot"/>).</para>
    ///
    /// <para><b>Apagado por defecto, y es una decisión.</b> Un servidor recién instalado tiene que mostrar lo
    /// que hay de verdad: si trajera gente de fábrica, el que hostea no podría distinguir a sus jugadores de
    /// los que le puso el programa. Prenderlos es del que hostea, y cuántos también.</para>
    ///
    /// <para>Cuestan lo que cuesta un jugador: ocupan lugar en <c>MaxUsers</c>, salen en el <c>/ONLINE</c> y
    /// en la página pública, y lo que hacen entra en los logs igual que lo de cualquiera. El tope son los
    /// nombres que hay para repartir (<see cref="MaxBots"/>).</para>
    /// </summary>
    public int Bots { get; private set; }

    /// <summary>El techo de <see cref="Bots"/>: son los nombres que trae la lista de
    /// <see cref="Net.PobladoDeBots.Nombres"/>. Un test cuida que los dos números no se separen.</summary>
    public const int MaxBots = 48;

    /// <summary>Privilegio del personaje según las listas del ini, o 1 (<c>PlayerType.User</c>) si no es staff.
    /// VB6 <c>UserDarPrivilegioLevel</c> (Admin.bas:418).</summary>
    public byte PrivilegioDe(string? nombre) =>
        nombre is not null && _staff.TryGetValue(nombre, out var p) ? p : (byte)1;

    public bool EsStaff(string? nombre) => PrivilegioDe(nombre) != 1;
    public int CantidadStaff => _staff.Count;

    /// <summary>Nombres de staff por rango, para poder mostrarlos (panel de admin / arranque).</summary>
    public IEnumerable<(string Nombre, byte Priv)> Staff => _staff.Select(kv => (kv.Key, kv.Value));

    /// <summary>El nombre del rango, para mostrarlo ("Dios", "SemiDios"…). Vacío si no es un rango conocido.</summary>
    public static string NombreDelRango(byte priv) =>
        Array.Find(Rangos, r => r.Priv == priv).Clave ?? "";

    /// <summary>Los rangos que se pueden otorgar, de mayor a menor autoridad.</summary>
    public static IEnumerable<(string Clave, byte Priv)> RangosOtorgables =>
        Rangos.Select(r => (r.Clave, r.Priv));

    /// <summary>
    /// DAR O SACAR STAFF, escribiendo el <c>Server.ini</c> — que es la ÚNICA autoridad de rango. Se reescribe
    /// la sección entera del rango (contador + <c>Dios1..DiosN</c>) y se recarga, así que el cambio vale para
    /// el próximo login de ese personaje, igual que si alguien hubiera editado el archivo a mano.
    ///
    /// Va por el ARCHIVO y no por el personaje a propósito: el rango se deriva en cada login y el <c>.chr</c>
    /// no manda. Guardarlo en el personaje sería exactamente el agujero que <c>AplicarPrivilegiosDeStaff</c>
    /// cerró — un <c>data/</c> movido a otra máquina llevándose los privilegios adentro.
    ///
    /// <paramref name="priv"/> = 1 (User) SACA el nombre de todas las listas. Devuelve false si no hay archivo
    /// (un server sin ini corre con defaults y no hay dónde escribir) o si el disco no deja.
    /// </summary>
    public bool GuardarStaff(string nombre, byte priv)
    {
        if (Ruta is null || string.IsNullOrWhiteSpace(nombre)) return false;
        nombre = nombre.Trim();
        try
        {
            var ini = File.Exists(Ruta) ? IniFile.Load(Ruta) : new IniFile();
            // El nombre sale de TODAS las listas antes de entrar en la suya: sin esto, subir a un Consejero a
            // Dios lo dejaría en las dos y el desempate por autoridad taparía el rastro.
            foreach (var (_, seccion, clave, _) in Rangos)
            {
                var quedan = LeerLista(ini, seccion, clave)
                    .Where(n => !string.Equals(n, nombre, StringComparison.OrdinalIgnoreCase)).ToList();
                EscribirLista(ini, seccion, clave, quedan);
            }
            if (priv != 1)
            {
                var rango = Array.Find(Rangos, r => r.Priv == priv);
                if (rango.Seccion is null) return false;                    // rango desconocido: no se inventa
                var lista = LeerLista(ini, rango.Seccion, rango.Clave).ToList();
                lista.Add(nombre);
                EscribirLista(ini, rango.Seccion, rango.Clave, lista);
            }
            ini.Save(Ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[ini] no se pudo escribir '{Ruta}' ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
        return Recargar();
    }

    /// <summary>El CONTADOR vive en <c>[INIT]</c>, no adentro de la sección. Es donde lo pone el formato del
    /// original y —lo que importa— donde lo lee <see cref="Cargar"/> al arrancar. Acá se leía y se escribía
    /// dentro de <c>[<paramref name="seccion"/>]</c>, así que el panel de administración escribía
    /// <c>[Dioses] Dioses=1</c> y el arranque seguía viendo el <c>[INIT] Dioses=0</c>: ascender a alguien
    /// desde el panel NO le daba el rango —ni en el momento, porque <see cref="Recargar"/> vuelve a pasar por
    /// <see cref="Cargar"/>— y el panel igual contestaba "queda como Dios. Toma efecto en su próximo login".
    /// Medido entrando con el personaje después de ascenderlo: seguía sin poder usar comandos de GM.</summary>
    private static List<string> LeerLista(IniFile ini, string seccion, string clave)
    {
        var salida = new List<string>();
        int n = ini.GetInt("INIT", seccion, 0);
        for (int i = 1; i <= n; i++)
        {
            string v = ini.GetString(seccion, clave + i, "").Trim();
            if (v.Length > 0) salida.Add(v);
        }
        return salida;
    }

    private static void EscribirLista(IniFile ini, string seccion, string clave, List<string> nombres)
    {
        // Se reescriben TODAS las claves viejas en blanco antes de poner las nuevas: si la lista se achica, las
        // sobrantes (`Dios5=PEPE` con el contador en 4) quedarían en el archivo y volverían al recargar si
        // alguien sube el contador a mano.
        int viejo = ini.GetInt("INIT", seccion, 0);
        for (int i = 1; i <= Math.Max(viejo, nombres.Count); i++)
            ini.Set(seccion, clave + i, i <= nombres.Count ? nombres[i - 1] : "");
        // El contador va a [INIT], que es de donde lo lee el arranque (ver la nota de LeerLista).
        ini.Set("INIT", seccion, nombres.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Los ajustes que NO toman efecto con un <see cref="Recargar"/>, porque el server ya los usó al arrancar
    /// y quien los consume se quedó con una copia: los puertos ya están escuchando, el tope de conexiones y
    /// los cupos de cadencia viven en el <c>ConnectionGuard</c> y en cada sesión ya creada, y el archivo de
    /// log ya está abierto.
    ///
    /// La lista existe para poder DECIRLO. Un `/RELOADSINI` que conteste "recargado" y deje el puerto viejo
    /// escuchando es peor que no tenerlo: el host cambia el número, ve el OK, y descubre que no pasó nada
    /// recién cuando alguien no puede entrar.
    /// </summary>
    public static readonly string[] PidenReiniciar =
        { "StartPort", "PuertoWebSocketPublico", "MaxUsers", "LimiteConexionesPorIp",
          "MaxPaquetesPorSegundo", "RafagaDePaquetes", "LogDias" };

    /// <summary>Relee el archivo y actualiza ESTA instancia, para que la vea todo el que guarda la referencia
    /// (el despachador consulta <c>_ini</c> en caliente en cada login y en cada gate de cadencia).
    /// Devuelve false si no hay archivo de dónde releer.</summary>
    public bool Recargar()
    {
        if (Ruta is null) return false;
        var nuevo = Cargar(Ruta);
        foreach (var prop in typeof(ServerIni).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (prop.CanWrite && prop.Name != nameof(Ruta))
                prop.SetValue(this, prop.GetValue(nuevo));
        _staff.Clear();
        foreach (var (nombre, priv) in nuevo.Staff) _staff[nombre] = priv;
        return true;
    }

    /// <summary>Lee el Server.ini si existe; si no, devuelve los defaults (el server arranca igual).</summary>
    public static ServerIni Cargar(string? ruta)
    {
        if (ruta is null) return new ServerIni();
        // Un problema de disco leyendo la config no puede matar el arranque: el ini es OPCIONAL —sin él el
        // server levanta con los defaults— así que un archivo con permisos mal puestos, abierto en exclusiva
        // por un editor, o un directorio sin permiso de listado en Linux tiene que degradar igual que un
        // archivo ausente. Antes la excepción subía a Program.cs sin try y el proceso moría con un stack
        // trace, sin llegar a imprimir una sola línea del banner.
        try
        {
            string? real = File.Exists(ruta) ? ruta : BuscarIgnorandoMayusculas(ruta);
            var ini = real is null ? new IniFile() : IniFile.Load(real);
            AplicarEntorno(ini);          // las variables de entorno PISAN al archivo (ver el método)
            return Desde(ini, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[ini] no se pudo leer '{ruta}' ({ex.GetType().Name}: {ex.Message}). Se usan los valores por defecto.");
            return new ServerIni();
        }
    }

    /// <summary>
    /// Deja que el ENTORNO pise cualquier clave del <c>Server.ini</c>: <c>COVERAO_&lt;SECCION&gt;_&lt;CLAVE&gt;</c>.
    /// Por ejemplo <c>COVERAO_INIT_MAXUSERS=200</c>, <c>COVERAO_INIT_STARTPORT=7666</c>,
    /// <c>COVERAO_DIOSES_DIOS1=TuPersonaje</c>.
    ///
    /// Existe por los contenedores. Adentro de una imagen de Docker no se edita un archivo: la configuración
    /// entra por <c>-e</c> o por el <c>environment:</c> del compose, y si el único camino es el .ini hay que
    /// armar un volumen y un archivo para cambiar un número. Cualquier hosting espera poder hacer esto.
    ///
    /// Se aplica ANTES de parsear y sobre el mismo <see cref="IniFile"/>, así que vale para TODAS las claves
    /// —incluidas las listas de staff— sin tocar las cuarenta lecturas de <see cref="Desde"/>, y sin que
    /// <see cref="Desde"/> deje de ser puro para los tests. Las claves y secciones no distinguen mayúsculas.
    ///
    /// Las variables de una sola palabra (<c>COVERAO_DB</c>, <c>COVERAO_FIXTURE</c>) NO son claves del ini y
    /// se ignoran acá: no tienen sección.
    /// </summary>
    private static void AplicarEntorno(IniFile ini)
    {
        const string Prefijo = "COVERAO_";
        var pisadas = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string nombre || !nombre.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase)) continue;
            string resto = nombre[Prefijo.Length..];
            int corte = resto.IndexOf('_');
            if (corte <= 0 || corte >= resto.Length - 1) continue;   // sin sección: no es una clave del ini
            ini.Set(resto[..corte], resto[(corte + 1)..], e.Value as string ?? "");
            pisadas.Add(resto);
        }
        // Se dice EN VOZ ALTA. Un valor que viene del entorno y no del archivo es justo el que hace perder una
        // tarde: el host mira el .ini, ve una cosa, y el servidor hace otra.
        if (pisadas.Count > 0)
        {
            pisadas.Sort(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"[ini] pisado por el entorno: {string.Join(", ", pisadas)}");
        }
    }

    /// <summary>En Linux el nombre del archivo distingue mayúsculas: un host que guarda <c>server.ini</c> o
    /// <c>SERVER.INI</c> vería el servidor arrancar con los defaults y sin GMs, sin ninguna pista de por qué.
    /// Buscamos el archivo por nombre sin distinguir caso; en Windows esta rama nunca corre.</summary>
    private static string? BuscarIgnorandoMayusculas(string ruta)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(ruta)) ?? ".";
        string nombre = Path.GetFileName(ruta);
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir))
            if (string.Equals(Path.GetFileName(f), nombre, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>El parseo propiamente dicho, separado del disco (así los tests no necesitan archivos).</summary>
    public static ServerIni Desde(IniFile ini, string? ruta = null)
    {
        var cfg = new ServerIni();
        cfg.Ruta = ruta;
        // QUÉ OPCIONES NO TIENE ESTE ARCHIVO. Sólo tiene sentido si hubo archivo: sin él todo es default y
        // listar las 32 sería ruido. Ver ClavesConocidas para por qué existe esto.
        if (ruta is not null)
        {
            var traidas = new HashSet<string>(ini.Claves("INIT"), StringComparer.OrdinalIgnoreCase);
            cfg.ClavesAusentes = ClavesConocidas.Where(k => !traidas.Contains(k)).ToArray();
        }
        // El PUERTO se acota: el server abre `StartPort` y `StartPort + 1` (el WebSocket del cliente web), así
        // que el techo real es 65534. Sin esto un `StartPort=70000` —o un 65535, que empuja el WS a 65536—
        // hacía que `TcpListener` tirara ArgumentOutOfRangeException y el proceso muriera; y un 0 abría un
        // puerto efímero al azar al que nadie se podía conectar. Es la clase de typo que comete cualquiera que
        // edite el ini a mano.
        int puerto = ini.GetInt("INIT", "StartPort", cfg.StartPort);
        cfg.StartPort = puerto is >= 1 and <= 65534 ? puerto : cfg.StartPort;
        cfg.MaxUsers = ini.GetInt("INIT", "MaxUsers", 0);
        cfg.CodigoFuente = (ini.GetString("INIT", "CodigoFuente", "") ?? "").Trim();
        cfg.VersionRequerida = (ini.GetString("INIT", "Version", "") ?? "").Trim();
        cfg.IntervaloNpcApropiado = ini.GetInt("TIMERS", "IntervaloOwnedNpc", cfg.IntervaloNpcApropiado);
        // El tope de un día es por si alguien escribe un número absurdo: una criatura que no vuelve nunca
        // se pide con el 0 de la otra punta, no con un millón de segundos.
        cfg.RespawnSegundos = Math.Clamp(ini.GetInt("RESPAWN", "Segundos", cfg.RespawnSegundos), 0, 86_400);
        cfg.RespawnSegundosGordo = Math.Clamp(ini.GetInt("RESPAWN", "SegundosGordo", cfg.RespawnSegundosGordo), 0, 86_400);
        cfg.RespawnSegundosJefe = Math.Clamp(ini.GetInt("RESPAWN", "SegundosJefe", cfg.RespawnSegundosJefe), 0, 86_400);
        cfg.RespawnExpGordo = Math.Max(0, ini.GetInt("RESPAWN", "ExpGordo", (int)cfg.RespawnExpGordo));
        cfg.RespawnExpJefe = Math.Max(0, ini.GetInt("RESPAWN", "ExpJefe", (int)cfg.RespawnExpJefe));
        cfg.IntervaloPuedeSerAtacado = Math.Clamp(ini.GetInt("TIMERS", "IntervaloPuedeSerAtacado", 5000), 0, 600_000);
        // Mismo recorte que StartPort: un valor fuera de rango se ignora y vale el real. Anunciarle al
        // cliente un puerto imposible lo dejaría sin poder conectarse nunca, y el ini se edita a mano.
        int wsPublico = ini.GetInt("INIT", "PuertoWebSocketPublico", 0);
        cfg.PuertoWebSocketPublico = wsPublico is >= 1 and <= 65535 ? wsPublico : 0;
        cfg.AllowMultiLogins = ini.GetInt("INIT", "AllowMultiLogins", 1) != 0;
        cfg.LimiteConexionesPorIp = ini.GetInt("INIT", "LimiteConexionesPorIp", 0);
        cfg.TrampasParaEchar = Math.Max(0, ini.GetInt("INIT", "TrampasParaEchar", 3));
        cfg.TrampasParaBanear = Math.Max(0, ini.GetInt("INIT", "TrampasParaBanear", 6));
        cfg.EconomiaUmbralOro = Math.Max(0, ini.GetInt("INIT", "EconomiaUmbralOro", 1000));
        cfg.MaxPersonajesPorIp = Math.Max(0, ini.GetInt("INIT", "MaxPersonajesPorIp", 10));
        cfg.CuentasNuevasPorIp = Math.Max(0, ini.GetInt("INIT", "CuentasNuevasPorIp", 3));
        cfg.ClaveMinima = Math.Max(0, ini.GetInt("INIT", "ClaveMinima", 6));
        cfg.InitEscritas = ini.Claves("INIT").ToArray();
        cfg.MaxPaquetesPorSegundo = Math.Max(0, ini.GetInt("INIT", "MaxPaquetesPorSegundo", 150));
        cfg.RafagaDePaquetes = Math.Max(1, ini.GetInt("INIT", "RafagaDePaquetes", 200));
        cfg.MsEntreMensajes = Math.Max(0, ini.GetInt("INIT", "MsEntreMensajes", 700));
        cfg.RafagaDeMensajes = Math.Max(1, ini.GetInt("INIT", "RafagaDeMensajes", 5));
        cfg.MsEntreConsultas = Math.Max(0, ini.GetInt("INIT", "MsEntreConsultas", 300));
        cfg.RafagaDeConsultas = Math.Max(1, ini.GetInt("INIT", "RafagaDeConsultas", 10));
        cfg.PuedeCrearPersonajes = ini.GetInt("INIT", "PuedeCrearPersonajes", 1) != 0;
        cfg.ServerSoloGms = ini.GetInt("INIT", "ServerSoloGMs", 0) != 0;
        cfg.IdleLimitMinutos = ini.GetInt("INIT", "IdleLimit", 0);
        // Un multiplicador directo, como en el original (ver Exp). Se leía como porcentaje, con un `/ 100`
        // que el VB6 no tiene: el `Exp=200` del install daba ×2 donde allá da ×200.
        cfg.Exp = Math.Clamp(ini.GetInt("INIT", "Exp", 1), 1, TasaMaxima);
        cfg.Oro = Math.Clamp(ini.GetInt("INIT", "Oro", 1), 1, TasaMaxima);
        // Se acotan acá y no en el consumidor: un número absurdo en el ini (un backup por minuto durante un año)
        // no puede volverse un problema operativo silencioso.
        // AUTOSAVE: la clave del original es `IntervaloGuardarUsuarios` y vive en [INTERVALOS], no en [INIT]
        // (VB6 `FileIO.bas:1511`). Acá se leía "MinutosGuardarUsuarios", que es el nombre de la VARIABLE donde
        // el VB6 guarda ese valor (`Admin.bas:42`), no el de la clave — así que en el Server.ini real del
        // install la búsqueda fallaba, caía al default 0 y el autosave quedaba APAGADO, con su `IntervaloGuardarUsuarios=15`
        // ahí escrito y sin efecto. Un crash perdía todo lo jugado desde el login de cada conectado.
        // Se acepta también la clave vieja en [INIT] para no romperle la config a quien ya la haya escrito.
        cfg.AutosaveMinutos = Math.Clamp(
            ini.GetInt("INTERVALOS", "IntervaloGuardarUsuarios", ini.GetInt("INIT", "MinutosGuardarUsuarios", DefaultAutosaveMin)),
            0, 240);
        // BACKUP: no tiene equivalente en el original —el VB6 sólo hace world-save manual desde el panel— así
        // que la clave es nuestra.
        cfg.BackupMinutos = Math.Clamp(ini.GetInt("INIT", "MinutosBackup", DefaultBackupMin), 0, 1440);

        // RELOJES DE COMBATE (VB6 FileIO.bas:1493-1500). Los cuatro estaban en el Server.ini del install con
        // su valor y NADIE los leía: el de casteo directamente no existía en el port —un mago tiraba hechizos
        // a velocidad de red— y los otros tres tampoco. Ver Game/CombatTiming.cs para la semántica, que no es
        // la que sugiere el nombre: los dos cruzados HABILITAN antes de tiempo, no bloquean.
        cfg.IntervaloCastear = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloLanzaHechizo", 1000), 0, 60_000);
        cfg.IntervaloMagiaGolpe = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloMagiaGolpe", 400), 0, 60_000);
        cfg.IntervaloGolpeMagia = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloGolpeMagia", 400), 0, 60_000);
        cfg.IntervaloGolpeUsar = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloGolpeUsar", 400), 0, 60_000);
        cfg.IntervaloHechizoUsar = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloHechizoUsar", 0), 0, 60_000);
        cfg.IntervaloTrabajo = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloTrabajo", 700), 0, 60_000);
        // CADENCIA DE GOLPE. La lee el VB6 en FileIO.bas:1495 y el ini del install dice 1000; el port la tenía
        // CABLEADA en 1200 (Program.cs), o sea que se peleaba un 20% más lento que el original.
        cfg.IntervaloAtacar = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloUserPuedeAtacar", 1000), 0, 60_000);
        cfg.IntervaloArco = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloFlechasCazadores", 1100), 0, 60_000);
        cfg.IntervalosEscritos = ini.Claves("INTERVALOS").ToArray();
        cfg.IntervaloParalizado = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloParalizado", 500), 1, 100_000);
        cfg.IntervaloCerrarConexion = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloCerrarConexion", 5), 0, 3600);
        // El mismo techo que la parálisis del hechizo, y piso 1: un 0 dejaría el efecto vencido en el tick
        // siguiente, que se ve igual que "no anda". El dado tiene piso 1 porque `_rng.Next(1, tope + 1)` con
        // tope 0 tira siempre 1 — paralizaría en cada golpe, que no es lo que quiere quien escribe un 0.
        cfg.GolpeParalizanteVueltas = Math.Clamp(
            ini.GetInt("INIT", "GolpeParalizanteVueltas", (int)GameLimits.TicksDeUnContadorDeCriatura(30)), 1, 100_000);
        cfg.GolpeParalizanteVueltasPvP = Math.Clamp(ini.GetInt("INIT", "GolpeParalizanteVueltasPvP", 150), 1, 100_000);
        cfg.GolpeParalizanteDado = Math.Clamp(ini.GetInt("INIT", "GolpeParalizanteDado", 500), 1, 100_000);
        cfg.GolpeParalizanteDadoGladiadorBardo =
            Math.Clamp(ini.GetInt("INIT", "GolpeParalizanteDadoGladiadorBardo", 300), 1, 100_000);
        // `IntervaloMover` (=200) está en el ini y NO se lee — pero acá el veredicto es distinto: tiene CERO
        // lecturas en todo el servidor VB6, igual que `IntervaloWS`. Es una clave decorativa. Y va más lejos:
        // el original NO tiene ningún gate de cadencia de caminata (no existen IntervaloPermiteCaminar,
        // PuedeCaminar ni TimerCaminar). Nuestros 130 ms son un agregado propio contra speedhack (SEC-2), una
        // divergencia deliberada — no una infidelidad que haya que cerrar leyendo esa clave.
        cfg.BackupsAConservar = Math.Clamp(ini.GetInt("INIT", "BackupsAConservar", DefaultBackupsAConservar), 0, 500);
        cfg.LogDias = Math.Clamp(ini.GetInt("INIT", "LogDias", 7), 0, 365);
        // Los relojes se acotan con PISO 1 minuto: un 0 (o un negativo) haría que el día entero durara una
        // vuelta del loop y el mundo amaneciera y anocheciera 25 veces por segundo, con su aviso de chat cada
        // vez. El techo de 1440 es un día real; más que eso el ciclo deja de ser un ciclo.
        cfg.MinutosDelDia = Math.Clamp(ini.GetInt("INIT", "MinutosDelDia", cfg.MinutosDelDia), 1, 1440);
        cfg.MinutosDeClima = Math.Clamp(ini.GetInt("INIT", "MinutosDeClima", cfg.MinutosDeClima), 1, 1440);
        cfg.PorcentajeLluvia = Math.Clamp(ini.GetInt("INIT", "PorcentajeLluvia", cfg.PorcentajeLluvia), 0, 100);
        // Las reglas de la casa. El tope del oro es alto a propósito (no es una opinión sobre cuánto está
        // bien) pero no infinito: un valor absurdo desbalancea el mercado del server sin que nadie lo note.
        cfg.OroAlSubirNivel = Math.Clamp(ini.GetInt("INIT", "OroAlSubirNivel", 0), 0, 1_000_000);
        cfg.CuraAlSubirNivel = ini.GetInt("INIT", "CuraAlSubirNivel", 0) != 0;
        cfg.QuitarEquipoNovato = ini.GetInt("INIT", "QuitarEquipoNovato", 0) != 0;
        // LA TABLA DE NIVELES MÍNIMOS: una línea `mapa=nivel` por mapa. Sin la sección queda vacía y la
        // regla no corre. Lo que no es un par de enteros se ignora en silencio, igual que el resto del ini:
        // un renglón mal tipeado no puede dejar al host sin arrancar.
        var minimos = new Dictionary<int, int>();
        foreach (var clave in ini.Claves("NIVELMINIMO"))
        {
            // EL COMENTARIO PEGADO AL VALOR SE CORTA. `IniFile.Parse` parte en el primer `=` y se queda con
            // TODO lo que sigue, así que `753=20 ; Cueva orca` vale el texto "20 ; Cueva orca" y no parsea.
            // Escribir el nombre de la cueva al lado del número es lo natural, y sin esto el mapa quedaba
            // sin restringir y sin que nadie avisara.
            string crudo = ini.GetString("NIVELMINIMO", clave, "");
            int corte = crudo.IndexOfAny(new[] { ';', '\'', '#' });
            if (corte >= 0) crudo = crudo[..corte];
            if (int.TryParse(clave, out int mapa) && mapa > 0
                && int.TryParse(crudo.Trim(), out int nivel) && nivel > 0)
                minimos[mapa] = nivel;
        }
        cfg.NivelMinimoPorMapa = minimos;
        cfg.PersistirSuelo = ini.GetInt("INIT", "PersistirSuelo", 0) != 0;
        cfg.MinutosDeFogata = Math.Max(0, ini.GetInt("INIT", "MinutosDeFogata", 0));
        cfg.Centinela = ini.GetInt("INIT", "Centinela", 0) != 0;
        cfg.FamiliarBusca = ini.GetInt("INIT", "FamiliarBusca", 0) != 0;
        cfg.PasesDeRinkel = ini.GetInt("INIT", "PasesDeRinkel", 0) != 0;
        cfg.HipocamposEnMarAbierto = ini.GetInt("INIT", "HipocamposEnMarAbierto", 1) != 0;
        cfg.NavegacionParaCruzar = Math.Clamp(ini.GetInt("INIT", "NavegacionParaCruzar", 80), 0, SkillProgression.MaxSkillPoints);
        cfg.PaginaPublica = ini.GetInt("INIT", "PaginaPublica", 1) != 0;
        cfg.OrigenesPermitidos = ini.GetString("INIT", "OrigenesPermitidos")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 0 apaga; si no, "uno cada N" con N >= 1. El tope alto es sólo para que un número absurdo no
        // desborde: a un millón de criaturas por ítem ya no cae nunca, que es igual que apagarlo.
        cfg.DropGlobal = Math.Clamp(ini.GetInt("INIT", "DropGlobal", 10000), 0, 1_000_000);
        // -1 es permanente y 0 apaga; de ahí para arriba son segundos. Cualquier negativo cae en el -1: pedir
        // "menos que permanente" no significa nada, y el tope de una hora sigue siendo para el reloj.
        cfg.MetamorfosisDelDruida = Math.Clamp(ini.GetInt("INIT", "MetamorfosisDelDruida", -1), -1, 3600);
        cfg.GuardiasAtacan = ini.GetInt("INIT", "GuardiasAtacan", 1) != 0;
        cfg.AtacarAliadoVuelveCriminal = ini.GetInt("INIT", "AtacarAliadoVuelveCriminal", 1) != 0;
        cfg.EventoAlRematar = ini.GetInt("INIT", "EventoAlRematar", 0) != 0;
        cfg.OroPorMonton = Math.Clamp(ini.GetInt("INIT", "OroPorMonton", 10_000), 1, short.MaxValue);
        cfg.AlcanceDelHechizo = ini.GetInt("INIT", "AlcanceDelHechizo", 1) != 0;
        cfg.DestinoDeLosPortales = ini.GetInt("INIT", "DestinoDeLosPortales", 1) != 0;
        // 0 apaga; un negativo también (no hay jefes "de menos vida que ninguna").
        cfg.VidaMinimaDeJefe = Math.Max(0, ini.GetInt("INIT", "VidaMinimaDeJefe", 50_000));
        cfg.GuardiasPersiguen = ini.GetInt("INIT", "GuardiasPersiguen", 1) != 0;
        cfg.GuardiasAtacanNewbies = ini.GetInt("INIT", "GuardiasAtacanNewbies", 0) != 0;
        var desconocidas = new List<string>();
        cfg.EnemigosDeLosGuardias = new Dictionary<int, IReadOnlySet<byte>>
        {
            [GuardiaImperial] = LeerEnemigos(ini.GetString("INIT", "GuardiasImperialesAtacanA", ImperialesAtacanPorDefecto), desconocidas),
            [GuardiaRepublicano] = LeerEnemigos(ini.GetString("INIT", "GuardiasRepublicanosAtacanA", RepublicanosAtacanPorDefecto), desconocidas),
            [GuardiaDelCaos] = LeerEnemigos(ini.GetString("INIT", "GuardiasDelCaosAtacanA", DelCaosAtacanPorDefecto), desconocidas),
        };
        cfg.ComercioPorZona = new Dictionary<int, IReadOnlySet<byte>>
        {
            [ZonaRinkel] = LeerEnemigos(ini.GetString("INIT", "ComercioEnRinkel", ComercioEnRinkelPorDefecto), desconocidas),
            [ZonaLindos] = LeerEnemigos(ini.GetString("INIT", "ComercioEnLindos", ComercioEnLindosPorDefecto), desconocidas),
            [ZonaCiudadesViejas] = LeerEnemigos(ini.GetString("INIT", "ComercioEnCiudadesViejas", ""), desconocidas),
            [ZonaResto] = LeerEnemigos(ini.GetString("INIT", "ComercioEnElResto", ""), desconocidas),
        };
        cfg.FaccionesDesconocidas = desconocidas;
        cfg.Bots = Math.Clamp(ini.GetInt("INIT", "Bots", 0), 0, MaxBots);

        foreach (var (contador, seccion, clave, priv) in Rangos)
        {
            // El contador viene del archivo y se usa como tope de un for: sin techo, un `Dioses=2000000000`
            // (un cero de más al editar) cuelga el arranque miles de millones de vueltas ANTES de abrir el
            // puerto y sin imprimir nada, o sea que el host ve el proceso vivo y el server "no levanta". 1000
            // es holgado: el install original lista una decena.
            int n = Math.Clamp(ini.GetInt("INIT", contador, 0), 0, 1000);
            for (int i = 1; i <= n; i++)
            {
                string nombre = (ini.GetString(seccion, clave + i, "") ?? "").Trim();
                if (nombre.Length > 0 && (nombre[0] == '*' || nombre[0] == '+')) nombre = nombre[1..].Trim();
                if (nombre.Length == 0) continue;
                // GANA EL PRIMERO: `Rangos` está en orden de autoridad DESCENDENTE, igual que la cadena de
                // ElseIf del original. No se puede desempatar comparando el bit de PlayerType — el de
                // RoleMaster es 32, más grande que el 16 del Admin, y no por eso manda más.
                cfg._staff.TryAdd(nombre, priv);
            }
        }
        return cfg;
    }

    /// <summary>Pasa los minutos del ini a vueltas del game-loop y arma los dos relojes del mundo.
    ///
    /// El día tiene CUATRO períodos (amanecer/día/atardecer/noche), así que <c>PeriodTicks</c> es un cuarto
    /// del día; el piso de 1 evita una división por cero si alguien pone un día más corto que cuatro vueltas.</summary>
    public void AplicarRelojes()
    {
        int vueltasPorMinuto = 60_000 / GameLoop.TickMs;
        DayNight.PeriodTicks = Math.Max(1, MinutosDelDia * vueltasPorMinuto / 4);
        Weather.CicloTicks = Math.Max(1, MinutosDeClima * vueltasPorMinuto);
        Weather.LluviaPorciento = PorcentajeLluvia;
    }

    /// <summary>Vuelca lo leído en la config viva del panel (modo mantenimiento y autosave). Las tasas NO
    /// van: los multiplicadores del panel son los EVENTOS y arrancan apagados; la tasa se lee de acá en cada
    /// pago (ver <see cref="Exp"/>).</summary>
    public void AplicarA(AdminConfig admin)
    {
        admin.OnlyGms = ServerSoloGms;
        admin.AutosaveMinutes = AutosaveMinutos;   // el panel lo puede cambiar en vivo; el ini fija el arranque
    }

    /// <summary>El <c>Faccion.Status</c> que el original le pone a un GM al entrar (TCP.bas:1941): Admin y Dios
    /// van en 9, SemiDios en 8, Consejero y RoleMaster en 7. No es cosmético — ese byte viaja en el
    /// <c>CharacterCreate</c> y es lo que hace que el cliente pinte el nick con el color de GM
    /// (<c>ColorNombresPriv</c> Case 7,8,9). Devuelve 0 si no es staff (se conserva el status de facción).</summary>
    public static byte StatusDeStaff(byte priv) => priv switch
    {
        16 or 8 => 9,   // Admin / Dios
        4 => 8,         // SemiDios
        2 or 32 => 7,   // Consejero / RoleMaster
        _ => 0,
    };
}
