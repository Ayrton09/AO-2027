using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL LEÑADOR Y EL CARPINTERO VENDEN EN LA SUBASTA (pedido del tester, 25/11, punto 1; el autor eligió el precio por el costo).
///
/// <list type="bullet">
/// <item>EL LEÑADOR publica la leña en lotes de <see cref="LenaPorLote"/> al precio de la subasta sobre su valor (10 cada una), en vez
/// de vendérsela al mercader a un tercio. Lo que pasa de <see cref="LenaQueGuardaElLenador"/> se lo sigue vendiendo al mercader.</item>
/// <item>EL CARPINTERO fabrica de a un lote por vez (<see cref="ElegirLoteDeCarpinteria"/>): flechas de a <see cref="FlechasPorLote"/>,
/// rotando entre los tipos que sabe hacer; cada tanto un arco y, rara vez, un barco, que se compra una vez y nunca más. Junta la
/// leña del lote talando y comprándole lotes al Leñador. El precio sale de la leña que lleva, más la mitad, como la herrería.</item>
/// <item>EL CARPINTERO TRABAJA EN ZONA SEGURA (el tester, 26/11: "que se ponga a laburar en zona segura, después cuando esté por salir
/// a seguir farmeando, que guarde la leña, que ponga a vender lo que hizo y listo"; en la ronda 174 Osmar murió dos veces con la leña y
/// el lote encima). Con plata para comprar la leña (<see cref="PlataParaComprarLena"/>) no tala, que es sólo en mapas inseguros: se la
/// compra al Leñador y fabrica sólo en sus jornadas de oficio, que arrancan retirando del banco lo del oficio. En las otras sesiones
/// publica lo terminado y guarda en el banco la leña y lo que tenga a medio hacer (<see cref="GuardaLoDelOficio"/>).</item>
/// </list>
/// </summary>
public sealed partial class Bot
{
    public const int LenaPorLote = 500, LenaQueGuardaElLenador = 3_000, LenaQueJuntaElCarpintero = 20_000;

    /// <summary>De cada cuántos lotes uno es un barco (si sabe hacerlo) y uno un arco.</summary>
    public const int BarcoCadaTantosLotes = 10, ArcoCadaTantosLotes = 4;

    /// <summary>Cuántos lotes terminados guarda sin publicar antes de dejar de fabricar.</summary>
    public const int LotesListosQueGuarda = 2;

    /// <summary>Desde cuánta plata (encima y en el banco) el Carpintero compra la leña en vez de talarla.</summary>
    public const int PlataParaComprarLena = 20_000;

    /// <summary>¿Compra la leña en vez de talarla? El Carpintero con plata.</summary>
    private bool CarpinteroCompraLaLena(SentidosDelBot s) => _clase == ClaseCarpintero && PlataDe(s) >= PlataParaComprarLena;

    /// <summary>¿Lo guarda en el banco antes de salir a cazar? Lo del oficio del Carpintero que compra la leña, fuera de sus jornadas: la
    /// leña y lo que tiene a medio hacer. Lo terminado ya lo publicó al pasar por la subasta.</summary>
    private bool GuardaLoDelOficio(SentidosDelBot s, short obj) =>
        (!_jornadaDeOficio && CarpinteroCompraLaLena(s) && (obj == Lena || EsCarpinteriaParaVender(obj)) && !ParaPublicar(s, obj))
        || GuardaElRepuesto(s, obj);

    /// <summary>Al arrancar la jornada, lo del oficio que dejó en el banco pasa a retirarse en la primera visita; en otra sesión, no
    /// (salvo lo comprado, que el Centro le debe).</summary>
    private void RetirarLoDelOficioAlEmpezar()
    {
        if (_clase != ClaseCarpintero) return;
        var delOficio = _entorno.Datos.CarpenterItems.Select(o => (short)o).Where(o => EsDeCarpinteriaParaVender(_entorno.Datos, o)).Append(Lena);
        foreach (short obj in delOficio)
            if (_jornadaDeOficio) _porRetirar.Add(obj);
            else if (!_comprados.Contains(obj)) _porRetirar.Remove(obj);
        if (_jornadaDeOficio) _proximoMercadoDelOficio = 0;
    }

