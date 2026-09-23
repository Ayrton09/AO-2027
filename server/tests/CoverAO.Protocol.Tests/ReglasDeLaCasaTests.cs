using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS REGLAS DE LA CASA que el tester pidió y que el ORIGINAL NO HACE: oro al subir de nivel (2.12),
/// curarse al subir (2.13), que el equipo de novato desaparezca al pasar el nivel 15 (2.2b), que el suelo
/// sobreviva a un reinicio, el CENTINELA anti-macro y que el FAMILIAR salga a buscar pelea solo (las dos
/// últimas, del reporte del 2026-08-24).
///
/// <para>Están implementadas, pero <b>apagadas</b>: cambian el balance del juego y esa decisión no es del
/// port. Lo que fija este test es justamente eso — que <b>el default sea el comportamiento del VB6</b>. Es la
/// mitad que importa: si mañana alguien le pone un default distinto "porque queda mejor", un server que se
/// creía fiel empieza a regalar oro sin que nadie lo haya decidido.</para>
///
/// <para>Y se prueban las dos direcciones. Un test que sólo mirara el default pasaría igual si las opciones no
/// se leyeran del archivo en absoluto: el interruptor tiene que ENCENDER de verdad.</para>
/// </summary>
public class ReglasDeLaCasaTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_casa_" + Guid.NewGuid().ToString("N") + ".ini");
    private readonly string _datos = Path.Combine(Path.GetTempPath(), "coverao_casa_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { File.Delete(_ruta); } catch { /* el temp queda */ }
        try { Directory.Delete(_datos, recursive: true); } catch { /* idem */ }
    }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ruta, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ruta);
    }

    [Fact]
    public void Sin_ini_el_server_se_comporta_como_el_original()
    {
        var cfg = new ServerIni();
        Assert.Equal(0, cfg.OroAlSubirNivel);       // `SubirNivel` del VB6 no toca el oro
        Assert.False(cfg.CuraAlSubirNivel);         // sube los MÁXIMOS y llena sólo la vida, como el original
        Assert.False(cfg.QuitarEquipoNovato);       // no existe ningún `QuitarNewbieObj`
        Assert.False(cfg.PersistirSuelo);           // el suelo del VB6 vive en RAM y se repuebla del .csm
        // La fogata prendida del original NO se apaga nunca: `AccionParaRamita` mete su posición en
        // `TrashCollector` con el comentario "Las fogatas prendidas se deben eliminar" (Acciones.bas:572) y esa
        // colección aparece DOS veces en todo el servidor —esa línea y su declaración—: nadie la vacía jamás.
        Assert.Equal(0, cfg.MinutosDeFogata);
        // El módulo del centinela existe entero en el VB6 pero sus dos llamadas están COMENTADAS
        // (frmMain.frm:749 y mMainLoop.bas:21): en el install no aparece nunca.
        Assert.False(cfg.Centinela);
        Assert.False(cfg.FamiliarBusca);            // la mascota del VB6 es SigueAmo: no busca enemigo sola

        // LA EXCEPCIÓN, Y ES A PROPÓSITO. `HipocamposEnMarAbierto` sale PRENDIDA por decisión del autor: los
        // cuatro peces de la red ya se usan y el Hipocampo no lo pescaba nadie —en el original NADA lo
        // produce—, así que prefiere entregarlo puesto. O sea que "sin Server.ini" ya NO es exactamente el
        // install, y el título de este test tiene una salvedad.
        //
        // Queda afirmado acá, y no simplemente omitido, para que se lea como lo que es: una decisión tomada,
        // no un default que se coló. Si mañana alguien quiere volver a la fidelidad total, este assert es el
        // que hay que cambiar, y al cambiarlo se entera de por qué estaba así.
        Assert.True(cfg.HipocamposEnMarAbierto);

        // Y LAS OTRAS DOS EXCEPCIONES (2026-08-31), las que completan lo que el original dejó a medio hacer.
        // Son NUMÉRICAS y no un sí/no porque en las dos el número es lo único que la fuente no dejó escrito:
        // una de cada cuántas muertes cae un drop global, y cuánto dura una transformación. Cero apaga las dos.
        //
        // La de la metamorfosis dejó de ser un número elegido el 2026-09-08: en el AO clásico la
        // transformación NO VENCE, así que el default pasó a ser el -1 —permanente— y los segundos quedaron
        // para quien quiera acortarla.
        //
        // Con esto, "sin Server.ini" ya son TRES cosas que no son el install. Quedan afirmadas acá, y no
        // omitidas, por lo mismo que la de los hipocampos: para que se lean como decisiones tomadas y no como
        // defaults que se colaron. Quien vuelva a la fidelidad total cambia estos tres asserts y, al hacerlo,
        // se entera de por qué estaban así.
        Assert.Equal(10000, cfg.DropGlobal);          // uno cada diez mil criaturas
        Assert.Equal(-1, cfg.MetamorfosisDelDruida);  // permanente, como en el AO clásico

        // Y LA CUARTA (2026-09-23): los guardias de ciudad ATACAN, a pedido del tester. En el original no corren
        // IA (AI_NPC.bas:700). Persiguen y respetan a los newbies, también por decisión del autor.
        Assert.True(cfg.GuardiasAtacan);
        Assert.True(cfg.GuardiasPersiguen);
        Assert.False(cfg.GuardiasAtacanNewbies);

        // Y LA QUINTA (2026-09-23): los pasajes que cruzan el mar piden 80 de Navegación, también a pedido del tester.
        // En el original la marca `NecesitaNave` del obj.dat no la lee nadie.
        Assert.Equal(80, cfg.NavegacionParaCruzar);
    }

    [Fact]
    public void Con_un_ini_que_no_las_menciona_siguen_apagadas()
    {
        // El caso REAL: el Server.ini que se distribuye no las trae. Un default que se aplicara sólo cuando no
        // hay archivo dejaría prendidas las reglas en todo server con ini, que son todos los de producción.
        var cfg = Cargar("MaxUsers=50\r\n");
        Assert.Equal(0, cfg.OroAlSubirNivel);
        Assert.False(cfg.CuraAlSubirNivel);
        Assert.False(cfg.QuitarEquipoNovato);
        Assert.False(cfg.PersistirSuelo);
        // Las dos prendidas siguen prendidas con un ini que no las nombra, que es el caso REAL de un host que
        // actualiza: su Server.ini viejo no las trae. Un default que sólo valiera SIN archivo las dejaría
        // apagadas justamente en todos los servidores de producción.
        Assert.Equal(10000, cfg.DropGlobal);
        Assert.Equal(-1, cfg.MetamorfosisDelDruida);
        Assert.True(cfg.GuardiasAtacan);
        Assert.Equal(80, cfg.NavegacionParaCruzar);
    }

    [Fact]
    public void Los_interruptores_encienden_de_verdad()
    {
        var cfg = Cargar("OroAlSubirNivel=500\r\nCuraAlSubirNivel=1\r\nQuitarEquipoNovato=1\r\nPersistirSuelo=1\r\n"
                       + "DropGlobal=5\r\nMetamorfosisDelDruida=120\r\n");
        Assert.Equal(500, cfg.OroAlSubirNivel);
        Assert.True(cfg.CuraAlSubirNivel);
        Assert.True(cfg.QuitarEquipoNovato);
        Assert.True(cfg.PersistirSuelo);
        Assert.Equal(5, cfg.DropGlobal);
        Assert.Equal(120, cfg.MetamorfosisDelDruida);
    }

    /// <summary>Las dos numéricas se recortan igual que el oro por nivel, y por lo mismo: un valor absurdo
    /// desbalancea el server sin que nadie lo note. El tope del drop global es un millón porque es "uno cada
    /// N"; el de la metamorfosis, una hora — más que eso, la duración que se quiere es la permanente. Y el 0
    /// tiene que llegar intacto: es la única forma que tiene el host de volver al comportamiento del install.
    ///
    /// <para>El negativo NO se trata igual en las dos, y es a propósito: en el drop global no significa nada
    /// y cae en el 0, pero en la metamorfosis el -1 es la transformación PERMANENTE, que es el default. Por
    /// eso cualquier negativo cae en el -1 y no en el 0: apagar la regla es una decisión que se escribe.</para></summary>
    [Fact]
    public void Las_dos_numericas_tienen_tope()
    {
        // El 0 EXPLÍCITO tiene que llegar intacto: es la única forma que tiene el host de volver al install,
        // y las dos vienen prendidas, así que un clamp que lo subiera al default las dejaría sin apagado.
        Assert.Equal(0, Cargar("DropGlobal=0\r\n").DropGlobal);
        Assert.Equal(0, Cargar("MetamorfosisDelDruida=0\r\n").MetamorfosisDelDruida);
        Assert.Equal(0, Cargar("DropGlobal=-5\r\n").DropGlobal);
        Assert.Equal(1_000_000, Cargar("DropGlobal=99999999\r\n").DropGlobal);
        Assert.Equal(-1, Cargar("MetamorfosisDelDruida=-1\r\n").MetamorfosisDelDruida);
        Assert.Equal(-1, Cargar("MetamorfosisDelDruida=-50\r\n").MetamorfosisDelDruida);
        Assert.Equal(3600, Cargar("MetamorfosisDelDruida=99999\r\n").MetamorfosisDelDruida);
    }

    [Fact]
    public void El_oro_por_nivel_tiene_tope_y_no_acepta_negativos()
    {
        // No es una opinión sobre cuánto está bien: es que un valor absurdo desbalancea el mercado del server
        // sin que nadie lo note, y uno negativo le sacaría oro al jugador por subir de nivel.
        Assert.Equal(0, Cargar("OroAlSubirNivel=-9999\r\n").OroAlSubirNivel);
        Assert.Equal(1_000_000, Cargar("OroAlSubirNivel=99999999\r\n").OroAlSubirNivel);
    }
    /// <summary>Ubica un archivo del repo subiendo directorios desde donde corre el test (bin/…).</summary>
    private static string BuscarEnElRepo(string relativo)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relativo);
            if (File.Exists(cand)) return cand;
        }
        throw new FileNotFoundException($"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// LO QUE ELIGIÓ EL AUTOR, fijado aparte del default del SERVIDOR.
    ///
    /// <para>Los tests de arriba fijan que el server, sin ini o con un ini que no las menciona, se comporte
    /// como el VB6 — y eso no cambia. Este fija la otra mitad: qué trae el <c>Server.ini</c> que se
    /// distribuye, que es lo que decide cómo se juega en un servidor recién instalado. El 2026-08-17 el autor
    /// pidió curarse al subir y que el equipo de novato desaparezca al dejar de serlo, y dejar el oro
    /// apagado.</para>
    ///
    /// <para>Existe porque las dos cosas se leen parecido y la de arriba dice "el default tiene que ser 0":
    /// sin este test, alguien que viniera a "arreglar" la plantilla la devolvería a ceros creyendo que
    /// corrige una infidelidad, y se llevaría puesta una decisión tomada.</para>
    /// </summary>
    /// <summary>
    /// EL NIVEL MÍNIMO POR CUEVA (punto 5 del informe del tester, aprobado por el autor: sólo mínimo, sin
    /// tope máximo). Sin la sección la regla NO corre, que es lo que hace el original: tiene un campo
    /// <c>Restringir</c> por mapa, lo carga y deja que un GM lo edite, pero nadie lo lee para negar entrada.
    /// </summary>
    [Fact]
    public void Sin_la_seccion_no_hay_nivel_minimo_en_ningun_mapa()
    {
        var cfg = new ServerIni();
        Assert.Empty(cfg.NivelMinimoPorMapa);
        Assert.True(cfg.DejaEntrarAlMapa(753, 1, esGm: false));   // el nivel 1 entra a la Cueva orca
    }

    /// <summary>Y con la sección puesta, el interruptor ENCIENDE de verdad: se lee la tabla y se aplica.</summary>
    [Theory]
    // mapa | nivel del que pisa la salida | ¿es GM? | ¿lo deja entrar?
    [InlineData(753, 19, false, false)]   // uno menos que el mínimo: no entra
    [InlineData(753, 20, false, true)]    // el nivel JUSTO entra
    [InlineData(753, 40, false, true)]
    [InlineData(753, 1, true, true)]      // el GM entra siempre, igual que en el gate de zona NEWBIE
    [InlineData(1, 1, false, true)]       // un mapa sin mínimo no restringe a nadie
    public void Con_la_seccion_el_minimo_se_aplica(int mapa, int nivel, bool esGm, bool entra)
    {
        var cfg = Cargar("\r\n[NIVELMINIMO]\r\n753=20\r\n117=30\r\n");
        Assert.Equal(entra, cfg.DejaEntrarAlMapa(mapa, nivel, esGm));
    }

    /// <summary>Un renglón que no es un par de enteros se ignora, como el resto del ini: un error de tipeo
    /// del host no puede dejar el servidor sin arrancar ni, peor, restringiendo un mapa que no quiso.</summary>
    [Fact]
    public void Un_renglon_mal_tipeado_no_restringe_nada()
    {
        var cfg = Cargar("\r\n[NIVELMINIMO]\r\ncueva orca=20\r\n753=cero\r\n117=0\r\n230=40\r\n");
        Assert.True(cfg.DejaEntrarAlMapa(753, 1, esGm: false));    // el valor no era un numero
        Assert.True(cfg.DejaEntrarAlMapa(117, 1, esGm: false));    // cero = sin minimo
        Assert.False(cfg.DejaEntrarAlMapa(230, 39, esGm: false));  // el que si estaba bien, se aplica
    }

    /// <summary>
    /// Y EL NOMBRE DE LA CUEVA AL LADO DEL NÚMERO no rompe la tabla. <c>IniFile.Parse</c> parte en el primer
    /// <c>=</c> y se queda con todo lo que sigue, así que sin cortarlo el valor es el texto entero, no parsea,
    /// y el mapa queda sin restringir <b>sin que nadie avise</b>. Escribirlo así es lo natural en una tabla de
    /// veintitrés renglones, y las tres marcas son las que el propio parser acepta al principio de línea.
    /// </summary>
    [Theory]
    [InlineData("753=20 ; Cueva orca")]
    [InlineData("753=20   \u0027 la marca del VB6")]
    [InlineData("753=20 # la que se escribe por costumbre")]
    public void El_nombre_de_la_cueva_al_lado_del_numero_no_rompe_la_tabla(string renglon)
    {
        var cfg = Cargar("\r\n[NIVELMINIMO]\r\n" + renglon + "\r\n");
        Assert.False(cfg.DejaEntrarAlMapa(753, 19, esGm: false));
        Assert.True(cfg.DejaEntrarAlMapa(753, 20, esGm: false));
    }

    /// <summary>La tabla que el autor aprobó viaja en la plantilla, con las trece cuevas del tester repartidas
    /// en sus mapas —una cueva son varios— y ningún tope máximo.</summary>
    [Fact]
    public void El_ini_de_ejemplo_trae_la_tabla_de_las_cuevas()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        Assert.True(cfg.NivelMinimoPorMapa.Count >= 20,
            $"la plantilla trae {cfg.NivelMinimoPorMapa.Count} mapas con mínimo; la tabla del tester son trece cuevas");
        Assert.Equal(20, cfg.NivelMinimoPorMapa[753]);   // Cueva orca
        Assert.Equal(40, cfg.NivelMinimoPorMapa[230]);   // Dungeon Cristal, los tres mapas
        Assert.Equal(40, cfg.NivelMinimoPorMapa[231]);
        Assert.Equal(40, cfg.NivelMinimoPorMapa[232]);
        // Y NINGUNO es un tope máximo disfrazado: el autor decidió sólo mínimo.
        Assert.All(cfg.NivelMinimoPorMapa.Values, n => Assert.InRange(n, 1, 45));
    }

    /// <summary>
    /// AL TESORO NO SE LLEGA SIN CRUZAR UN MINIMO. Es el error que casi se comete al agregar el Dungeon Forzhe:
    /// ponerle el minimo SOLO al mapa del oro (754, con sus 34 vetas) y dejar el resto abierto.
    ///
    /// <para>Al Forzhe se entra desde el 650 (Latifundio Abandonado) por TRES puertas distintas —al 755, al 756 y
    /// al 757—, y el subsuelo del oro cuelga del 755. O sea que gatear un solo piso no gatea nada: se entra por
    /// cualquiera de las otras dos y se baja igual. El test lo fija contra la data de verdad, no contra una lista
    /// escrita a mano, asi que si alguien mueve una salida o borra un renglon del ini, se entera.</para>
    /// </summary>
    [Fact]
    public void Al_oro_del_Forzhe_no_se_llega_sin_cruzar_un_minimo()
    {
        var d = GameData.Load();
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        int[] elForzhe = { 754, 755, 756, 757, 760 };
        const int ElOro = 754;

        // La data tiene que traer el dungeon, o el test no probaria nada.
        Assert.All(elForzhe, m => Assert.True(d.MapExits.Keys.Any(k => k.Map == m),
                                              $"el mapa {m} no tiene salidas en la data"));

        // TODA entrada al dungeon desde afuera cae en un mapa con minimo.
        var entradas = d.MapExits
            .Where(e => !elForzhe.Contains(e.Key.Map) && elForzhe.Contains(e.Value.DestMap))
            .Select(e => e.Value.DestMap).Distinct().ToList();
        Assert.NotEmpty(entradas);
        Assert.All(entradas, m => Assert.True(cfg.NivelMinimoPorMapa.ContainsKey(m),
                                             $"al Forzhe se entra por el mapa {m} y ese mapa no tiene minimo: la puerta queda abierta"));

        // Y AL ORO en particular: sus vecinos de adentro tambien tienen el suyo, asi que no hay atajo.
        var vecinosDelOro = d.MapExits
            .Where(e => e.Value.DestMap == ElOro && e.Key.Map != ElOro)
            .Select(e => e.Key.Map).Distinct().ToList();
        Assert.NotEmpty(vecinosDelOro);
        Assert.All(vecinosDelOro, m => Assert.True(cfg.NivelMinimoPorMapa.ContainsKey(m),
                                                  $"al oro del {ElOro} se baja desde el {m}, que no tiene minimo"));

        // El minimo que MANDA para llegar al oro es el mas alto del camino, no el del subsuelo.
        int mandaDeVerdad = vecinosDelOro.Max(m => cfg.NivelMinimoPorMapa[m]);
        Assert.True(mandaDeVerdad >= cfg.NivelMinimoPorMapa[ElOro],
                    "el minimo del subsuelo es mas alto que el del piso por el que se baja: entonces manda el de abajo y el comentario del ini miente");
    }

    /// <summary>
    /// LOS ESCALONES DE RESPAWN, contra la data. Es REGLA DE LA CASA: el original reaparece AL INSTANTE
    /// (<c>MuereNpc</c> llama a <c>ReSpawnNpc</c> sin ninguna espera, y su campo <c>RespawnTime</c> del
    /// NPCs.dat es data muerta — una línea suelta lo pisa para los 291), y aoweb tampoco espera salvo con
    /// los dragones. Los tres escalones los eligió el autor a pedido del tester.
    ///
    /// <para>Lo que fija este test no son los números sino DÓNDE CAE EL CORTE: con el umbral de jefe en un
    /// millón son cuatro criaturas en todo el juego. Bajarlo por error convertiría media tabla en jefes
    /// —media hora para volver— y eso no lo cantaría ningún test que sólo comparara constantes.</para>
    /// </summary>
    [Fact]
    public void Los_escalones_de_respawn_alcanzan_a_los_jefes_y_a_nadie_mas()
    {
        var d = GameData.Load();
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));

        // El orden de los escalones: volver tarde tiene que ser para el que MÁS vale.
        Assert.True(cfg.RespawnSegundosJefe >= cfg.RespawnSegundosGordo,
                    "el jefe vuelve antes que un gordo: los escalones están dados vuelta");
        Assert.True(cfg.RespawnSegundosGordo >= cfg.RespawnSegundos,
                    "un gordo vuelve antes que una rata: los escalones están dados vuelta");
        Assert.True(cfg.RespawnExpJefe > cfg.RespawnExpGordo);

        // Los cuatro jefes del mundo, por NOMBRE: si alguien mueve el umbral, el test dice cuáles entraron.
        var hostiles = d.Npcs.Values.Where(n => n.Hostile).ToList();
        Assert.NotEmpty(hostiles);
        var jefes = hostiles.Where(n => cfg.SegundosDeRespawn(n.GiveExp) == cfg.RespawnSegundosJefe)
                            .Select(n => n.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Dragón Legendario", "El Abominable Grinch", "Gran Hechicero Garveloth", "Rey Dragón" },
                     jefes);

        // Y que el escalón de arriba siga siendo una EXCEPCIÓN: un puñado, no media tabla.
        int gordos = hostiles.Count(n => cfg.SegundosDeRespawn(n.GiveExp) == cfg.RespawnSegundosGordo);
        Assert.InRange(gordos, 1, hostiles.Count / 4);
        int comunes = hostiles.Count(n => cfg.SegundosDeRespawn(n.GiveExp) == cfg.RespawnSegundos);
        Assert.True(comunes > hostiles.Count / 2,
                    $"sólo {comunes} de {hostiles.Count} hostiles quedaron en el escalón común: el umbral está muy bajo");
    }

    /// <summary>
    /// Y QUE EL RETRASO SE APLIQUE, no sólo que la cuenta dé bien. Se matan dos criaturas en el mismo tick
    /// —una común y un jefe— y se mira quién vuelve primero: pasada la espera común tiene que estar la rata
    /// y NO el jefe, y recién mucho después el jefe.
    /// </summary>
    [Fact]
    public void Al_jefe_lo_hacen_esperar_mucho_mas_que_a_una_rata()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        var world = new CoverAO.Server.Game.World();
        var sys = new CoverAO.Server.Net.NpcSystem(world, new GameData(), ini: cfg);

        NpcInfo Ficha(short i, string n, long exp) =>
            new(Index: i, Name: n, Body: 1, Head: 1, Heading: 3, MaxHp: 10, MinHit: 1, MaxHit: 1,
                Def: 0, GiveExp: exp, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 1);

        // posOrig: vuelven a su baldosa, así el respawn no depende del sorteo de posición.
        sys.AddSpawn(Ficha(1, "Rata", 10), new CoverAO.Server.Game.WorldPos(1, 10, 10), posOrig: true);
        sys.AddSpawn(Ficha(2, "Jefe", cfg.RespawnExpJefe), new CoverAO.Server.Game.WorldPos(1, 20, 20), posOrig: true);
        int Vivas(string nombre) => world.Npcs.Count(n => n.Template.Name == nombre);
        Assert.Equal(1, Vivas("Rata"));
        Assert.Equal(1, Vivas("Jefe"));

        foreach (var n in world.Npcs.ToList()) world.RemoveNpc(n.CharIndex);
        sys.Update(1);                       // la vuelta en que se entera de las dos muertes
        Assert.Equal(0, Vivas("Rata"));

        // Justo pasada la espera común: vuelve la rata y el jefe sigue afuera.
        int ticksComun = cfg.RespawnSegundos * 1000 / CoverAO.Server.Game.GameLoop.TickMs;
        sys.Update(1 + ticksComun + 1);
        Assert.Equal(1, Vivas("Rata"));
        Assert.Equal(0, Vivas("Jefe"));

        // NI SIQUIERA pasada la espera de un gordo: sin esto, un jefe que cayera al escalón de abajo
        // pasaría el test igual, porque la comprobación de arriba sólo lo separa del escalón común.
        int ticksGordo = cfg.RespawnSegundosGordo * 1000 / CoverAO.Server.Game.GameLoop.TickMs;
        sys.Update(1 + ticksGordo + 1);
        Assert.Equal(0, Vivas("Jefe"));

        // Y pasada la del jefe, vuelve.
        int ticksJefe = cfg.RespawnSegundosJefe * 1000 / CoverAO.Server.Game.GameLoop.TickMs;
        sys.Update(1 + ticksJefe + 1);
        Assert.Equal(1, Vivas("Jefe"));
    }

    [Fact]
    public void El_ini_de_ejemplo_trae_lo_que_eligio_el_autor()
    {
        var cfg = ServerIni.Cargar(BuscarEnElRepo(Path.Combine("hosting", "Server.ini.ejemplo")));
        Assert.True(cfg.CuraAlSubirNivel);        // pedido del tester 2.13, aprobado
        Assert.True(cfg.QuitarEquipoNovato);      // 2.2b, y saca SÓLO los "(Newbies)"
        Assert.Equal(0, cfg.OroAlSubirNivel);     // 2.12: "por ahora lo dejamos off"
        // 2.27, y ésta NO la eligió el autor: la puso quien la implementó, porque en una beta el server se
        // reinicia y cada reinicio le borra a la gente lo que se le cayó. El comentario del ini lo dice con
        // todas las letras. Si el autor prefiere lo del original, acá se cambia el true por false.
        Assert.True(cfg.PersistirSuelo);
        // Las fogatas SÍ las eligió el autor, el 2026-09-19, después de que el tester preguntara cuánto tardan
        // en apagarse: "ponela en 5 min y que obviamente este la opcion de elgir cuanto tiempo en el .ini".
        Assert.Equal(5, cfg.MinutosDeFogata);
        // Y las tasas en 1, el 2026-09-20: "mejor seria que lo normal sea 1 y la persona le agregue el multiplo
        // que quiera por ej 5.10.100".
        Assert.Equal(1, cfg.Exp);
        Assert.Equal(1, cfg.Oro);
    }

    /// <summary>
    /// EL PLAZO DE LA FOGATA, EN LA UNIDAD DEL MUNDO. El ini habla en MINUTOS y el reloj del mundo cuenta
    /// VUELTAS del loop, no milisegundos — confundir esas dos unidades ya pasó dos veces en este proyecto y
    /// deshidrataba al jugador cuarenta veces más rápido. Acá los minutos se comparan contra
    /// <see cref="GameLoop.TickMs"/>, no contra un número copiado: si mañana la vuelta dura otra cosa, esto
    /// sigue diciendo la verdad.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    public void La_fogata_se_apaga_a_los_minutos_que_dice_el_ini(int minutos)
    {
        long vence = PacketDispatcher.TickEnQueSeApaga(1000, minutos);
        double milisegundos = (vence - 1000) * (double)GameLoop.TickMs;
        Assert.Equal(minutos * 60_000d, milisegundos, 1);
    }

    /// <summary>
    /// LA FOGATA QUE SOBREVIVE A UN REINICIO. La lista de vencimientos vive en memoria y el suelo se guarda en
    /// disco (<c>PersistirSuelo</c>, que la plantilla repartida trae encendido), así que una fogata prendida
    /// antes de apagar el server volvía sin plazo y ya no se apagaba nunca — el agujero sólo se notaba después
    /// de un reinicio, que es cuando nadie lo está mirando.
    ///
    /// <para>Y se mira el borde: una vuelta ANTES del plazo la fogata tiene que seguir ahí. Sin eso el test
    /// pasaría igual con un apagado que borra todo desde el primer tick.</para>
    /// </summary>
    [Fact]
    public void Una_fogata_que_viene_del_arranque_anterior_tambien_se_apaga()
    {
        const short Fogata = 63, Ramitas = 136, Lenia = 58, Mapa = 1;
        var enElPiso = new WorldPos(Mapa, 50, 50);
        var tirado = new WorldPos(Mapa, 51, 50);
        var decorado = new WorldPos(Mapa, 52, 50);
        var mundo = new World();
        mundo.SetGround(enElPiso, Fogata, 1);                    // lo que restauró el suelo persistido
        mundo.SetGround(tirado, Lenia, 3);                       // y al lado, leña de alguien: no es una fogata
        mundo.SetGround(decorado, Ramitas, 1, esFijo: true);     // lo que puso el .csm: no es de nadie

        var dispatcher = Dispatcher(mundo, Cargar("MinutosDeFogata=5\r\n"));
        dispatcher.AnotarFogatasDelSueloRestaurado();

        long vence = PacketDispatcher.TickEnQueSeApaga(0, 5);
        dispatcher.ApagarFogatasVencidas(vence - 1);
        Assert.True(mundo.TryGetGround(enElPiso, out _));        // una vuelta antes, sigue prendida
        dispatcher.ApagarFogatasVencidas(vence);
        Assert.False(mundo.TryGetGround(enElPiso, out _));       // y al plazo, se apagó sola
        Assert.True(mundo.TryGetGround(tirado, out _));          // lo tirado al piso no lo barre nadie
        Assert.True(mundo.TryGetGround(decorado, out _));        // el decorado del mapa, intacto
    }

    /// <summary>
    /// EL PLAZO ES DE LA FOGATA, NO DE LA BALDOSA. Anotar al arrancar todo lo que haya en el suelo, y no sólo
    /// las fogatas, parece inofensivo —el apagado igual mira qué quedó en el tile— pero deja un vencimiento
    /// colgado: la fogata que alguien prenda DESPUÉS en esa misma baldosa se apagaría con el plazo del objeto
    /// viejo, antes de tiempo. Con 30.000 objetos en el mundo eso no es un caso raro.
    /// </summary>
    [Fact]
    public void El_plazo_de_lo_que_ya_estaba_no_apaga_la_fogata_que_venga_despues()
    {
        var baldosa = new WorldPos(1, 50, 50);
        var mundo = new World();
        mundo.SetGround(baldosa, 58, 3);                         // leña de alguien, no una fogata

        var dispatcher = Dispatcher(mundo, Cargar("MinutosDeFogata=5\r\n"));
        dispatcher.AnotarFogatasDelSueloRestaurado();

        mundo.RemoveGround(baldosa);
        mundo.SetGround(baldosa, 63, 1);                         // alguien prende una ahí, con SU propio plazo
        dispatcher.ApagarFogatasVencidas(PacketDispatcher.TickEnQueSeApaga(0, 5));
        Assert.True(mundo.TryGetGround(baldosa, out _));
    }

    /// <summary>El default es el original: sin la regla, la fogata no se apaga NUNCA. Es el control negativo
    /// del test de arriba — sin él, "desapareció" no distingue la regla de cualquier otra cosa que borre.</summary>
    [Fact]
    public void Con_la_regla_apagada_la_fogata_se_queda_para_siempre()
    {
        var enElPiso = new WorldPos(1, 50, 50);
        var mundo = new World();
        mundo.SetGround(enElPiso, 63, 1);

        var dispatcher = Dispatcher(mundo, new ServerIni());     // MinutosDeFogata = 0
        dispatcher.AnotarFogatasDelSueloRestaurado();
        dispatcher.ApagarFogatasVencidas(PacketDispatcher.TickEnQueSeApaga(0, 60 * 24));
        Assert.True(mundo.TryGetGround(enElPiso, out _));
    }

    /// <summary>Un dispatcher mínimo, con su carpeta de datos propia para que la persistencia no toque nada.</summary>
    private PacketDispatcher Dispatcher(World mundo, ServerIni ini)
    {
        Directory.CreateDirectory(_datos);
        var datos = GameData.Load();
        var leveling = new Leveling(datos.LevelExp);
        return new PacketDispatcher(mundo, new GameLoop(), new Persistence(new FileStore(_datos), leveling),
                                    datos, leveling, serverIni: ini);
    }
}
