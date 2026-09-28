using System.Collections.Immutable;
using System.Reflection;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// FICHAS PARA PROBAR UN ALMACÉN: una con TODOS los campos distintos de su valor por defecto y distintos entre sí. Un campo
/// que un almacén no guarde vuelve con su default; dos columnas cruzadas vuelven cambiadas. Las dos cosas las ve una
/// comparación campo por campo (<see cref="AssertIgual"/>).
/// </summary>
internal static class FichasDePrueba
{
    public static FichaDePersonaje Llena(string nombre, int semilla = 1)
    {
        var ficha = new FichaDePersonaje { Nombre = nombre };
        int n = semilla * 3;
        bool b = semilla % 2 == 0;
        foreach (var prop in typeof(FichaDePersonaje).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == nameof(FichaDePersonaje.Nombre)) continue;
            object? porDefecto = prop.GetValue(ficha);
            object valor = prop.PropertyType switch
            {
                var t when t == typeof(byte) => (byte)(n++ % 250 + 1),
                var t when t == typeof(short) => (short)(100 + n++),
                var t when t == typeof(int) => 1000 + n++,
                var t when t == typeof(long) => 100_000L + n++,
                var t when t == typeof(double) => 123_456.25 + n++,
                var t when t == typeof(bool) => b = !b,
                var t when t == typeof(string) => $"v{prop.Name}{semilla}",
                var t when t == typeof(WorldPos) => new WorldPos((short)(7 + semilla), (byte)(11 + semilla), (byte)(13 + semilla)),
                var t when t == typeof(ImmutableArray<byte>) => Llenar((ImmutableArray<byte>)porDefecto!, i => (byte)((i * 7 + semilla) % 250 + 1)),
                var t when t == typeof(ImmutableArray<short>) => Llenar((ImmutableArray<short>)porDefecto!, i => (short)(i * 11 + semilla)),
                var t when t == typeof(ImmutableArray<string>) => ImmutableArray.Create($"{prop.Name}A{semilla}", $"{prop.Name}B{semilla}"),
                // La bóveda no guarda "equipado" —no significa nada ahí, y el .chr no lo escribe—: va en false.
                var t when t == typeof(ImmutableArray<RanuraGuardada>) => Llenar((ImmutableArray<RanuraGuardada>)porDefecto!,
                    i => new RanuraGuardada((short)(i * 13 + semilla), (short)(i + semilla),
                        prop.Name != nameof(FichaDePersonaje.Boveda) && (i + semilla) % 2 == 0)),
                var t when t == typeof(FichaDeFamiliar) => new FichaDeFamiliar((byte)(3 + semilla), $"Firulais{semilla}",
                    (byte)(4 + semilla), 5000 + semilla, 6000 + semilla, 70 + semilla, 80 + semilla, 9 + semilla, 10 + semilla,
                    semilla % 2 == 1, ImmutableArray.Create(11 + semilla, 12 + semilla, 13 + semilla, 14 + semilla)),
                _ => throw new InvalidOperationException(
                    $"FichaDePersonaje.{prop.Name} es {prop.PropertyType.Name}: este test no sabe llenarlo, hay que enseñarle"),
            };
            prop.SetValue(ficha, valor);
        }
        return ficha;
    }

    /// <summary>El índice 0 no se usa (convención del <see cref="Player"/>): queda en su default, como lo deja todo almacén.</summary>
    private static ImmutableArray<T> Llenar<T>(ImmutableArray<T> molde, Func<int, T> valor)
    {
        var arreglo = new T[molde.Length];
        for (int i = 1; i < arreglo.Length; i++) arreglo[i] = valor(i);
        return ImmutableArray.Create(arreglo);
    }

    /// <summary>Campo por campo, con el nombre del campo en el mensaje. El nombre del personaje, sin mayúsculas: los
    /// archivos lo devuelven como se lo pidió.</summary>
    public static void AssertIgual(FichaDePersonaje esperada, FichaDePersonaje leida)
    {
        foreach (var prop in typeof(FichaDePersonaje).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? a = prop.GetValue(esperada), b = prop.GetValue(leida);
            switch (a)
            {
                case string s when prop.Name == nameof(FichaDePersonaje.Nombre):
                    Assert.True(string.Equals(s, (string)b!, StringComparison.OrdinalIgnoreCase), $"{prop.Name}: {s} / {b}");
                    break;
                case ImmutableArray<byte> x: Assert.True(x.SequenceEqual((ImmutableArray<byte>)b!), prop.Name); break;
                case ImmutableArray<short> x: Assert.True(x.SequenceEqual((ImmutableArray<short>)b!), prop.Name); break;
                case ImmutableArray<string> x: Assert.True(x.SequenceEqual((ImmutableArray<string>)b!), prop.Name); break;
                case ImmutableArray<RanuraGuardada> x: Assert.True(x.SequenceEqual((ImmutableArray<RanuraGuardada>)b!), prop.Name); break;
                case FichaDeFamiliar f:
                    var g = Assert.IsType<FichaDeFamiliar>(b);
                    Assert.Equal(f with { Habilidades = default }, g with { Habilidades = default });
                    Assert.True(f.Habilidades.SequenceEqual(g.Habilidades), "Familiar.Habilidades");
                    break;
                default:
                    Assert.True(Equals(a, b), $"{prop.Name}: {a} / {b}");
                    break;
            }
        }
    }
}

