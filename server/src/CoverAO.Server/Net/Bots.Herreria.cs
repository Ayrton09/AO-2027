using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;

namespace CoverAO.Server.Net;

public sealed partial class Bot
{
    private const byte ClaseHerrero = 12;
    public const int LingotesPorLote = 100;
    public const int SaltosDelOficio = 12;
    private long _proximaFundicion, _proximoMercadoDelOficio;
    private (int Mapa, short Preferido, long Hasta, short Elegido) _minaBuscada;

    public static bool EsLingote(int obj) => obj is GameLimits.IngotIronObj or GameLimits.IngotSilverObj or GameLimits.IngotGoldObj;

    /// <summary>El valor cero de las recetas indica que no se venden en tiendas. Para subastar se usa el costo de materiales
    /// más el trabajo, sin cambiar el valor del catálogo ni lo que paga un NPC.</summary>
    public static int ValorDeMercado(GameData datos, short obj)
    {
        if (datos.Obj(obj) is not { } o) return 0;
        if (o.Value > 0) return o.Value;
        if (!datos.BlacksmithRecipes.Contains(obj)) return 0;
        long costo = Oficios.Herreria.Materiales(o).Sum(m => (long)Math.Max(0, m.PorUnidad) * Math.Max(0, datos.Obj(m.Obj)?.Value ?? 0));
        return (int)Math.Min(int.MaxValue, costo + costo / 2);
    }

    private bool EsInsumoMetalurgico(short obj) =>
        (_clase == ClaseMinero && _entorno.Datos.Obj(obj) is { IsMineral: true }) || (_clase == ClaseHerrero && EsLingote(obj));

    private bool EsProduccionMetalurgica(short obj) =>
        (_clase == ClaseMinero && EsLingote(obj)) || (_clase == ClaseHerrero && _entorno.Datos.BlacksmithRecipes.Contains(obj));

    private bool EsFabricanteMetalurgico => _clase is ClaseMinero or ClaseHerrero;

    // El transporte rodea a las criaturas que no puede combatir antes de atraerlas. No se excluye el mapa:
    // las minas requieren cruzarlo, y prohibirlo dejaría al minero sin ningún camino al pueblo.
    private List<(byte X, byte Y)> PeligrosDelViaje(SentidosDelBot s, bool evitaLoDelHueco = false)
    {
        if (!EsFabricanteMetalurgico && !evitaLoDelHueco) return PeligrosALaVista(s);
        int vida = s.VidaMaxima, nivel = s.Nivel;
        bool pociones = s.CuantoTiene(EsPocionDeVida) >= PocionesParaPelear;
        bool antidoto = s.CuantoTiene(EsPocionQueCuraElVeneno) > 0;
        return s.DondeEstan(f => EsPeligroso(f, vida, nivel, pociones, antidoto)
            || EsDelHueco(f, vida, nivel, pociones, antidoto));
    }

    public static HashSet<(byte X, byte Y)> ZonasQueEvitaElTransportista((byte X, byte Y) desde,
        IEnumerable<(byte X, byte Y)> amenazas)
    {
        var zonas = new HashSet<(byte X, byte Y)>();
        foreach (var p in amenazas.Where(p => !AlAlcanceDeSuVista(desde, p)))
            for (int x = Math.Max(1, p.X - NpcSystem.RangoVisionX); x <= Math.Min(100, p.X + NpcSystem.RangoVisionX); x++)
                for (int y = Math.Max(1, p.Y - NpcSystem.RangoVisionY); y <= Math.Min(100, p.Y + NpcSystem.RangoVisionY); y++)
                    zonas.Add(((byte)x, (byte)y));
        return zonas;
    }

    private bool HayLugarParaProducir(SentidosDelBot s)
    {
        int limite = _clase == ClaseMinero ? LingotesPorLote * SubastasPropias : SubastasPropias;
        return SusSubastasConocidas(s) < SubastasPropias && s.RanurasOcupadas().Count < GameLimits.MaxInventorySlots
            && ProduccionPendiente(s, listaParaVender: true) < limite && ProduccionPendiente(s) < limite * 2;
    }

    private int ProduccionPendiente(SentidosDelBot s, bool listaParaVender = false) => s.RanurasOcupadas().Select(s.QueHayEn).Distinct()
        .Where(o => EsProduccionMetalurgica(o) && (!listaParaVender || SePuedeRepublicar(o))).Sum(o => _clase == ClaseMinero ? s.CuantoTiene(i => i == o)
            : Math.Max(0, s.CuantoTiene(i => i == o) - (ReservaEquipoPropio(s, o) ? 1 : 0)));

