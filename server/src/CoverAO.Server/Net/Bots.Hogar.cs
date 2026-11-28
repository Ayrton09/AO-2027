using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// EL HOGAR DONDE HAY MENOS BOTS (pedido del tester, 27/11, decidido por el autor). Los bots se amontonaban en los mismos lugares, y los
/// más fuertes se quedaban con todo. En la base del tester del 28/11, 62 de los 100 hogares estaban en Rinkel. El bot pone el hogar
/// donde le venden (<see cref="PonerElHogarAsync"/>), y con el Server.ini que se distribuye eso es casi sólo Rinkel. Las otras ciudades
/// donde le venden (Tiama, Nueva Esperanza y, para el republicano, Illiandor, Lindos y Suramei) quedan en otras tierras: a pie no se
/// llega, así que va en pasaje como por la montura (<see cref="CruzarConUnPaseAsync"/>). Una mudanza por sesión, y sólo si en su hogar
/// hay <see cref="BotsDeMasParaMudarse"/> bots más que en el que menos tiene de los que puede elegir.
/// </summary>
public sealed partial class Bot
{
    /// <summary>Cuántos bots más que en la ciudad que menos tiene tiene que haber en su hogar para que se mude.</summary>
    public const int BotsDeMasParaMudarse = 3;

    /// <summary>Cada cuánto vuelve a mirar si se muda, y cuántas veces lo intenta por sesión si no llega.</summary>
    private const int EsperaDeLaMudanzaMs = 20 * 60_000, IntentosDeMudanza = 3;

    private long _proximaMudanza;
    private int _intentosDeMudanza;
    private bool _seMudoDeHogar;

    /// <summary>
    /// ¿A QUÉ CIUDAD SE MUDA? A la de <paramref name="candidatas"/> con menos bots, si en la suya (<paramref name="miCiudad"/>) hay
    /// <see cref="BotsDeMasParaMudarse"/> o más que en ésa. <paramref name="bots"/> cuenta los OTROS bots por ciudad. Entre las que tienen
    /// lo mismo, la de su misma tierra (<paramref name="mismaTierra"/>), que no pide pasaje, y después la del sorteo por su nombre, así
    /// no van todos a la misma. Null si se queda.
    /// </summary>
    public static (int Mapa, byte Ciudad)? CiudadParaMudarse(IEnumerable<(int Mapa, byte Ciudad)> candidatas, byte miCiudad,
                                                           IReadOnlyDictionary<byte, int> bots, Func<int, bool> mismaTierra,
                                                           string nombre)
    {
        int enLaMia = bots.GetValueOrDefault(miCiudad);
        var mejor = candidatas.Where(c => c.Ciudad != miCiudad)
            .OrderBy(c => bots.GetValueOrDefault(c.Ciudad))
            .ThenByDescending(c => mismaTierra(c.Mapa))
            .ThenBy(c => (SemillaDelNombre(nombre) ^ c.Mapa * 7919) & 0x7FFF)
            .Cast<(int Mapa, byte Ciudad)?>()
            .FirstOrDefault();
        return mejor is { } m && enLaMia >= bots.GetValueOrDefault(m.Ciudad) + BotsDeMasParaMudarse ? m : null;
    }

    /// <summary>Las ciudades que pueden ser su hogar y donde le venden, con un sacerdote para pedírselo.</summary>
    private IEnumerable<(int Mapa, byte Ciudad)> CiudadesParaElHogar() =>
        PacketDispatcher.MapasDeHogar.Where(c => PacketDispatcher.PuedeSerHogarDe(c.Mapa, _miBando) && LeVendenAca(c.Mapa)
            && _entorno.Datos.MapSpawns.TryGetValue(c.Mapa, out var nacen) && nacen.Any(s => EsSacerdote((short)s.NpcIndex)));

    /// <summary>
    /// LO QUE SUMA LA CAZA DESDE UN LUGAR: lo mejor entre lo de ahí y el coto que más suma a <see cref="SaltosHastaElCotoQueSuma"/> mapas o
    /// menos sin cruzar peligro, contado como en <see cref="BuscarDondeCazarAsync"/>. Para no mudarse adonde no tiene qué cazar: en la ronda
    /// 199 (27/11) Ciro, Guerrero de nivel 29, se mudó a Nueva Esperanza y ganó 107 mil de experiencia contra 1,13 millones sin mudarse,
    /// yendo y viniendo entre el 111, el 112 y el 114.
    /// </summary>
    private int LoQueSumaDesde(SentidosDelBot s, int mapa, (byte X, byte Y) desde)
    {
        int nivel = s.Nivel, vida = s.VidaMaxima;
        bool curaVeneno = s.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        var datos = _entorno.Datos;
        Func<int, int> rinde = f => EsPresa((short)f, vida, nivel, true, curaVeneno) ? RecompensaDePresa(datos.Npc(f), nivel) : 0;
        Func<int, bool> seCazaAhi = m => !HayGuardiaQueLoAtaca(m)
            && SeCazaAhi(datos.IsPkMap(m), datos.MapSpawns.TryGetValue(m, out var nacen) ? nacen.Select(n => n.NpcIndex) : [],
                         f => EsPeligroso((short)f, vida, nivel, true, curaVeneno));
        return Math.Max(seCazaAhi(mapa) ? _entorno.Mapas.LoQueSuma(mapa, desde, rinde)?.Suma ?? 0 : 0,
                        _entorno.Mapas.CotoQueMasSuma(mapa, desde, rinde, SaltosHastaElCotoQueSuma, CruzaSinPeligro(s), seCazaAhi)?.Suma ?? 0);
    }

