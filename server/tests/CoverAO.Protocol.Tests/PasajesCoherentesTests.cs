using System.Collections.Generic;
using System.Linq;
using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los pases de barco tienen que poder COBRARSE: usarlos valida <c>Pos.Map = DesdeMap</c> estando al lado de
/// un transportador (VB6 <c>InvUsuario.bas:1404</c>), así que un pase cuyo <c>Desde</c> no es un mapa donde
/// spawnea su vendedor no sirve en ningún lado — se compra y el marino "se muestra enojado" para siempre.
///
/// El tester lo encontró en Rinkel (2026-08-26): el pirata está en el Puerto de Rinkel (mapa 99) y sus dos
/// pases (595 y 1053) traen <c>Desde=20</c>, la ciudad. Es el ÚNICO desparejo de los diez transportadores, y
/// está roto también en el VB6 — es un bug de la data del install.
///
/// Por eso el arreglo es una REGLA DE LA CASA (<c>PasesDeRinkel</c> del Server.ini), apagada por defecto:
/// apagada el server se comporta como el install y como el oráculo; prendida, los dos pases sirven. Este
/// archivo fija los DOS estados y el invariante general que habría cantado el desparejo solo.
/// </summary>
public class PasajesCoherentesTests
{
    private const int Transportador = 7;   // eNPCType.transportadores (Declares.bas:392)
    private const int Pasaje = 36;         // eOBJType.otPasajes (Declares.bas:614)
    private const int PaseANuevaEsperanza = 595, PaseATiama = 1053;

    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");
        return data;
    }

    /// <summary>APAGADA (el default): los dos pases quedan como en el install, saliendo del mapa 20. Es lo
    /// que hace el VB6, y lo que tiene que ver el oráculo.</summary>
    [Fact]
    public void By_default_the_install_data_is_left_untouched()
    {
        var data = LoadOrSkip();
        foreach (var idx in new[] { PaseANuevaEsperanza, PaseATiama })
        {
            var pase = data.Obj(idx);
            Assert.NotNull(pase);
            Assert.Equal(Pasaje, pase!.ObjType);
            Assert.Equal(20, pase.PasajeDesdeMap);   // la ciudad: donde NO hay transportador
        }
    }

    /// <summary>PRENDIDA: los dos pases pasan a salir del mapa donde su vendedor está parado, y el destino
    /// no se toca.</summary>
    [Fact]
    public void The_house_rule_makes_the_Rinkel_passes_usable()
    {
        var data = LoadOrSkip();
        var antes = new[] { PaseANuevaEsperanza, PaseATiama }
            .Select(i => (i, destino: data.Obj(i)!.PasajeHastaMap, x: data.Obj(i)!.PasajeHastaX)).ToArray();

        data.ArreglarPasesDeRinkel();

        int mapaDelPirata = MapasDeCadaNpc(data)
            .First(kv => data.Npc(kv.Key)?.StockItems?.Any(it => it.ObjIndex == PaseANuevaEsperanza) == true)
            .Value.Single();
        foreach (var (idx, destino, x) in antes)
        {
            var pase = data.Obj(idx)!;
            Assert.Equal(mapaDelPirata, pase.PasajeDesdeMap);   // ahora sale del puerto
            Assert.Equal(destino, pase.PasajeHastaMap);         // y el destino quedó intacto
            Assert.Equal(x, pase.PasajeHastaX);
        }
    }

    /// <summary>La regla NO re-rompe una data ya arreglada: si el <c>Desde</c> ya coincide con el mapa del
    /// vendedor, aplicarla otra vez no cambia nada (es idempotente y no tiene el 20 cableado).</summary>
    [Fact]
    public void Applying_the_house_rule_twice_changes_nothing()
    {
        var data = LoadOrSkip();
        data.ArreglarPasesDeRinkel();
        var unaVez = new[] { PaseANuevaEsperanza, PaseATiama }.Select(i => data.Obj(i)!.PasajeDesdeMap).ToArray();
        data.ArreglarPasesDeRinkel();
        var dosVeces = new[] { PaseANuevaEsperanza, PaseATiama }.Select(i => data.Obj(i)!.PasajeDesdeMap).ToArray();
        Assert.Equal(unaVez, dosVeces);
    }

    /// <summary>El invariante, con la regla PRENDIDA: cada pase que vende un transportador poblado sale de un
    /// mapa donde ESE transportador está parado. Si una data nueva trae otro vendedor que no puede cobrar lo
    /// que vende, esto lo canta con nombre y apellido.</summary>
    [Fact]
    public void With_the_house_rule_every_sold_pass_departs_from_its_sellers_map()
    {
        var data = LoadOrSkip();
        data.ArreglarPasesDeRinkel();
        var mapasDe = MapasDeCadaNpc(data);

        var rotos = new List<string>();
        int vendedores = 0, pasesMirados = 0;
        foreach (var (npcIndex, npc) in data.Npcs)
        {
            if (npc.NpcType != Transportador || npc.StockItems is not { Count: > 0 } stock) continue;
            if (!mapasDe.TryGetValue(npcIndex, out var mapas)) continue;   // no poblado: no lo ve nadie
            vendedores++;
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
        Assert.True(vendedores >= 8, $"sólo {vendedores} transportadores con stock");
        Assert.True(pasesMirados >= 20, $"sólo {pasesMirados} pases mirados");
        Assert.True(rotos.Count == 0, string.Join("\n", rotos));
    }

    private static Dictionary<int, HashSet<int>> MapasDeCadaNpc(GameData data)
    {
        var mapasDe = new Dictionary<int, HashSet<int>>();
        foreach (var (mapa, spawns) in data.MapSpawns)
            foreach (var s in spawns)
                (mapasDe.TryGetValue(s.NpcIndex, out var set) ? set : mapasDe[s.NpcIndex] = new()).Add(mapa);
        return mapasDe;
    }
}
