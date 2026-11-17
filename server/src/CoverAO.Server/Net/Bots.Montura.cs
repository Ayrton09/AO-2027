using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

/// <summary>
/// LA MONTURA (pedido del autor, 2026-11-16). La montura suma golpe y defensa y se anda más rápido, y la vende un solo NPC en todo el
/// mundo: el Vendedor de Caballos de Tiama, que es otra tierra. Así que el bot que tiene con qué —Equitación para la que le sirve, la
/// Navegación que piden los pasajes que cruzan el agua (<c>NavegacionParaCruzar</c>) y el oro— va al pirata del puerto más cercano que
/// vende un pase a Tiama, cruza, la compra y se vuelve con la runa. Los puntos los junta <see cref="PuntosParaViajar"/>.
///
/// <para>En una cueva o bajo techo no se monta (el servidor lo baja del animal, VB6 <c>Modulo_UsUaRiOs.bas:718</c>): la montura es para
/// viajar y cazar afuera, y <see cref="Montarse"/> lo vuelve a subir al salir.</para>
/// </summary>
public sealed partial class Bot
{
    /// <summary>Desde este nivel piensa en la montura: antes la plata va al equipo y a las pociones.</summary>
    public const int NivelParaLaMontura = 20;

    /// <summary>Lo que se aparta para el pase, además del precio de la montura: el más caro de los que llegan a Tiama sale 15.000.</summary>
    public const int PlataDelPase = 15_000;

    /// <summary>Cada cuánto vuelve a pensar en el viaje si no salió, para no intentarlo en cada vuelta.</summary>
    private const int EsperaDeLaMonturaMs = 10 * 60_000;

    private const int TipoPirata = 7;   // eNPCType.transportadores

    /// <summary>Hasta cuántos mapas se busca al vendedor. En la ronda 106 Fabia, en el 18, no encontraba ningún pirata a los cinco
    /// del viaje común y no salía nunca.</summary>
    private const int SaltosHastaElVendedor = 12;

    private long _proximaMontura;
    private (int Mapa, NpcInfo Ficha)? _vendedorDeMonturas;   // se busca una vez: recorre todos los mapas

    /// <summary>¿Es una montura? (otMonturas, 44.)</summary>
    private bool EsMontura(short obj) => _entorno.Datos.Obj(obj) is { IsMount: true };

    /// <summary>¿La lleva puesta, o sea va montado? El servidor avisa la ranura al subir y al bajar.</summary>
    private bool VaMontado(SentidosDelBot sentidos) => sentidos.LlevaPuesto(EsMontura);

    /// <summary>
    /// ¿QUÉ MONTURA LE SIRVE? De las que vende <paramref name="vendedor"/>, la más cara que puede montar —la Equitación que pide, y el
    /// nivel, la clase y la raza, como el servidor (<see cref="Equipment.QueLeFaltaParaMontar"/>)— y que puede pagar. Null si ninguna.
    /// </summary>
    public static ObjInfo? MonturaQueLeSirve(GameData datos, NpcInfo? vendedor, int nivel, byte clase, byte raza, int equitacion, double plata)
    {
        if (vendedor?.StockItems is not { } stock) return null;
        return stock.Select(s => datos.Obj(s.ObjIndex))
            .Where(o => o is { IsMount: true } && o.MinSkill <= equitacion && o.Value <= plata
                        && Equipment.QueLeFaltaParaUsarlo(o, nivel, clase, raza) == 0)
            .OrderByDescending(o => o!.Value)
            .FirstOrDefault();
    }

    /// <summary>El que vende monturas y en qué mapa nace (el primero de la data: hoy es uno solo, el de Tiama).</summary>
    public static (int Mapa, NpcInfo Ficha)? VendedorDeMonturas(GameData datos)
    {
        foreach (var (mapa, spawns) in datos.MapSpawns.OrderBy(kv => kv.Key))
            foreach (var s in spawns)
                if (datos.Npc(s.NpcIndex) is { StockItems: { } stock } n
                    && stock.Any(it => datos.Obj(it.ObjIndex) is { IsMount: true }))
                    return (mapa, n);
        return null;
    }