/// <summary>
/// LA BATERÍA DE CONTRATO (fase5/postgres/DISENO.md, sección 9): una sola lista de pruebas que corre contra TODOS los
/// almacenes. Es lo que impide que los motores se separen —un campo que uno guarda y el otro no, un tope que uno mira y el
/// otro no—.
/// </summary>
public abstract class ContratoDelAlmacen : IDisposable
{
    protected abstract IAlmacen Almacen { get; }
    public abstract void Dispose();

    private static Account Cuenta(string nombre) => new() { Name = nombre, PasswordHash = "pbkdf2$1$a$b", Salt = "" };

    private Account CuentaGuardada(string nombre)
    {
        var cuenta = Cuenta(nombre);
        Almacen.GuardarCuenta(cuenta);
        return cuenta;
    }

    [Fact]
    public void Una_ficha_con_todos_los_campos_se_lee_igual()
    {
        var cuenta = CuentaGuardada("Duena");
        var ficha = FichasDePrueba.Llena("Completo");

        Assert.True(Almacen.CrearPersonaje(cuenta, ficha));
        FichasDePrueba.AssertIgual(ficha, Almacen.LeerPersonaje("Completo")!);

        // Y un guardado con OTROS valores pisa todo, también lo de las ranuras.
        var otra = FichasDePrueba.Llena("Completo", semilla: 2);
        Almacen.GuardarPersonaje(otra);
        FichasDePrueba.AssertIgual(otra, Almacen.LeerPersonaje("completo")!);
    }

