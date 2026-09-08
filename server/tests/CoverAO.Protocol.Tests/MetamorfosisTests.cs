using System;
using System.IO;
using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA METAMORFOSIS DEL DRUIDA, el hechizo que el original cobra y no ejecuta.
///
/// <para>Los once hechizos (#38-50) están enteros en el <c>Hechizos.dat</c> del install: cuerpo, cabeza, FX,
/// sonido, maná, energía, skill mínima y bonos al golpe y a la defensa. Y sus pergaminos se venden, de 35.000
/// a 2.200.000 de oro. <b>Lanzarlos no hace nada.</b> El servidor VB6 lee la clave <c>metamorfosis</c> al
/// cargar el archivo (FileIO.bas:367) y no la usa en ninguna línea: la única otra mención está comentada
/// (Modulo_UsUaRiOs.bas:1255), y ningún <c>Char.body = </c> del servidor sale de un hechizo.</para>
///
/// <para>Lo que se cuida acá es la DATA —que los once campos se lean y que ningún otro hechizo se contagie— y
/// las dos piezas que hacen que la transformación no se quede pegada: el guardián de la apariencia y la
/// defensa extra. El camino completo (lanzar, verse dragón, volver) lo mide <c>metamorfosis_flow.py</c>
/// contra el servidor de verdad.</para>
/// </summary>
public class MetamorfosisTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_meta_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ruta); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ruta, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ruta);
    }

    /// <summary>Un Player mínimo: los tres campos que el modelo exige más lo que pida cada caso.</summary>
    private static Player Nuevo(long hasta = 0, short body = 0) => new()
    {
        Name = "Druida", Account = "cuenta_test", CharIndex = 1, Race = 1, Gender = 1,
        MetamorfosisHastaTick = hasta, MetamorfosisBody = body,
    };

    private static GameData Datos()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        return d;
    }

    // ── LA REGLA ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>PRENDIDA y PERMANENTE, con archivo y sin archivo. El -1 no es un negativo que se coló: en el
    /// AO clásico la transformación no vence —se conserva hasta desencantar o morir—, y el 2026-09-08 esa fue
    /// la decisión del autor. Los segundos siguen valiendo para quien quiera acortarla, y el 0 la apaga.</summary>
    [Fact]
    public void Viene_prendida_y_permanente()
    {
        Assert.Equal(-1, new ServerIni().MetamorfosisDelDruida);
        Assert.Equal(-1, Cargar("MaxUsers=50\r\n").MetamorfosisDelDruida);
        Assert.True(new ServerIni().MetamorfosisPermanente);
        Assert.True(new ServerIni().HayMetamorfosis);

        var conReloj = Cargar("MetamorfosisDelDruida=300\r\n");
        Assert.Equal(300, conReloj.MetamorfosisDelDruida);
        Assert.False(conReloj.MetamorfosisPermanente);
        Assert.True(conReloj.HayMetamorfosis);

        var apagada = Cargar("MetamorfosisDelDruida=0\r\n");
        Assert.Equal(0, apagada.MetamorfosisDelDruida);
        Assert.False(apagada.HayMetamorfosis);
        Assert.False(apagada.MetamorfosisPermanente);
    }

    /// <summary>El tick de "no vence" no vence: ni en la vuelta siguiente ni en la última que el loop podría
    /// contar. Es la prueba de que permanente no necesita un caso especial en <c>VencerMetamorfosis</c> —el
    /// game loop pregunta lo mismo de siempre y la respuesta es no—, y de que sigue siendo un estado que
    /// <c>OlvidarMetamorfosis</c> apaga, que es por donde salen morir y desconectarse.</summary>
    [Fact]
    public void La_permanente_no_vence_nunca()
    {
        var p = Nuevo(hasta: Player.MetamorfosisSinVencimiento, body: 82);
        Assert.True(p.EstaMetamorfoseado(0));
        Assert.True(p.EstaMetamorfoseado(long.MaxValue - 1));   // ~11.700 millones de años de loop a 40 ms
        p.OlvidarMetamorfosis();
        Assert.False(p.EstaMetamorfoseado(0));
    }

    // ── LA DATA ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LOS ONCE, Y SÓLO LOS ONCE. Se fija el conjunto exacto de índices, no un conteo: si un campo se leyera
    /// con la grafía equivocada, un "&gt;= 11" podría seguir pasando con otros hechizos adentro. Y el 41 y el
    /// 48 NO están — el bloque tiene huecos en el archivo del original.
    /// </summary>
    [Fact]
    public void Son_once_hechizos_y_son_estos()
    {
        var d = Datos();
        var indices = Enumerable.Range(1, 200).Select(i => d.Spell(i)).Where(s => s is { EsMetamorfosis: true })
                                .Select(s => (int)s!.Index).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 38, 39, 40, 42, 43, 44, 45, 46, 47, 49, 50 }, indices);
    }

    /// <summary>
    /// Los cuerpos y los bonos, contra la data real. Son los dos extremos de la progresión que el install
    /// dejó escrita: el Murciélago es el barato (skill 5, 50 de maná) y el Dragón el caro (skill 100, 1300).
    /// Se fijan los números porque son ELLOS los que hacen al hechizo — si el transcode dejara de traer
    /// <c>ExtraHIT</c>, la transformación seguiría funcionando y sólo dejaría de pegar más fuerte, que es
    /// justo la clase de pérdida silenciosa que este proyecto ya se comió varias veces.
    /// </summary>
    [Theory]
    [InlineData(49, 9, 6, 20, 50)]      // Murciélago
    [InlineData(40, 10, 10, 15, 100)]   // Lobo
    [InlineData(38, 141, 30, 30, 1000)] // Gólem
    [InlineData(39, 82, 50, 20, 1300)]  // Dragón
    public void Cada_forma_trae_su_cuerpo_y_sus_bonos(int hechizo, int body, int hit, int def, int mana)
    {
        var s = Datos().Spell(hechizo);
        Assert.NotNull(s);
        Assert.True(s!.EsMetamorfosis);
        Assert.Equal(body, s.BodyMetamorfosis);
        Assert.Equal(hit, s.ExtraHit);
        Assert.Equal(def, s.ExtraDef);
        Assert.Equal(mana, s.ManaCost);
        Assert.Equal(0, s.HeadMetamorfosis);   // los once: la criatura no tiene cabeza humana
    }

    /// <summary>Ningún otro hechizo trae esos campos, así que la rama nueva no puede alcanzar a uno que no
    /// sea de metamorfosis. Es el control del test de arriba.</summary>
    [Fact]
    public void Ningun_otro_hechizo_trae_esos_campos()
    {
        var d = Datos();
        var contagiados = Enumerable.Range(1, 200).Select(i => d.Spell(i))
            .Where(s => s is not null && !s.EsMetamorfosis
                        && (s.Metamorfosis > 0 || s.BodyMetamorfosis > 0 || s.ExtraHit > 0 || s.ExtraDef > 0))
            .Select(s => $"#{s!.Index} {s.Name}").ToArray();
        Assert.True(contagiados.Length == 0, "no son de metamorfosis y traen sus campos: " + string.Join(", ", contagiados));
    }

    // ── EL ESTADO ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El estado arranca limpio y <c>OlvidarMetamorfosis</c> lo deja limpio. Los cuatro campos, no
    /// sólo el tick: dejar los bonos puestos es exactamente el bug de "revivís y seguís pegando como dragón".</summary>
    [Fact]
    public void Olvidar_borra_los_cuatro_campos()
    {
        var p = new Player { Name = "Druida", Account = "cuenta_test", CharIndex = 1,
                             MetamorfosisHastaTick = 500, MetamorfosisBody = 82,
                             MetamorfosisExtraHit = 50, MetamorfosisExtraDef = 20 };
        Assert.True(p.EstaMetamorfoseado(100));
        p.OlvidarMetamorfosis();
        Assert.Equal(0, p.MetamorfosisHastaTick);
        Assert.Equal(0, p.MetamorfosisBody);
        Assert.Equal(0, p.MetamorfosisExtraHit);
        Assert.Equal(0, p.MetamorfosisExtraDef);
        Assert.False(p.EstaMetamorfoseado(100));
    }

    /// <summary>Un jugador sin transformar no está transformado en ningún tick — el estado apagado es el 0,
    /// no un tick en el pasado. Sin esto, un <c>tick &gt;= 0</c> daría "transformado" al recién conectado.</summary>
    [Fact]
    public void Sin_transformar_nunca_esta_transformado()
    {
        var p = Nuevo();
        Assert.False(p.EstaMetamorfoseado(0));
        Assert.False(p.EstaMetamorfoseado(long.MaxValue));
    }

    /// <summary>Vence por tick: adentro sí, justo en el borde no.</summary>
    [Fact]
    public void Vence_en_su_tick()
    {
        var p = Nuevo(hasta: 1000);
        Assert.True(p.EstaMetamorfoseado(999));
        Assert.False(p.EstaMetamorfoseado(1000));
        Assert.False(p.EstaMetamorfoseado(1001));
    }

    // ── EL MIEDO DE LAS CRIATURAS ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LAS DOS ESCALAS ENCAJAN POR LOS EXTREMOS, y eso es lo que hace que el ×10 no sea un número a dedo: el
    /// <c>asusta</c> más alto de los once hechizos es 10 y el <c>Nivel</c> más alto de las criaturas del
    /// mundo es 100. Si mañana la data trajera un asusta de 12 o una criatura de Nivel 150, el puente dejaría
    /// de estar anclado — y este test se pondría rojo, que es exactamente para lo que está.
    /// </summary>
    [Fact]
    public void El_asusta_y_el_nivel_de_las_criaturas_encajan_por_los_extremos()
    {
        var d = Datos();
        var asustas = Enumerable.Range(1, 200).Select(i => d.Spell(i))
            .Where(s => s is { EsMetamorfosis: true }).Select(s => s!.Asusta).ToArray();
        Assert.Equal(11, asustas.Length);
        Assert.Equal(1, asustas.Min());     // el murciélago
        Assert.Equal(10, asustas.Max());    // el dragón (y el árbol)
        Assert.Equal(100, SpellInfo.EscalaDelAsusta * asustas.Max());

        var hostiles = d.Npcs.Values.Where(n => n.Hostile).Select(n => n.Nivel).ToArray();
        Assert.True(hostiles.Length > 100, $"esperaba muchos hostiles y hay {hostiles.Length}");
        Assert.Equal(100, hostiles.Max());  // el techo del mundo, que es lo que alcanza el dragón
    }

    /// <summary>
    /// El miedo, forma por forma, contra criaturas REALES del mundo. La comparación es estricta —"más
    /// poderoso QUE ellas"—, así que el techo no se asusta ni del dragón: los doce bichos de Nivel 100 le
    /// pegan a cualquiera. Eso importa, porque si no la forma de dragón volvería inmune al jugador.
    /// </summary>
    [Theory]
    [InlineData(49, 5, true)]      // murciélago (asusta 1 → 10) vs una Rata (Nivel 5): la rata huye
    [InlineData(49, 10, false)]    // ...pero un Lobo (10) no: 10 no es MÁS que 10
    [InlineData(40, 10, true)]     // lobo (asusta 2 → 20) vs ese mismo Lobo: ahora sí
    [InlineData(40, 26, false)]    // ...y no contra un Tigre (26)
    [InlineData(47, 42, true)]     // demonio (asusta 9 → 90) vs un Demonio del mundo (42)
    [InlineData(39, 99, true)]     // dragón (asusta 10 → 100) asusta a todo lo que esté debajo del techo
    [InlineData(39, 100, false)]   // ...menos al techo: el Dragón Legendario sigue atacando
    public void Una_criatura_huye_solo_de_una_forma_mas_poderosa(int hechizo, int nivelCriatura, bool huye)
    {
        var s = Datos().Spell(hechizo);
        Assert.NotNull(s);
        var p = Nuevo();
        p.MetamorfosisAsusta = s!.Asusta;
        Assert.Equal(huye, p.AsustaAUnaCriaturaDeNivel(nivelCriatura));
    }

    /// <summary>Sin transformar no asusta a nada, ni siquiera a lo más débil del mundo. Es el control: sin
    /// esto, un bug que dejara el multiplicador en 1 igual pasaría los casos de arriba.</summary>
    [Fact]
    public void Sin_transformar_no_asusta_ni_a_lo_mas_debil()
    {
        var p = Nuevo();
        Assert.Equal(0, p.MetamorfosisAsusta);
        Assert.False(p.AsustaAUnaCriaturaDeNivel(1));
    }

    // ── LAS DOS PIEZAS QUE SOSTIENEN LA TRANSFORMACIÓN ────────────────────────────────────────────────

    /// <summary>
    /// EL GUARDIÁN DE LA APARIENCIA. <c>RefreshAppearance</c> recalcula el cuerpo desde la armadura y lo llaman
    /// diez caminos (equipar, desequipar, desmontar, desembarcar, revivir…). Sin el guardián, ponerse cualquier
    /// cosa te devolvía a humano en el medio de la transformación, en silencio y sin que se acabara el efecto.
    ///
    /// <para>El control es la otra mitad: sin metamorfosis, la misma llamada tiene que devolver el cuerpo del
    /// equipo. Un guardián que se quedara pegado sería peor que no tenerlo.</para>
    /// </summary>
    [Fact]
    public void La_apariencia_no_pisa_la_transformacion()
    {
        var d = Datos();
        var p = Nuevo(hasta: 500, body: 82);
        PacketDispatcher.RefreshAppearance(p, d);
        Assert.Equal(82, p.Body);                                  // sigue siendo dragón

        p.OlvidarMetamorfosis();
        PacketDispatcher.RefreshAppearance(p, d);
        Assert.Equal(Equipment.NakedBody(p.Race, p.Gender), p.Body);  // control: vuelve el cuerpo real
        Assert.NotEqual(82, p.Body);
    }

    /// <summary>
    /// LA DEFENSA EXTRA se SUMA a lo que absorbe el equipo, no lo reemplaza. La primera versión de esto
    /// inicializaba el acumulador con el extra y las dos ramas lo ASIGNABAN encima, así que el bono se perdía
    /// entero cada vez que la víctima llevaba algo puesto — o sea, siempre.
    /// </summary>
    [Fact]
    public void La_defensa_extra_se_suma_a_la_del_equipo()
    {
        // Golpe de 100, armadura que absorbe exactamente 10, sin azar (rollDef devuelve el mínimo).
        static long Golpe(int extra) => Combat.AplicarDefensa(
            100, zona: 2, casco: null, armadura: (10, 10), escudo: null, rollDef: (lo, _) => lo, defensaExtra: extra);

        Assert.Equal(90, Golpe(0));    // control: sin transformar, la fórmula portada intacta
        Assert.Equal(70, Golpe(20));   // dragón: +20 encima de los 10 de la armadura
    }

    /// <summary>
    /// EL CUERPO DE UN NAVEGANTE NO SALE DEL EQUIPO. Es la razón por la que transformarse en barca (o a
    /// caballo) se rechaza: <c>RefreshAppearance</c> —el camino de vuelta— sólo sabe vestir a alguien parado
    /// en tierra, así que al vencer la transformación devolvería el cuerpo de la armadura y el druida quedaría
    /// caminando sobre el agua. Este test fija la premisa; el rechazo en sí vive en <c>Transformar</c>.
    /// </summary>
    [Fact]
    public void La_apariencia_desde_el_equipo_no_sabe_de_barcas()
    {
        var d = Datos();
        var p = Nuevo();
        p.Navigating = true;
        p.Body = PacketDispatcher.BoatBody;
        PacketDispatcher.RefreshAppearance(p, d);
        Assert.NotEqual(PacketDispatcher.BoatBody, p.Body);
    }

    /// <summary>Y sin equipo el extra absorbe solo — el druida transformado y desnudo no queda sin defensa.
    /// Con el clamp del original: un golpe sobre-absorbido sube a 1, nunca a 0 ni a negativo.</summary>
    [Fact]
    public void La_defensa_extra_sola_absorbe_y_respeta_el_clamp()
    {
        static long Golpe(int dano, int extra) => Combat.AplicarDefensa(
            dano, zona: 2, casco: null, armadura: null, escudo: null, rollDef: (lo, _) => lo, defensaExtra: extra);

        Assert.Equal(5, Golpe(25, 20));
        Assert.Equal(1, Golpe(10, 20));   // sobre-absorbido → 1, el `If daño < 0 Then daño = 1` del VB6
    }
}