    /// <summary>¿Es un pase que sale de <paramref name="desde"/> y llega a la tierra de <paramref name="mapaDestino"/>?</summary>
    public static bool EsPaseA(GameData datos, short obj, int desde, int mapaDestino) =>
        datos.Obj(obj) is { ObjType: 36 } o && o.PasajeDesdeMap == desde && o.PasajeHastaMap > 0
        && datos.MismaTierra(o.PasajeHastaMap, mapaDestino);

    /// <summary>¿Es un pirata que vende, para salir del mapa donde nace, un pase a la tierra de <paramref name="mapaDestino"/>?</summary>
    public static bool EsPirataHacia(GameData datos, int ficha, int mapaDeLaFicha, int mapaDestino) =>
        datos.Npc(ficha) is { NpcType: TipoPirata, StockItems: { } stock }
        && stock.Any(it => EsPaseA(datos, (short)it.ObjIndex, mapaDeLaFicha, mapaDestino));

    internal static bool PuedeComprarlePasaje(GameData datos, ServerIni ini, int ficha, int desde, int destino, byte faccion) =>
        EsPirataHacia(datos, ficha, desde, destino)
        && (datos.PirataAtiendePorFaccion(desde, ficha, faccion) || ElMercaderLeVende(ini, datos.MapBattleMode(desde), faccion));

    /// <summary>
    /// SUBIRSE AL ANIMAL: si tiene una montura que puede usar, no va montado, está vivo y afuera (en una cueva o bajo techo el servidor
    /// no deja). Devuelve si mandó el pedido.
    /// </summary>
    private bool Montarse(TransporteDeBot transporte, SentidosDelBot sentidos)
    {
        if (!sentidos.Vivo || VaMontado(sentidos) || sentidos.Comerciando || sentidos.Meditando
            || Environment.TickCount64 < _proximoMontar) return false;
        var (x, y) = sentidos.Posicion;
        if (_entorno.Datos.NoSeMontaAca(sentidos.Mapa, x, y)) return false;
        int equitacion = sentidos.Skill(GameLimits.SkillEquitacion);
        byte ranura = sentidos.Ranura(obj => _entorno.Datos.Obj(obj) is { IsMount: true } o && o.MinSkill <= equitacion
                                             && !_noSeLoPone.Contains(obj));
        if (ranura == 0) return false;
        // UN PEDIDO CADA TANTO: si el servidor no lo deja (una baldosa que no se ve como techo, la raza), sin esto lo pediría en cada
        // vuelta.
        _proximoMontar = Environment.TickCount64 + EsperaParaMontarMs;
        Console.WriteLine($"[bot {Nombre}] se sube a la montura en m{sentidos.Mapa} ({x},{y})");
        return transporte.Mandar(Paquete(ClientPacketId.UseItem, w => w.WriteByte(ranura)));
    }

    private const int EsperaParaMontarMs = 20_000;
    private long _proximoMontar;

