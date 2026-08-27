using System.Collections.Generic;
using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los pases de barco tienen que poder COBRARSE: usarlos valida <c>Pos.Map = DesdeMap</c> al lado de un
/// transportador (VB6 InvUsuario.bas:1394), así que un pase cuyo <c>Desde</c> no es un mapa donde spawnea
/// su vendedor no sirve en ningún lado — se compra y el marino "se muestra enojado" para siempre.
///
/// El tester lo encontró en Rinkel (2026-08-26): el pirata está en el Puerto de Rinkel (mapa 99) y sus dos
/// pases (595 y 1053) traían <c>Desde=20</c>, la ciudad. Era el ÚNICO desparejo de los diez transportadores
/// —bug de la data del install, roto también en el VB6— y se corrige en <c>GameData.LoadObjects</c>.
/// Este archivo fija la corrección puntual y el invariante general que la habría cantado sola.
/// </summary>
public class PasajesCoherentesTests
{
    private const int Transportador = 7;   // eNPCType.transportadores (Declares.bas:392)
    private const int Pasaje = 36;         // eOBJType.otPasajes (Declares.bas:614)

    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>Los dos pases del pirata de Rinkel salen del mapa donde él está parado (99, el puerto),
    /// no de la ciudad (20) que traía la data.</summary>
    [Fact]
    public void Rinkel_passes_depart_from_the_pirates_map()
    {
        var data = LoadOrSkip();
        foreach (var idx in new[] { 595, 1053 })
        {
            var pase = data.Obj(idx);
            Assert.NotNull(pase);
            Assert.Equal(Pasaje, pase!.ObjType);
            Assert.Equal(99, pase.PasajeDesdeMap);
        }
    }

    /// <summary>El invariante: cada pase que vende un transportador poblado en el mundo tiene su
    /// <c>Desde</c> en un mapa donde ESE transportador está parado. Si una data nueva vuelve a traer un
    /// vendedor que no puede cobrar lo que vende, esto lo canta con nombre y apellido.</summary>
    [Fact]
    public void Every_sold_pass_departs_from_a_map_where_its_seller_stands()
    {
        var data = LoadOrSkip();

        // dónde spawnea cada NPC (índice → mapas)
        var mapasDe = new Dictionary<int, HashSet<int>>();
        foreach (var (mapa, spawns) in data.MapSpawns)
            foreach (var s in spawns)
                (mapasDe.TryGetValue(s.NpcIndex, out var set) ? set : mapasDe[s.NpcIndex] = new()).Add(mapa);

        var rotos = new List<string>();
        int transportadoresConStock = 0, pasesMirados = 0;
        foreach (var (npcIndex, npc) in data.Npcs)
        {
            if (npc.NpcType != Transportador || npc.StockItems is not { Count: > 0 } stock) continue;
            if (!mapasDe.TryGetValue(npcIndex, out var mapas)) continue;   // no poblado: no lo ve ningún jugador
            transportadoresConStock++;
            foreach (var (objIndex, _) in stock)
            {
                if (data.Obj(objIndex) is not { ObjType: Pasaje } pase) continue;
                pasesMirados++;
                if (!mapas.Contains(pase.PasajeDesdeMap))
                    rotos.Add($"{npc.Name} (NPC {npcIndex}, mapas {string.Join(",", mapas)}) vende " +
                              $"'{pase.Name}' ({objIndex}) con Desde={pase.PasajeDesdeMap}");
            }
        }

        // La población de control: si esto da 0, el test no midió nada (cambió un tipo, una clave, un
        // formato) y un verde así es el silencio de un guardia que no mira.
        Assert.True(transportadoresConStock >= 8, $"sólo {transportadoresConStock} transportadores con stock");
        Assert.True(pasesMirados >= 20, $"sólo {pasesMirados} pases mirados");
        Assert.True(rotos.Count == 0, string.Join("\n", rotos));
    }
}