    private short? _loteDeCarpinteria;

    /// <summary>¿Es de lo que el Carpintero fabrica para vender? Las flechas, los arcos y los barcos.</summary>
    public static bool EsDeCarpinteriaParaVender(GameData datos, short obj) =>
        datos.CarpenterItems.Contains(obj) && datos.Obj(obj) is { IsAmmo: true } or { IsBow: true } or { IsBoat: true };

    /// <summary>El valor por la leña que lleva, más la mitad; 0 si no es de lo que se vende.</summary>
    public static int ValorDeCarpinteria(GameData datos, short obj)
    {
        if (!EsDeCarpinteriaParaVender(datos, obj) || datos.Obj(obj) is not { } o) return 0;
        long costo = Oficios.Carpinteria.Materiales(o).Sum(m => (long)Math.Max(0, m.PorUnidad) * Math.Max(0, datos.Obj(m.Obj)?.Value ?? 0));
        return (int)Math.Min(int.MaxValue, costo + costo / 2);
    }

    /// <summary>Cuántas hacen un lote: las flechas de a <see cref="FlechasPorLote"/>, lo demás de a uno.</summary>
    public static int CuantasPorLote(ObjInfo o) => o.IsAmmo ? FlechasPorLote : 1;

    /// <summary>
    /// EL LOTE QUE SIGUE: de lo que sabe hacer y no tiene ya un lote entero (encima o publicado). Si toca barco, el más grande que sabe;
    /// si toca arco, el mejor; si no, la flecha de la que menos tiene, y entre las que tiene igual, la que lleva menos leña (medido en la
    /// ronda 175: la Incendiaria pide 10.000 por lote y en media hora salieron 50). Null si no
    /// hay ninguno. EL BARCO Y EL ARCO, SÓLO CON LA LEÑA YA ENCIMA: medido en las rondas 172 y 173 (26/11), Osmar eligió la Barca, que
    /// pide 10.000 leñas, juntó 1.734 en media hora y no fabricó nada más.
    /// </summary>
    public static short? ElegirLoteDeCarpinteria(GameData datos, int skill, Func<short, int> enStock, bool tocaBarco, bool tocaArco,
                                                 int lena = int.MaxValue)
    {
        var posibles = datos.CarpenterItems.Select(i => datos.Obj(i)).OfType<ObjInfo>()
            .Where(o => EsDeCarpinteriaParaVender(datos, o.Index) && o.SkillCarpentry <= skill && enStock(o.Index) < CuantasPorLote(o))
            .ToList();
        if (tocaBarco && posibles.Where(o => o.IsBoat && o.Wood <= lena).OrderByDescending(o => o.SkillCarpentry).FirstOrDefault() is { } barco) return barco.Index;
        if (tocaArco && posibles.Where(o => o.IsBow && o.Wood <= lena).OrderByDescending(o => o.SkillCarpentry).FirstOrDefault() is { } arco) return arco.Index;
        return posibles.Where(o => o.IsAmmo).OrderBy(o => enStock(o.Index)).ThenBy(o => o.Wood).ThenByDescending(o => o.SkillCarpentry)
            .Select(o => (short?)o.Index).FirstOrDefault();
    }

    private bool EsCarpinteriaParaVender(short obj) => _clase == ClaseCarpintero && EsDeCarpinteriaParaVender(_entorno.Datos, obj);

    private bool EsLenaParaVender(short obj) => _clase == ClaseLenador && obj == Lena;

    /// <summary>Lo que tiene de eso: encima y publicado por él.</summary>
    private int EnStock(SentidosDelBot s, short obj) => s.CuantoTiene(i => i == obj)
        + s.Subastas.Where(f => f.Objeto == obj && string.Equals(f.Vendedor, Nombre, StringComparison.OrdinalIgnoreCase)).Sum(f => (int)f.Cantidad);

