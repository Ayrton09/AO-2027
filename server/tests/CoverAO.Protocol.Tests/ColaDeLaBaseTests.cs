using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA COLA DE LA BASE (<see cref="ColaDeLaBase"/>) y lo que la persistencia hace encima: un solo escritor, en orden, y los
/// cambios a una cuenta adentro del escritor. Ver fase5/postgres/DISENO.md, sección 3.
/// </summary>
public class ColaDeLaBaseTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_cola_" + Guid.NewGuid().ToString("N"));
    private readonly FileStore _store;
    private readonly Persistence _db;

    public ColaDeLaBaseTests()
    {
        _store = new FileStore(_raiz);
        _db = new Persistence(_store, new Leveling(new Dictionary<int, long> { [1] = 300, [2] = 450 }));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }

    /// <summary>La copia que viaja a la cola tiene que llevar TODO: un campo nuevo de la cuenta que no entre en
    /// <see cref="Account.Copia"/> se guardaría con su valor por defecto, en silencio, cada vez.</summary>
    [Fact]
    public void La_copia_de_la_cuenta_lleva_todos_los_campos()
    {
        // Las que se pueden escribir: una calculada (CodigoLegado) sale de las otras y se copia sola.
        var propiedades = typeof(Account).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToArray();
        var original = new Account { Name = "Cuenta" };
        foreach (var prop in propiedades)
        {
            if (prop.Name == nameof(Account.Name)) continue;
            if (prop.GetValue(original) is List<string> lista) { lista.Add("Pj" + prop.Name); continue; }
            object valor = prop.PropertyType == typeof(string) ? "v" + prop.Name
                : prop.PropertyType == typeof(bool) ? true
                : prop.PropertyType == typeof(int) ? 7
                : prop.PropertyType == typeof(DateTime?) ? new DateTime(2026, 9, 28, 13, 15, 0, DateTimeKind.Utc)
                : throw new InvalidOperationException(
                    $"Account.{prop.Name} es {prop.PropertyType.Name}: este test no sabe llenarlo, hay que enseñarle");
            prop.SetValue(original, valor);
        }

        var copia = original.Copia();

        foreach (var prop in propiedades)
        {
            object? a = prop.GetValue(original), b = prop.GetValue(copia);
            if (a is List<string> la)
            {
                Assert.Equal(la, (List<string>)b!);
                Assert.NotSame(la, b);   // la lista viva puede cambiar mientras la cola guarda
            }
            else Assert.True(Equals(a, b), $"Account.Copia no copia {prop.Name}");
        }
    }

    /// <summary>El orden es la garantía: una lectura ve lo que se pidió escribir antes, aunque la escritura no se haya
    /// esperado. Es lo que hace que el que sale y vuelve a entrar lea lo que se guardó al salir.</summary>
    [Fact]
    public async Task La_lectura_ve_lo_que_se_encolo_antes()
    {
        for (int oro = 1; oro <= 50; oro++)
            _db.SaveCharacter(new Player { CharIndex = 1, Name = "Viajero", Account = "c", Gold = oro });

        var ficha = await _db.LeerPersonajeAsync("Viajero");

        Assert.NotNull(ficha);
        Assert.Equal(50, ficha!.CrearPlayer(1, "c").Gold);
    }

    /// <summary>Un pedido que falla se anota y la cola sigue: un personaje que no se pudo guardar no frena a los demás.</summary>
    [Fact]
    public async Task Un_pedido_que_falla_no_frena_a_los_que_siguen()
    {
        using var cola = new ColaDeLaBase(new AlmacenDeTexto(_store));
        cola.Escribir("una escritura que falla", _ => throw new IOException("disco lleno"));
        var fallida = cola.PedirAsync<int>("una lectura que falla", _ => throw new IOException("disco lleno"));

        Assert.Equal(42, await cola.PedirAsync("la que sigue", _ => 42));
        await Assert.ThrowsAsync<IOException>(() => fallida);
    }

    /// <summary>Cerrar la cola espera lo encolado: el apagado depende de esto —el hilo de la cola es de fondo, y lo que
    /// quedara adentro moriría con el proceso—.</summary>
    [Fact]
    public void Cerrar_la_cola_escribe_lo_que_quedaba()
    {
        for (int i = 0; i < 20; i++)
            _db.SaveCharacter(new Player { CharIndex = 1, Name = "Pj" + (char)('A' + i), Account = "c" });

        _db.Dispose();

        for (int i = 0; i < 20; i++)
            Assert.NotNull(_store.ReadCharacter("Pj" + (char)('A' + i)));
    }

    /// <summary>
    /// UN CAMBIO A LA CUENTA NO PISA LO QUE CAMBIÓ EN EL MEDIO. El login lee la cuenta, verifica la contraseña (25 ms de
    /// PBKDF2) y, si el hash era viejo, guarda el migrado. Guardaba la cuenta leída ENTERA: un baneo del panel que cayera
    /// en esos milisegundos se perdía, y el baneado seguía entrando.
    /// </summary>
    [Fact]
    public void El_hash_migrado_no_pisa_un_baneo_del_medio()
    {
        var vieja = new Account { Name = "Legado", Salt = "abcdefghij" };
        vieja.PasswordHash = PasswordHasher.Hash("clave", vieja.Salt);
        _db.SaveAccount(vieja);

        var leida = _db.LoadAccount("Legado")!;                 // el login la lee...
        _db.CambiarCuenta("Legado", c => c.Banned = true);     // ...el panel la banea...
        Assert.True(leida.VerifyPassword("clave", out bool migrado));
        Assert.True(migrado);
        _db.GuardarClaveMigrada(leida);                         // ...y el login guarda el hash migrado

        var guardada = _db.LoadAccount("Legado")!;
        Assert.True(guardada.Banned);
        Assert.True(PasswordHasher.IsModern(guardada.PasswordHash));
        Assert.True(guardada.VerifyPassword("clave"));
    }

    /// <summary>Si la contraseña se cambió mientras tanto, el hash migrado de la vieja no la pisa.</summary>
    [Fact]
    public void El_hash_migrado_no_pisa_una_clave_cambiada()
    {
        var vieja = new Account { Name = "Legado2", Salt = "abcdefghij" };
        vieja.PasswordHash = PasswordHasher.Hash("vieja", vieja.Salt);
        _db.SaveAccount(vieja);

        var leida = _db.LoadAccount("Legado2")!;
        string nueva = PasswordHasher.HashModern("nueva");
        _db.CambiarCuenta("Legado2", c => { c.PasswordHash = nueva; c.Salt = ""; });
        Assert.True(leida.VerifyPassword("vieja", out _));
        _db.GuardarClaveMigrada(leida);

        var guardada = _db.LoadAccount("Legado2")!;
        Assert.True(guardada.VerifyPassword("nueva"));
        Assert.False(guardada.VerifyPassword("vieja"));
    }

    /// <summary>El alta de un personaje lo suma a la cuenta GUARDADA: la que trae el que crea se leyó al verificar la
    /// contraseña, y guardarla entera deshacía un baneo del medio.</summary>
    [Fact]
    public void El_alta_de_un_personaje_no_pisa_lo_que_cambio_en_la_cuenta()
    {
        _db.CreateAccount("Creadora", "clave");
        var leida = _db.LoadAccount("Creadora")!;
        _db.CambiarCuenta("Creadora", c => c.Banned = true);

        _db.CreateCharacter(leida, "Nuevito", 1, race: 1, gender: 1, @class: 1, homeland: 1, head: 1);

        var guardada = _db.LoadAccount("Creadora")!;
        Assert.True(guardada.Banned);
        Assert.Contains("Nuevito", guardada.Characters);
    }

    /// <summary>Dos altas simultáneas del mismo nombre: una sola cuenta. El "¿existe?" y el "crear" iban separados, y la
    /// segunda pisaba a la primera —con otra contraseña—.</summary>
    [Fact]
    public void Dos_altas_simultaneas_del_mismo_nombre_crean_una_sola_cuenta()
    {
        using var largada = new Barrier(8);
        var resultados = new Account?[8];
        var hilos = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            largada.SignalAndWait();
            resultados[i] = _db.CrearCuentaSiNoExiste("Disputada", "clave" + i);
        })).ToList();
        hilos.ForEach(h => h.Start());
        hilos.ForEach(h => h.Join());

        var ganadora = Assert.Single(resultados, r => r is not null);
        var guardada = _db.LoadAccount("Disputada")!;
        Assert.Equal(ganadora!.PasswordHash, guardada.PasswordHash);
    }
}
