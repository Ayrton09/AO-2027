using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LAS PIELES Y EL SASTRE (pedido del tester, 25/11, punto 1: "el sastre debería conseguir pieles, ya sea matando las criaturas o
/// comprándoselas a los demás"; "todos los que consiguen pieles deberían vendérselas directo al sastre o venderlas en el vendedor
/// general"; "las vestimentas comunes es el único item que le venderá al vendedor general, los demás items los venderá en el subastador").
///
/// <list type="bullet">
/// <item>LOS DEMÁS juntan las pieles (lobo, oso pardo y oso polar) y las publican en lotes de <see cref="PielesPorLote"/>, en vez de
/// guardarlas en el banco. Lo que pasa de <see cref="PielesQueGuarda"/> de una se lo venden al mercader.</item>
/// <item>EL SASTRE les compra el lote cuando le faltan pieles: para aprender (las Vestimentas, que le vende al mercader como siempre)
/// o para la prenda que fabrica para vender.</item>
/// <item>CON LA SKILL DE LAS PRENDAS BUENAS (las túnicas y los gorros piden 80 o más) fabrica una por vez
/// (<see cref="ElegirPrendaDeSastreria"/>) y la subasta, con el precio sacado de las pieles más la mitad, como la herrería.</item>
/// </list>
/// </summary>
public sealed partial class Bot
{
    public const int PielesPorLote = 100, PielesQueGuarda = 300;

    /// <summary>Cuántas prendas terminadas guarda sin publicar antes de dejar de fabricar.</summary>
    public const int PrendasListasQueGuarda = 2;

    private short? _prendaDeSastreria;

    /// <summary>¿Es una de las pieles con las que cose el Sastre?</summary>
    public static bool EsPielDeSastre(int obj) => obj is GameLimits.SkinWolfObj or GameLimits.SkinBrownBearObj or GameLimits.SkinPolarBearObj;

    /// <summary>¿Es una prenda que el Sastre fabrica para la subasta? Las que no valen nada en el obj.dat: las Vestimentas valen 50 y
    /// se las compra el mercader.</summary>
    public static bool EsDeSastreriaParaVender(GameData datos, short obj) => datos.TailorItems.Contains(obj) && datos.Obj(obj) is { Value: 0 };

    /// <summary>El valor por las pieles que lleva, más la mitad; 0 si no es de lo que se vende.</summary>
    public static int ValorDeSastreria(GameData datos, short obj)
    {
        if (!EsDeSastreriaParaVender(datos, obj) || datos.Obj(obj) is not { } o) return 0;
        long costo = Oficios.Sastreria.Materiales(o).Sum(m => (long)Math.Max(0, m.PorUnidad) * Math.Max(0, datos.Obj(m.Obj)?.Value ?? 0));
        return (int)Math.Min(int.MaxValue, costo + costo / 2);
    }

    /// <summary>LA PRENDA QUE SIGUE: de las que sabe y no tiene (encima o publicada), la que lleva menos pieles. Null si ninguna.</summary>
    public static short? ElegirPrendaDeSastreria(GameData datos, int skill, Func<short, int> enStock) =>
        datos.TailorItems.Select(i => datos.Obj(i)).OfType<ObjInfo>()
            .Where(o => EsDeSastreriaParaVender(datos, o.Index) && o.SkillTailoring <= skill && enStock(o.Index) == 0)
            .OrderBy(o => o.SkinWolf + o.SkinBrownBear + o.SkinPolarBear).Select(o => (short?)o.Index).FirstOrDefault();

    private bool EsPielParaVender(short obj) => _clase != ClaseSastre && EsPielDeSastre(obj);

    private bool EsSastreriaParaVender(short obj) => _clase == ClaseSastre && EsDeSastreriaParaVender(_entorno.Datos, obj);

    /// <summary>La prenda en la que está, o una nueva si terminó la anterior o ya no le sirve.</summary>
    private short? PrendaDeSastreria(SentidosDelBot s)
    {
        if (_clase != ClaseSastre) return null;
        int skill = s.Skill(GameLimits.SkillSastreria);
        if (skill < 0) return null;
        if (_prendaDeSastreria is { } actual && _entorno.Datos.Obj(actual) is { } o && o.SkillTailoring <= skill
            && s.CuantoTiene(i => i == actual) == 0)
            return actual;
        _prendaDeSastreria = ElegirPrendaDeSastreria(_entorno.Datos, skill, obj => EnStock(s, obj));
        if (_prendaDeSastreria is { } nueva) Console.WriteLine($"[bot {Nombre}] prenda de sastreria: obj {nueva}");
        return _prendaDeSastreria;
    }

    private int PrendasListas(SentidosDelBot s) => s.RanurasOcupadas().Select(s.QueHayEn).Distinct()
        .Where(EsSastreriaParaVender).Sum(o => s.CuantoTiene(i => i == o));

    /// <summary>¿El Sastre fabrica para vender? Con una prenda por hacer y sin demasiadas listas.</summary>
    private bool SastreFabricaParaVender(SentidosDelBot s) =>
        _clase == ClaseSastre && PrendasListas(s) < PrendasListasQueGuarda && PrendaDeSastreria(s) is not null;

    /// <summary>Las pieles de esa clase que le faltan: las de la prenda, o las de lo que todavía aprende.</summary>
    private int PielesQueLeFaltan(SentidosDelBot s, int piel)
    {
        if (_clase != ClaseSastre || !EsPielDeSastre(piel)) return 0;
        int tiene = s.CuantoTiene(i => i == piel);
        if (SastreFabricaParaVender(s) && _prendaDeSastreria is { } prenda && _entorno.Datos.Obj(prenda) is { } o)
        {
            int pide = Oficios.Sastreria.Materiales(o).Where(m => m.Obj == piel).Sum(m => m.PorUnidad);
            if (pide > tiene) return pide - tiene;
        }
        return LoQueSaleACazarParaSuOficio(s).Contains(piel) ? PielesPorLote : 0;
    }

    /// <summary>¿Va a la subasta por pieles? El Sastre al que le faltan.</summary>
    private bool SastreBuscaPieles(SentidosDelBot s) =>
        _clase == ClaseSastre && _acompana is null
        && new[] { GameLimits.SkinWolfObj, GameLimits.SkinBrownBearObj, GameLimits.SkinPolarBearObj }.Any(p => PielesQueLeFaltan(s, p) > 0);

    private bool LeSirvenLasPieles(SentidosDelBot s, ServerMessages.FilaDeSubasta f, int plata) =>
        _clase == ClaseSastre && EsPielDeSastre(f.Objeto) && !_porRetirar.Contains(f.Objeto) && PielesQueLeFaltan(s, f.Objeto) is var faltan and > 0
        && LeConvieneElLote(f, _entorno.Datos.Obj(f.Objeto)?.Value ?? 0, Nombre, plata, Math.Max(faltan, PielesPorLote));
}
