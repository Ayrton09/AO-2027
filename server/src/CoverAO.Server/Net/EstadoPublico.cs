using System.Text.Json;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// QUIÉN ESTÁ JUGANDO Y CÓMO VA CADA UNO, para que se pueda mirar sin entrar al juego.
///
/// <para>Con tres o cuatro amigos, lo que decide si alguien se conecta es saber si hay alguien adentro. El
/// juego tiene <c>/ONLINE</c>, pero para leerlo hay que estar ya conectado, que es justo lo que uno está
/// tratando de decidir. Y de tabla de posiciones no había NADA: los contadores existen y se guardan desde
/// siempre —<c>[MUERTES] UserMuertes</c> y <c>NpcsMuertes</c> son del original (TCP.bas:2166-2167)— y no los
/// mostraba nadie.</para>
///
/// <para>Esto NO toca el juego: no hay paquete nuevo, ni handler, ni nada que el oráculo pueda ver. Es una
/// lectura del mundo y del almacén, servida por el mismo HttpListener que ya reparte el cliente web.</para>
///
/// <para><b>Qué NO sale, y es a propósito.</b> Ni cuentas, ni IPs, ni el MAPA o las coordenadas de nadie:
/// publicar dónde está parado cada uno es regalarle el mapa a quien quiera cazar gente. Sale lo mismo que ya
/// se ve dentro del juego —nombre, nivel, clase, clan— y nada más.</para>
///
/// <para><b>El staff no entra en la tabla.</b> Un GM tiene <c>/CI</c> y se pone el nivel que quiera; con él
/// adentro, la tabla no mide nada. Sí aparece en la lista de conectados, que es información y no
/// competencia.</para>
///
/// <para><b>Cachés, y son la defensa además de la velocidad.</b> Sin ellas, recargar la página en un bucle
/// obliga a barrer el almacén entero por cada request — un amplificador de trabajo servido a cualquiera que
/// alcance el puerto. Con el barrido cacheado, mil pedidos por segundo cuestan lo mismo que uno.</para>
/// </summary>
public sealed class EstadoPublico
{
    private readonly World _world;
    private readonly IStore _store;
    private readonly GameLoop _loop;
    private readonly Func<string, bool> _esStaff;
    private readonly string _version;
    private readonly int _tope;

    /// <summary>Lo que dura la foto entera. Corta es a propósito: lo que se viene a mirar es quién está
    /// conectado AHORA, y cinco segundos de atraso no se notan.</summary>
    private const long MsDeFoto = 5_000;

    /// <summary>Lo que dura la tabla de posiciones. Es la parte cara —abre un archivo por personaje— y la que
    /// menos cambia: nadie sube de nivel dos veces por minuto.</summary>
    private const long MsDeRanking = 120_000;

    private readonly object _puerta = new();
    private string _foto = "";
    private long _fotoHasta;
    private object[] _ranking = Array.Empty<object>();
    private long _rankingHasta;

    /// <param name="esStaff">Quién NO entra en la tabla. Sale del <c>Server.ini</c> vivo y no del personaje,
    /// por lo mismo de siempre: el rango se deriva en cada login y el <c>.chr</c> no manda.</param>
    /// <param name="tope">Cuántos puestos publica la tabla.</param>
    public EstadoPublico(World world, IStore store, GameLoop loop, Func<string, bool> esStaff,
                         string version, int tope = 25)
    {
        _world = world; _store = store; _loop = loop; _esStaff = esStaff; _version = version; _tope = tope;
    }

    /// <summary>El JSON que se sirve, cacheado. Se puede llamar desde cualquier hilo MENOS el del game loop:
    /// lee el disco.</summary>
    public string Json()
    {
        long ahora = Environment.TickCount64;
        lock (_puerta)
        {
            if (_foto.Length > 0 && ahora < _fotoHasta) return _foto;
            _foto = Armar(ahora);
            _fotoHasta = ahora + MsDeFoto;
            return _foto;
        }
    }

