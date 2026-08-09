using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// El plazo para esperar algo que hace el ESCRITOR DE SALIDA (que el paquete salga, que el socket baje).
///
/// POR QUÉ EXISTE. Desde que la salida tiene cola, <c>ClientSession.Close</c> no baja el socket: lo baja el
/// escritor cuando termina de drenar, o —si el escritor no llegó a correr— la red de <c>Close</c>, que espera
/// <see cref="TcpTransport.SendTimeoutMs"/>. O sea que todo lo que se afirme sobre el socket depende de que
/// el pool de hilos le dé un turno a una continuación, y eso NO es instantáneo cuando corren los 588 tests
/// en paralelo.
///
/// Con plazos escritos a mano (5 s acá, 10 s allá, 15 s más allá) tres tests dieron rojo INTERMITENTE en la
/// suite y verde sueltos, 3 de 3. El síntoma se lee como "el producto está lento" y no lo es: es un plazo
/// elegido sin mirar la constante contra la que compite.
///
/// El plazo NO es algo que se esté afirmando —nadie promete que el socket baje en N segundos—: es una red
/// contra el cuelgue, para que un test roto no cuelgue la suite. Por eso va holgado y sale de la constante
/// del producto, así no se desincroniza el día que alguien la toque.
/// </summary>
internal static class EsperaDelEscritor
{
    public static TimeSpan Plazo => TimeSpan.FromMilliseconds(TcpTransport.SendTimeoutMs + 10_000);

    /// <summary>Espera hasta que la condición se cumpla o venza el plazo. No afirma nada: el veredicto lo da
    /// el <c>Assert</c> de después, con el mensaje que corresponde a ESE test.</summary>
    public static void Hasta(Func<bool> condicion) => SpinWait.SpinUntil(condicion, Plazo);
}