    /// <summary>
    /// ¿Su hogar de ahora es uno al que se mudó, en una ciudad donde le venden? Nix e Illiandor, adonde lo manda el servidor al
    /// graduarse, no cuentan: ésos se siguen cambiando al pasar por Rinkel, como siempre. En la ronda 199 a 203 (28/11), contándolos,
    /// Karim, Herrero republicano de nivel 15, se quedaba con el hogar en Illiandor, donde el banquero no le presta: 21 mil de
    /// experiencia contra 191 a 278 mil.
    /// </summary>
    private bool YaTieneElHogarDondeLeVenden() =>
        _entorno.HogarDe(Nombre) is { } ciudad && ciudad is not (Cities.Nix or Cities.Illiandor)
        && CiudadesParaElHogar().Any(c => c.Ciudad == ciudad);

    /// <summary>Los otros bots conectados, por la ciudad de su hogar.</summary>
    private Dictionary<byte, int> HogaresDeLosOtros()
    {
        var cuenta = new Dictionary<byte, int>();
        foreach (var nombre in _entorno.EnElMundo.Keys)
            if (!string.Equals(nombre, Nombre, StringComparison.OrdinalIgnoreCase) && _entorno.HogarDe(nombre) is { } c)
                cuenta[c] = cuenta.GetValueOrDefault(c) + 1;
        return cuenta;
    }

    /// <summary>
    /// MUDARSE DE HOGAR a la ciudad con menos bots (<see cref="CiudadParaMudarse"/>): cruza en pasaje si es otra tierra, camina hasta el
    /// sacerdote y le pide el hogar. Devuelve si se ocupó de esto.
    /// </summary>
    private async Task<bool> MudarseDeHogarAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        long ahora = Environment.TickCount64;
        if (_acompana is not null || _seMudoDeHogar || _intentosDeMudanza >= IntentosDeMudanza || ahora < _proximaMudanza
            || !sentidos.Vivo || _miBando == 0 || sentidos.Nivel <= GameLimits.NewbieMaxLevel) return false;
        _proximaMudanza = ahora + EsperaDeLaMudanzaMs;
        if (_entorno.HogarDe(Nombre) is not { } miCiudad) return false;
        var datos = _entorno.Datos;
        int desde = sentidos.Mapa;
        bool puedeCruzar = sentidos.Skill(GameLimits.SkillNavegacion) >= _entorno.Ini.NavegacionParaCruzar
                           && PlataDe(sentidos) >= PlataDelPase;
        // Y SÓLO ADONDE DESDE EL SACERDOTE TIENE PARA CAZAR LO MISMO QUE DESDE DONDE ESTÁ: ver `LoQueSumaDesde`.
        int aca = LoQueSumaDesde(sentidos, desde, sentidos.Posicion);
        var candidatas = CiudadesParaElHogar().Where(c => puedeCruzar || datos.MismaTierra(c.Mapa, desde))
            .Where(c => datos.MapSpawns[c.Mapa].First(s => EsSacerdote((short)s.NpcIndex)) is var cura
                        && LoQueSumaDesde(sentidos, c.Mapa, ((byte)cura.X, (byte)cura.Y)) >= aca)
            .ToList();
        if (CiudadParaMudarse(candidatas, miCiudad, HogaresDeLosOtros(), m => datos.MismaTierra(m, desde), Nombre) is not { } destino)
            return false;
        _intentosDeMudanza++;
        var templo = datos.MapSpawns[destino.Mapa].First(s => EsSacerdote((short)s.NpcIndex));
        Console.WriteLine($"[bot {Nombre}] se muda de hogar a m{destino.Mapa} (ciudad {destino.Ciudad}) desde m{desde}");
        if (!datos.MismaTierra(sentidos.Mapa, destino.Mapa))
        {
            if (sentidos.Oro < PlataDelPase && TieneBanquero(sentidos.Mapa)) await RetirarParaComprarAsync(transporte, sentidos, cerrada, ct);
            if (sentidos.Oro < PlataDelPase || !await CruzarConUnPaseAsync(transporte, sentidos, destino.Mapa, cerrada, ct)) return true;
        }
        if (!await IrHastaAsync(transporte, sentidos, destino.Mapa, (byte)templo.X, (byte)templo.Y, cerrada, ct, RadioDelHogar,
                                porCualquierCamino: true)
            && sentidos.Mapa != destino.Mapa) return true;
        _proximoHogar = 0;
        await PedirElHogarAsync(transporte, sentidos, cerrada, ct);
        _seMudoDeHogar = _entorno.HogarDe(Nombre) == destino.Ciudad;
        Console.WriteLine($"[bot {Nombre}] {(_seMudoDeHogar ? "se mudó" : "no pudo mudarse")} de hogar a m{destino.Mapa}");
        return true;
    }
}