    internal short MineralQueBusca(SentidosDelBot s) => MineralesPorPrioridad(s).FirstOrDefault()?.Index ?? 0;

    private IEnumerable<ObjInfo> MineralesPorPrioridad(SentidosDelBot s)
    {
        // La demanda mueve la prioridad como máximo medio lote; el herrero sigue necesitando los tres metales.
        return _entorno.Datos.Objects.Values.Where(o => o.IsMineral && Oficios.PuedeFundir(o.MinSkill, s.Skill(GameLimits.SkillMineria), _clase))
            .OrderBy(o => s.CuantoTiene(i => i == o.LingoteIndex) +
                          s.CuantoTiene(i => i == o.Index) / Oficios.MineralesPorLingote(o.Index) +
                          s.Subastas.Where(f => f.Vendedor.Equals(Nombre, StringComparison.OrdinalIgnoreCase) && f.Objeto == o.LingoteIndex).Sum(f => (int)f.Cantidad)
                          - Math.Clamp(_entorno.DemandaDeSubasta((short)o.LingoteIndex), -5, 5) * (LingotesPorLote / 10))
            .ThenBy(o => o.MinSkill);
    }

    /// <summary>Mientras aprende Minería puede picar hierro aunque todavía no sepa fundirlo.</summary>
    private Func<int, bool> YacimientoParaElMinero(SentidosDelBot s)
    {
        short mineral = MineralQueBusca(s);
        if (mineral == 0) mineral = GameLimits.OreIronObj;
        if (_minaBuscada.Mapa == s.Mapa && _minaBuscada.Preferido == mineral && Environment.TickCount64 < _minaBuscada.Hasta)
            mineral = _minaBuscada.Elegido;
        else
        {
            short preferido = mineral;
            var faena = MapaParaSuFaena(s);
            var cruza = CruzaSinPeligro(s);
            var opciones = MineralesPorPrioridad(s).Concat(_entorno.Datos.Obj(GameLimits.OreIronObj) is { } hierro ? [hierro] : [])
                .DistinctBy(o => o.Index);
            foreach (var o in opciones)
            {
                if (_entorno.Mapas.DondeSeTrabaja(s.Mapa, false, i => _entorno.Datos.Obj(i)?.MineralIndex == o.Index,
                    s.Posicion, faena, cruza, SaltosDelOficio) is null) continue;
                mineral = o.Index;
                break;
            }
            _minaBuscada = (s.Mapa, preferido, Environment.TickCount64 + 60_000, mineral);
        }
        return obj => _entorno.Datos.Obj(obj) is { IsDeposit: true } y && y.MineralIndex == mineral;
    }

    private bool MineroTieneReserva(SentidosDelBot s) => _clase == ClaseMinero &&
        (!HayLugarParaProducir(s) || _entorno.Datos.Objects.Values.Any(o => o.IsMineral &&
            s.CuantoTiene(i => i == o.Index) >= Oficios.MineralesPorLingote(o.Index) * LingotesPorLote));

