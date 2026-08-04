using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Las piezas de estado que agregaron las tandas 9 a 13 de comandos GM: los campos de MapInfo que no se
/// cargan de ningún archivo, el trigger por tile (que además tiene que MOVER el tile de conjunto) y la cola
/// de consultas a los GMs. Los flows ya prueban el protocolo de punta a punta contra un server vivo; esto
/// fija la semántica de las estructuras, que es donde vive lo fácil de romper sin que se note.
/// </summary>
public class MapInfoAndHelpQueueTests
{
    // --- MapInfo: los flags de /MODMAPINFO ---

    [Fact]
    public void Map_flags_default_off_and_toggle()
    {
        var data = new GameData();
        const int map = 15;
        Assert.False(data.MapNoMagic(map));
        Assert.False(data.MapNoInvi(map));
        Assert.False(data.MapNoResu(map));
        Assert.False(data.MapBackup(map));

        data.SetMapNoMagic(map, true);
        data.SetMapNoInvi(map, true);
        data.SetMapNoResu(map, true);
        data.SetMapBackup(map, true);
        Assert.True(data.MapNoMagic(map));
        Assert.True(data.MapNoInvi(map));
        Assert.True(data.MapNoResu(map));
        Assert.True(data.MapBackup(map));

        // Y son POR MAPA: prenderlos en el 15 no toca al 16.
        Assert.False(data.MapNoMagic(16));
        Assert.False(data.MapNoResu(16));

        data.SetMapNoMagic(map, false);
        Assert.False(data.MapNoMagic(map));
    }

    [Fact]
    public void Map_zone_and_terrain_have_the_defaults_of_the_dat()
    {
        var data = new GameData();
        // Sin MapInfo.json cargado: "CAMPO" para la zona y "BOSQUE" para el terreno, que es lo que trae el
        // .dat de casi todos los mapas (el terreno no lo extrajo el transcode, así que arranca fijo).
        Assert.Equal("CAMPO", data.MapZone(15));
        Assert.Equal("BOSQUE", data.MapTerrain(15));
        Assert.Equal("NO", data.MapRestrict(15));

        data.SetMapZone(15, "DUNGEON");
        data.SetMapTerrain(15, "NIEVE");
        data.SetMapRestrict(15, "ARMADA");
        Assert.Equal("DUNGEON", data.MapZone(15));
        Assert.Equal("NIEVE", data.MapTerrain(15));
        Assert.Equal("ARMADA", data.MapRestrict(15));
        Assert.Equal("CAMPO", data.MapZone(16));   // por mapa
    }

    // --- El trigger por tile (/TRIGGER) ---

    [Fact]
    public void Tile_trigger_is_zero_by_default()
    {
        var data = new GameData();
        Assert.Equal(0, data.TileTrigger(15, 50, 50));
    }

    [Theory]
    [InlineData((byte)1)]   // BAJOTECHO
    [InlineData((byte)2)]   // trigger_2
    [InlineData((byte)3)]   // POSINVALIDA
    [InlineData((byte)4)]   // ZONASEGURA
    [InlineData((byte)5)]   // ANTIPIQUETE
    [InlineData((byte)6)]   // ZONAPELEA
    public void Set_tile_trigger_round_trips_every_value_of_the_enum(byte trigger)
    {
        var data = new GameData();
        data.SetTileTrigger(15, 50, 50, trigger);
        Assert.Equal(trigger, data.TileTrigger(15, 50, 50));
        Assert.Equal(0, data.TileTrigger(15, 51, 50));   // sólo ese tile
    }

    [Fact]
    public void Set_tile_trigger_moves_the_tile_between_sets_so_it_has_effect()
    {
        // Lo importante de /TRIGGER no es recordar el número: el trigger ES el dato que consultan los gates.
        var data = new GameData();
        const int map = 15;
        Assert.False(data.IsSafeZone(map, 50, 50));
        Assert.False(data.IsArena(map, 50, 50));
        Assert.False(data.IsNpcInvalidTile(map, 50, 50));

        data.SetTileTrigger(map, 50, 50, 4);            // ZONASEGURA
        Assert.True(data.IsSafeZone(map, 50, 50));
        Assert.False(data.IsArena(map, 50, 50));

        data.SetTileTrigger(map, 50, 50, 6);            // pasa a ZONAPELEA: deja de ser segura
        Assert.True(data.IsArena(map, 50, 50));
        Assert.False(data.IsSafeZone(map, 50, 50));

        data.SetTileTrigger(map, 50, 50, 3);            // POSINVALIDA
        Assert.True(data.IsNpcInvalidTile(map, 50, 50));
        Assert.False(data.IsArena(map, 50, 50));

        data.SetTileTrigger(map, 50, 50, 0);            // Nada: sale de los tres
        Assert.False(data.IsSafeZone(map, 50, 50));
        Assert.False(data.IsArena(map, 50, 50));
        Assert.False(data.IsNpcInvalidTile(map, 50, 50));
        Assert.Equal(0, data.TileTrigger(map, 50, 50));
    }

