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

    /// <summary>Aviso ÚNICO cuando algún puerto quedó en loopback: si no, el host se entera cuando un jugador
    /// no puede entrar. Se imprime una sola vez aunque caigan los dos puertos, con el comando exacto.</summary>
    private static bool _yaAviso;

    public static void AvisarSiSoloLocal(bool publico, int puerto)
    {
        if (publico || _yaAviso) return;
        _yaAviso = true;
        Console.WriteLine();
        Console.WriteLine("!! Los puertos HTTP quedaron accesibles SOLO DESDE ESTA MAQUINA.");
        Console.WriteLine("   Para jugar vos solo alcanza. Para que entren desde otra computadora, en Windows:");
        Console.WriteLine("     - abri el servidor como Administrador, o");
        Console.WriteLine($"     - reserva la URL una sola vez (como Administrador):");
        Console.WriteLine($"         netsh http add urlacl url=http://+:{puerto}/ user=%USERNAME%");
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
    public static void ImprimirComoEntrar(int puertoWeb, int puertoWs, int puertoJuego, bool publico)
    {
        Console.WriteLine();
        Console.WriteLine("== Como entrar ==");
        Console.WriteLine($"   Vos, en esta maquina : http://localhost:{puertoWeb}/");
        var ip = IpDeRedLocal();
        if (publico && ip is not null)
            Console.WriteLine($"   Desde tu red (WiFi)  : http://{ip}:{puertoWeb}/     <- pasale ESTA a tus amigos");
        else if (!publico)
            Console.WriteLine("   Desde tu red (WiFi)  : (no disponible, ver el aviso de arriba)");
        Console.WriteLine($"   Desde internet       : abri en tu router los puertos {puertoWeb} y {puertoWs} hacia esta maquina");
        Console.WriteLine($"                          (y el {puertoJuego} si alguien usa el cliente clasico de Windows)");
        Console.WriteLine("                          Con dominio propio conviene un reverse-proxy con HTTPS: ver HOSTING.md");
        Console.WriteLine();
    }
}