    /// <summary>
    /// IR POR LA MONTURA. Devuelve si se ocupó de esto (aunque no la haya conseguido).
    /// </summary>
    private async Task<bool> IrPorLaMonturaAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Task cerrada, CancellationToken ct)
    {
        if (_acompana is not null || Environment.TickCount64 < _proximaMontura || sentidos.Nivel < NivelParaLaMontura) return false;
        if (sentidos.CuantoTiene(EsMontura) > 0 || sentidos.Identidad is not { } yo) return false;
        _vendedorDeMonturas ??= VendedorDeMonturas(_entorno.Datos) ?? (0, null!);
        if (_vendedorDeMonturas is not { Mapa: > 0 } vendedor) return false;
        bool enSuTierra = _entorno.Datos.MismaTierra(sentidos.Mapa, vendedor.Mapa);
        double plata = PlataParaGustos(sentidos) - (enSuTierra ? 0 : PlataDelPase);
        if (MonturaQueLeSirve(_entorno.Datos, vendedor.Ficha, sentidos.Nivel, yo.Clase, yo.Raza,
                              sentidos.Skill(GameLimits.SkillEquitacion), plata) is not { } montura) return false;
        int paraCruzar = _entorno.Ini.NavegacionParaCruzar;
        if (!enSuTierra && sentidos.Skill(GameLimits.SkillNavegacion) < paraCruzar) return false;
        _proximaMontura = Environment.TickCount64 + EsperaDeLaMonturaMs;

        // LA PLATA, ENCIMA: el mostrador y el pirata cobran de lo que lleva. Si está en la bóveda, se retira donde haya banquero.
        int hace = montura.Value + (enSuTierra ? 0 : PlataDelPase);
        if (sentidos.Oro < hace)
        {
            if (!TieneBanquero(sentidos.Mapa)) { _proximaMontura = 0; return false; }   // ya pasará por un pueblo con banquero
            await RetirarParaComprarAsync(transporte, sentidos, cerrada, ct);
            if (sentidos.Oro < hace) return false;
        }
        Console.WriteLine($"[bot {Nombre}] va por la montura {montura.Name} ({montura.Value}) desde m{sentidos.Mapa}");

        if (!enSuTierra && !await CruzarConUnPaseAsync(transporte, sentidos, vendedor.Mapa, cerrada, ct)) return true;
        if (!_entorno.Datos.MismaTierra(sentidos.Mapa, vendedor.Mapa)) return true;

        bool compro = await ComprarleAsync(transporte, sentidos, f => f == vendedor.Ficha.Index, obj => obj == montura.Index,
                                           cerrada, ct);
        Console.WriteLine($"[bot {Nombre}] {(compro ? "compró" : "no pudo comprar")} la montura {montura.Name} en m{sentidos.Mapa}");
        // DE VUELTA, CON LA RUNA: lo deja en su hogar, del lado donde caza.
        if (compro) Usar(transporte, sentidos, o => o.ObjType == ObjTypeRuna);
        return true;
    }

    /// <summary>
    /// CRUZAR EL AGUA CON UN PASE: al pirata más cercano que vende uno a la tierra de <paramref name="mapaDestino"/>, comprárselo,
    /// clickearlo y usar el pase. Devuelve si cambió de tierra.
    /// </summary>
    private async Task<bool> CruzarConUnPaseAsync(TransporteDeBot transporte, SentidosDelBot sentidos, int mapaDestino, Task cerrada,
                                                  CancellationToken ct)
    {
        var datos = _entorno.Datos;
        int mapaAntes = sentidos.Mapa;
        // El pirata se reconoce por la ficha; que venda desde el mapa donde está parado lo dice `mapaQueVende`, y lo que se compra se
        // elige al llegar, con el mapa de ese momento.
        bool compro = await ComprarleAsync(transporte, sentidos,
            f => datos.Npc(f) is { NpcType: TipoPirata }, obj => EsPaseA(datos, obj, sentidos.Mapa, mapaDestino), cerrada, ct,
            mapaQueVende: m => !HayGuardiaQueLoAtaca(m) && datos.MapSpawns.TryGetValue(m, out var sp)
                && sp.Any(s => PuedeComprarlePasaje(datos, _entorno.Ini, s.NpcIndex, m, mapaDestino, _miBando)));
        if (!compro) return false;
        if (sentidos.MasCercano(f => datos.Npc(f) is { NpcType: TipoPirata }) is not { } pirata)
        { Console.WriteLine($"[bot {Nombre}] compró el pase y no ve al pirata en m{sentidos.Mapa}"); return false; }
        // EL CLIC MIRA LA BALDOSA DE ABAJO PRIMERO: parado justo debajo del pirata, el bot se clickea a sí mismo (ver
        // `IrAlSubastadorAsync`).
        if (sentidos.Posicion == (pirata.Quien.X, (byte)(pirata.Quien.Y + 1))
            && await UnPasoHaciaAsync(transporte, sentidos, 1, 0, ct) != Paso.Dado) return false;
        if (!transporte.Mandar(Paquete(ClientPacketId.LeftClick, w => w.WriteByte(pirata.Quien.X).WriteByte(pirata.Quien.Y)))) return false;
        await Task.Delay(_rng.Next(500, 1_200), ct);
        byte pase = sentidos.Ranura(obj => EsPaseA(datos, obj, sentidos.Mapa, mapaDestino));
        mapaAntes = sentidos.Mapa;   // el del pirata: el viaje hasta él ya cambió de mapa
        if (pase == 0 || !transporte.Mandar(Paquete(ClientPacketId.UseItem, w => w.WriteByte(pase))))
        { Console.WriteLine($"[bot {Nombre}] no pudo usar el pase en m{sentidos.Mapa} (ranura {pase})"); return false; }
        for (int espera = 0; espera < 20 && sentidos.Mapa == mapaAntes; espera++) await Task.Delay(250, ct);
        bool cruzo = datos.MismaTierra(sentidos.Mapa, mapaDestino);
        Console.WriteLine($"[bot {Nombre}] {(cruzo ? "cruzó" : "no pudo cruzar")} con el pase de m{mapaAntes} (ahora m{sentidos.Mapa})");
        return cruzo;
    }

    /// <summary>Dónde nace un vendedor así, en un mapa donde le venden: el de este mapa si hay, y si no el del mapa más cercano por
    /// donde se pasa. Sirve para el que está detrás de una puerta, al que la búsqueda caminando no encuentra.</summary>
    private (int Mapa, byte X, byte Y)? DondeNaceElVendedor(SentidosDelBot sentidos, Func<int, bool> esElVendedor, Func<int, bool> mapaQueVende)
    {
        (int Mapa, byte X, byte Y)? mejor = null;
        int saltos = int.MaxValue;
        foreach (var (mapa, spawns) in _entorno.Datos.MapSpawns)
        {
            if (!mapaQueVende(mapa) || spawns.FirstOrDefault(s => esElVendedor(s.NpcIndex)) is not { NpcIndex: > 0 } s) continue;
            int cuantos = mapa == sentidos.Mapa ? 0
                : _entorno.Mapas.PuertaHacia(sentidos.Mapa, mapa, sentidos.Posicion, pisable: Pisable(sentidos),
                                             topeDeSaltos: SaltosHastaElVendedor) is { } p ? p.Saltos : int.MaxValue;
            if (cuantos < saltos) { saltos = cuantos; mejor = (mapa, (byte)s.X, (byte)s.Y); }
        }
        return mejor;
    }

    /// <summary>
    /// COMPRARLE A UN NPC UNA COSA: ir hasta él (caminando, con <see cref="IrAbriendoAsync"/>), abrirle la tienda con doble clic,
    /// comprar una de lo que <paramref name="queCompra"/> acepta y cerrar. Devuelve si la tiene.
    /// </summary>
    private async Task<bool> ComprarleAsync(TransporteDeBot transporte, SentidosDelBot sentidos, Func<int, bool> esElVendedor,
                                            Func<short, bool> queCompra, Task cerrada, CancellationToken ct, Func<int, bool>? mapaQueVende = null)
    {
        bool Sirve(short f) => esElVendedor(f);
        // Las compras comunes siguen la regla de comercio por zona. Al buscar un pasaje se usa la del transportador.
        mapaQueVende ??= LeVendenAca;
        bool Corta(string porque) { Console.WriteLine($"[bot {Nombre}] no le compra en m{sentidos.Mapa} {sentidos.Posicion}: {porque}"); return false; }
        if (sentidos.MasCercano(Sirve) is null || !mapaQueVende(sentidos.Mapa))
        {
            var lugarCaminando = _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => esElVendedor(f), mapaQueVende,
                                                         Pisable(sentidos), SaltosHastaElVendedor);
            // DETRÁS DE UNA PUERTA: el Vendedor de Caballos de Tiama está adentro de un edificio cerrado, y la búsqueda sólo encuentra
            // a quien se llega caminando (ronda 109: Elmo parado en el 218 y "no hay vendedor a mano"). Se va a su mapa y se abre la
            // puerta, como en las compras de siempre (`AbrirUnaPuertaAsync`).
            if (lugarCaminando is null && DondeNaceElVendedor(sentidos, esElVendedor, mapaQueVende) is { } nace)
            {
                if (sentidos.Mapa != nace.Mapa)
                    await IrHastaAsync(transporte, sentidos, nace.Mapa, nace.X, nace.Y, cerrada, ct, RadioDelMostrador,
                                       topeDeMapas: SaltosHastaElVendedor);
                if (sentidos.Mapa == nace.Mapa && await AbrirUnaPuertaAsync(transporte, sentidos, f => esElVendedor(f), cerrada, ct))
                    lugarCaminando = _entorno.Mapas.DondeHay(sentidos.Mapa, sentidos.Posicion, f => esElVendedor(f), mapaQueVende,
                                                             Pisable(sentidos), SaltosHastaElVendedor);
            }
            if (lugarCaminando is not { } lugar) return Corta("no hay vendedor a mano");
            if (!await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, RadioDelMostrador,
                                    topeDeMapas: Math.Max(Navegante.TopeDeMapas, lugar.Saltos))
                && !(sentidos.Mapa == lugar.Mapa && await AbrirUnaPuertaAsync(transporte, sentidos, f => esElVendedor(f), cerrada, ct)
                     && await IrHastaAsync(transporte, sentidos, lugar.Mapa, lugar.X, lugar.Y, cerrada, ct, RadioDelMostrador))
                && sentidos.Mapa != lugar.Mapa) return Corta($"no llegó a m{lugar.Mapa} ({lugar.X},{lugar.Y})");
        }
        if (sentidos.MasCercano(Sirve) is not { } v) return Corta("no ve al vendedor");
        if (v.Pasos > RadioDelMostrador
            && !await IrHastaAsync(transporte, sentidos, sentidos.Mapa, v.Quien.X, v.Quien.Y, cerrada, ct, RadioDelMostrador))
            return Corta($"no llega al mostrador ({v.Pasos} pasos)");
        if (sentidos.Donde(v.Quien.CharIndex) is not { } mostrador) return Corta("lo perdió de vista");
        transporte.Mandar(Paquete(ClientPacketId.DoubleClick, w => w.WriteByte(mostrador.X).WriteByte(mostrador.Y)));
        for (int espera = 0; espera < 12 && !sentidos.Comerciando; espera++) await Task.Delay(250, ct);
        if (!sentidos.Comerciando) return Corta($"no le abrió la tienda (ficha {v.Quien.FichaNpc})");
        bool la = false;
        if (sentidos.EnLaTienda(queCompra) is not { } oferta) Corta("no tiene lo que busca en la vitrina");
        else
        {
            int antes = sentidos.CuantoTiene(queCompra);
            transporte.Mandar(Paquete(ClientPacketId.CommerceBuy, w => w.WriteByte(oferta.Ranura).WriteInteger(1)));
            for (int espera = 0; espera < 12 && sentidos.CuantoTiene(queCompra) <= antes; espera++) await Task.Delay(250, ct);
            la = sentidos.CuantoTiene(queCompra) > antes;
            if (!la) Corta($"no le vendió (ranura {oferta.Ranura}, precio {oferta.Precio}, oro {sentidos.Oro})");
        }
        transporte.Mandar(Paquete(ClientPacketId.CommerceEnd, _ => { }));
        await Task.Delay(_rng.Next(400, 900), ct);
        return la;
    }
}
