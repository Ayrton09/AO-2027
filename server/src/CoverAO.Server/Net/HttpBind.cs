using System.Net;

namespace CoverAO.Server.Net;

/// <summary>
/// Abre un <see cref="HttpListener"/> en TODA interfaz, con caída a loopback si el sistema no lo permite.
///
/// Existe porque los dos puertos HTTP del servidor —el cliente web y el WebSocket del juego— tienen que
/// tomar la MISMA decisión, y no la estaban tomando: el de archivos probaba <c>http://+:puerto/</c> y caía a
/// loopback, y el del WebSocket estaba cableado a loopback "para evitar el URL ACL de Windows". Con esa
/// asimetría, un servidor hosteado servía la página a todo el mundo y no dejaba conectarse a nadie: el amigo
/// abría la URL, veía el login y le rebotaba el WebSocket. Es el defecto más caro que puede tener algo que se
/// reparte para hostear, porque el que lo sufre no tiene forma de darse cuenta de qué le pasó.
///
/// La diferencia entre plataformas es real y por eso hay caída: en Linux abrir <c>+</c> no pide ningún
/// permiso especial, mientras que en Windows <c>HttpListener</c> exige que el proceso sea administrador o que
/// exista una reserva de URL (<c>netsh http add urlacl</c>). Si no se puede, el servidor NO se cae: queda en
/// loopback —que alcanza para jugar en la misma máquina y para un reverse-proxy— y lo dice.
/// </summary>
internal static class HttpBind
{
    /// <summary>Resultado de abrir: el listener y si quedó accesible desde afuera de la máquina.</summary>
    internal readonly record struct Resultado(HttpListener Listener, bool Publico);

    /// <param name="queEs">Cómo nombrar este puerto en los mensajes ("Cliente web", "WebSocket").</param>
    /// <param name="token">Se usa para CORTAR el listener al apagar. No es opcional: ver abajo.</param>
    /// <param name="soloLocal">Para el panel de administración, que no se publica nunca.</param>
    /// <returns><c>null</c> si no se pudo abrir ni siquiera en loopback (puerto ocupado, típicamente).</returns>
    public static Resultado? Abrir(int puerto, string queEs, CancellationToken token, bool soloLocal = false)
    {
        var l = Intentar(puerto, queEs, soloLocal);
        if (l is null) return null;
        // EL LISTENER SE PARA DESDE EL TOKEN, y esto es lo que hace que el servidor pueda apagarse.
        //
        // `HttpListener.GetContextAsync()` NO mira el CancellationToken: se queda esperando un pedido que
        // puede no llegar nunca. El `while (!token.IsCancellationRequested)` de los bucles sólo se evalúa
        // ENTRE pedidos, así que un servidor sin tráfico quedaba estacionado ahí para siempre, y el
        // `Task.WhenAll` del apagado no volvía nunca. Medido en Linux: el proceso imprimía "Apagando servidor
        // (SIGTERM)... / Listener detenido." y seguía vivo cinco minutos después, en futex_wait.
        //
        // Para quien hostea eso NO es cosmético: `systemctl stop` y `docker stop` esperan su timeout y después
        // mandan SIGKILL, y el guardado de los conectados —que corre DESPUÉS del WhenAll— nunca llega a correr.
        // O sea que apagar el servidor perdía todo lo hecho desde el último autosave.
        //
        // Parar el listener hace que `GetContextAsync` tire y el bucle se desarme solo, que es el mecanismo
        // que la API ofrece para esto.
        token.Register(() => { try { l.Stop(); } catch { /* ya cerrado */ } });
        return new Resultado(l, !soloLocal && EstaEnTodaInterfaz(l));
    }

    private static bool EstaEnTodaInterfaz(HttpListener l) => l.Prefixes.Any(p => p.StartsWith("http://+:"));

