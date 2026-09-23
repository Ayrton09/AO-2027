namespace CoverAO.Server.Net;

/// <summary>Hardening de red (auditoría de seguridad SEC-3). Cap de conexiones concurrentes GLOBAL y por-IP
/// para frenar floods de conexión (una IP no puede acaparar todos los slots ni agotar los recursos con
/// miles de sockets pre-auth). Thread-safe: lo consultan los accept loops de TCP y WebSocket.</summary>
public sealed class ConnectionGuard
{
    private readonly int _maxTotal;
    private readonly int _maxPerIp;
    private int _total;
    private readonly Dictionary<string, int> _perIp = new();
    private readonly object _gate = new();

    public ConnectionGuard(int maxTotal, int maxPerIp)
    {
        _maxTotal = maxTotal;
        _maxPerIp = maxPerIp;
    }

    /// <summary>Cómo terminó el pedido de un slot. Los dos rechazos no son iguales en el original: el servidor lleno
    /// se avisa con un cartel y el tope por IP (<c>aDos.MaxConexiones</c>) corta callado.</summary>
    public enum Reserva { Hecha, ServidorLleno, TopeDeLaIp }

    /// <summary>Los dos carteles con que el original rechaza una conexión antes del login (wskapiAO.bas:517 y :543).</summary>
    public const string AvisoServidorLleno = "El servidor se encuentra lleno en este momento. Disculpe las molestias ocasionadas.";
    public const string AvisoIpBloqueada = "Su IP se encuentra bloqueada en este servidor.";

    /// <summary>Intenta reservar un slot para <paramref name="ip"/> y, si no puede, dice cuál de los dos cupos lo
    /// frenó. El global se mira primero, como el <c>NextOpenUser</c> del original (wskapiAO.bas:502).</summary>
    public Reserva Reservar(string ip)
    {
        lock (_gate)
        {
            if (_maxTotal > 0 && _total >= _maxTotal) return Reserva.ServidorLleno;
            _perIp.TryGetValue(ip, out int n);
            if (_maxPerIp > 0 && n >= _maxPerIp) return Reserva.TopeDeLaIp;
            _total++;
            _perIp[ip] = n + 1;
            return Reserva.Hecha;
        }
    }

    /// <summary>Intenta reservar un slot para <paramref name="ip"/>. false si se superó el cap global o por-IP.</summary>
    public bool TryAcquire(string ip) => Reservar(ip) == Reserva.Hecha;

    public void Release(string ip)
    {
        lock (_gate)
        {
            if (_total > 0) _total--;
            if (_perIp.TryGetValue(ip, out int n))
            {
                if (n <= 1) _perIp.Remove(ip);
                else _perIp[ip] = n - 1;
            }
        }
    }

    /// <summary>Extrae la IP de un endpoint "1.2.3.4:5678" o "[::1]:5678" (descarta el puerto).</summary>
    public static string IpOf(string remoteEndpoint)
    {
        int i = remoteEndpoint.LastIndexOf(':');
        return i > 0 ? remoteEndpoint[..i] : remoteEndpoint;
    }

    /// <summary>
    /// IP canónica de un endpoint, en la MISMA forma que la que compara el accept de cada transporte:
    /// descarta el prefijo de transporte ("tcp:"/"ws:"), el puerto y los corchetes IPv6. Así el chequeo
    /// de baneo (accept) y la acción de banear (panel, sobre <c>Connection.RemoteEndpoint</c>) coinciden.
    /// Idempotente: NormalizeIp("1.2.3.4") == "1.2.3.4", NormalizeIp("::1") == "::1".
    /// </summary>
    public static string NormalizeIp(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        // Prefijo de transporte "tcp:"/"ws:" (empieza con letra y tiene ':'): descartar hasta el primer ':'.
        if (char.IsLetter(s[0]))
        {
            int p = s.IndexOf(':');
            if (p > 0 && p + 1 < s.Length) s = s[(p + 1)..];
        }
        // "[IPv6]:port" o "[IPv6]".
        if (s.StartsWith('['))
        {
            int end = s.IndexOf(']');
            if (end > 1) return s[1..end];
        }
        // "IPv4:port" tiene un solo ':'; IPv6 crudo ("::1") tiene varios y no lleva puerto en esta forma.
        int colons = 0; foreach (char c in s) if (c == ':') colons++;
        if (colons == 1) return s[..s.IndexOf(':')];
        return s;
    }
}

/// <summary>Rate-limit de intentos de login por IP (SEC-3). Frena la fuerza bruta de contraseñas y la
/// amplificación de DoS por PBKDF2 (cada intento válido-de-cuenta cuesta 210k iteraciones al server). Tras
/// <c>maxFails</c> fallos dentro de una ventana, bloquea la IP por <c>blockMs</c>. Un login exitoso resetea.</summary>
public sealed class LoginThrottle
{
    private readonly int _maxFails;
    private readonly long _windowMs;
    private readonly long _blockMs;
    private readonly Dictionary<string, Entry> _map = new();
    private readonly object _gate = new();

    private struct Entry { public int Fails; public long WindowStartMs; public long BlockedUntilMs; }
    private int _altas;

    public LoginThrottle(int maxFails, long windowMs, long blockMs)
    {
        _maxFails = maxFails;
        _windowMs = windowMs;
        _blockMs = blockMs;
    }

    /// <summary>true si la IP está actualmente bloqueada por exceso de intentos fallidos.</summary>
    public bool IsBlocked(string ip)
    {
        long now = Environment.TickCount64;
        lock (_gate)
            return _map.TryGetValue(ip, out var e) && e.BlockedUntilMs > now;
    }

    /// <summary>Cuántas IPs tiene anotadas. Para el test de la purga.</summary>
    public int Anotadas { get { lock (_gate) return _map.Count; } }

    public void RecordFailure(string ip)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            // PURGA. El mapa crecía para siempre: una IP por cada fallo de login que hubo alguna vez. Con muchas
            // IPs distintas (un barrido desde una botnet) era memoria que no volvía. Cada tantas altas se sacan
            // las entradas cuya ventana ya venció y no están bloqueadas.
            if (++_altas % 256 == 0)
                foreach (var k in _map.Where(kv => kv.Value.BlockedUntilMs <= now && now - kv.Value.WindowStartMs > _windowMs).Select(kv => kv.Key).ToList())
                    _map.Remove(k);
            _map.TryGetValue(ip, out var e);
            if (e.WindowStartMs == 0 || now - e.WindowStartMs > _windowMs) { e.WindowStartMs = now; e.Fails = 0; }
            e.Fails++;
            if (e.Fails >= _maxFails) e.BlockedUntilMs = now + _blockMs;
            _map[ip] = e;
        }
    }

    public void RecordSuccess(string ip)
    {
        lock (_gate) _map.Remove(ip);
    }
}