    /// <summary>Guardar MENOS saca lo que ya no está: la ranura que se vació, el amigo que se borró, el familiar que no hay.
    /// Reescribir sólo lo ocupado dejaría vivo lo del guardado anterior.</summary>
    [Fact]
    public void Un_guardado_con_menos_cosas_no_deja_las_viejas()
    {
        var cuenta = CuentaGuardada("Duena");
        Assert.True(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Menguante")));

        var vacia = new FichaDePersonaje { Nombre = "Menguante" };
        Almacen.GuardarPersonaje(vacia);

        FichasDePrueba.AssertIgual(vacia, Almacen.LeerPersonaje("Menguante")!);
    }

    [Fact]
    public void La_cuenta_se_lee_igual_y_sin_mayusculas()
    {
        var cuenta = new Account
        {
            Name = "Cuentita", PasswordHash = "pbkdf2$9$sal$hash", Salt = "viejo", UserCodeHash = "pbkdf2$9$c$h",
            Banned = true, EsBot = true, Trampas = 4, Donador = true,
            UltimoIngreso = new DateTime(2026, 9, 28, 13, 15, 0, DateTimeKind.Utc), UltimaIp = "10.1.2.3",
        };
        Almacen.GuardarCuenta(cuenta);

        Assert.True(Almacen.ExisteCuenta("CUENTITA"));
        var leida = Almacen.LeerCuenta("cuentita")!;
        Assert.Equal(cuenta.PasswordHash, leida.PasswordHash);
        Assert.Equal(cuenta.Salt, leida.Salt);
        Assert.Equal(cuenta.UserCodeHash, leida.UserCodeHash);
        Assert.True(leida.Banned);
        Assert.True(leida.EsBot);
        Assert.Equal(4, leida.Trampas);
        Assert.True(leida.Donador);
        Assert.Equal(cuenta.UltimoIngreso, leida.UltimoIngreso);
        Assert.Equal("10.1.2.3", leida.UltimaIp);
        Assert.Contains(Almacen.NombresDeCuentas(), n => string.Equals(n, "Cuentita", StringComparison.OrdinalIgnoreCase));
        Assert.Null(Almacen.LeerCuenta("NadieAqui"));
        Assert.False(Almacen.ExisteCuenta("NadieAqui"));
    }

    [Fact]
    public void El_alta_suma_los_personajes_a_la_cuenta_en_el_orden_en_que_se_crearon()
    {
        var cuenta = CuentaGuardada("Familia");
        foreach (var nombre in new[] { "Primero", "Segundo", "Tercero" })
            Assert.True(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena(nombre)));

        Assert.Equal(new[] { "Primero", "Segundo", "Tercero" }, Almacen.LeerCuenta("Familia")!.Characters);
        foreach (var nombre in new[] { "Primero", "Segundo", "Tercero" })
        {
            Assert.True(Almacen.ExistePersonaje(nombre.ToUpperInvariant()));
            Assert.Contains(Almacen.NombresDePersonajes(), n => string.Equals(n, nombre, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>El tope del original (<c>[PJS] PJ1..PJ10</c>, Cuentas.bas:100). En los archivos el undécimo se caía al
    /// guardar la cuenta y quedaba un personaje que no figuraba en ninguna.</summary>
    [Fact]
    public void La_cuenta_llena_no_admite_un_personaje_mas()
    {
        var cuenta = CuentaGuardada("Llena");
        for (int i = 0; i < AccountFile.MaxCharacters; i++)
            Assert.True(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Pj" + (char)('A' + i))));

        Assert.False(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Sobrante")));
        Assert.False(Almacen.ExistePersonaje("Sobrante"));
        Assert.Equal(AccountFile.MaxCharacters, Almacen.LeerCuenta("Llena")!.Characters.Count);
    }

    [Fact]
    public void Apartar_un_personaje_libera_el_nombre()
    {
        var cuenta = CuentaGuardada("Duena");
        Assert.True(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Efimero")));

        Assert.True(Almacen.ApartarPersonaje("efimero"));

        Assert.False(Almacen.ExistePersonaje("Efimero"));
        Assert.Null(Almacen.LeerPersonaje("Efimero"));
        Assert.DoesNotContain(Almacen.NombresDePersonajes(), n => string.Equals(n, "Efimero", StringComparison.OrdinalIgnoreCase));
        Assert.False(Almacen.ApartarPersonaje("Efimero"));   // ya no hay nada que apartar

        // El nombre queda libre: otro personaje lo puede tomar, y es ése el que se lee.
        var nuevo = FichasDePrueba.Llena("Efimero", semilla: 5);
        Assert.True(Almacen.CrearPersonaje(cuenta, nuevo));
        FichasDePrueba.AssertIgual(nuevo, Almacen.LeerPersonaje("Efimero")!);
    }

    [Fact]
    public void Acreditar_la_boveda_suma_al_saldo_y_no_toca_lo_demas()
    {
        var cuenta = CuentaGuardada("Duena");
        var ficha = FichasDePrueba.Llena("Ahorrista");
        Assert.True(Almacen.CrearPersonaje(cuenta, ficha));

        Assert.Equal(ficha.OroEnBanco + 50, Almacen.AcreditarBoveda("ahorrista", 50));
        Assert.Equal(ficha.OroEnBanco + 80, Almacen.AcreditarBoveda("Ahorrista", 30));
        Assert.Null(Almacen.AcreditarBoveda("NadieAqui", 10));

        FichasDePrueba.AssertIgual(ficha with { OroEnBanco = ficha.OroEnBanco + 80 }, Almacen.LeerPersonaje("Ahorrista")!);
    }

    [Fact]
    public void La_tabla_de_posiciones_trae_a_todos_con_sus_numeros()
    {
        var cuenta = CuentaGuardada("Duena");
        var a = FichasDePrueba.Llena("Alto");
        var b = FichasDePrueba.Llena("Bajo", semilla: 2);
        Assert.True(Almacen.CrearPersonaje(cuenta, a));
        Assert.True(Almacen.CrearPersonaje(cuenta, b));

        var filas = Almacen.FilasDeRanking();

        Assert.Equal(2, filas.Count);
        foreach (var ficha in new[] { a, b })
        {
            var fila = Assert.Single(filas, f => string.Equals(f.Nombre, ficha.Nombre, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ficha.Nivel, fila.Nivel);
            Assert.Equal(ficha.Exp, fila.Exp);
            Assert.Equal(ficha.Clase, fila.Clase);
            Assert.Equal(ficha.Clan, fila.Clan);
            Assert.Equal(ficha.UsuariosMatados, fila.Usuarios);
            Assert.Equal(ficha.CriaturasMatadas, fila.Criaturas);
            Assert.Equal(ficha.VecesMuerto, fila.Muertes);
        }
    }

    [Fact]
    public void El_resumen_de_la_lista_de_personajes()
    {
        var cuenta = CuentaGuardada("Duena");
        var ficha = FichasDePrueba.Llena("Resumido");
        Assert.True(Almacen.CrearPersonaje(cuenta, ficha));

        var r = Almacen.LeerResumen("resumido")!;

        Assert.Equal(ficha.Cabeza, r.Head);
        Assert.Equal(ficha.Cuerpo, r.Body);
        Assert.Equal(ficha.AnimCasco, r.Helmet);
        Assert.Equal(ficha.AnimArma, r.Weapon);
        Assert.Equal(ficha.AnimEscudo, r.Shield);
        Assert.Equal(ficha.Nivel, r.Level);
        Assert.Equal(ficha.Clase, r.Class);
        Assert.Equal(ficha.Posicion.Map, r.Map);
        Assert.Null(Almacen.LeerResumen("NadieAqui"));
    }

    /// <summary>Un clan con todo, y las listas en un orden que no es el alfabético: el de los aspirantes es el que ve el
    /// líder.</summary>
    internal static ClanGuardado ClanLleno(string nombre, int semilla) => new()
    {
        Nombre = nombre, Lider = "Lider" + semilla, Descripcion = "descripcion " + semilla, Noticias = "noticias " + semilla,
        Alineacion = (byte)(semilla % 4 + 1), Url = "http://clan" + semilla, Codex = "codex " + semilla,
        EleccionesAbiertas = semilla % 2 == 1,
        Miembros = ImmutableArray.Create("Lider" + semilla, "Zeta" + semilla, "Alfa" + semilla),
        Aliados = ImmutableArray.Create("Amigo" + semilla),
        Enemigos = ImmutableArray.Create("Rival" + semilla, "Enemigo" + semilla),
        PropuestasDeAlianza = ImmutableArray.Create(new KeyValuePair<string, string>("Otro" + semilla, "aliemonos " + semilla)),
        PropuestasDePaz = ImmutableArray.Create(new KeyValuePair<string, string>("Rival" + semilla, "basta " + semilla)),
        Aspirantes = ImmutableArray.Create(new KeyValuePair<string, string>("Zulu" + semilla, "quiero entrar"),
                                           new KeyValuePair<string, string>("Alba" + semilla, "yo tambien")),
        Votos = ImmutableArray.Create(new KeyValuePair<string, string>("Zeta" + semilla, "Alfa" + semilla)),
    };

    internal static void AssertIgual(ClanGuardado esperado, ClanGuardado leido)
    {
        Assert.Equal(esperado with
        {
            Miembros = default, Aliados = default, Enemigos = default, PropuestasDeAlianza = default, PropuestasDePaz = default,
            Aspirantes = default, Votos = default,
        }, leido with
        {
            Miembros = default, Aliados = default, Enemigos = default, PropuestasDeAlianza = default, PropuestasDePaz = default,
            Aspirantes = default, Votos = default,
        });
        // Como secuencias: un ImmutableArray se compara por referencia con su Equals.
        Assert.Equal(esperado.Miembros.AsEnumerable(), leido.Miembros.AsEnumerable());
        Assert.Equal(esperado.Aliados.AsEnumerable(), leido.Aliados.AsEnumerable());
        Assert.Equal(esperado.Enemigos.AsEnumerable(), leido.Enemigos.AsEnumerable());
        Assert.Equal(esperado.PropuestasDeAlianza.AsEnumerable(), leido.PropuestasDeAlianza.AsEnumerable());
        Assert.Equal(esperado.PropuestasDePaz.AsEnumerable(), leido.PropuestasDePaz.AsEnumerable());
        Assert.Equal(esperado.Aspirantes.AsEnumerable(), leido.Aspirantes.AsEnumerable());
        Assert.Equal(esperado.Votos.AsEnumerable(), leido.Votos.AsEnumerable());
    }

    [Fact]
    public void Los_clanes_se_leen_igual_y_en_el_orden_del_juego()
    {
        Assert.Empty(Almacen.LeerClanes());
        var clanes = new[] { ClanLleno("Sur", 1), ClanLleno("Norte", 2) };
        Almacen.GuardarClanes(clanes);

        var leidos = Almacen.LeerClanes();
        Assert.Equal(new[] { "Sur", "Norte" }, leidos.Select(c => c.Nombre));
        for (int i = 0; i < clanes.Length; i++) AssertIgual(clanes[i], leidos[i]);

        // Se reemplazan todos: el que no viene, se cerró.
        Almacen.GuardarClanes(new[] { clanes[1] with { Noticias = "otras" } });
        var quedo = Assert.Single(Almacen.LeerClanes());
        AssertIgual(clanes[1] with { Noticias = "otras" }, quedo);
    }

    [Fact]
    public void Los_rechazos_de_clan_se_leen_igual_y_se_reemplazan()
    {
        Assert.Empty(Almacen.LeerRechazosDeClan());
        var rechazos = new[] { new KeyValuePair<string, string>("Aldo", "no sos del bando"), new KeyValuePair<string, string>("Bruma", "muy nueva") };
        Almacen.GuardarRechazosDeClan(rechazos);
        Assert.Equal(rechazos, Almacen.LeerRechazosDeClan());

        Almacen.GuardarRechazosDeClan(new[] { rechazos[1] });
        Assert.Equal(new[] { rechazos[1] }, Almacen.LeerRechazosDeClan());
    }

    /// <summary>El correo se guarda POR CASILLA: cambiar la de uno no toca las de los demás, y una vacía queda sin cartas.
    /// El orden de las cartas es el que el cliente usa para señalarlas.</summary>
    [Fact]
    public void El_correo_se_guarda_por_casilla()
    {
        Assert.Empty(Almacen.LeerCorreo());
        var aldo = new[] { new MailEntry("Bruma", "hola", 2, 1), new MailEntry("Ciro", "chau", 0, 0, 1), new MailEntry("Dana", "che", 5, 3) };
        var bruma = new[] { new MailEntry("Aldo", "respuesta", 0, 0) };
        Almacen.GuardarCasillas(new Dictionary<string, IReadOnlyList<MailEntry>> { ["Aldo"] = aldo, ["Bruma"] = bruma });
        Assert.Equal(aldo, Almacen.LeerCorreo()["aldo"]);

        var aldoDespues = new[] { aldo[2], aldo[0] };
        Almacen.GuardarCasillas(new Dictionary<string, IReadOnlyList<MailEntry>> { ["aldo"] = aldoDespues });
        var correo = Almacen.LeerCorreo();
        Assert.Equal(aldoDespues, correo["Aldo"]);
        Assert.Equal(bruma, correo["Bruma"]);

        Almacen.GuardarCasillas(new Dictionary<string, IReadOnlyList<MailEntry>> { ["Bruma"] = Array.Empty<MailEntry>() });
        Assert.False(Almacen.LeerCorreo().ContainsKey("Bruma"));
        Assert.Equal(aldoDespues, Almacen.LeerCorreo()["Aldo"]);
    }

    [Fact]
    public void El_suelo_las_llaves_y_el_record_se_leen_igual()
    {
        Assert.Empty(Almacen.LeerSuelo());
        Assert.Empty(Almacen.LeerLlavesVendidas());
        Assert.Equal(0, Almacen.LeerRecord());

        var suelo = new[] { new ObjetoEnElSuelo(new WorldPos(1, 50, 50), 12, 3), new ObjetoEnElSuelo(new WorldPos(17, 89, 61), 474, 1) };
        var llaves = new[] { new LlaveVendida(40, 3, 53), new LlaveVendida(40, 5, 54), new LlaveVendida(41, 1, 55) };
        Almacen.GuardarSuelo(suelo);
        Almacen.GuardarLlavesVendidas(llaves);
        Almacen.GuardarRecord(7);

        Assert.Equal(suelo.OrderBy(o => o.Posicion.Map), Almacen.LeerSuelo().OrderBy(o => o.Posicion.Map));
        Assert.Equal(llaves, Almacen.LeerLlavesVendidas().OrderBy(l => l.Mercader).ThenBy(l => l.Ranura));
        Assert.Equal(7, Almacen.LeerRecord());

        Almacen.GuardarSuelo(new[] { suelo[1] });
        Almacen.GuardarLlavesVendidas(Array.Empty<LlaveVendida>());
        Assert.Equal(new[] { suelo[1] }, Almacen.LeerSuelo());
        Assert.Empty(Almacen.LeerLlavesVendidas());
    }

    [Fact]
    public void El_backup_deja_algo_en_el_disco()
    {
        var cuenta = CuentaGuardada("Duena");
        Assert.True(Almacen.CrearPersonaje(cuenta, FichasDePrueba.Llena("Respaldado")));
        string destino = Path.Combine(Path.GetTempPath(), "coverao_contrato_bk_" + Guid.NewGuid().ToString("N"));
        try
        {
            string ruta = Almacen.Backup(destino, "coverao-prueba");
            Assert.True(File.Exists(ruta) || Directory.Exists(ruta), ruta);
        }
        finally { try { Directory.Delete(destino, recursive: true); } catch { } }
    }
}

public sealed class ContratoDelAlmacenDeArchivos : ContratoDelAlmacen
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_contrato_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenDeTexto _almacen;

    public ContratoDelAlmacenDeArchivos() => _almacen = new AlmacenDeTexto(new FileStore(_raiz));
    protected override IAlmacen Almacen => _almacen;
    public override void Dispose() { try { Directory.Delete(_raiz, recursive: true); } catch { } }
}

public sealed class ContratoDelAlmacenSqlite : ContratoDelAlmacen
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "coverao_contrato_" + Guid.NewGuid().ToString("N"));
    private readonly AlmacenSqlite _almacen;

    public ContratoDelAlmacenSqlite() => _almacen = new AlmacenSqlite(Path.Combine(_raiz, "coverao.db"));
    protected override IAlmacen Almacen => _almacen;

    public override void Dispose()
    {
        _almacen.Dispose();
        try { Directory.Delete(_raiz, recursive: true); } catch { }
    }
}

/// <summary>La misma batería contra la base en memoria, que es la que van a usar los tests que sólo necesitan dónde dejar
/// personajes.</summary>
public sealed class ContratoDelAlmacenSqliteEnMemoria : ContratoDelAlmacen
{
    private readonly AlmacenSqlite _almacen = AlmacenSqlite.EnMemoria();
    protected override IAlmacen Almacen => _almacen;
    public override void Dispose() => _almacen.Dispose();
}
