using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_test_" + Guid.NewGuid().ToString("N"));
    private readonly Persistence _db;

    public PersistenceTests()
    {
        var leveling = new Leveling(new Dictionary<int, long> { [1] = 300, [2] = 450, [3] = 675 });
        _db = new Persistence(new FileStore(_root), leveling);
    }
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void CharacterRoundTripsAllModeledFields()
    {
        var p = new Player { CharIndex = 7, Name = "Rugura", Account = "cuenta_test" };
        p.Class = 3; p.Race = 2; p.Gender = 1; p.Level = 25; p.Gold = 123456;
        p.MaxHp = 250; p.Hp = 200; p.MaxMana = 500; p.Mana = 480; p.Elu = 90000; p.Exp = 12345;
        p.Pos = new WorldPos(34, 55, 60); p.Heading = 2; p.Head = 10; p.Body = 20;
        p.Attributes[1] = 21; p.Attributes[2] = 19;
        p.Inventory[1] = new InventorySlot { ObjIndex = 100, Amount = 5, Equipped = true };
        p.Inventory[5] = new InventorySlot { ObjIndex = 88, Amount = 1, Equipped = false };
        p.Spells[1] = 12; p.Spells[2] = 30;
        p.Skills[GameLimits.SkillWrestling] = 42; p.Skills[GameLimits.SkillTacticas] = 33;

        _db.SaveCharacter(p);
        var loaded = _db.LoadCharacter("Rugura", charIndex: 99, account: "cuenta_test")!;

        Assert.Equal(3, loaded.Class);
        Assert.Equal(25, loaded.Level);
        Assert.Equal(123456, loaded.Gold);
        Assert.Equal(250, loaded.MaxHp);
        Assert.Equal(200, loaded.Hp);
        Assert.Equal(90000, loaded.Elu);
        Assert.Equal(new WorldPos(34, 55, 60), loaded.Pos);
        Assert.Equal((byte)21, loaded.Strength);
        Assert.Equal((byte)19, loaded.Agility);
        Assert.Equal((short)100, loaded.Inventory[1].ObjIndex);
        Assert.Equal((short)5, loaded.Inventory[1].Amount);
        Assert.True(loaded.Inventory[1].Equipped);
        Assert.Equal((short)88, loaded.Inventory[5].ObjIndex);
        Assert.Equal((short)12, loaded.Spells[1]);
        Assert.Equal((short)30, loaded.Spells[2]);
        Assert.Equal((byte)42, loaded.Skills[GameLimits.SkillWrestling]);
        Assert.Equal((byte)33, loaded.Skills[GameLimits.SkillTacticas]);
        Assert.Equal((short)99, loaded.CharIndex); // charIndex is assigned at load, not from file
    }

    [Fact]
    public void CharacterFilenameIsUppercase()
    {
        var p = new Player { CharIndex = 1, Name = "MiXeDcAsE", Account = "acc" };
        _db.SaveCharacter(p);
        _db.EsperarEscriturasPendientes();   // el guardado se encola: el archivo aparece cuando la cola lo escribe
        Assert.True(File.Exists(Path.Combine(_root, "Charfile", "MIXEDCASE.chr")));
        Assert.NotNull(_db.LoadCharacter("mixedcase", 1, "acc")); // case-insensitive lookup
    }

    [Fact]
    public void AccountPasswordVerifies()
    {
        var acc = _db.CreateAccount("cuenta_test", "secreto123");
        Assert.True(acc.VerifyPassword("secreto123"));
        Assert.False(acc.VerifyPassword("wrong"));
        Assert.True(PasswordHasher.IsModern(acc.PasswordHash)); // new accounts use PBKDF2 (salt embedded)
        Assert.Equal("", acc.Salt);

        var reloaded = _db.LoadAccount("cuenta_test")!;
        Assert.True(reloaded.VerifyPassword("secreto123"));
        Assert.True(PasswordHasher.IsModern(reloaded.PasswordHash));
    }

    [Fact]
    public void SqliteBackendRoundTripsAccountAndCharacter()
    {
        var leveling = new Leveling(new Dictionary<int, long> { [1] = 300 });
        using var store = new SqliteStore(Path.Combine(_root, "test.db"));
        var db = new Persistence(store, leveling);

        var acc = db.CreateAccount("cuenta", "secreto");
        db.CreateCharacter(acc, "Hero", charIndex: 1, race: 1, gender: 1, @class: 3, homeland: 1, head: 7);

        Assert.True(db.CharacterExists("Hero"));
        var loaded = db.LoadCharacter("Hero", charIndex: 9, account: "cuenta")!;
        Assert.Equal("Hero", loaded.Name);
        Assert.Equal(3, loaded.Class);
        // El oro del fixture (sin kit newbie). Subió a 5000 cuando el correo pasó a cobrar 1750 por carta:
        // lo que este test verifica es que el valor SOBREVIVE al round-trip por SQLite, no el número en sí.
        Assert.Equal(5000, loaded.Gold);
        Assert.True(db.LoadAccount("cuenta")!.VerifyPassword("secreto"));
        Assert.Equal("Hero", db.LoadSummary("Hero")!.Name);
    }

    [Fact]
    public void SeedCreatesAccountWithCharacter()
    {
        _db.SeedDemoIfEmpty();
        var acc = _db.LoadAccount("cuenta_test")!;
        Assert.True(acc.VerifyPassword("hash_password"));
        Assert.Contains("Rugura", acc.Characters);
        var summary = _db.LoadSummary("Rugura")!;
        Assert.Equal("Rugura", summary.Name);
    }

    /// <summary>Sin --fixture, la cuenta demo deja de entrar con la clave que está en el código; el fixture se la devuelve.</summary>
    [Fact]
    public void Sin_fixture_la_cuenta_demo_no_entra_con_su_clave()
    {
        _db.SeedDemoIfEmpty();

        Assert.True(_db.CerrarLaCuentaDemo());
        Assert.False(_db.LoadAccount("cuenta_test")!.VerifyPassword("hash_password"));
        Assert.Contains("Rugura", _db.LoadAccount("cuenta_test")!.Characters);

        _db.SeedDemoIfEmpty();
        Assert.True(_db.LoadAccount("cuenta_test")!.VerifyPassword("hash_password"));
    }

    /// <summary>Una cuenta_test con otra clave es de alguien: no se toca.</summary>
    [Fact]
    public void Una_cuenta_test_con_otra_clave_no_se_cierra()
    {
        _db.CreateAccount("cuenta_test", "otra_clave");

        Assert.False(_db.CerrarLaCuentaDemo());
        Assert.True(_db.LoadAccount("cuenta_test")!.VerifyPassword("otra_clave"));
    }
}