    private async Task<bool> FundirAsync(TransporteDeBot t, SentidosDelBot s, Task cerrada, CancellationToken ct)
    {
        if (_clase != ClaseMinero || Environment.TickCount64 < _proximaFundicion || HostilCerca(s) || Urge(s)) return false;
        var mineral = _entorno.Datos.Objects.Values.Where(o => o.IsMineral && Oficios.PuedeFundir(o.MinSkill, s.Skill(GameLimits.SkillMineria), _clase)
            && s.CuantoTiene(i => i == o.Index) >= Oficios.MineralesPorLingote(o.Index) * 10).OrderBy(o => o.Index).FirstOrDefault();
        if (mineral is null) return false;
        _proximaFundicion = Environment.TickCount64 + 60_000;
        bool Fragua(int obj) => _entorno.Datos.Obj(obj)?.IsForge == true;
        const int puertasDeFragua = 3;
        var cruza = CruzaSinPeligro(s);
        var lugar = _entorno.Mapas.DondeSeTrabaja(s.Mapa, false, Fragua, s.Posicion, MapaSeguro, cruza, SaltosDelOficio, puertasDeFragua);
        if (lugar is not { } f) return false;
        Console.WriteLine($"[bot {Nombre}] lleva mineral {mineral.Index} a la fragua de m{f.Mapa} desde m{s.Mapa}");
        if (s.Mapa != f.Mapa)
        {
            // La fragua está dentro de un edificio: primero llega a la entrada que permite abrirlo.
            var entrada = _entorno.Mapas.MapasAlcanzables(s.Mapa, s.Posicion, SaltosDelOficio, cruza)
                .Where(e => e.Mapa == f.Mapa && _entorno.Mapas.PuertasDelCamino(e.Mapa, e.Entrada, [(f.X,f.Y)], DistanciaParaFaenar)
                    is { } puertas && puertas.Count <= puertasDeFragua)
                .Select(e => ((byte X,byte Y)?)e.Entrada).FirstOrDefault();
            if (entrada is not { } llega || !await IrHastaAsync(t, s, f.Mapa, llega.X, llega.Y, cerrada, ct,
                    topeDeMapas: SaltosDelOficio, soloALaMeta: true)) return true;
        }
        for (int abiertas = 0; ; abiertas++)
        {
            if (await IrHastaAsync(t, s, f.Mapa, f.X, f.Y, cerrada, ct, DistanciaParaFaenar)) break;
            if (s.Mapa != f.Mapa || Urge(s) || HostilCerca(s) || abiertas == puertasDeFragua
                || _entorno.Mapas.PuertasDelCamino(f.Mapa, s.Posicion, [(f.X,f.Y)], DistanciaParaFaenar) is not { Count: > 0 } puertas
                || !await AbrirLaPuertaQueAsync(t, s, p => p == puertas[0], cerrada, ct)) return true;
        }
        if (s.EnLaMochila(o => o == mineral.Index) is not { } item || Urge(s)) return true;
        t.Mandar(Paquete(ClientPacketId.UseItem, w => w.WriteByte(item.Ranura)));
        t.Mandar(Paquete(ClientPacketId.WorkLeftClick, w => w.WriteByte(f.X).WriteByte(f.Y).WriteByte(GameLimits.SkillFundir)));
        int antes = s.CuantoTiene(o => o == mineral.Index);
        // El servidor consume el mineral y entrega los lingotes en sus intervalos normales.
        for (int i = 0; i < 120 && !cerrada.IsCompleted && !Urge(s) && !HostilCerca(s); i++)
        {
            await Task.Delay(700, ct);
            int quedan = s.CuantoTiene(o => o == mineral.Index);
            if (quedan < Oficios.MineralesPorLingote(mineral.Index) || (i >= 5 && quedan >= antes)) break;
        }
        return true;
    }

    /// <summary>Una receta concreta por vez: termina de abastecerla antes de comprar materiales para otra.</summary>
    private short? _recetaDeHerreria;
    private readonly HashSet<short> _recetasFabricadas = new();
    private int _mercadosSinMaterial;
    private const int MercadosAntesDeImprovisar = 3;

    internal short? RecetaDeHerreria(SentidosDelBot s)
    {
        int skill = s.Skill(GameLimits.SkillHerreria);
        var posibles = _entorno.Datos.BlacksmithRecipes.Select(i => _entorno.Datos.Obj(i)!)
            .Where(o => o.SkillBlacksmith <= skill && ValorDeMercado(_entorno.Datos, o.Index) > 0 && SePuedeRepublicar(o.Index));
        if (_recetaDeHerreria is { } elegida && posibles.Any(o => o.Index == elegida)) return elegida;
        // Prioriza una receta que pueda completar con lo que tiene. Al terminar rota entre las menos abastecidas.
        var receta = posibles.OrderBy(o => _entorno.DemandaDeSubasta(o.Index) < 0)
            .ThenBy(o => _recetasFabricadas.Contains(o.Index))
            .ThenBy(o => s.CuantoTiene(i => i == o.Index) + s.Subastas.Count(f => f.Objeto == o.Index))
            .ThenByDescending(o => _entorno.DemandaDeSubasta(o.Index))
            .ThenBy(o => Oficios.Herreria.Materiales(o).Sum(m => (long)Math.Max(0, m.PorUnidad - s.CuantoTiene(i => i == m.Obj)) * (_entorno.Datos.Obj(m.Obj)?.Value ?? 0)))
            .ThenBy(o => o.Index).FirstOrDefault();
        _mercadosSinMaterial = 0;
        return _recetaDeHerreria = receta?.Index;
    }

