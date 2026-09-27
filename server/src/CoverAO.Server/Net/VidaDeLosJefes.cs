using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LA BARRA DE VIDA DE LOS JEFES, del lado del servidor (pedido del tester, 2026-09-27; no existe en el original). Le cuenta
/// al cliente la vida de cada jefe que alguien está viendo, con <see cref="ServerMessages.VidaDelJefe"/>.
///
/// <para>Un JEFE es una criatura hostil de las de la data —ni mascota ni invocada— que da experiencia y tiene de vida
/// máxima por lo menos <see cref="ServerIni.VidaMinimaDeJefe"/>. La experiencia deja afuera a los blancos de práctica
/// (100.000 de vida, cero de experiencia).</para>
///
/// <para>Se manda en cada vuelta en que la vida CAMBIÓ —el golpe, el hechizo, la flecha, la mascota: todos terminan en
/// <c>Npc.Hp</c>, así que mirar la vida atrapa todos los caminos sin tocarlos— y además una vez por segundo aunque no
/// cambie. Eso segundo es lo que le da la barra al que recién llega: sin él habría que engancharse en cada lugar donde un
/// jugador empieza a ver una criatura (el login, el cambio de área, la criatura que camina hasta él, la que reaparece).
/// Son 11 bytes por jefe a la vista.</para>
/// </summary>
public sealed class VidaDeLosJefes
{
    /// <summary>Cada cuántas vueltas del loop se repite la vida aunque no haya cambiado: un segundo.</summary>
    public const int VueltasDelRepaso = 1000 / GameLoop.TickMs;

    private readonly World _world;
    private readonly ServerIni _ini;
    private readonly Dictionary<Npc, int> _anunciada = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<short> _mapas = new();

    public VidaDeLosJefes(World world, ServerIni ini)
    {
        _world = world;
        _ini = ini;
    }

    /// <summary>¿Es un jefe con este umbral? 0 (o menos) = no hay jefes.</summary>
    public static bool EsJefe(Npc n, int vidaMinima) =>
        vidaMinima > 0 && n.OwnerCharIndex == 0 && n.Template.Hostile && n.Template.GiveExp > 0 && n.Template.MaxHp >= vidaMinima;

    /// <summary>Una vuelta del loop. Corre en el hilo del juego, después de las criaturas.</summary>
    public void Tick(long tick)
    {
        int umbral = _ini.VidaMinimaDeJefe;
        if (umbral <= 0) return;
        bool repaso = tick % VueltasDelRepaso == 0;
        _mapas.Clear();
        foreach (var p in _world.Players) _mapas.Add(p.Pos.Map);
        foreach (var mapa in _mapas)
            foreach (var n in _world.NpcsOnMap(mapa))
            {
                if (!n.IsAlive || !EsJefe(n, umbral)) continue;
                bool cambio = !_anunciada.TryGetValue(n, out var antes) || antes != n.Hp;
                if (!cambio && !repaso) continue;
                _anunciada[n] = n.Hp;
                _world.BroadcastToObservers(n.Pos, ServerMessages.VidaDelJefe(n.CharIndex, n.Hp, n.Template.MaxHp));
            }
        // Los que murieron o se fueron del mundo no vuelven a mirarse: se sueltan en el repaso.
        if (repaso && _anunciada.Count > 0)
            foreach (var n in _anunciada.Keys.Where((n) => !n.IsAlive || _world.NpcByIndex(n.CharIndex) != n).ToList())
                _anunciada.Remove(n);
    }
}
