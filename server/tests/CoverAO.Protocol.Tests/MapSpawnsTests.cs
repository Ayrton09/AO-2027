using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Locks in the map-spawn pipeline: MapSpawns.json (decoded from the .csm by decode_map.py) loads
/// into GameData, and the NPC hostility/movement flags that drive spawn behaviour are parsed. These
/// are the town of Ullathorpe (map 1) — its authentic AO population.
/// </summary>
public class MapSpawnsTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        // In a data-less environment (no gamedata) there is nothing to assert.
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    [Fact]
    public void Map1_spawns_load_the_ullathorpe_population()
    {
        var data = LoadOrSkip();
        Assert.True(data.MapSpawns.ContainsKey(1), "map 1 spawns should be present");
        var spawns = data.MapSpawns[1];
        // 23 spawns: los de `CoverAO-2021/Servidor/Maps/mapa1.csm`, que es EL QUE CARGA EL ORÁCULO. Antes acá
        // decía 26 — la población de IAO-Clon. Hay tres mapa1.csm distintos en el árbol y MapSpawns.json se
        // había transcodificado del equivocado, así que este test fijaba el mundo de OTRO JUEGO (8 serpientes y
        // sin entrenadores de clase). Ver el aviso en fase0/transcode/decode_map.py.
        Assert.Equal(23, spawns.Count);

        // Every spawn is inside a 100x100 map with a positive NpcIndex.
        Assert.All(spawns, s =>
        {
            Assert.InRange(s.X, 1, 100);
            Assert.InRange(s.Y, 1, 100);
            Assert.True(s.NpcIndex > 0);
        });

        // Vecinos conocidos de Ullathorpe por índice: Banquero=24, Carpintero=44, Sacerdote=5.
        Assert.Contains(spawns, s => s.NpcIndex == 24);
        Assert.Contains(spawns, s => s.NpcIndex == 44);
        Assert.Contains(spawns, s => s.NpcIndex == 5);
        // UNA sola Serpiente (504), en (22,40): es el único bicho agresivo del pueblo. El mundo de IAO tenía 8 —
        // el 8 de antes era la huella de la proveniencia equivocada.
        Assert.Equal(1, spawns.Count(s => s.NpcIndex == 504));
        // Y los entrenadores de clase que SÓLO tiene CoverAO: Clérigo=81, Bardo=82, Paladín=85, Mago=86,
        // Asesino=87, Mercenario=64. Su presencia es lo que distingue este mapa del de IAO.
        Assert.All(new[] { 81, 82, 85, 86, 87, 64 }, idx => Assert.Contains(spawns, s => s.NpcIndex == idx));
    }

    [Fact]
    public void Map1_carga_los_objetos_del_mundo_incluidas_las_puertas()
    {
        // El .csm trae objetos colocados (VB6 FileIO.bas:1326 → MapData(m,x,y).ObjInfo). decode_map.py salteaba
        // la lista entera, así que el mundo no tenía ni árboles ni carteles ni PUERTAS — y sin puertas no se
        // entra a ningún edificio, porque el .csm ya trae su tile bloqueado y sólo abrirlas lo libera.
        var data = LoadOrSkip();
        Assert.True(data.MapObjects.ContainsKey(1), "map 1 debería traer sus objetos");
        var objs = data.MapObjects[1];
        Assert.Equal(176, objs.Count);                       // los de Servidor/Maps/mapa1.csm

        var puertas = objs.Where(o => data.Obj(o.ObjIndex) is { IsDoor: true }).ToList();
        Assert.Equal(17, puertas.Count);
        // Las 17 arrancan CERRADAS. Ojo: `Closed` sale de la clave `abierta` del obj.dat, INVERTIDA
        // (FileIO.bas:981) — si alguien la leyera como "está abierta", todas arrancarían al revés.
        Assert.All(puertas, o => Assert.Equal(1, data.Obj(o.ObjIndex)!.Closed));
        // 4 son con llave (las casas de la columna x=29) y no se abren nunca con doble-clic.
        Assert.Equal(4, puertas.Count(o => data.Obj(o.ObjIndex)!.Llave != 0));
        // La del templo (49,40): par abierta/cerrada 1411/1412, sin llave.
        var templo = Assert.Single(puertas, o => o.X == 49 && o.Y == 40);
        var info = data.Obj(templo.ObjIndex)!;
        Assert.Equal(0, info.Llave);
        Assert.Equal(1411, info.IndexAbierta);
        Assert.Equal(1412, info.IndexCerrada);
        // Y el tile de la puerta viene BLOQUEADO en el .csm, igual que el de su oeste (la puerta ocupa dos).
        Assert.True(data.IsBlockedTile(1, 49, 40));
        Assert.True(data.IsBlockedTile(1, 48, 40));

        // Casi todo el decorado del mundo es NO agarrable (Agarrable=1, invertido): sin ese gate un `g` sobre
        // un árbol se lo lleva al inventario.
        Assert.True(objs.Count(o => data.Obj(o.ObjIndex) is { IsPickable: false }) >= 170);
    }

    [Fact]
    public void El_fixture_mapa_15_no_tiene_objetos_propios()
    {
        // El mapa del fixture no trae ni un objeto en su .csm: por eso el trabajo de puertas/objetos no puede
        // romper ningún flow. Sus 28 criaturas hostiles SÍ existen ahora (antes este test asertaba que el 15 no
        // estaba en MapSpawns.json — o sea que se apoyaba en que el mapa nunca se transcodificara; era el estado
        // incompleto disfrazado de invariante). Conviven con el fixture porque están lejos del spawn.
        var data = LoadOrSkip();
        Assert.False(data.MapObjects.TryGetValue(15, out var objs) && objs.Count > 0);
        Assert.Equal(28, data.MapSpawns[15].Count);
    }

    [Fact]
    public void La_fauna_de_un_mapa_de_numero_alto_no_se_derrama_en_otro()
    {
        // EL bug que este test existe para que no vuelva: el nº de mapa es short (AO tiene 863), pero el poblado
        // hacía `new WorldPos((byte)mapNum, …)`. El cast truncaba y la fauna de los mapas 271, 527 y 783 caía en
        // el mapa 15 (271 & 255 = 15) — el del fixture —, donde mataba a los personajes de test antes de que
        // pudieran mover un dedo: Rugura llegaba a loguear con 37/100 y moría en menos de un segundo. Estuvo
        // latente todo el tiempo que hubo sólo 7 mapas transcodificados, porque ninguno pasaba de 255.
        var data = LoadOrSkip();
        var world = new World();
        var sys = new NpcSystem(world, data);
        var (placed, maps) = WorldPopulator.SpawnNpcs(sys, data);
        Assert.True(placed > 5000, $"el mundo entero debería poblar miles de NPCs, pobló {placed}");
        Assert.True(maps > 300, $"debería poblar cientos de mapas, pobló {maps}");

        // Hay mapas de número alto de verdad: sin esto el test no probaría nada.
        var altos = data.MapSpawns.Keys.Where(m => m > 255).ToList();
        Assert.True(altos.Count > 100, $"la data real tiene mapas > 255 (hay {altos.Count})");

        // Cada NPC vivo está en un mapa que REALMENTE tiene esa población. Con el (byte) de antes, los NPCs de
        // los mapas altos aparecían en mapNum & 255 y esto fallaba.
        Assert.All(world.Npcs, n => Assert.True(data.MapSpawns.ContainsKey(n.Pos.Map),
            $"un NPC quedó en el mapa {n.Pos.Map}, que no tiene spawns propios: se truncó el nº de mapa"));

        // Y concretamente: el mapa 15 tiene exactamente los suyos, no los del 271/527/783 encima.
        var enElFixture = world.Npcs.Count(n => n.Pos.Map == 15);
        Assert.Equal(data.MapSpawns[15].Count, enElFixture);
        foreach (var alto in new[] { 271, 527, 783 })
            if (data.MapSpawns.ContainsKey(alto))
                Assert.Equal(data.MapSpawns[alto].Count, world.Npcs.Count(n => n.Pos.Map == alto));
    }

    [Fact]
    public void Un_tile_exit_a_un_mapa_de_numero_alto_conserva_su_destino()
    {
        // La otra cara del mismo bug: HandleWalk teletransportaba con `new WorldPos((byte)dest.DestMap, …)`, así
        // que la salida al mapa 555 dejaba al jugador en el 43 (555 & 255). Ya estaba roto ANTES de transcodificar
        // el mundo — los mapas 5 y 11 salen al 555 —, sólo que ningún flow pisaba esa salida.
        var data = LoadOrSkip();
        var altos = data.MapExits.Where(kv => kv.Value.DestMap > 255).ToList();
        Assert.NotEmpty(altos);
        Assert.All(altos, kv =>
        {
            var destino = (short)kv.Value.DestMap;
            Assert.Equal(kv.Value.DestMap, new WorldPos(destino, (byte)kv.Value.DestX, (byte)kv.Value.DestY).Map);
        });
        // El caso concreto: el mapa 5 sale al 555, que NO es el 43.
        Assert.Contains(data.MapExits, kv => kv.Key.Map == 5 && kv.Value.DestMap == 555);
    }

    [Fact]
    public void Abrir_una_puerta_desbloquea_su_tile_y_el_de_su_oeste()
    {
        // VB6 AccionParaPuerta (Acciones.bas:465-470): abrir pone Blocked=0 en (X,Y) Y en (X-1,Y) — la puerta
        // ocupa dos tiles. Cerrar los vuelve a 1. Es el mecanismo que permite entrar a un edificio.
        var data = LoadOrSkip();
        Assert.True(data.IsBlockedTile(1, 49, 40));
        Assert.True(data.IsBlockedTile(1, 48, 40));
        data.SetTileBlocked(1, 49, 40, false);
        data.SetTileBlocked(1, 48, 40, false);
        Assert.False(data.IsBlockedTile(1, 49, 40));
        Assert.False(data.IsBlockedTile(1, 48, 40));
        data.SetTileBlocked(1, 49, 40, true);
        data.SetTileBlocked(1, 48, 40, true);
        Assert.True(data.IsBlockedTile(1, 49, 40));   // vuelve a bloquear el tile que trae el .csm
        Assert.True(data.IsBlockedTile(1, 48, 40));
    }

    [Fact]
    public void Map1_tile_exits_teleport_to_neighbour_maps()
    {
        var data = LoadOrSkip();
        Assert.NotEmpty(data.MapExits);
        // Map 1's exits lead to its neighbours (2/5/8/11 in the decoded set).
        var destMaps = data.MapExits.Where(kv => kv.Key.Map == 1).Select(kv => kv.Value.DestMap).ToHashSet();
        Assert.Contains(2, destMaps);
        Assert.Contains(5, destMaps);
        Assert.Contains(8, destMaps);
        Assert.Contains(11, destMaps);
        // A concrete exit: tile (9,7) on map 1 warps to map 5 (9,93) — the first TE in mapa1.csm.
        Assert.True(data.MapExits.TryGetValue((1, 9, 7), out var dest));
        Assert.Equal((5, 9, 93), dest);
    }

    /// <summary>Los dos efectos visuales de un hechizo son CAMPOS DISTINTOS y el original los emite en líneas
    /// distintas: <c>If FXgrh &lt;&gt; 0</c> manda CreateFX y <c>If Particle &lt;&gt; 0</c> manda
    /// EfectoCharParticula (InfoHechizo, modHechizos.bas:1747 y :1749). Este test fija la data que hace que la
    /// diferencia importe: hay hechizos de una clase, de la otra, y 49 de los 113 son SÓLO partícula — que es
    /// justo el grupo que se veía mal cuando el port emitía un CreateFX por tener partícula.</summary>
    [Fact]
    public void Spell_fx_covers_both_grh_and_particle_effects()
    {
        var data = LoadOrSkip();
        // Paralizar (9): grh FX (FXgrh 8) y ninguna partícula → sólo CreateFX.
        var paralyze = data.Spell(9);
        Assert.NotNull(paralyze);
        Assert.True(paralyze!.FxGrh > 0);
        // Saeta ígnea (6): partícula (35) sin FXgrh → sólo EfectoCharParticula, NADA de CreateFX.
        var arrow = data.Spell(6);
        Assert.NotNull(arrow);
        Assert.Equal(0, arrow!.FxGrh);
        Assert.True(arrow.Particle > 0);
        // El grupo entero: 48 de los 113 hechizos son sólo partícula. Si el emisor volviera a mandarles un
        // CreateFX con fx = 0, serían 48 hechizos que en pantalla no hacen absolutamente nada.
        int soloParticula = data.Spells.Values.Count(s => s.FxGrh == 0 && s.Particle > 0);
        Assert.Equal(48, soloParticula);
        Assert.Equal(113, data.SpellCount);

        // LA CLAVE DEL FX VIENE CON TRES GRAFÍAS en Hechizos.dat: `FXgrh` (35 hechizos), `fxgrh` (11, las once
        // metamorfosis) y `FxGrh` (4: los dos Materializar, Aura sagrada y Arma mágica). El lector del original
        // normaliza con `UCase$`, y el de acá las cubre porque `Candidatos()` compara sin distinguir mayúsculas.
        // Se fija con un caso de cada grafía alternativa: si alguien "simplificara" el lector a la clave exacta,
        // 15 hechizos perderían su FX EN SILENCIO. Aura sagrada es además el que hacía dar 49 en vez de 48 —
        // tiene FX por la grafía rara Y partícula—, y ese error era del script que contaba, no de la data.
        Assert.Equal(30, data.Spell(38)!.FxGrh);   // Metamorfosis: Gólem — clave `fxgrh`
        Assert.Equal(24, data.Spell(70)!.FxGrh);   // Aura sagrada  — clave `FxGrh`
        Assert.True(data.Spell(70)!.Particle > 0); // y con partícula: por eso no es "sólo partícula"
    }

    [Fact]
    public void Npc_hostility_flags_separate_monsters_from_town_services()
    {
        var data = LoadOrSkip();

        // Serpiente (504): a huntable hostile monster — aggros (Hostile && GiveExp>0).
        var snake = data.Npc(504);
        Assert.NotNull(snake);
        Assert.True(snake!.Hostile);
        Assert.True(snake.Attackable);
        Assert.True(snake.GiveExp > 0);

        // Banquero (24): a service NPC — flagged Hostile in the data but GiveExp==0, so the spawn
        // logic (aggro = Hostile && GiveExp>0) keeps it passive. Guards the "no death-trap town" rule.
        var banker = data.Npc(24);
        Assert.NotNull(banker);
        Assert.Equal(0, banker!.GiveExp);
        Assert.False(banker.Hostile && banker.GiveExp > 0);
    }
}
