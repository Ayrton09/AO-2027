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
    public int Exp { get; private set; } = 1;
    public int Oro { get; private set; } = 1;
    public string? Ruta { get; private set; }              // de dónde se leyó (null = defaults)

    /// <summary>De dónde bajar el código fuente de ESTE servidor. Se le publica al cliente y el jugador lo ve
    /// en el menú.
    ///
    /// No es decorativo: AO2026 es AGPL-3.0 y su cláusula de red obliga a ofrecerle el código a quien
    /// interactúa con el servidor por la red — o sea, a cada persona que entra a jugar. Un host que sólo
    /// levanta el ejecutable no tiene forma de cumplir eso salvo que el juego lo muestre, y no se le puede
    /// pedir que arme una página aparte. Poniendo una URL acá queda cumplido.</summary>
    public string CodigoFuente { get; private set; } = "";

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
    public int IntervaloAtacar { get; private set; } = 1000;        // IntervaloUserPuedeAtacar

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
        // El PUERTO se acota: el server abre `StartPort` y `StartPort + 1` (el WebSocket del cliente web), así
        // que el techo real es 65534. Sin esto un `StartPort=70000` —o un 65535, que empuja el WS a 65536—
        // hacía que `TcpListener` tirara ArgumentOutOfRangeException y el proceso muriera; y un 0 abría un
        // puerto efímero al azar al que nadie se podía conectar. Es la clase de typo que comete cualquiera que
        // edite el ini a mano.
        int puerto = ini.GetInt("INIT", "StartPort", cfg.StartPort);
        cfg.StartPort = puerto is >= 1 and <= 65534 ? puerto : cfg.StartPort;
        cfg.MaxUsers = ini.GetInt("INIT", "MaxUsers", 0);
        cfg.CodigoFuente = (ini.GetString("INIT", "CodigoFuente", "") ?? "").Trim();
        // Mismo recorte que StartPort: un valor fuera de rango se ignora y vale el real. Anunciarle al
        // cliente un puerto imposible lo dejaría sin poder conectarse nunca, y el ini se edita a mano.
        int wsPublico = ini.GetInt("INIT", "PuertoWebSocketPublico", 0);
        cfg.PuertoWebSocketPublico = wsPublico is >= 1 and <= 65535 ? wsPublico : 0;
        cfg.AllowMultiLogins = ini.GetInt("INIT", "AllowMultiLogins", 1) != 0;
        cfg.LimiteConexionesPorIp = ini.GetInt("INIT", "LimiteConexionesPorIp", 0);
        cfg.MaxPaquetesPorSegundo = Math.Max(0, ini.GetInt("INIT", "MaxPaquetesPorSegundo", 150));
        cfg.RafagaDePaquetes = Math.Max(1, ini.GetInt("INIT", "RafagaDePaquetes", 200));
        cfg.MsEntreMensajes = Math.Max(0, ini.GetInt("INIT", "MsEntreMensajes", 700));
        cfg.RafagaDeMensajes = Math.Max(1, ini.GetInt("INIT", "RafagaDeMensajes", 5));
        cfg.MsEntreConsultas = Math.Max(0, ini.GetInt("INIT", "MsEntreConsultas", 300));
        cfg.RafagaDeConsultas = Math.Max(1, ini.GetInt("INIT", "RafagaDeConsultas", 10));
        cfg.PuedeCrearPersonajes = ini.GetInt("INIT", "PuedeCrearPersonajes", 1) != 0;
        cfg.ServerSoloGms = ini.GetInt("INIT", "ServerSoloGMs", 0) != 0;
        cfg.IdleLimitMinutos = ini.GetInt("INIT", "IdleLimit", 0);
        // Exp/Oro del original son PORCENTAJES (el install trae 200 = doble). Acá se guardan como el
        // multiplicador entero que ya usa AdminConfig, redondeando hacia abajo y con piso 1.
        cfg.Exp = Math.Max(1, ini.GetInt("INIT", "Exp", 100) / 100);
        cfg.Oro = Math.Max(1, ini.GetInt("INIT", "Oro", 100) / 100);
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
        // CADENCIA DE GOLPE. La lee el VB6 en FileIO.bas:1495 y el ini del install dice 1000; el port la tenía
        // CABLEADA en 1200 (Program.cs), o sea que se peleaba un 20% más lento que el original.
        cfg.IntervaloAtacar = Math.Clamp(ini.GetInt("INTERVALOS", "IntervaloUserPuedeAtacar", 1000), 0, 60_000);
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
        cfg.PersistirSuelo = ini.GetInt("INIT", "PersistirSuelo", 0) != 0;
        cfg.Centinela = ini.GetInt("INIT", "Centinela", 0) != 0;
        cfg.FamiliarBusca = ini.GetInt("INIT", "FamiliarBusca", 0) != 0;
        cfg.PasesDeRinkel = ini.GetInt("INIT", "PasesDeRinkel", 0) != 0;

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

    /// <summary>Vuelca lo leído en la config viva del panel (rates y modo mantenimiento).</summary>
    public void AplicarA(AdminConfig admin)
    {
        admin.ExpMult = Exp;
        admin.GoldMult = Oro;
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
