using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL GUERRERO PELEA CON POCIONES (pedido del tester, 20/11): "ir al frente, pegar fuerte y aguantar y evadir golpes, pero no
/// tiene magia", y del Druida necesita "pociones para remover parálisis y otras". Ya iba al frente y pegaba; lo que no hacía es lo
/// que hace cualquier Guerrero antes de pelear con gente: tomarse la Verde (fuerza, pega más) y la Amarilla (agilidad, esquiva y
/// acierta más). Las vende el Alquimista, el mismo de las rojas, a 250 y 150.
///
/// <para>Cada una dura 50.000 vueltas de 40 ms, unos 33 minutos (<c>DuracionEfecto</c> del obj.dat; <c>Potions.VueltasDeEfecto</c>),
/// así que se toma una de cada al empezar una pelea con gente si no se tomó otra hace menos de <see cref="EfectoDeAtributoMs"/>, y
/// lleva <see cref="PocionesDeAtributoQueLleva"/> de cada una. El bot no ve su fuerza ni su agilidad en el momento (el servidor
/// manda el atributo pero los sentidos no lo guardan), así que lo cuenta él. Morir no borra el efecto en el servidor.</para>
///
/// <para>Y PARALIZADO SIN MAGIA, LA POCIÓN DE REMOVER PARÁLISIS (subtipo 5, lanza el hechizo 10 sobre quien la toma): la fabrica el
/// Druida con alquimia y no se vende. Sin ella, como antes, se queda quieto hasta que se le pase.</para>
/// </summary>
public sealed partial class Bot
{
    public const int PocionesDeAtributoQueLleva = 2;

    /// <summary>Cuánto da por activo el efecto de una poción de atributo: un poco menos de los 33 minutos que dura.</summary>
    public const int EfectoDeAtributoMs = 30 * 60_000;

    private long _fuerzaHasta, _agilidadHasta;

    /// <summary>¿Usa las pociones de atributo? Por ahora el Guerrero: las otras clases, cuando les toque.</summary>
    public static bool UsaPocionesDeAtributo(byte clase) => clase == GameLimits.ClaseGuerrero;

    public static bool EsPocionDeAgilidad(ObjInfo? o) => o is { IsPotion: true, SubType: 1 };
    public static bool EsPocionDeFuerza(ObjInfo? o) => o is { IsPotion: true, SubType: 2 };

    /// <summary>¿Se toma ésta? Si tiene y no se tomó otra hace menos de <see cref="EfectoDeAtributoMs"/>.</summary>
    public static bool TomaLaPocionDeAtributo(bool tiene, long ahora, long hasta) => tiene && ahora >= hasta;

    private int AgilidadPropia(short obj) => CuentaComoPropio(obj) && EsPocionDeAgilidad(_entorno.Datos.Obj(obj)) ? 1 : 0;
    private int FuerzaPropia(short obj) => CuentaComoPropio(obj) && EsPocionDeFuerza(_entorno.Datos.Obj(obj)) ? 1 : 0;

    /// <summary>En el Alquimista, el que pelea con gente repone las de atributo, sin tocar lo que guarda para comer y tomar.</summary>
    private async Task<bool> ReponerLasDeAtributoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!UsaPocionesDeAtributo(_clase) || !PeleaConGente(sentidos.Nivel)) return false;
        int reserva = ReservaPara(sentidos, comida: true, bebida: true);
        bool compro = await ReponerAsync(transporte, sentidos, FuerzaPropia, PocionesDeAtributoQueLleva, ct, reserva);
        compro |= await ReponerAsync(transporte, sentidos, AgilidadPropia, PocionesDeAtributoQueLleva, ct, reserva);
        return compro;
    }

    /// <summary>Al empezar una pelea con gente, la Verde y la Amarilla que no tenga encima. Devuelve si tomó alguna.</summary>
    private async Task<bool> TomarLasDeAtributoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!UsaPocionesDeAtributo(_clase)) return false;
        bool tomo = false;
        long ahora = Environment.TickCount64;
        if (TomaLaPocionDeAtributo(sentidos.CuantoTiene(o => EsPocionDeFuerza(_entorno.Datos.Obj(o))) > 0, ahora, _fuerzaHasta)
            && Usar(transporte, sentidos, EsPocionDeFuerza))
        {
            _fuerzaHasta = ahora + EfectoDeAtributoMs;
            tomo = true;
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
        }
        ahora = Environment.TickCount64;
        if (TomaLaPocionDeAtributo(sentidos.CuantoTiene(o => EsPocionDeAgilidad(_entorno.Datos.Obj(o))) > 0, ahora, _agilidadHasta)
            && Usar(transporte, sentidos, EsPocionDeAgilidad))
        {
            _agilidadHasta = ahora + EfectoDeAtributoMs;
            tomo = true;
            await Task.Delay(_rng.Next(PocionMinMs, PocionMaxMs), ct);
        }
        return tomo;
    }

    /// <summary>¿Es la poción que le saca la parálisis al que la toma?</summary>
    private bool EsPocionDeRemoverParalisis(ObjInfo? o) =>
        o is { IsPotion: true, SubType: 5, LanzaHechizo: > 0 } && _entorno.Datos.Spell((short)o.LanzaHechizo) is { IsRemoveParalysis: true };

    /// <summary>Paralizado y sin el hechizo, la poción. Devuelve si la tomó.</summary>
    private bool TomarLaDeRemoverParalisis(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        if (!sentidos.Paralizado || Environment.TickCount64 < _proximaRemocion
            || sentidos.CuantoTiene(o => EsPocionDeRemoverParalisis(_entorno.Datos.Obj(o))) == 0
            || !Usar(transporte, sentidos, EsPocionDeRemoverParalisis)) return false;
        _proximaRemocion = Environment.TickCount64 + EntreRemocionesMs;
        return true;
    }
}