    private short? ElegirFabricacion(SentidosDelBot s, IReadOnlySet<int> recetas, Oficios.Fabricacion regla, int skill, Func<int, int> tiene)
    {
        if (_clase != ClaseHerrero) return QueFabricar(_entorno.Datos, recetas, regla, skill, tiene);
        if (!HayLugarParaProducir(s) || RecetaDeHerreria(s) is not { } r) return null;
        if (CuantasAlcanzan(_entorno.Datos, r, regla, tiene) > 0) return r;
        if (_mercadosSinMaterial < MercadosAntesDeImprovisar || _entorno.Datos.Obj(r) is not { } encargo) return null;

        // Una tanda alternativa sólo usa excedentes. El encargo sigue siendo lo que busca en la subasta.
        var reservado = regla.Materiales(encargo).GroupBy(m => m.Obj).ToDictionary(g => g.Key, g => g.Sum(m => m.PorUnidad));
        int Libre(int obj) => Math.Max(0, tiene(obj) - reservado.GetValueOrDefault(obj));
        return recetas.Select(i => _entorno.Datos.Obj(i))
            .Where(o => o is not null && o.Index != r && o.SkillBlacksmith <= skill && SePuedeRepublicar(o.Index)
                && ValorDeMercado(_entorno.Datos, o.Index) > 0 && CuantasAlcanzan(_entorno.Datos, o.Index, regla, Libre) > 0)
            .OrderBy(o => _entorno.DemandaDeSubasta(o!.Index) < 0)
            .ThenBy(o => _recetasFabricadas.Contains(o!.Index)).ThenBy(o => o!.Index).FirstOrDefault()?.Index;
    }

    private void AnotarMaterialEnElMercado(SentidosDelBot s, int plata)
    {
        if (_clase != ClaseHerrero || !HayLugarParaProducir(s) || RecetaDeHerreria(s) is not { } receta
            || _entorno.Datos.Obj(receta) is not { } o) return;
        var faltan = Oficios.Herreria.Materiales(o).Where(m => s.CuantoTiene(i => i == m.Obj) < m.PorUnidad).ToArray();
        if (faltan.Length == 0 || faltan.Any(m => _porRetirar.Contains((short)m.Obj)) || s.Subastas.Any(f => LeSirveElLote(s, f, plata)))
            _mercadosSinMaterial = 0;
        else
            _mercadosSinMaterial = Math.Min(_mercadosSinMaterial + 1, MercadosAntesDeImprovisar);
    }

    private bool FabricaParaVender(SentidosDelBot s) => _clase == ClaseHerrero && HayLugarParaProducir(s);

    public static bool LeConvieneElLote(ServerMessages.FilaDeSubasta f, int valorUnitario, string yo, int plata, int faltan) =>
        f.Cantidad > 0 && f.Cantidad <= Math.Max(faltan, LingotesPorLote) && faltan > 0 && f.PrecioFinal > 0 &&
        f.PrecioFinal <= plata && (long)f.PrecioFinal * 100 <= (long)valorUnitario * f.Cantidad * PorcentajeQuePaga &&
        !string.Equals(f.Vendedor, yo, StringComparison.OrdinalIgnoreCase);

    private bool LeSirveElLote(SentidosDelBot s, ServerMessages.FilaDeSubasta f, int plata)
    {
        if (_clase != ClaseHerrero || !EsLingote(f.Objeto) || _porRetirar.Contains(f.Objeto) || !HayLugarParaProducir(s)
            || RecetaDeHerreria(s) is not { } receta || _entorno.Datos.Obj(receta) is not { } o) return false;
        int necesita = Oficios.Herreria.Materiales(o).Where(m => m.Obj == f.Objeto).Sum(m => m.PorUnidad);
        return LeConvieneElLote(f, _entorno.Datos.Obj(f.Objeto)?.Value ?? 0, Nombre, plata, necesita - s.CuantoTiene(i => i == f.Objeto));
    }