    [Fact]
    public void Set_tile_trigger_accepts_values_outside_the_enum()
    {
        // El `If tTrigger >= 0` del original no filtra nada (viene de un Byte): cualquier valor entra y los
        // gates simplemente no lo reconocen. Se porta igual.
        var data = new GameData();
        data.SetTileTrigger(15, 50, 50, 200);
        Assert.Equal(200, data.TileTrigger(15, 50, 50));
        Assert.False(data.IsSafeZone(15, 50, 50));
        Assert.False(data.IsArena(15, 50, 50));
    }

    // --- La cola de consultas a los GMs (VB6 `Ayuda`) ---

    [Fact]
    public void Help_queue_starts_empty()
    {
        var world = new World();
        Assert.Empty(world.HelpEntries);
        Assert.False(world.HelpExists("Merlin"));
        Assert.False(world.HelpRemove("Merlin"));
    }

    [Fact]
    public void Help_entry_has_the_format_of_the_original()
    {
        var world = new World();
        world.HelpPush("Merlin", 5);
        var entry = Assert.Single(world.HelpEntries);
        // ModCola.Push: "[" & UCase$(Nombre) & "]" & "-" & Time$ & "-" & CStr(Tipo) & "="
        Assert.StartsWith("[MERLIN]-", entry);
        Assert.EndsWith("-5=", entry);
        Assert.Matches(@"^\[MERLIN\]-\d{2}:\d{2}:\d{2}-5=$", entry);
    }

    [Fact]
    public void Help_queue_dedupes_by_name_case_insensitively()
    {
        // En el VB6 vivo `Existe` está roto (compara contra mid$(elemento,10,…), que cae en la hora) así que
        // el dedupe nunca dispara; acá funciona, y es una divergencia deliberada documentada en fase5 §4k.
        var world = new World();
        world.HelpPush("Merlin", 5);
        Assert.True(world.HelpExists("merlin"));
        Assert.True(world.HelpExists("MERLIN"));
        Assert.False(world.HelpExists("Marino"));
    }

    [Fact]
    public void Help_queue_keeps_arrival_order_and_removes_only_one()
    {
        var world = new World();
        world.HelpPush("Merlin", 5);
        world.HelpPush("Marino", 3);
        Assert.Equal(2, world.HelpEntries.Count);
        Assert.StartsWith("[MERLIN]-", world.HelpEntries[0]);
        Assert.StartsWith("[MARINO]-", world.HelpEntries[1]);

        Assert.True(world.HelpRemove("merlin"));         // case-insensitive, como el UCase$ del original
        var left = Assert.Single(world.HelpEntries);
        Assert.StartsWith("[MARINO]-", left);
        Assert.False(world.HelpExists("Merlin"));
        Assert.False(world.HelpRemove("Merlin"));        // ya no está: segundo intento es no-op
    }

    [Fact]
    public void Help_clear_empties_the_queue_and_reports_how_many()
    {
        var world = new World();
        world.HelpPush("Merlin", 5);
        world.HelpPush("Marino", 3);
        Assert.Equal(2, world.HelpClear());
        Assert.Empty(world.HelpEntries);
        Assert.Equal(0, world.HelpClear());
    }

    // --- La cuenta regresiva ---

    [Fact]
    public void Countdown_starts_at_zero_and_holds_what_the_command_sets()
    {
        var world = new World();
        Assert.Equal(0, world.CountdownRemaining);
        Assert.Equal(0, world.CountdownMap);
        world.CountdownRemaining = 3;
        world.CountdownMap = 15;
        Assert.Equal(3, world.CountdownRemaining);
        Assert.Equal(15, world.CountdownMap);
    }
}
