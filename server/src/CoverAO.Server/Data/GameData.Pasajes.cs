using CoverAO.Server.Game;

namespace CoverAO.Server.Data;

public sealed partial class GameData
{
    private enum BandoDelPuerto { Neutral, Imperio, Republica }
    private readonly record struct Puerto(int Mapa, int Pirata, BandoDelPuerto Bando);

    private const int ViajeroDeUllathorpe = 628;
    private static readonly Puerto[] Puertos =
    [
        new(1, ViajeroDeUllathorpe, BandoDelPuerto.Imperio),
        new(34, 107, BandoDelPuerto.Imperio),         // Nix
        new(61, 106, BandoDelPuerto.Imperio),         // Banderbill
        new(64, 142, BandoDelPuerto.Republica),       // Lindos
        new(PuertoDeRinkel, 116, BandoDelPuerto.Neutral),
        new(111, 109, BandoDelPuerto.Neutral),        // Nueva Esperanza
        new(150, 108, BandoDelPuerto.Neutral),        // Arghâl
        new(179, 141, BandoDelPuerto.Republica),      // Illiandor
        new(181, 150, BandoDelPuerto.Neutral),        // Orac
        new(183, 143, BandoDelPuerto.Republica),      // Suramei
        new(217, 139, BandoDelPuerto.Neutral),        // Tiama
    ];

    private bool _pasajesPorFaccion;
    private bool _pasesDeRinkel;

    public void ConfigurarPasajes(ServerIni ini)
    {
        _pasesDeRinkel = ini.PasesDeRinkel;
        _pasajesPorFaccion = ini.PasajesPorFaccion;
        if (_pasesDeRinkel) ArreglarPasesDeRinkel();
        if (!_pasajesPorFaccion) return;

        CorregirPaseABanderbill();
        // Ullathorpe no tiene puerto. El viajero sale por la puerta sur de la ciudad.
        if (MapExists(1) && Npcs.ContainsKey(ViajeroDeUllathorpe))
        {
            var spawns = MapSpawns.GetValueOrDefault(1) ?? [];
            if (!spawns.Any(s => s.NpcIndex == ViajeroDeUllathorpe))
                MapSpawns = new Dictionary<int, IReadOnlyList<MapNpcSpawn>>(MapSpawns)
                { [1] = [..spawns, new MapNpcSpawn(55, 82, ViajeroDeUllathorpe)] };
        }
        ArmarMostradoresDePasajes();
    }

    private void CorregirPaseABanderbill()
    {
        // El pase 1075 se anuncia a Banderbill, pero el install lo manda al puerto de Orac.
        if (Obj(1075) is not { ObjType: TipoPasaje, PasajeDesdeMap: 150, PasajeHastaMap: 181 } pase
            || Obj(598) is not { ObjType: TipoPasaje, PasajeHastaMap: 61 } destino) return;
        Objects = new Dictionary<int, ObjInfo>(Objects)
        {
            [pase.Index] = pase with
            {
                PasajeHastaMap = destino.PasajeHastaMap, PasajeHastaX = destino.PasajeHastaX, PasajeHastaY = destino.PasajeHastaY,
            },
        };
    }

    private void ArmarMostradoresDePasajes()
    {
        var poblados = Puertos.Where(p => Npcs.TryGetValue(p.Pirata, out var n) && n.NpcType == 7
            && MapSpawns.TryGetValue(p.Mapa, out var spawns) && spawns.Any(s => s.NpcIndex == p.Pirata)).ToArray();
        var rutas = Objects.Values.Where(o => o.ObjType == TipoPasaje && o.Value > 0 && EsDesembarcoValido(o))
            // Los pases viejos a Rinkel llegan a la ciudad; también cuentan como ruta a Rinkel.
            .ToLookup(o => (o.PasajeDesdeMap, Destino: o.PasajeHastaMap == 20 ? PuertoDeRinkel : o.PasajeHastaMap));
        var fichas = new Dictionary<int, NpcInfo>(Npcs);
        foreach (var origen in poblados)
        {
            var stock = new List<(int ObjIndex, int Amount)>();
            foreach (var destino in poblados)
            {
                if (origen == destino || (origen.Bando != BandoDelPuerto.Neutral
                    && destino.Bando != BandoDelPuerto.Neutral && origen.Bando != destino.Bando)) continue;
                // El índice menor conserva el pasaje viejo y su precio si ya cubría esta ruta.
                var pase = rutas[(origen.Mapa, destino.Mapa)].MinBy(o => o.Index);
                if (pase is not null) stock.Add((pase.Index, 100));
            }
            fichas[origen.Pirata] = fichas[origen.Pirata] with { StockItems = stock, LineasDelDat = stock, NroItems = stock.Count };
        }
        Npcs = fichas;
    }

    private bool EsDesembarcoValido(ObjInfo o)
    {
        var (mapa, x, y) = (o.PasajeHastaMap, o.PasajeHastaX, o.PasajeHastaY);
        return x is >= GameLimits.MinXBorder and <= GameLimits.MaxXBorder
            && y is >= GameLimits.MinYBorder and <= GameLimits.MaxYBorder
            && MapExists(mapa) && !IsWater(mapa, x, y) && !IsBlockedTile(mapa, x, y)
            && !MapExits.ContainsKey((mapa, x, y))
            && (!MapSpawns.TryGetValue(mapa, out var spawns) || !spawns.Any(s => s.X == x && s.Y == y));
    }

    internal bool EsPirataDeLaRed(Npc npc) => _pasajesPorFaccion && npc.IsTransportador
        && Puertos.Any(p => p.Mapa == npc.Pos.Map && p.Pirata == npc.Template.Index);

    internal bool PirataAtiendePorFaccion(int mapa, int ficha, byte faccion) => _pasajesPorFaccion
        && Puertos.Any(p => p.Mapa == mapa && p.Pirata == ficha && (p.Bando switch
        {
            BandoDelPuerto.Neutral => true,
            BandoDelPuerto.Imperio => faccion is Player.StatusCiudadano or Player.StatusArmada,
            BandoDelPuerto.Republica => faccion is Player.StatusRepublicano or Player.StatusMilicia,
            _ => false,
        }));
}
