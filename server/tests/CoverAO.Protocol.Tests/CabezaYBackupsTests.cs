using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Dos cosas que se guardaban mal y sólo se notaban DESPUÉS: una cabeza cambiada que volvía sola a la de
/// antes, y una retención de backups que valía la mitad de lo configurado. Las dos son de la familia más
/// difícil de ver — no fallan cuando pasan, fallan cuando uno vuelve a mirar.
/// </summary>
public class CabezaYBackupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coverao-cb-" + Guid.NewGuid().ToString("N")[..8]);
    public CabezaYBackupsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Player Personaje() => new()
    {
        CharIndex = 1, Name = "Rugura", Account = "cuenta_test",
        Class = 1, Race = 1, Gender = 1, Level = 5, MaxHp = 100, Hp = 100,
    };

    [Fact]
    public void La_cabeza_cambiada_sobrevive_al_guardado()
    {
        // El guardado prefería SIEMPRE la cabeza "viva" (OrigHead) cuando había una, y OrigHead queda cargada
        // para siempre después de la primera muerte. Resultado: a cualquiera que hubiera muerto una vez,
        // `/MOD <nick> 4 <cabeza>` le cambiaba la cabeza en pantalla y el .chr seguía con la vieja.
        // La regla del original (FileIO.bas:1719) es al revés: guarda la cabeza ACTUAL, y sólo cae en
        // OrigChar.Head cuando la actual no vale.
        var p = Personaje();
        p.OrigHead = 10;      // murió alguna vez: la cabeza viva quedó anotada
        p.Head = 77;          // y ahora un GM se la cambió
        string path = Path.Combine(_dir, "RUGURA.chr");
        CharacterFile.Save(p, path);

        var vuelto = CharacterFile.Load(path, 1, "Rugura", "cuenta_test");
        Assert.Equal(77, vuelto.Head);
    }

    [Fact]
    public void Un_muerto_guarda_la_cabeza_VIVA_no_la_de_cadaver()
    {
        // El otro lado de la misma moneda, que es por lo que la regla existía: si uno se desconecta muerto,
        // el .chr no puede quedar con la cabeza de fantasma o sería permanente.
        var p = Personaje();
        p.OrigHead = 10;
        p.Head = 500;         // cabeza de cadáver
        p.Hp = 0;
        string path = Path.Combine(_dir, "MUERTO.chr");
        CharacterFile.Save(p, path);

        var vuelto = CharacterFile.Load(path, 1, "Muerto", "cuenta_test");
        Assert.Equal(10, vuelto.Head);
    }

    [Fact]
    public void Un_backup_de_SQLite_con_sueltos_cuenta_como_UNO_solo()
    {
        // Con SQLite, `Crear` deja DOS entradas por backup: el .db y una carpeta al lado con los archivos
        // sueltos de data/. Las dos empiezan con `coverao-`, así que la poda las contaba por separado:
        // configurar "conservar 3" guardaba un backup y medio, y el más viejo quedaba a medio borrar.
        string dataRoot = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "banned-ips.txt"), "1.2.3.4");
        string backups = Path.Combine(_dir, "backups");

        using (var store = new SqliteStore(Path.Combine(dataRoot, "vivo.db")))
        {
            store.WriteCharacter("Rugura", "[FLAGS]\r\n");
            for (int d = 1; d <= 4; d++)
                Backups.Crear(new AlmacenDeTexto(store), backups, new DateTime(2026, 7, d, 12, 0, 0), dataRoot);
        }

        Assert.Equal(4, Backups.Listar(backups).Length);          // cuatro backups, no ocho entradas
        Assert.Equal(2, Backups.Podar(backups, conservar: 2));     // y podar dos deja DOS de verdad
        var quedan = Backups.Listar(backups).Select(Path.GetFileNameWithoutExtension).ToArray();
        Assert.Equal(new[] { "coverao-20260704-120000", "coverao-20260703-120000" }, quedan);
        // Del podado no puede quedar ni la mitad: ni el .db ni la carpeta de sueltos.
        Assert.False(File.Exists(Path.Combine(backups, "coverao-20260701-120000.db")));
        Assert.False(Directory.Exists(Path.Combine(backups, "coverao-20260701-120000")));
    }
}
