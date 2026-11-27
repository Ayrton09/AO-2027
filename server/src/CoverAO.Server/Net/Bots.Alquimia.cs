using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL DRUIDA Y SUS POCIONES (pedido del tester, 25/11, punto 1: "obviamente el druida, al ser una clase de pelea, va a estar más peleando
/// o farmeando que haciendo botánica y alquimia, pero estaría bueno que cada tanto se ponga a hacer sus trabajos"; "Poción Roja: las
/// crafteará solo para él mismo"; "Poción Azul: las crafteará solo para él mismo").
///
/// <list type="bullet">
/// <item>CADA TANTO UNA JORNADA DE OFICIO: en <see cref="JornadasDelDruida"/> de cada cien sesiones, la mitad que los de oficio, junta
/// raíces (hasta <see cref="RaicesDeLaJornada"/>) y fabrica con las pausas cortas.</item>
/// <item>SE HACE LAS ROJAS Y LAS AZULES: con la skill para la receta y menos de <see cref="PocionesQueQuiere"/> de esa poción, la
/// fabrica aunque la skill esté en el tope del nivel, de a <see cref="PocionesPorPaquete"/>. La Violeta la sigue haciendo para aprender
/// y para el antídoto. Las especiales (remover parálisis, invisibilidad y las anti) piden 100 de alquimia.</item>
/// </list>
/// </summary>
public sealed partial class Bot
{
    public const int JornadasDelDruida = 20, RaicesDeLaJornada = 500, PocionesPorPaquete = 10;

    /// <summary>¿Se fabrica esa poción para él? El Druida, la Roja o la Azul, si tiene menos de las que quiere.</summary>
    private bool DruidaSeLaHace(SentidosDelBot s, short receta) => _clase == ClaseDruida
        && ((EsPocionDeVida(receta) && s.Suma(PocionPropia) < PocionesQueQuiere)
            || (EsPocionDeMana(receta) && s.Suma(PocionDeManaPropia) < PocionesQueQuiere));

    /// <summary>La Roja o la Azul que le falta y para la que tiene raíces: la de la que tiene menos. Null si ninguna.</summary>
    private short? PocionQueSeHace(SentidosDelBot s, IReadOnlySet<int> recetas, Oficios.Fabricacion regla, int skill, Func<int, int> tiene)
    {
        if (_clase != ClaseDruida) return null;
        return recetas.Select(i => _entorno.Datos.Obj(i)).OfType<Data.ObjInfo>()
            .Where(o => regla.Pide(o) <= skill && DruidaSeLaHace(s, o.Index) && CuantasAlcanzan(_entorno.Datos, o.Index, regla, tiene) > 0)
            .OrderBy(o => EsPocionDeVida(o.Index) ? s.Suma(PocionPropia) : s.Suma(PocionDeManaPropia))
            .Select(o => (short?)o.Index).FirstOrDefault();
    }
}
