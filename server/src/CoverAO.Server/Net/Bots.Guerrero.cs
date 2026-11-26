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
    public const int PocionesDeAtributoQueLleva = 5;

    /// <summary>Cuánto da por activo el efecto de una poción de atributo: un poco menos de los 33 minutos que dura.</summary>
    public const int EfectoDeAtributoMs = 30 * 60_000;

    private long _fuerzaHasta, _agilidadHasta;

    /// <summary>¿Usa alguna de las pociones de atributo? Ver <see cref="UsaLaVerde"/> y <see cref="UsaLaAmarilla"/>.</summary>
    public static bool UsaPocionesDeAtributo(byte clase) => UsaLaVerde(clase) || UsaLaAmarilla(clase);

    /// <summary>¿Usa la Verde (fuerza)? Las clases que pelean sin magia: el Guerrero, el Gladiador, el Mercenario y el Cazador (el
    /// tester, 20/11: "no tiene magia, necesita pociones").</summary>
    public static bool UsaLaVerde(byte clase) =>
        clase is GameLimits.ClaseGuerrero or GameLimits.ClaseGladiador or GameLimits.ClaseCazador or GameLimits.ClaseMercenario;

    /// <summary>¿Usa la Amarilla (agilidad)? Las que pelean sin magia y TAMBIÉN las que pelean con magia: el tester, 25/11, "las clases
    /// mágicas necesitan tener pociones amarillas para subirse la agilidad para bajar las posibilidades de recibir golpes".</summary>
    public static bool UsaLaAmarilla(byte clase) =>
        UsaLaVerde(clase) || clase is GameLimits.ClaseMago or GameLimits.ClaseClerigo or GameLimits.ClaseBardo or GameLimits.ClaseDruida
            or GameLimits.ClasePaladin or GameLimits.ClaseAsesino or GameLimits.ClaseNigromante;

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
        bool compro = UsaLaVerde(_clase) && await ReponerAsync(transporte, sentidos, FuerzaPropia, PocionesDeAtributoQueLleva, ct, reserva);
        compro |= UsaLaAmarilla(_clase) && await ReponerAsync(transporte, sentidos, AgilidadPropia, PocionesDeAtributoQueLleva, ct, reserva);
        return compro;
    }

    /// <summary>Al empezar una pelea con gente, la Verde y la Amarilla que no tenga encima. Devuelve si tomó alguna.</summary>
    private async Task<bool> TomarLasDeAtributoAsync(TransporteDeBot transporte, SentidosDelBot sentidos, CancellationToken ct)
    {
        if (!UsaPocionesDeAtributo(_clase)) return false;
        bool tomo = false;
        long ahora = Environment.TickCount64;
        if (UsaLaVerde(_clase)
            && TomaLaPocionDeAtributo(sentidos.CuantoTiene(o => EsPocionDeFuerza(_entorno.Datos.Obj(o))) > 0, ahora, _fuerzaHasta)
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

    private bool EsDeAtributo(short obj) => _entorno.Datos.Obj(obj) is var o
        && ((UsaLaVerde(_clase) && EsPocionDeFuerza(o)) || (UsaLaAmarilla(_clase) && EsPocionDeAgilidad(o)));

    /// <summary>Lo más barato que cuesta llevarse las que usa, para no salir a comprarlas sin la plata: la Amarilla 150 y la Verde 250.</summary>
    private int CuestanLasDeAtributo => (UsaLaAmarilla(_clase) ? 150 : 0) + (UsaLaVerde(_clase) ? 250 : 0);

    /// <summary>
    /// ¿SALE A COMPRARLAS? El que las usa y pelea con gente, sin ninguna de alguna de las dos y con la plata, como el antídoto
    /// (<see cref="ValeSalirPorElAntidoto"/>). Medido en la ronda 122 (21/11): Ciro, con 399 rojas, no fue nunca al Alquimista
    /// en 20 minutos, y comprarlas sólo cuando iba por otra cosa no alcanzaba.
    /// </summary>
    private bool LeFaltanLasDeAtributo(SentidosDelBot sentidos) =>
        UsaPocionesDeAtributo(_clase) && PeleaConGente(sentidos.Nivel) && PlataDe(sentidos) >= CuestanLasDeAtributo
        && ((UsaLaVerde(_clase) && sentidos.Suma(FuerzaPropia) == 0) || (UsaLaAmarilla(_clase) && sentidos.Suma(AgilidadPropia) == 0));

    /// <summary>
    /// LO QUE SU CLASE NO PUEDE USAR (<c>ClasesProhibidas</c> del obj.dat) no se guarda como "traído de casa". Medido en la ronda 122:
    /// Ciro, Guerrero, entró con 24 de las 25 ranuras ocupadas, entre ellas una Túnica de Druida y un Gorro de Aprendiz de Mago que
    /// levantó en otra sesión; con la mochila llena el servidor no le vende nada que no se apile.
    /// </summary>
    private bool SuClaseNoLoUsa(short obj) => _entorno.Datos.Obj(obj) is { } o && o.ClassForbidden(_clase);

    /// <summary>
    /// LA RANURA DEL BOTÍN QUE MENOS VALE, para hacerle lugar a la Verde y la Amarilla con la mochila llena. Medido en la ronda 124
    /// (21/11): Ciro, con 365 mil monedas, volvía a llenar la mochila levantando botín, y rico no sale a venderlo
    /// (<see cref="ValeElViajeParaVender"/>): con las 25 ranuras ocupadas no salía a comprarlas. Lo puesto, lo de novato y lo que no
    /// se tira, nunca. null si no lleva botín.
    /// </summary>
    private byte? BotinParaHacerLugar(SentidosDelBot sentidos)
    {
        byte? mejor = null;
        int valor = int.MaxValue;
        foreach (byte ranura in sentidos.RanurasOcupadas())
        {
            short obj = sentidos.QueHayEn(ranura);
            if (sentidos.EstaPuesto(ranura) || _entorno.Datos.Obj(obj) is not { } o || o.IsNewbie || o.NoSeTira || o.IsPotion
                || !EsBotin(sentidos, obj)) continue;
            if (o.Value < valor) { valor = o.Value; mejor = ranura; }
        }
        return mejor;
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