    /// <summary>El oficio tiene su propia visita al mercado aunque el equipo y los consumibles estén completos.</summary>
    private async Task<bool> MercadoDelOficioAsync(TransporteDeBot t, SentidosDelBot s, Task cerrada, CancellationToken ct)
    {
        if ((!EsFabricanteMetalurgico && s.Nivel < Leveling.MaxLevel) || Environment.TickCount64 < _proximoMercadoDelOficio || Urge(s) || HostilCerca(s)) return false;
        AnotarLoQueDejoLaSubasta(s);
        bool vende = s.RanurasOcupadas().Select(s.QueHayEn).Distinct().Any(o => EsProduccionMetalurgica(o)
            && CantidadParaSubastar(s, o, s.CuantoTiene(i => i == o)) > 0 && SePuedeRepublicar(o));
        bool compra = PlataParaElEquipo(s) >= PlataParaMirarLaSubasta && (_clase == ClaseHerrero
            ? HayLugarParaProducir(s) && RecetaDeHerreria(s) is { } receta && CuantasAlcanzan(_entorno.Datos, receta, Oficios.Herreria, o => s.CuantoTiene(i => i == o)) == 0
            : !EsFabricanteMetalurgico && BuscaMejorasEnSubasta);
        if (!vende && !compra && _porRetirar.Count == 0) return false;
        _proximoMercadoDelOficio = Environment.TickCount64 + (EsFabricanteMetalurgico ? 120_000 : MirarLaSubastaMs);
        var lugar = _entorno.Mapas.DondeHay(s.Mapa, s.Posicion, f => EsSubastador((short)f),
            m => MapaSeguro(m) && TieneBanquero(m), CruzaSinPeligro(s), topeDeSaltos: SaltosDelOficio);
        if (lugar is not { } mercado) return false;
        if (!await IrHastaAsync(t, s, mercado.Mapa, mercado.X, mercado.Y, cerrada, ct, RadioDelSubastador, topeDeMapas: SaltosDelOficio)) return true;
        // Vender no requiere retirar ahorros si ya puede pagar el depósito de sus lotes.
        // Ir primero al banco podía dejarlo dentro de otro edificio antes de publicar la carga.
        if ((compra && !vende) || s.Oro < DepositoDeLaProduccion(s)) await RetirarParaComprarAsync(t, s, cerrada, ct);
        _proximaMiradaALaSubasta = 0;
        await PasarPorLaSubastaAsync(t, s, cerrada, ct);
        await DepositarLoQueSobraAsync(t, s, cerrada, ct);
        return true;
    }

    private long DepositoDeLaProduccion(SentidosDelBot s) => s.RanurasOcupadas().Select(s.QueHayEn).Distinct()
        .Where(o => EsProduccionMetalurgica(o) && SePuedeRepublicar(o)).Take(SubastasPropias).Sum(o => {
            int cantidad = CantidadParaSubastar(s, o, s.CuantoTiene(i => i == o));
            int valor = (int)Math.Min(int.MaxValue, (long)ValorDeMercado(_entorno.Datos, o) * cantidad);
            var (inicial, final) = PrecioDeSubasta(valor);
            return cantidad > 0 ? CentroDeSubastas.Deposito(HorasDeSubasta, inicial, final) : 0L;
        });

    private int CantidadParaSubastar(SentidosDelBot s, short obj, int cantidad)
    {
        if (_clase == ClaseMinero && EsLingote(obj)) return cantidad < 10 ? 0 : Math.Min(LingotesPorLote, cantidad);
        if (_entorno.Datos.BlacksmithRecipes.Contains(obj))
        {
            return cantidad > (ReservaEquipoPropio(s, obj) ? 1 : 0) ? 1 : 0;
        }
        return Math.Min(cantidad, short.MaxValue);
    }

    private bool ReservaEquipoPropio(SentidosDelBot s, short obj) => _entorno.Datos.Obj(obj) is { } o
        && LoPuedeUsar(o, s.Nivel) && (QueSePone.Any(es => es(o)
            && EsLoMejorQueTieneDeEsaClase(ValorDeCombate(o), LoMejorQueTiene(s, es)))
            || (o.IsKnuckles && !TiraConArco(_clase) && LoMejorQueTiene(s, EsArma) == 0
                && EsLoMejorQueTieneDeEsaClase(ValorDeCombate(o), LoMejorQueTiene(s, n => n.IsKnuckles))));

    private async Task PrepararProduccionParaSubastarAsync(TransporteDeBot t, SentidosDelBot s, CancellationToken ct)
    {
        if (_clase != ClaseHerrero || !MapaSeguro(s.Mapa)) return;
        foreach (byte ranura in s.RanurasOcupadas())
        {
            short obj = s.QueHayEn(ranura);
            if (!s.EstaPuesto(ranura) || !EsProduccionMetalurgica(obj) || !GuardaParaLaSubasta(s, obj)
                || CantidadParaSubastar(s, obj, s.CuantoTiene(i => i == obj)) == 0) continue;
            // Dos armaduras iguales comparten ranura: se quita el equipo para vender el sobrante y se conserva una.
            t.Mandar(Paquete(ClientPacketId.EquipItem, w => w.WriteByte(ranura)));
            for (int i = 0; i < 8 && s.EstaPuesto(ranura); i++) await Task.Delay(250, ct);
        }
    }
}