    /// <summary>Los lotes enteros que tiene encima sin publicar.</summary>
    private int LotesListos(SentidosDelBot s) => s.RanurasOcupadas().Select(s.QueHayEn).Distinct()
        .Where(EsCarpinteriaParaVender).Sum(o => s.CuantoTiene(i => i == o) / CuantasPorLote(_entorno.Datos.Obj(o)!));

    /// <summary>El lote en el que está, o uno nuevo si terminó el anterior o ya no le sirve.</summary>
    private short? LoteDeCarpinteria(SentidosDelBot s)
    {
        if (_clase != ClaseCarpintero) return null;
        int skill = s.Skill(GameLimits.SkillCarpinteria);
        if (skill < 0) return null;
        if (_loteDeCarpinteria is { } actual && _entorno.Datos.Obj(actual) is { } o && o.SkillCarpentry <= skill
            && s.CuantoTiene(i => i == actual) < CuantasPorLote(o))
            return actual;
        _loteDeCarpinteria = ElegirLoteDeCarpinteria(_entorno.Datos, skill, obj => EnStock(s, obj),
            tocaBarco: _rng.Next(BarcoCadaTantosLotes) == 0, tocaArco: _rng.Next(ArcoCadaTantosLotes) == 0, lena: s.CuantoTiene(i => i == Lena));
        if (_loteDeCarpinteria is { } nuevo)
            Console.WriteLine($"[bot {Nombre}] lote de carpinteria: obj {nuevo}");
        return _loteDeCarpinteria;
    }

    /// <summary>¿El Carpintero fabrica para vender? Con un lote por hacer y sin demasiados listos.</summary>
    private bool CarpinteroFabricaParaVender(SentidosDelBot s) =>
        _clase == ClaseCarpintero && (_jornadaDeOficio || !CarpinteroCompraLaLena(s)) && LotesListos(s) < LotesListosQueGuarda && LoteDeCarpinteria(s) is not null;

    /// <summary>La leña que le falta para terminar el lote, sin contar lo que tiene.</summary>
    private int LenaDelLote(SentidosDelBot s)
    {
        if (!CarpinteroFabricaParaVender(s) || LoteDeCarpinteria(s) is not { } lote || _entorno.Datos.Obj(lote) is not { } o) return 0;
        int faltan = CuantasPorLote(o) - s.CuantoTiene(i => i == lote);
        return (int)Math.Min(LenaQueJuntaElCarpintero, (long)Math.Max(0, faltan) * Math.Max(0, o.Wood));
    }

    /// <summary>Cuánta leña junta antes de dejar de talar: la del lote, o la reserva de siempre.</summary>
    private int InsumoQueGuardaDe(SentidosDelBot s) => _clase == ClaseCarpintero ? Math.Max(InsumoQueGuarda, LenaDelLote(s))
        : _clase == ClaseDruida && _jornadaDeOficio ? RaicesDeLaJornada : InsumoQueGuarda;

    private int LenaQueLeFalta(SentidosDelBot s) => Math.Max(0, LenaDelLote(s) - s.CuantoTiene(i => i == Lena));

    /// <summary>¿Va a la subasta por leña? El Carpintero al que le falta para el lote.</summary>
    private bool CarpinteroBuscaLena(SentidosDelBot s) => _clase == ClaseCarpintero && LenaQueLeFalta(s) > 0 && _acompana is null;

    private bool LeSirveLaLena(SentidosDelBot s, ServerMessages.FilaDeSubasta f, int plata) =>
        _clase == ClaseCarpintero && f.Objeto == Lena && !_porRetirar.Contains(f.Objeto) && LenaQueLeFalta(s) is var faltan and > 0
        && LeConvieneElLote(f, _entorno.Datos.Obj(Lena)?.Value ?? 0, Nombre, plata, Math.Max(faltan, LenaPorLote));
}