    private static HttpListener? Intentar(int puerto, string queEs, bool soloLocal)
    {
        if (!soloLocal)
        {
            var todo = new HttpListener();
            todo.Prefixes.Add($"http://+:{puerto}/");
            try { todo.Start(); return todo; }
            catch (HttpListenerException) { /* sin permisos para toda interfaz: probamos loopback */ }
        }

        var local = new HttpListener();
        local.Prefixes.Add($"http://localhost:{puerto}/");
        local.Prefixes.Add($"http://127.0.0.1:{puerto}/");
        try { local.Start(); return local; }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"{queEs} no disponible en :{puerto} ({ex.Message}). " +
                                    "Suele ser que el puerto ya está en uso.");
            return null;
        }
    }

    /// <summary>Los puertos HTTP que quedaron en loopback. Se ACUMULAN y el aviso sale una sola vez con
    /// TODOS, que es lo que arregla el defecto que tenía este aviso: era único —bien— pero nombraba un solo
    /// puerto, y los que caen a loopback son DOS (el WebSocket y el cliente web). El host reservaba el que
    /// le dijimos, abría la página desde otra máquina, veía el login… y el WebSocket seguía en loopback, así
    /// que no podía conectarse nunca. Peor: el bloque "Como entrar" le decía que sí.</summary>
    private static readonly System.Collections.Generic.List<int> _enLoopback = new();
    private static bool _yaAviso;
    private static int _reportados;

    /// <summary>Cuántos servidores HTTP van a reportar cómo quedaron (el WebSocket siempre, el cliente web
    /// si esta instalación lo trae; el panel de admin NO, que nace loopback a propósito). Lo fija Program.cs.
    ///
    /// <para>Hace falta un contador y no alcanza con imprimir desde el último que se abre, porque el ORDEN
    /// no es el que parece: el cliente web arranca en su línea (`webTask = ... RunAsync`) y el WebSocket
    /// recién adentro del `Task.WhenAll` de más abajo, o sea que el que se enlaza ÚLTIMO es el WebSocket.
    /// Atar el aviso a ese orden lo dejaría roto en silencio el día que alguien mueva una de las dos
    /// líneas.</para></summary>
    public static int PuertosHttpEsperados = 2;

    /// <summary>Si el cliente web entra por su propio puerto (<c>/ws</c>), el puerto suelto del WebSocket deja
    /// de hacer falta para jugar: queda como camino de compatibilidad para un cliente que no conozca la ruta.
    /// Lo pone Program.cs cuando esta instalación trae cliente web.
    ///
    /// <para>Importa para el aviso, y no es cosmético: mientras los dos contaban igual, un WebSocket caído a
    /// loopback tapaba la línea "Desde tu red (WiFi)" y le pedía al host dos reservas de URL — con lo cual el
    /// que hacía una sola seguía sin poder jugar y el que las hacía las dos gastaba una de más. Ahora el aviso
    /// nombra lo que de verdad hay que abrir.</para></summary>
    public static bool ElJuegoEntraPorElPuertoWeb;

    /// <summary>¿Quedó algún puerto HTTP <b>necesario</b> accesible sólo desde esta máquina?</summary>
    public static bool HayPuertosEnLoopback => _enLoopback.Count > 0;

    /// <param name="opcional">Este puerto no hace falta para jugar (hoy: el del WebSocket suelto cuando el
    /// juego ya entra por el puerto del cliente web). No entra en la lista de reservas ni tapa el "Desde tu
    /// red", pero sí cuenta como reportado: el aviso sale recién cuando reportaron todos.</param>
    public static void AvisarSiSoloLocal(bool publico, int puerto, bool opcional = false)
    {
        if (!publico && !opcional && !_enLoopback.Contains(puerto)) _enLoopback.Add(puerto);
        // Se cuenta al que reporta, se haya quedado en loopback o no: el aviso sale cuando ya reportaron
        // TODOS, para poder nombrar de una vez cada URL que hay que reservar.
        if (++_reportados >= PuertosHttpEsperados) AvisarLoopbackPendiente();
    }

    /// <summary>Imprime el aviso —una vez— con la reserva de CADA puerto que quedó en loopback.</summary>
    public static void AvisarLoopbackPendiente()
    {
        if (_yaAviso || _enLoopback.Count == 0) return;
        _yaAviso = true;
        Console.WriteLine();
        bool una = _enLoopback.Count == 1;
        Console.WriteLine(una
            ? "!! El puerto HTTP quedo accesible SOLO DESDE ESTA MAQUINA."
            : "!! Los puertos HTTP quedaron accesibles SOLO DESDE ESTA MAQUINA.");
        Console.WriteLine("   Para jugar vos solo alcanza. Para que entren desde otra computadora, en Windows:");
        Console.WriteLine("     - abri el servidor como Administrador, o");
        Console.WriteLine(una
            ? "     - reserva la URL una sola vez (como Administrador):"
            : "     - reserva las URL una sola vez (como Administrador). Son " + _enLoopback.Count + ":");
        foreach (var p in _enLoopback)
            // EL USUARIO VA RESUELTO, no como `%USERNAME%`: en Windows 11 la consola de Administrador que se
            // abre por defecto es PowerShell, y ahí esa variable de cmd.exe NO se expande — el host pegaba la
            // línea que el servidor le dictó y no reservaba nada.
            Console.WriteLine($"         netsh http add urlacl url=http://+:{p}/ user={Environment.UserName}");
        if (!una)
            Console.WriteLine("   Hacen falta TODAS: con una sola, la pagina abre desde otra maquina pero el juego no conecta.");
        Console.WriteLine("   En Linux no hace falta nada de esto.");
        Console.WriteLine();
    }

    /// <summary>La direccion IPv4 de esta maquina en su red local, o <c>null</c> si no hay ninguna.
    ///
    /// Se imprime porque `http://localhost:7669/` NO le sirve a quien quiere jugar con amigos: es la unica
    /// URL que el servidor mostraba, y no funciona desde ninguna otra computadora. Averiguar la IP propia es
    /// justo el tipo de paso que hace abandonar a alguien que solo queria levantar un servidor para tres
    /// amigos.</summary>
    public static string? IpDeRedLocal()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;   // APIPA: no hay red de verdad
                    return s;
                }
            }
        }
        catch { /* sin red o sin permisos para enumerarla: no es motivo para no arrancar */ }
        return null;
    }

    /// <summary>El bloque "como entrar" del arranque: las tres formas de llegar, en orden de dificultad.
    /// Los tres puertos van EXPLICITOS y no derivados uno del otro: el web es configurable con
    /// <c>--web-port</c>, asi que cualquier cuenta del tipo "web menos dos" miente en cuanto alguien lo
    /// mueve al 80.</summary>
    public static void ImprimirComoEntrar(int puertoWeb, int puertoWs, bool publico)
    {
        Console.WriteLine();
        Console.WriteLine("== Como entrar ==");
        Console.WriteLine($"   Vos, en esta maquina : http://localhost:{puertoWeb}/");
        var ip = IpDeRedLocal();
        // Alcanza con que UNO de los dos puertos HTTP haya quedado en loopback para que desde otra maquina
        // no se pueda jugar, asi que esta linea mira los DOS y no solo el del cliente web: antes, con la
        // pagina publica y el WebSocket local, prometia una URL que abria el login y no conectaba nunca.
        bool desdeLaRed = publico && !HayPuertosEnLoopback;
        if (desdeLaRed && ip is not null)
            Console.WriteLine($"   Desde tu red (WiFi)  : http://{ip}:{puertoWeb}/     <- pasale ESTA a tus amigos");
        else if (!desdeLaRed)
            Console.WriteLine("   Desde tu red (WiFi)  : (no disponible, ver el aviso de arriba)");
        // UN SOLO PUERTO desde que el juego entra por /ws en el mismo del cliente web. La linea de antes
        // nombraba dos, y el que abria uno solo veia la pagina y no podia conectarse nunca.
        Console.WriteLine(ElJuegoEntraPorElPuertoWeb
            ? $"   Desde internet       : abri en tu router el puerto {puertoWeb} hacia esta maquina"
            : $"   Desde internet       : abri en tu router los puertos {puertoWeb} y {puertoWs} hacia esta maquina");
        // El puerto de juego NO se nombra acá a propósito: abrirlo no le sirve a nadie. Habla el protocolo
        // clásico, pero el cliente VB6 no pasa del login (manda la clave ofuscada con SEncriptar y este
        // servidor no la deshace), así que la línea anterior mandaba a abrir un puerto de más.
        Console.WriteLine("                          Con dominio propio conviene un reverse-proxy con HTTPS: ver HOSTING.md");
        Console.WriteLine();
    }
}