    private string Armar(long ahora)
    {
        // CONECTADOS. `World.Players` es un ConcurrentDictionary: iterarlo desde acá es seguro y no toca el
        // game loop. Ordenado por nivel para que la lista se lea sola.
        var conectados = _world.Players.ToArray()
            .OrderByDescending(p => (int)p.Level).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (object)new
            {
                nombre = p.Name,
                nivel = (int)p.Level,
                clase = GameLimits.NombreDeClase(p.Class),
                clan = string.IsNullOrWhiteSpace(p.GuildName) ? null : p.GuildName,
                gm = p.IsGm,
            })
            .ToArray();

        if (_ranking.Length == 0 || ahora >= _rankingHasta)
        {
            _ranking = ArmarRanking();
            _rankingHasta = ahora + MsDeRanking;
        }

        var doc = new
        {
            servidor = new
            {
                version = _version,
                uptimeSec = _loop.Tick * GameLoop.TickMs / 1000,
                online = conectados.Length,
            },
            online = conectados,
            ranking = _ranking,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            // Sin escapar de más: los nombres de clan y de personaje llevan acentos y la página los muestra
            // tal cual. El JSON viaja como UTF-8 declarado.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>CÓMO SE ESCRIBE CADA NOMBRE, que el <c>.chr</c> no guarda.
    ///
    /// <para>El archivo de un personaje se llama con el nombre en MAYÚSCULAS —el original hace lo mismo,
    /// <c>CharPath &amp; UCase$(.Name) &amp; ".chr"</c> (FileIO.bas:1597)— y adentro no hay ninguna clave con
    /// el nombre. O sea que barrer el almacén devuelve "RUGURA", y la lista de conectados —que sale del mundo
    /// vivo— dice "Rugura": la misma persona con dos grafías en la misma pantalla.</para>
    ///
    /// <para>La caja que el jugador escribió vive en la lista <c>[PJS]</c> de su cuenta, que es de donde la
    /// saca el propio juego al mostrar los personajes. Un personaje que no figure en ninguna cuenta se queda
    /// con el nombre del archivo, que es lo único que hay de él.</para></summary>
    private Dictionary<string, string> CajaDeLosNombres()
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cuenta in _store.AllAccountNames())
        {
            try
            {
                var texto = _store.ReadAccount(cuenta);
                if (texto is null) continue;
                foreach (var pj in AccountFile.FromIni(IniFile.Parse(texto), cuenta).Characters)
                    mapa[pj] = pj;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[publico] no se pudo leer la cuenta '{cuenta}': {ex.Message}");
            }
        }
        return mapa;
    }

    /// <summary>La tabla: un archivo por personaje, ordenada por nivel y experiencia.
    ///
    /// <para>Un <c>.chr</c> ilegible NO voltea la tabla. Es la diferencia entre "a alguien se le cortó un
    /// guardado" y "la página no anda": el personaje roto se saltea y los demás siguen apareciendo.</para>
    /// </summary>
    private object[] ArmarRanking()
    {
        var comoSeEscribe = CajaDeLosNombres();
        var filas = new List<CharacterFile.FilaDeRanking>();
        foreach (var nombre in _store.AllCharacterNames())
        {
            if (_esStaff(nombre)) continue;
            try
            {
                var texto = _store.ReadCharacter(nombre);
                if (texto is null) continue;
                var lindo = comoSeEscribe.TryGetValue(nombre, out var n) ? n : nombre;
                filas.Add(CharacterFile.RankingFromIni(IniFile.Parse(texto), lindo));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[publico] no se pudo leer '{nombre}' para la tabla: {ex.Message}");
            }
        }

        return filas
            .OrderByDescending(f => f.Nivel).ThenByDescending(f => f.Exp)
            .ThenBy(f => f.Nombre, StringComparer.OrdinalIgnoreCase)
            .Take(_tope)
            .Select((f, i) => (object)new
            {
                puesto = i + 1,
                nombre = f.Nombre,
                nivel = f.Nivel,
                clase = GameLimits.NombreDeClase(f.Clase),
                clan = string.IsNullOrWhiteSpace(f.Clan) ? null : f.Clan,
                criaturas = f.Criaturas,
                usuarios = f.Usuarios,
                muertes = f.Muertes,
            })
            .ToArray();
    }
}
