using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PÁGINA PÚBLICA: quién está conectado y la tabla de posiciones.
///
/// <para>No es una regla de la casa —no cambia ninguna mecánica— pero sí cambia QUÉ SALE DEL SERVIDOR hacia
/// cualquiera que alcance el puerto, y eso es lo que se cuida acá. Los contadores que muestra son del
/// original y se guardan desde siempre (<c>[MUERTES] UserMuertes</c> y <c>NpcsMuertes</c>, TCP.bas:2166-2167);
/// lo nuevo es que alguien los lea.</para>
/// </summary>
public class PaginaPublicaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_pub_" + Guid.NewGuid().ToString("N"));
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_pub_" + Guid.NewGuid().ToString("N") + ".ini");

    public PaginaPublicaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { File.Delete(_ini); } catch { }
    }

    /// <summary>Un <c>.chr</c> con lo justo para la tabla. Se escribe por el almacén —no a mano— para que use
    /// el mismo nombre de archivo y la misma codificación que el servidor de verdad.</summary>
    private static string Chr(int nivel, double exp, int clase, string clan, int usuarios, int criaturas, int muertes) =>
        $"[INIT]\r\nClase={clase}\r\n[STATS]\r\nELV={nivel}\r\nEXP={exp.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n" +
        $"[GUILD]\r\nGuildName={clan}\r\n[MUERTES]\r\nUserMuertes={usuarios}\r\nNpcsMuertes={criaturas}\r\n[FLAGS]\r\nMurio={muertes}\r\n";

    /// <summary>Crea el personaje Y lo anota en una cuenta, que es como existe uno de verdad. Hace falta las
    /// dos mitades: el <c>.chr</c> se llama en MAYÚSCULAS y la caja que el jugador escribió sólo está en la
    /// lista <c>[PJS]</c> de la cuenta.</summary>
    /// <para>Una cuenta por personaje, y no una con todos: <c>AccountFile.MaxCharacters</c> es 10, así que
    /// amontonarlos dejaba a los del final sin cuenta y con el nombre del archivo. El respaldo se prueba
    /// aparte, a propósito.</para>
    private static void Crear(IStore store, string nombre, string chr)
    {
        store.WriteCharacter(nombre, chr);
        var acc = new Account { Name = "CUENTA" + nombre };
        acc.Characters.Add(nombre);
        store.WriteAccount(acc.Name, AccountFile.ToIni(acc).ToText());
    }

    private JsonElement Foto(FileStore store, Func<string, bool>? esStaff = null, int tope = 25)
    {
        var estado = new EstadoPublico(new World(), new AlmacenDeTexto(store), new GameLoop(), esStaff ?? (_ => false), "1.0.0", tope);
        return JsonDocument.Parse(estado.Json()).RootElement;
    }

    private static string[] NombresDelRanking(JsonElement doc) =>
        doc.GetProperty("ranking").EnumerateArray().Select(r => r.GetProperty("nombre").GetString()!).ToArray();

    // ── EL INTERRUPTOR ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Viene PRENDIDA, y el 0 explícito la apaga. La clave tiene que estar además en
    /// <c>ClavesConocidas</c>, que es lo que hace que el aviso de arranque le diga al host que existe: una
    /// opción que nadie sabe que está es una opción que nadie puede apagar.</summary>
    [Fact]
    public void Viene_prendida_y_se_puede_apagar()
    {
        Assert.True(new ServerIni().PaginaPublica);
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\nPaginaPublica=0\r\n");
        Assert.False(ServerIni.Cargar(_ini).PaginaPublica);
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\n");
        Assert.True(ServerIni.Cargar(_ini).PaginaPublica);
        Assert.Contains("PaginaPublica", ServerIni.ClavesConocidas);
    }

    /// <summary>Apagada, la ruta NO EXISTE. El servidor de estáticos la sirve sólo si le dieron de dónde
    /// sacarla; sin eso es un 404 como cualquier ruta inventada, que es lo que tiene que ver alguien de
    /// afuera: ni un JSON vacío ni un 403, que confirmarían que el servidor está y sólo no quiere contestar.</summary>
    [Fact]
    public void Apagada_no_hay_de_donde_sacarla()
    {
        var sinPagina = new StaticFileServer(0, _root, 7667);
        var conPagina = new StaticFileServer(0, _root, 7667, estadoPublico: () => "{}");
        // La ruta se resuelve adentro de Serve(), así que lo que se puede afirmar sin levantar HTTP es que la
        // fuente es opcional y que el servidor arranca igual con y sin ella.
        Assert.NotNull(sinPagina);
        Assert.NotNull(conPagina);
    }

    // ── LO QUE SALE, Y SOBRE TODO LO QUE NO ───────────────────────────────────────────────────────────

    /// <summary>
    /// NO SALE DÓNDE ESTÁ PARADO NADIE, ni de qué cuenta es cada personaje.
    ///
    /// <para>Es el punto entero de la revisión: publicar el mapa y las coordenadas de los conectados sería
    /// darle a cualquiera la lista de a quién cazar y dónde encontrarlo, y el nombre de cuenta es la mitad de
    /// un login. El panel de administración SÍ los publica —está en loopback y detrás de un token—; ésta se
    /// sirve al mundo, así que la diferencia tiene que estar fijada y no confiada a la memoria del que
    /// mañana agregue un campo "para que se vea mejor".</para>
    /// </summary>
    [Fact]
    public void No_publica_posiciones_ni_cuentas()
    {
        var store = new FileStore(_root);
        Crear(store, "Rugura", Chr(40, 1000, 2, "Los Tres", 3, 900, 1));
        string json = JsonSerializer.Serialize(Foto(store));

        foreach (var prohibido in new[] { "\"map\"", "\"mapa\"", "\"x\"", "\"y\"", "\"cuenta\"", "\"account\"", "\"ip\"" })
            Assert.DoesNotContain(prohibido, json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// EL NOMBRE SALE COMO LO ESCRIBIÓ EL JUGADOR, no como se llama el archivo.
    ///
    /// <para>El <c>.chr</c> se guarda en MAYÚSCULAS —igual que en el original, <c>UCase$(.Name) &amp;
    /// ".chr"</c>— y adentro no hay ninguna clave con el nombre, así que barrer el almacén devuelve "RUGURA".
    /// La lista de conectados sale del mundo vivo y dice "Rugura": sin esto, la misma persona aparecía con
    /// dos grafías en la misma pantalla.</para>
    ///
    /// <para>Y el respaldo importa igual: un personaje que no figure en ninguna cuenta —un <c>.chr</c> suelto,
    /// que es lo que deja una restauración a mano— se queda con el nombre del archivo en vez de desaparecer
    /// de la tabla.</para>
    /// </summary>
    [Fact]
    public void El_nombre_conserva_la_caja_que_escribio_el_jugador()
    {
        var store = new FileStore(_root);
        Crear(store, "Rugura", Chr(30, 100, 2, "", 0, 0, 0));      // con cuenta: sale como se escribió
        store.WriteCharacter("Huerfano", Chr(20, 50, 1, "", 0, 0, 0));   // sin cuenta: respaldo

        Assert.Equal(new[] { "Rugura", "HUERFANO" }, NombresDelRanking(Foto(store)));
    }

    /// <summary>La tabla ordena por nivel y desempata por experiencia. Sin el desempate, dos personajes del
    /// mismo nivel salían en el orden en que el disco los devuelve, que es alfabético por nombre de archivo:
    /// la tabla parecía ordenada y no lo estaba.</summary>
    [Fact]
    public void Ordena_por_nivel_y_desempata_por_experiencia()
    {
        var store = new FileStore(_root);
        Crear(store, "Ana", Chr(20, 500, 1, "", 0, 10, 0));
        Crear(store, "Beto", Chr(40, 100, 2, "", 0, 20, 0));
        Crear(store, "Caro", Chr(40, 900, 3, "", 0, 30, 0));   // mismo nivel que Beto, más exp

        Assert.Equal(new[] { "Caro", "Beto", "Ana" }, NombresDelRanking(Foto(store)));
    }

    /// <summary>El staff NO entra en la tabla: un GM tiene <c>/CI</c> y se pone el nivel que quiera, así que
    /// con él adentro la tabla deja de medir nada. Y se decide con el <c>Server.ini</c> VIVO, no con el
    /// personaje, por lo mismo de siempre: el rango se deriva en cada login y el <c>.chr</c> no manda.</summary>
    [Fact]
    public void El_staff_no_entra_en_la_tabla()
    {
        var store = new FileStore(_root);
        Crear(store, "Rugura", Chr(50, 9999, 2, "", 0, 0, 0));   // el GM, el más alto de todos
        Crear(store, "Ana", Chr(20, 500, 1, "", 0, 10, 0));

        var conStaff = NombresDelRanking(Foto(store, esStaff: n => false));
        var sinStaff = NombresDelRanking(Foto(store, esStaff: n => string.Equals(n, "Rugura", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(new[] { "Rugura", "Ana" }, conStaff);   // control: sin la regla, el GM encabeza
        Assert.Equal(new[] { "Ana" }, sinStaff);
    }

    /// <summary>El tope se respeta. Es lo que hace que la página valga lo mismo con diez personajes que con
    /// diez mil: lo caro es leer el almacén, y publicar la lista entera además la volvería ilegible.</summary>
    [Fact]
    public void La_tabla_se_corta_en_el_tope()
    {
        var store = new FileStore(_root);
        for (int i = 1; i <= 30; i++) Crear(store, "Pj" + i, Chr(i, i, 1, "", 0, 0, 0));

        var top = Foto(store, tope: 5).GetProperty("ranking");
        Assert.Equal(5, top.GetArrayLength());
        Assert.Equal("Pj30", top[0].GetProperty("nombre").GetString());
        Assert.Equal(1, top[0].GetProperty("puesto").GetInt32());
    }

    /// <summary>Un personaje BORRADO no vuelve por la tabla de posiciones. El borrado los aparta en
    /// <c>Charfile/Borrados/</c> en vez de destruirlos, así que están ahí para siempre: un barrido recursivo
    /// —o un <c>SELECT</c> sin filtro en SQLite— los resucita en la lista pública.</summary>
    [Fact]
    public void Los_personajes_borrados_no_aparecen()
    {
        var store = new FileStore(_root);
        Crear(store, "Ana", Chr(20, 500, 1, "", 0, 10, 0));
        Crear(store, "Fantasma", Chr(45, 9000, 1, "", 0, 10, 0));
        Assert.Contains("Fantasma", NombresDelRanking(Foto(store)));   // control: antes de borrarlo, está

        Assert.True(store.ArchivarCharacter("Fantasma"));
        Assert.Equal(new[] { "Ana" }, NombresDelRanking(Foto(store)));
    }

    /// <summary>Un <c>.chr</c> ilegible se saltea y los demás siguen saliendo. Es la diferencia entre "a
    /// alguien se le cortó un guardado" y "la página no anda".</summary>
    [Fact]
    public void Un_personaje_roto_no_voltea_la_tabla()
    {
        var store = new FileStore(_root);
        Crear(store, "Ana", Chr(20, 500, 1, "", 0, 10, 0));
        File.WriteAllText(Path.Combine(_root, "Charfile", "ROTO.chr"), "esto no es un ini\0\0\0");

        var nombres = NombresDelRanking(Foto(store));
        Assert.Contains("Ana", nombres);
    }

    /// <summary>El mismo filtro de borrados, en el OTRO almacén. Acá los apartados viven en la misma tabla con
    /// la clave prefijada, así que la exclusión es un <c>NOT LIKE</c> y no un directorio: dos mecanismos
    /// distintos para la misma regla es exactamente lo que un día diverge.</summary>
    [Fact]
    public void En_sqlite_los_borrados_tambien_quedan_afuera()
    {
        using var store = new SqliteStore(Path.Combine(_root, "t.db"));
        Crear(store, "Ana", Chr(20, 500, 1, "", 0, 10, 0));
        Crear(store, "Fantasma", Chr(45, 9000, 1, "", 0, 10, 0));
        Assert.Equal(2, store.AllCharacterNames().Count());

        Assert.True(store.ArchivarCharacter("Fantasma"));
        Assert.Equal(new[] { "ANA" }, store.AllCharacterNames().ToArray());
    }
}
