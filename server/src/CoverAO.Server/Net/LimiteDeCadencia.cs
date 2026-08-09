namespace CoverAO.Server.Net;

/// <summary>
/// CUÁNTOS PAQUETES POR SEGUNDO acepta una conexión. Un cubo de fichas: se rellena a ritmo constante y cada
/// paquete gasta una; si no hay ficha, el cliente se pasó.
///
/// El original NO tiene esto —no hay control de flood en ningún lado del servidor VB6— y aun así hace falta,
/// porque el modelo de amenaza cambió: aquel corría con un cliente compilado, y acá el cliente es una página
/// web. Cualquiera abre la consola del navegador y manda por el WebSocket lo que se le ocurra, a la velocidad
/// que quiera. Y varios paquetes se AMPLIFICAN: un `Talk` es uno de entrada y uno de salida por cada
/// observador del área, así que el costo no lo paga el que inunda.
///
/// Por qué un cubo y no "N por segundo" a secas: el tráfico legítimo llega a tirones —el cliente junta varias
/// acciones en un segmento TCP, y una conexión que se congeló medio segundo descarga todo junto al volver—.
/// Un contador por ventana corta a ese jugador; el cubo lo absorbe con la ráfaga y sólo corta al que sostiene
/// el ritmo. Los dos números son distintos a propósito: la ráfaga perdona el pico, el ritmo define el techo.
///
/// El reloj entra por parámetro para poder testearlo sin esperar: ver <c>LimiteDeCadenciaTests</c>.
/// </summary>
public sealed class LimiteDeCadencia
{
    private readonly double _porSegundo;
    private readonly double _rafaga;
    private double _fichas;
    private long _ultimoMs;

    /// <summary>Pico de paquetes en un segundo que se le vio a esta conexión. Sirve para calibrar: el número
    /// que hay que mirar para elegir el techo es el del tráfico REAL, no uno inventado.</summary>
    public int Pico { get; private set; }
    private int _enElSegundo;
    private long _segundoActual;

    /// <summary><paramref name="porSegundo"/> 0 = sin límite (el cubo nunca se vacía).</summary>
    public LimiteDeCadencia(int porSegundo, int rafaga, long ahoraMs)
    {
        _porSegundo = porSegundo;
        _rafaga = Math.Max(1, rafaga);
        _fichas = _rafaga;
        _ultimoMs = ahoraMs;
        _segundoActual = ahoraMs / 1000;
    }

    /// <summary>Consume una ficha por el paquete que se va a despachar. false = se pasó del ritmo.</summary>
    public bool Permite(long ahoraMs)
    {
        // El pico se lleva SIEMPRE, incluso con el límite apagado: es lo que permite mirar un servidor real
        // y decidir el número con datos en vez de a ojo.
        long seg = ahoraMs / 1000;
        if (seg != _segundoActual) { _segundoActual = seg; _enElSegundo = 0; }
        _enElSegundo++;
        if (_enElSegundo > Pico) Pico = _enElSegundo;

        if (_porSegundo <= 0) return true;

        double transcurrido = (ahoraMs - _ultimoMs) / 1000.0;
        if (transcurrido > 0)
        {
            _ultimoMs = ahoraMs;
            _fichas = Math.Min(_rafaga, _fichas + transcurrido * _porSegundo);
        }
        if (_fichas < 1) return false;
        _fichas -= 1;
        return true;
    }
}

/// <summary>
/// LOS DOS TECHOS DEL SPAM EN EL JUEGO, que es un problema distinto del de la inundación.
///
/// El techo de paquetes por segundo frena a quien manda miles; pero con veinte mensajes por segundo uno se
/// queda MUY por debajo de ese techo y el chat igual queda inusable para todos los que están alrededor. Y acá
/// no corresponde desconectar: el que habla rápido es un jugador, no un atacante. Se le frena el mensaje y se
/// le avisa, y sigue jugando.
///
/// Son dos cubos porque son dos costos distintos:
///   - RUIDOSAS: hablar, susurrar, escribir al clan, pedir un GM. Molestan a OTROS y se difunden.
///   - CONSULTAS: los comandos que le hacen contestar al servidor sólo a quien pregunta (/online, /est,
///     /uptime…). No molestan a nadie, pero cuestan trabajo, así que van más sueltas.
///
/// El original no tiene nada de esto —no hay ningún intervalo de chat en su Server.ini ni en su código—, así
/// que los números son decisión nuestra y por eso viven en el ini.
/// </summary>
public sealed class CadenciaDeAcciones
{
    private readonly LimiteDeCadencia _ruidosas;
    private readonly LimiteDeCadencia _consultas;
    private long _ultimoAviso;

    public CadenciaDeAcciones(int msPorMensaje, int rafagaMensajes, int msPorConsulta, int rafagaConsultas, long ahoraMs)
    {
        // El ini habla en MILISEGUNDOS ENTRE ACCIONES, que es como lo piensa el que hostea ("un mensaje por
        // segundo"); el cubo trabaja en acciones por segundo. La conversión va acá y no en el ini.
        _ruidosas = new LimiteDeCadencia(msPorMensaje > 0 ? Math.Max(1, 1000 / msPorMensaje) : 0, rafagaMensajes, ahoraMs);
        _consultas = new LimiteDeCadencia(msPorConsulta > 0 ? Math.Max(1, 1000 / msPorConsulta) : 0, rafagaConsultas, ahoraMs);
    }

    public bool PermiteRuidosa(long ahoraMs) => _ruidosas.Permite(ahoraMs);
    public bool PermiteConsulta(long ahoraMs) => _consultas.Permite(ahoraMs);

    /// <summary>true si toca avisarle al jugador. Se limita a uno cada dos segundos: si no, el aviso de spam
    /// se vuelve el spam —una línea de "estás hablando muy rápido" por cada mensaje frenado—.</summary>
    public bool TocaAvisar(long ahoraMs)
    {
        if (ahoraMs - _ultimoAviso < 2000) return false;
        _ultimoAviso = ahoraMs;
        return true;
    }
}
