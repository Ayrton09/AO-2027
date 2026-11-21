using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL MAGO PELEA DE LEJOS (pedido del tester, 20/11): "solo pelea tirando hechizos de lejos, el mago tiene que mantenerse alejado
/// de los rivales". Antes lanzaba desde donde estaba, y lo que se le acercaba le pegaba al lado mientras él seguía casteando: el Mago
/// es la clase con menos vida.
///
/// <para>Lo que hace un jugador: si la presa le queda a menos de <see cref="DistanciaDelMago"/> baldosas, da uno o dos pasos para
/// atrás sin salirse del alcance del hechizo ni arrimarse a otro peligro, y sigue lanzando. Inmovilizar cuesta 250 de maná y gastarlo
/// en cada bicho débil lo deja sin maná para pegar, así que a una criatura la inmoviliza recién cuando lo vuelve a alcanzar aunque
/// se aleje (<see cref="AlcancesAntesDeQuietar"/>): es la que camina tan rápido como él. A una persona, apenas la tiene al lado.
/// Inmovilizada no camina pero pega al que tiene al lado (<c>Npc.EstaInmovilizada</c>), por eso igual se aleja.</para>
///
/// <para>Contra personas, siempre. Contra criaturas, sólo con las que lo lastiman de verdad (<see cref="SeAlejaDeLaCriatura"/>): con
/// el resto lanza donde está, como antes, porque alejarse le costaba hechizos (ronda 115).</para>
///
/// <para>Acorralado, o sin un paso que lo aleje sin acercarlo a otro peligro, sigue lanzando donde está, como antes. Las criaturas
/// inmunes (los jefes, <c>Inmunidad</c> en su ficha) no se intentan inmovilizar: el servidor no cobra pero tampoco las frena.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde esta distancia (Manhattan) la presa ya no lo alcanza de un paso: más cerca, se aleja.</summary>
    public const int DistanciaDelMago = 3;

    /// <summary>Cuántas veces seguidas una criatura lo alcanza aunque se aleje antes de gastar maná en inmovilizarla.</summary>
    public const int AlcancesAntesDeQuietar = 2;

    /// <summary>Cuánto la da por quieta a una CRIATURA: el original la deja 37 vueltas de su IA de 380 ms, unos 14 s
    /// (<c>PacketDispatcher.CastStateOnNpc</c>). Un poco menos, como <see cref="QuietaMs"/>.</summary>
    public const int QuietaNpcMs = 13_000;

    /// <summary>Las veces que la presa de esta pelea lo alcanzó (quedó al lado) sin estar quieta.</summary>
    private int _alcancesDelMago;

    /// <summary>La vuelta anterior de la pelea se alejó. Con la presa todavía cerca, ésta lanza: el que se aleja en todas las
    /// vueltas no pega nunca, y el Mago que todavía no sabe Inmovilizar quedaba caminando delante de la que es tan rápida como él.</summary>
    private bool _magoSeAlejo;

    /// <summary>
    /// CONTRA CRIATURAS, SÓLO LAS QUE LO LASTIMAN DE VERDAD: las que lo matan con <see cref="GolpesQueLoMatanParaAlejarse"/> golpes o
    /// menos. Medido en la ronda 115 (20/11, dos partidas de 30 min a x1): alejándose de todo, Bruma (Mago 20, 166 de vida) juntó un
    /// 30 % y un 48 % menos de experiencia que el control, sin morir en ninguna de las dos versiones. Contra lobos, ratas y
    /// murciélagos, que mueren en pocos hechizos, cada paso para atrás era un hechizo menos. Lo que el tester describía es la pelea
    /// con personas, y contra ellas vale siempre.
    /// </summary>
    public const int GolpesQueLoMatanParaAlejarse = 5;

    public static bool SeAlejaDeLaCriatura(NpcInfo? n, int vidaMaxima) =>
        n is { MaxHit: > 0 } && n.MaxHit * GolpesQueLoMatanParaAlejarse >= vidaMaxima;

    /// <summary>¿Gasta el maná en inmovilizarla? Pegada a él, que no sea inmune, y si es criatura, que ya lo haya alcanzado
    /// <see cref="AlcancesAntesDeQuietar"/> veces aunque se alejó.</summary>
    public static bool MagoInmoviliza(bool esNpc, int pasos, int alcances, bool inmune) =>
        !inmune && pasos <= 1 && (!esNpc || alcances >= AlcancesAntesDeQuietar);

    /// <summary>
    /// Lo del Mago dentro de la pelea, antes de lanzar: inmovilizar a la que lo alcanza o dar unos pasos para atrás. Devuelve si hizo
    /// algo, y entonces la vuelta siguiente vuelve a mirar todo.
    /// </summary>
    private async Task<bool> MagoTomaDistanciaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, short presa,
                                                    SentidosDelBot.Visible quien, int pasos, CancellationToken ct, Func<bool>? cortaSi)
    {
        if (_clase != GameLimits.ClaseMago || !sentidos.UsaMana || pasos >= DistanciaDelMago
            || (quien.EsNpc && !SeAlejaDeLaCriatura(_entorno.Datos.Npc(quien.FichaNpc), sentidos.VidaMaxima)))
        { _magoSeAlejo = false; return false; }
        long ahora = Environment.TickCount64;
        if (!SigueQuieta(_quieta, presa, quien.X, quien.Y, ahora))
        {
            if (pasos <= 1) _alcancesDelMago++;
            bool inmune = quien.EsNpc && _entorno.Datos.Npc(quien.FichaNpc) is { Inmunidad: not 0 };
            int manaAntes = sentidos.ManaEnPuntos;
            if (MagoInmoviliza(quien.EsNpc, pasos, _alcancesDelMago, inmune) && QuietarA(transporte, sentidos, quien))
            {
                await Task.Delay(_rng.Next(GolpeMinMs, GolpeMaxMs), ct);
                bool cobro = sentidos.ManaEnPuntos < manaAntes;
                _quieta = (presa, QuietaHasta(Environment.TickCount64, cobro, quien.EsNpc), quien.X, quien.Y);
                if (cobro) _alcancesDelMago = 0;
                return true;
            }
        }
        // Se aleja y lanza, se aleja y lanza: ver `_magoSeAlejo`.
        if (_magoSeAlejo) { _magoSeAlejo = false; return false; }
        _magoSeAlejo = await AlejarseDeLaPresaAsync(transporte, sentidos, quien, ct, cortaSi);
        return _magoSeAlejo;
    }

    /// <summary>
    /// Uno o dos pasos que lo dejan a <see cref="DistanciaDelMago"/> o más de la presa, sin salirse del alcance del hechizo, sin
    /// pisar a nadie ni una salida del mapa y sin arrimarse a otro peligro (<see cref="LoArrimaAlPeligro"/>). Devuelve si se movió.
    /// </summary>
    private async Task<bool> AlejarseDeLaPresaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, SentidosDelBot.Visible presa,
                                                   CancellationToken ct, Func<bool>? cortaSi)
    {
        if (_pasoTrabado || sentidos.Paralizado) return false;
        var desde = sentidos.Posicion;
        var evitar = sentidos.BaldosasOcupadas().ToHashSet();
        var peligros = PeligrosDelViaje(sentidos);
        // Las vecinas que lo sacan del alcance del hechizo o lo arriman a otro peligro no se pisan. Más allá de las vecinas lo cuida
        // el tope de dos pasos: con eso no llega a salirse del rectángulo del hechizo (6 por 4) desde la distancia en que pelea.
        for (byte rumbo = 1; rumbo <= 4; rumbo++)
        {
            var (dx, dy) = SentidosDelBot.Paso(rumbo);
            var vecino = (X: (byte)(desde.X + dx), Y: (byte)(desde.Y + dy));
            if (Math.Abs(presa.X - vecino.X) > AlcanceHechizoX || Math.Abs(presa.Y - vecino.Y) > AlcanceHechizoY
                || LoArrimaAlPeligro(desde, vecino, peligros)) evitar.Add(vecino);
        }
        if (_entorno.Mapas.LejosDe(sentidos.Mapa, desde, (presa.X, presa.Y), 2, evitar) is not { Count: > 0 } camino) return false;
        var donde = desde;
        foreach (var paso in camino)
        {
            if (await UnPasoHaciaAsync(transporte, sentidos, paso.X - donde.X, paso.Y - donde.Y, ct, cortaSi: cortaSi) != Paso.Dado
                || sentidos.Posicion != paso) break;
            donde = paso;
        }
        return sentidos.Posicion != desde;
    }
}
