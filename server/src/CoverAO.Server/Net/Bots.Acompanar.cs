using CoverAO.Protocol;

namespace CoverAO.Server.Net;

/// <summary>
/// CAZAR CON UNA PERSONA QUE LO INVITA (decisión del autor, 21/10: los grupos son con jugadores reales, no entre bots). Si una
/// persona le dice "vamos?" o "cazamos?" (el tema <see cref="DeQueHabla.Grupo"/> de <see cref="Charlar"/>) y la tiene a la vista, el
/// bot contesta que sí (<see cref="FrasesAcepta"/>) y la acompaña un rato (<see cref="AcompanaMinMin"/> a <see cref="AcompanaMaxMin"/>
/// minutos): le pega a lo que ella pelea o tiene al lado, y si no hay nada, la sigue. Es como en el original, sin party: cada uno se
/// lleva la experiencia de su daño, y el oro el que remata.
///
/// <para>Las reglas de peligro no cambian: el ciclo huye, se cura y compra antes de llegar acá, y sólo le pega a lo que para él es
/// presa (<see cref="EsPresa"/>). Deja de acompañar cuando se cumple el rato, cuando pasan <see cref="AburreMs"/> sin pelear nada,
/// cuando no llega hasta ella <see cref="IntentosSinLlegar"/> veces seguidas (en esos tres se despide en la charla), cuando la persona
/// se le despide, o cuando la pierde de vista más de <see cref="PerdidaMs"/> (y se lo dice por susurro). La misma persona que lo
/// vuelve a invitar no alarga el rato: así nadie se queda con un bot toda la sesión.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Cuánto la acompaña, en minutos: se sortea al aceptar.</summary>
    public const int AcompanaMinMin = 10, AcompanaMaxMin = 25;

    /// <summary>Sin verla este rato, la da por perdida.</summary>
    public const int PerdidaMs = 40_000;

    /// <summary>Hasta cuándo sirve un movimiento visto sobre una salida pública antes de perderla de vista.</summary>
    public const int MovimientoParaSeguirSalidaMs = 5_000;

    /// <summary>Le pega a lo que esté a esta distancia de ella o menos: lo que pelea, o lo que tiene cerca.</summary>
    public const int RadioDeAyuda = 6;

    /// <summary>Hasta esta distancia de ella no camina para acercarse.</summary>
    public const int CercaDeElla = 3;

    /// <summary>Acompañando sin pelear nada este rato, se aburre y se va: la persona parada en el pueblo no lo tiene de adorno.</summary>
    public const int AburreMs = 5 * 60_000;

    /// <summary>Tantas vueltas seguidas sin poder llegar hasta ella (del otro lado del agua o de una pared), y la deja.</summary>
    public const int IntentosSinLlegar = 5;

    public static readonly string[] FrasesAcepta = { "dale", "dale vamos", "de una", "bueno, vamos", "dale, te sigo" };
    public static readonly string[] FrasesLejos = { "estoy lejos, otro dia", "no estoy cerca", "ahora estoy lejos" };
    public static readonly string[] FrasesSeVa = { "bueno me voy, suerte", "me voy a cazar por mi lado, suerte", "listo, me voy" };
    public static readonly string[] FrasesLaPerdio = { "te perdi", "te perdi de vista, suerte", "donde estas? bueno, suerte" };

    /// <summary>Qué hace con una invitación a cazar juntos.</summary>
    public enum Invitacion { Acepta, Lejos, NoPuede }

    /// <summary>
    /// ¿LA ACEPTA? Vivo y con quien lo invita a la vista, sí; si ya acompaña a otra persona, o está muerto, no; si no la ve (le
    /// susurró desde otro lado), que está lejos. La misma persona que vuelve a invitar no alarga el rato.
    /// </summary>
    public static Invitacion QueHaceConLaInvitacion(bool vivo, bool laVe, string? acompanaA, string quien, bool laInvita = true,
                                                    bool esBlanco = false) =>
        !vivo || !laInvita || esBlanco
        || (acompanaA is not null && !string.Equals(acompanaA, quien, StringComparison.OrdinalIgnoreCase)) ? Invitacion.NoPuede
        : !laVe ? Invitacion.Lejos
        : Invitacion.Acepta;

    /// <summary>¿La deja de acompañar? Por el rato cumplido, o porque no la ve hace más de <see cref="PerdidaMs"/>.</summary>
    public static bool DejaDeAcompanar(long ahora, long hasta, long vistaEn) => ahora >= hasta || ahora - vistaEn > PerdidaMs;

    /// <summary>¿Se aburre? Sin pelear nada en <see cref="AburreMs"/>, o sin poder llegar hasta ella <see cref="IntentosSinLlegar"/>
    /// veces seguidas.</summary>
    public static bool SeAburre(long ahora, long ultimaPelea, int sinLlegar) =>
        ahora - ultimaPelea > AburreMs || sinLlegar >= IntentosSinLlegar;

    private (string Quien, long Hasta, long VistaEn, long UltimaPelea, int SinLlegar)? _acompana;
    private long _numeroDeAcompanamiento;
    private (long Grupo, long Huella)? _salidaIntentada;

    /// <summary>A quién acompaña ahora, o null.</summary>
    public string? AcompanaA => _acompana?.Quien;

    /// <summary>La frase con la que contesta una invitación, y si la acepta empieza a acompañar. Lo llama <see cref="Charlar"/>. Lo que
    /// no es una invitación (<see cref="EsInvitacion"/>) se contesta que no, y a la persona a la que le sale a pegar
    /// (<see cref="EsBlancoParaMi"/>) también: si no, la acompañaba y en la vuelta siguiente le pegaba.</summary>
    private string ContestarLaInvitacion(SentidosDelBot sentidos, string quien, string texto, long ahora)
        => ResolverLaInvitacion(sentidos, quien, texto, ahora).Frase;

    // El sí demorado se asocia a esta salida, no sólo al nombre: volver a invitar después de un chau crea otra.
    private (string Frase, long? Acompanamiento) ResolverLaInvitacion(SentidosDelBot sentidos, string quien, string texto, long ahora)
    {
        var persona = sentidos.PersonaLlamada(quien);
        switch (QueHaceConLaInvitacion(sentidos.Vivo, persona is not null, _acompana?.Quien, quien, EsInvitacion(texto),
                                       persona is { } v && EsBlancoParaMi()(v)))
        {
            case Invitacion.Acepta:
                // LA MISMA QUE VUELVE A INVITAR no alarga el rato: ver la nota de la clase.
                if (_acompana is null)
                {
                    _numeroDeAcompanamiento++;
                    _salidaIntentada = null;
                    sentidos.ObservarSalidaDe(quien);
                    _acompana = (quien, ahora + _rng.Next(AcompanaMinMin, AcompanaMaxMin + 1) * 60_000L, ahora, ahora, 0);
                    Console.WriteLine($"[bot {Nombre}] acompaña a {PacketDispatcher.ParaElLog(quien)}");
                }
                return (FrasesAcepta[_rng.Next(FrasesAcepta.Length)], _numeroDeAcompanamiento);
            case Invitacion.Lejos:
                return (FrasesLejos[_rng.Next(FrasesLejos.Length)], null);
            default:
                var no = Frases[DeQueHabla.Grupo];
                return (no[_rng.Next(no.Length)], null);
        }
    }

    /// <summary>Deja de acompañar, y lo dice: en la charla si la ve, por susurro si la perdió.</summary>
    private void DejarDeAcompanar(TransporteDeBot transporte, string quien, bool laVe)
    {
        _acompana = null;
        Console.WriteLine($"[bot {Nombre}] deja de acompañar a {PacketDispatcher.ParaElLog(quien)}");
        if (laVe)
            transporte.Mandar(Paquete(ClientPacketId.Talk,
                w => w.WriteAsciiString(FrasesSeVa[_rng.Next(FrasesSeVa.Length)]).WriteByte((byte)SentidosDelBot.ComoLoDijo.Charla)));
        else
            transporte.Mandar(Paquete(ClientPacketId.Whisper,
                w => w.WriteAsciiString(quien).WriteAsciiString(FrasesLaPerdio[_rng.Next(FrasesLaPerdio.Length)])));
    }

    /// <summary>Si la que se despide es la que acompaña, deja de acompañarla sin decir más (la despedida la contesta la charla).</summary>
    private void SeDespidio(string quien)
    {
        if (_acompana is { } a && string.Equals(a.Quien, quien, StringComparison.OrdinalIgnoreCase))
        {
            _acompana = null;
            Console.WriteLine($"[bot {Nombre}] deja de acompañar a {PacketDispatcher.ParaElLog(quien)}: se despidió");
        }
    }

    /// <summary>
    /// ACOMPAÑAR, una vuelta: le pega a lo que ella pelea o tiene cerca (<see cref="RadioDeAyuda"/>), y si no hay nada camina hasta
    /// <see cref="CercaDeElla"/> o la espera. Devuelve null si se cayó la conexión, true si hizo algo y false si no acompaña a nadie
    /// (el ciclo sigue como siempre).
    /// </summary>
    private async Task<bool?> AcompanarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Func<short, bool> esPresa,
                                             bool empiezaUnaPelea, Task cerrada, CancellationToken ct)
    {
        if (_acompana is not { } a) { sentidos.ObservarSalidaDe(null); return false; }
        long ahora = Environment.TickCount64;
        var ella = sentidos.PersonaLlamada(a.Quien);
        if (ella is not null) _acompana = a = a with { VistaEn = ahora };
        if (DejaDeAcompanar(ahora, a.Hasta, a.VistaEn) || (ella is not null && SeAburre(ahora, a.UltimaPelea, a.SinLlegar)))
        {
            sentidos.ObservarSalidaDe(null);
            DejarDeAcompanar(transporte, a.Quien, ella is not null);
            return false;
        }
        // SIN VERLA UN MOMENTO, LA ESPERA: suele volver a la vista.
        if (ella is not { } p)
        {
            if (await SeguirLaSalidaQueVioAsync(transporte, sentidos, a.Quien, cerrada, ct))
                return cerrada.IsCompleted ? null : true;
            await QuedarseAsync(sentidos, _rng.Next(800, 1_600), ct);
            return cerrada.IsCompleted ? null : true;
        }
        // LO QUE ELLA PELEA O TIENE CERCA, si para él es presa, si está al alcance de su caza y si no es la que ya no alcanzó
        // (`NoSeLlegaA`, como en la caza de siempre). Si la pelea vuelve enseguida (se murió, otro se la llevó), un paso de espera:
        // nunca da vueltas sin esperar.
        if (empiezaUnaPelea
            && sentidos.MasCercanoA((p.X, p.Y), esPresa, RadioDeAyuda, SiguenSinCamino(_sinCamino, ahora)) is { Pasos: <= RadioDeCaza } presa)
        {
            _acompana = a with { UltimaPelea = ahora };
            if (!await CazarAsync(transporte, sentidos, presa.Quien.CharIndex, cerrada, ct)) return null;
            if (Environment.TickCount64 - ahora < PasoMinMs) await Task.Delay(UnPaso(), ct);
            return cerrada.IsCompleted ? null : true;
        }
        var (x, y) = sentidos.Posicion;
        if (Math.Abs(p.X - x) + Math.Abs(p.Y - y) > CercaDeElla)
        {
            // Y SI NO LLEGA, ESPERA, y a la quinta seguida se va (`SeAburre`).
            // El viaje pertenece a esta salida. La charla de cada paso puede terminarla o aceptar otra invitación.
            long salida = _numeroDeAcompanamiento;
            bool SigueLaSalida() => _acompana is not null && _numeroDeAcompanamiento == salida;
            bool llego = await IrHastaAsync(transporte, sentidos, sentidos.Mapa, p.X, p.Y, cerrada, ct, tolerancia: CercaDeElla - 1,
                                           cortaSi: () => !SigueLaSalida(), cortaAntesDePaso: () => !SigueLaSalida());
            if (!SigueLaSalida()) return cerrada.IsCompleted ? null : true;
            if (_acompana is { } b) _acompana = b with { SinLlegar = llego ? 0 : b.SinLlegar + 1 };
            if (!llego) await QuedarseAsync(sentidos, _rng.Next(800, 1_600), ct);
            return cerrada.IsCompleted ? null : true;
        }
        if (_acompana is { } c) _acompana = c with { SinLlegar = 0 };
        await QuedarseAsync(sentidos, _rng.Next(800, 1_600), ct);
        return cerrada.IsCompleted ? null : true;
    }

    /// <summary>Busca una sola vez por el último Move+Remove visto, usando el exit del mapa público. No renueva VistaEn.</summary>
    private async Task<bool> SeguirLaSalidaQueVioAsync(TransporteDeBot transporte, SentidosDelBot sentidos, string quien,
                                                      Task cerrada, CancellationToken ct)
    {
        if (sentidos.UltimoMovimientoPerdidoDe(quien) is not { } huella || huella.Mapa != sentidos.Mapa
            || Environment.TickCount64 - huella.Cuando > MovimientoParaSeguirSalidaMs
            || !_entorno.Datos.MapExits.TryGetValue((huella.Mapa, huella.X, huella.Y), out var salida) || salida.DestMap <= 0) return false;
        long grupo = _numeroDeAcompanamiento;
        if (_salidaIntentada == (grupo, huella.Numero)) return false;
        bool SigueElIntento() => _acompana is { } a && _numeroDeAcompanamiento == grupo
            && string.Equals(a.Quien, quien, StringComparison.OrdinalIgnoreCase) && !cerrada.IsCompleted && !ct.IsCancellationRequested
            && sentidos.Mapa == huella.Mapa && !Urge(sentidos) && sentidos.PersonaLlamada(quien) is null
            && sentidos.UltimoMovimientoPerdidoDe(quien)?.Numero == huella.Numero
            && Environment.TickCount64 - huella.Cuando <= MovimientoParaSeguirSalidaMs
            && _entorno.Datos.MapExits.TryGetValue((huella.Mapa, huella.X, huella.Y), out var vigente) && vigente == salida
            && CruzaSinPeligro(sentidos)(salida.DestMap) && _entorno.Ini.DejaEntrarAlMapa(salida.DestMap, sentidos.Nivel, esGm: false)
            && !LoPeligrosoEstaATiro(sentidos.Posicion, PeligrosALaVista(sentidos));
        if (!SigueElIntento()) return false;
        _salidaIntentada = (grupo, huella.Numero); // También consume el intento si el camino se corta o alguien tapa el exit.
        // Se acerca al exit sin pisarlo. IrHasta con tolerancia 0 podría cruzarlo y después intentar volver al mapa anterior.
        if (!await IrHastaAsync(transporte, sentidos, huella.Mapa, huella.X, huella.Y, cerrada, ct, tolerancia: 1,
                               cortaSi: () => !SigueElIntento(), cortaAntesDePaso: () => !SigueElIntento()) || !SigueElIntento()) return true;
        var (x, y) = sentidos.Posicion;
        int dx = huella.X - x, dy = huella.Y - y;
        if (Math.Abs(dx) + Math.Abs(dy) != 1) return true;
        // Un solo paso final: cualquier warp termina la búsqueda; sólo un nuevo paquete visible vuelve a ubicar a la compañera.
        await UnPasoHaciaAsync(transporte, sentidos, dx, dy, ct, puedeSalir: true, cortaSi: () => !SigueElIntento());
        return true;
    }
}
