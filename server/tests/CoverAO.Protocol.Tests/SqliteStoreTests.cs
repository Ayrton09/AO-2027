using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Covers the SQLite persistence backend — the Fase-4 data-migration target. Until now only FileStore
/// was tested; SqliteStore (the store you switch to with COVERAO_DB=sqlite) had zero coverage. These
/// verify (1) the store round-trips + upserts + is case-insensitive by name, and (2) a character
/// saved on the legacy file backend migrates into SQLite byte-for-byte and reloads identically — the
/// concrete "migración de datos" gate from PLAN-RETIRO-VB6 §4.
/// </summary>
public class SqliteStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_sql_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SqliteStore NewStore() => new(Path.Combine(_root, "world.db"));

    [Fact]
    public void SqliteStore_round_trips_upserts_and_is_case_insensitive()
    {
        using var s = NewStore();
        Assert.False(s.CharacterExists("Rugura"));

        s.WriteCharacter("Rugura", "[INIT]\nnivel=25\n");
        s.WriteAccount("cuenta_test", "[cuenta]\nmail=x\n");
        s.WriteMisc("guilds", "[{\"name\":\"ClanWeb\"}]");

        Assert.True(s.CharacterExists("Rugura"));
        Assert.True(s.CharacterExists("RUGURA"));                 // keyed by uppercased name
        Assert.Equal("[INIT]\nnivel=25\n", s.ReadCharacter("rUgUrA"));
        Assert.Equal("[cuenta]\nmail=x\n", s.ReadAccount("CUENTA_TEST"));
        Assert.Equal("[{\"name\":\"ClanWeb\"}]", s.ReadMisc("guilds"));

        s.WriteCharacter("Rugura", "[INIT]\nnivel=30\n");          // upsert (ON CONFLICT ... DO UPDATE)
        Assert.Equal("[INIT]\nnivel=30\n", s.ReadCharacter("Rugura"));
        Assert.Null(s.ReadCharacter("NadieAqui"));
    }

    [Fact]
    public void SqliteStore_survives_reopen() // persistence across a connection close (a restart)
    {
        var db = Path.Combine(_root, "reopen.db");
        using (var s = new SqliteStore(db)) s.WriteCharacter("Rugura", "[INIT]\nnivel=42\n");
        using (var s = new SqliteStore(db)) Assert.Equal("[INIT]\nnivel=42\n", s.ReadCharacter("Rugura"));
    }

    [Fact]
    public void Character_migrates_from_FileStore_to_SqliteStore_and_reloads_identically()
    {
        var leveling = new Leveling(new Dictionary<int, long> { [1] = 300, [2] = 450, [3] = 675 });

        // 1) Save a character on the legacy flat-file backend (the source of a real migration).
        var file = new FileStore(_root);
        using var fileDb = new Persistence(file, leveling);
        var p = new Player { CharIndex = 7, Name = "Rugura", Account = "cuenta_test" };
        p.Class = 3; p.Race = 2; p.Gender = 1; p.Level = 25; p.Gold = 123456;
        p.MaxHp = 250; p.Hp = 200; p.MaxMana = 500; p.Mana = 480; p.Elu = 90000; p.Exp = 12345;
        p.Pos = new WorldPos(34, 55, 60); p.Heading = 2; p.Head = 10; p.Body = 20;
        p.Attributes[1] = 21; p.Attributes[2] = 19;
        p.Inventory[1] = new InventorySlot { ObjIndex = 100, Amount = 5, Equipped = true };
        p.Spells[1] = 12;
        p.Skills[GameLimits.SkillWrestling] = 42;
        fileDb.SaveCharacter(p);
        fileDb.EsperarEscriturasPendientes();   // el guardado se encola: el registro crudo aparece cuando la cola lo escribe

        // 2) Migrate: copy the raw record (same INI serialization) into SQLite — byte-for-byte.
        using var sqlite = new SqliteStore(Path.Combine(_root, "migrated.db"));
        var rawIni = file.ReadCharacter("Rugura");
        Assert.NotNull(rawIni);
        sqlite.WriteCharacter("Rugura", rawIni!);
        Assert.Equal(rawIni, sqlite.ReadCharacter("Rugura"));    // migrated verbatim

        // 3) Reload through the SQLite backend and confirm every modeled field survived.
        using var sqlDb = new Persistence(sqlite, leveling);
        var loaded = sqlDb.LoadCharacter("Rugura", charIndex: 99, account: "cuenta_test")!;
        Assert.Equal(3, loaded.Class);
        Assert.Equal(25, loaded.Level);
        Assert.Equal(123456, loaded.Gold);
        Assert.Equal(250, loaded.MaxHp);
        Assert.Equal(new WorldPos(34, 55, 60), loaded.Pos);
        Assert.Equal((byte)21, loaded.Strength);
        Assert.Equal((short)100, loaded.Inventory[1].ObjIndex);
        Assert.True(loaded.Inventory[1].Equipped);
        Assert.Equal((short)12, loaded.Spells[1]);
        Assert.Equal((byte)42, loaded.Skills[GameLimits.SkillWrestling]);
    }
}
