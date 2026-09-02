namespace CoverAO.Server.Game;

/// <summary>
/// EL DETECTOR DE SPEEDHACK del original (<c>HandleWalk</c>, Protocol.bas:1597-1625). Mide de a
/// <see cref="PasosPorVentana"/> pasos: si esos 34 tardaron menos de <see cref="VentanaMs"/>, deja una marca;
/// si vuelve a pasar antes de que la marca se olvide (<see cref="OlvidoMs"/>), echa. La primera ventana rápida
/// nunca echa — es a propósito, para no cortarle la sesión a alguien por un tirón de red.
///
/// <para><b>Es un piso SOSTENIDO, no una cadencia por paso.</b> 5200/34 = 152,9 ms por paso promediados sobre
/// una ventana larga; el original no tiene ninguna otra traba de movimiento, así que ése es todo su límite.
/// Este port sí tiene una traba por paso (130 ms en <c>HandleWalk</c>, que allá no existe): en ráfaga protege
/// mucho más, y en sostenido dejaba pasar el tramo entre 130 y 152,9 ms. Este detector es el que cierra ese
/// tramo, y las dos trabas conviven sin pisarse.</para>
///
/// <para><b>Por qué no le pega a nadie honesto.</b> Una baldosa del cliente dura 209 ms, y 174 a caballo o de
/// muerto (<c>PASO_MS</c> y <c>VELOCIDAD_MONTANDO</c>, que salen del TileEngine del original). Treinta y
/// cuatro pasos al ritmo más rápido que un jugador puede pedir son 5916 ms, un 14% por encima de la ventana —
/// y aun así haría falta que se repitiera dentro de los 30 s para que eche.</para>
///
/// <para>Va aparte y sin dependencias para que se pueda ejercer sin levantar un servidor: es puro tiempo y
/// contadores, y el reloj entra por parámetro.</para>
/// </summary>
public sealed class DetectorDeSpeedHack
{
    /// <summary>El <c>TiempoDeWalk</c> del original (:1595), que es una variable local con un literal.</summary>
    public const int PasosPorVentana = 34;
    /// <summary>Los 5200 ms de :1605. El comentario del propio autor explica el número: son menos de los que
    /// hacen falta en condiciones perfectas para dar 30 pasos.</summary>
    public const long VentanaMs = 5200;
    /// <summary>Los 30000 ms de :1606: pasado ese rato la marca anterior se borra y se vuelve a empezar.</summary>
    public const long OlvidoMs = 30_000;

    private int _pasos;
    private long _arranqueDeVentana;
    private long _marcaAnterior;      // flags.CountSH

    public enum Veredicto
    {
        /// <summary>Nada que decir: la ventana no se cerró, o se cerró a un ritmo normal.</summary>
        Nada,
        /// <summary>Primera ventana rápida. El original sólo la anota y deja seguir.</summary>
        Sospecha,
        /// <summary>Segunda ventana rápida dentro de los 30 s: el original loguea, avisa al staff y cierra.</summary>
        Echar,
    }

    /// <summary>Borra la marca de sospecha. El original lo hace en la rama de PARALIZADO de su
    /// <c>HandleWalk</c> (<c>.flags.CountSH = 0</c>, Protocol.bas:1640): mientras no te podés mover, los
    /// tiempos entre paquetes no dicen nada, así que se perdona lo anotado.</summary>
    public void Olvidar() => _marcaAnterior = 0;

    /// <summary>Cuenta un paso YA ACEPTADO y dice qué hacer. <paramref name="ahoraMs"/> es un reloj monótono
    /// en milisegundos (el <c>GetTickCount</c> del original).</summary>
    public Veredicto Paso(long ahoraMs)
    {
        if (++_pasos < PasosPorVentana) return Veredicto.Nada;
        long duro = ahoraMs - _arranqueDeVentana;
        _pasos = 0;
        _arranqueDeVentana = ahoraMs;
        if (duro >= VentanaMs) return Veredicto.Nada;
        if (ahoraMs - _marcaAnterior > OlvidoMs) _marcaAnterior = 0;   // :1606, la marca se olvida
        if (_marcaAnterior != 0) return Veredicto.Echar;
        _marcaAnterior = ahoraMs;
        return Veredicto.Sospecha;
    }
}
