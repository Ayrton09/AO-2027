using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Hechizos de ÁREA (VB6 <c>HechizoTerrenoEstado</c>, modHechizos.bas:382). El port los trataba como hechizos
/// de un solo objetivo: la Bomba mágica le pegaba a UNA víctima y el Círculo curativo curaba a UNO. Estos tests
/// fijan las dos mitades que el flow no puede aislar:
///
///   · el <b>censo de la data</b> — quién es de área y quién no, y por qué la clasificación necesita mirar
///     <c>Target</c> y <c>Tipo</c> además de los dos flags de área;
///   · la <b>aritmética</b> del daño y la cura, incluida la asimetría 2%/3% y el piso en 0, que en el flow
///     queda tapada por la tirada aleatoria.
/// </summary>
public class AreaSpellTests
{
    private static GameData LoadOrSkip()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "test needs gamedata on disk");
        return data;
    }

    // ── La data ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void El_transcode_trae_los_flags_de_area()
    {
        var data = LoadOrSkip();
        var bomba = data.Spell(8);
        Assert.NotNull(bomba);
        Assert.Equal(1, bomba!.HechizoDeArea);
        Assert.Equal(2, bomba.AreaEfecto);
        Assert.True(bomba.IsArea);
        // Y los campos que hasta ahora se tiraban y la emisión al terreno necesita.
        Assert.Equal(129, bomba.Wav);
        Assert.Equal(50, bomba.FxGrh);
    }

    [Fact]
    public void Son_ocho_los_hechizos_de_area_que_entran_por_la_rama_del_terreno()
    {
        var data = LoadOrSkip();
        var deArea = data.Spells.Values.Where(s => s.IsArea && s.IsTerrainState).Select(s => (int)s.Index)
                                       .OrderBy(i => i).ToArray();
        // 8 Bomba mágica · 15 Tormenta de fuego · 23 Corriente eléctrica · 57 Tormenta ácida · 65 Tornado ·
        // 71 Pregonar la palabra de Dios · 75 Círculo curativo · 78 Terremoto.
        //
        // ERAN SIETE, y el que faltaba lo tapaba un bug NUESTRO: el 78 escribe sus dos claves en minúscula
        // (`hechizodearea=1`, `areaefecto=3`) y el loader comparaba string exacto. El VB6 no se entera de la
        // grafía porque `clsIniManager` normaliza con `UCase$`, así que en el original Terremoto SIEMPRE fue
        // de área, con radio 3 y 50-75 de daño — su propia descripción lo dice ("en un radio de 3 tiles").
        // Nosotros lo tratábamos como monoblanco. Ver ClavesConMayusculasTests.
        Assert.Equal(new[] { 8, 15, 23, 57, 65, 71, 75, 78 }, deArea);
    }

    [Fact]
    public void El_target_del_71_se_parsea_aunque_el_dat_traiga_un_comentario_pegado()
    {
        var data = LoadOrSkip();
        // Hechizos.dat trae `Target=4 'Efecto área` en el 71: el valor y un comentario en la MISMA línea. Si el
        // loader no se quedara con el entero de adelante, el Target saldría 0 y el hechizo no entraría nunca a
        // la rama del terreno (o sea: se perdería un hechizo de área por un apóstrofo).
        var pregonar = data.Spell(71);
        Assert.NotNull(pregonar);
        Assert.Equal(4, pregonar!.Target);
        Assert.True(pregonar.IsTerrainState);
    }

    [Fact]
    public void Los_de_area_con_Target_3_NO_entran_por_la_rama_del_terreno()
    {
        var data = LoadOrSkip();
        // Lamento de la Banshee(63), Lamento de las almas(84), Bomba mágica(97) y Apocalipsis GMs(101) tienen
        // HechizoDeArea=1 pero Target=3 (usuarios y NPCs), así que el original NO los rutea a
        // HechizoTerrenoEstado: van por HandleHechizoUsuario/NPC, donde nadie mira el 3×3. Son de área en la data
        // y de un solo objetivo en la práctica. Se respeta: clasificarlos por los flags a secas los mandaría a la
        // rama del área e inventaría gameplay que el original no tiene.
        foreach (int ix in new[] { 63, 84, 97, 101 })
        {
            var s = data.Spell(ix);
            Assert.NotNull(s);
            Assert.True(s!.IsArea, $"el {ix} sí trae los flags de área");
            Assert.False(s.IsTerrainState, $"pero el {ix} no debe entrar por la rama del terreno");
        }
    }

    [Fact]
    public void El_Tornado_es_de_area_pero_no_toca_el_HP_de_nadie()
    {
        var data = LoadOrSkip();
        var tornado = data.Spell(65);
        Assert.NotNull(tornado);
        Assert.True(tornado!.IsArea);
        // SubeHP=0: el `Select Case` del 3×3 (modHechizos.bas:423) sólo tiene Case 1 y Case 2, así que ninguna
        // rama lo agarra y el hechizo se queda en efecto visual — aunque su descripción prometa parálisis y el
        // campo Paraliza esté cargado. Divergencia del ORIGINAL que se porta tal cual.
        Assert.Equal(0, tornado.SubeHp);
        Assert.False(tornado.IsHeal);
        Assert.False(tornado.IsDamage);
        Assert.Equal(1, tornado.Paraliza);
    }

    [Fact]
    public void El_obj_dat_trae_la_resistencia_magica_y_el_bano_del_baculo()
    {
        var data = LoadOrSkip();
        // Los dos campos que el port no leía, y sin los cuales el daño de área sale más alto de lo que debe.
        Assert.Contains(data.Objects.Values, o => o.ResistenciaMagica > 0);
        Assert.Contains(data.Objects.Values, o => o.EfectoMagico == 14);   // eMagicType.DañoMagico

        // Valores DUROS de objetos concretos, y no sólo "hay alguno": los dos campos se insertaron en el MEDIO
        // del record ObjInfo, cuyo loader pasa los argumentos por POSICIÓN. Un desfase de un lugar correría en
        // silencio todos los campos que vienen detrás (HechizoIndex, LingoteIndex, Newbie, Aura) y el compilador
        // no diría nada, porque son todos int. Estos asserts anclan el alineamiento.
        Assert.Equal(20, data.Obj(993)!.ResistenciaMagica);
        Assert.Equal(10, data.Obj(992)!.ResistenciaMagica);
        var baculo = data.Obj(1147);
        Assert.Equal(14, baculo!.EfectoMagico);
        Assert.Equal(20, baculo.CuantoAumento);
        Assert.Equal(0, baculo.ResistenciaMagica);   // el báculo suma daño, no resiste
    }

    // ── La aritmética ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    // tirada, nivel, báculo, resistencia → esperado.
    //
    // OJO LOS DOS DEL MEDIO. El bono sale del `Porcentaje` del original (Matematicas.bas:32), que divide en
    // PUNTO FLOTANTE y devuelve un Long: REDONDEA al par, no trunca. Los tres casos que había acá usaban
    // números que dan exacto —500/100, 1000/100— así que pasaban igual con la división entera de C#, que da un
    // punto menos cada vez que la cuenta no cierra. Estos dos son los que separan una cosa de la otra.
    [InlineData(10, 1, 0, 0, 10)]      // 10*2/100 = 0,2 → 0 en los dos
    [InlineData(10, 13, 0, 0, 13)]     // 10*26/100 = 2,6 → 3; truncando daría 12
    [InlineData(7, 25, 0, 0, 11)]      // 7*50/100 = 3,5 → 4 (al par); truncando daría 10
    [InlineData(10, 25, 0, 0, 15)]     // nivel 25: 10 + 500/100 = 15
    [InlineData(10, 50, 0, 0, 20)]     // nivel 50: 10 + 1000/100 = 20
    [InlineData(10, 25, 7, 0, 22)]     // + báculo de DañoMagico
    [InlineData(10, 25, 0, 5, 10)]     // − resistencia mágica del equipo
    [InlineData(10, 25, 7, 30, 0)]     // más resistencia que daño → 0, NO 1
    public void El_dano_ofensivo_es_el_2_por_ciento_por_nivel_mas_baculo_menos_resistencia(
        int tirada, int nivel, int baculo, int resistencia, int esperado)
        => Assert.Equal(esperado, Combat.DanoHechizoOfensivo(tirada, nivel, baculo, resistencia));

    [Fact]
    public void El_dano_ofensivo_no_baja_de_cero_aunque_la_resistencia_sea_enorme()
    {
        // El golpe FÍSICO tiene piso 1 (AplicarDefensa), el hechizo tiene piso 0. Un jugador con suficiente
        // resistencia mágica anula el hechizo por completo, cosa que con el melee no puede.
        Assert.Equal(0, Combat.DanoHechizoOfensivo(5, 50, 0, 999));
        Assert.Equal(1, Combat.AplicarDefensa(5, 1, (99, 99), null, null, (_, _) => 99));
    }

    [Theory]
    [InlineData(30, 1, 31)]      // 30*3/100 = 0,9 → 1; truncando daría 30 y la cura de nivel 1 no rendiría nada
    [InlineData(30, 25, 52)]     // 30*75/100 = 22,5 → 22 (al par), igual que truncando
    [InlineData(30, 50, 75)]     // nivel 50: 30 + 4500/100 = 75
    public void La_cura_de_area_usa_el_3_por_ciento_por_nivel_no_el_2(int tirada, int nivel, int esperado)
        => Assert.Equal(esperado, Combat.EfectoHechizoArea3Pct(tirada, nivel));

    [Fact]
    public void La_asimetria_2_vs_3_por_ciento_es_real_y_no_un_typo()
    {
        // Con la MISMA tirada y el mismo nivel, curar rinde más que dañar. Es de la fuente: `HechizoTerrenoHP`
        // usa `3 * ELV` en la rama de cura (:494) y `2 * ELV` en la de daño (:519). Este test existe para que
        // "unificar" las dos fórmulas rompa algo en vez de pasar desapercibido.
        Assert.Equal(20, Combat.DanoHechizoOfensivo(10, 50, 0, 0));
        Assert.Equal(25, Combat.EfectoHechizoArea3Pct(10, 50));
    }

    [Theory]
    // wrestling, nivel -> probabilidad % de desarmar. VB6: Wrestling*0.2 + ELV*0.66, asignado a un Integer.
    [InlineData(0, 1, 1)]        // 0 + 0.66 -> 1
    [InlineData(100, 1, 21)]     // 20 + 0.66 = 20.66 -> 21
    [InlineData(100, 50, 53)]    // 20 + 33 = 53
    [InlineData(0, 0, 0)]        // sin skill ni nivel no desarma nunca
    public void La_probabilidad_de_desarmar_sale_del_wrestling_y_el_nivel_DEL_ATACANTE(
        int wrestling, int nivel, int esperado)
        => Assert.Equal(esperado, Combat.ProbabilidadDesarmar(wrestling, nivel));

    [Fact]
    public void Desarmar_es_del_atacante_asi_que_un_novato_sin_skill_casi_nunca_lo_logra()
    {
        // El gap que encontró el censo (VB6 Desarmar, Trabajo.bas:2269): un ladrón/bardo/gladiador puede
        // arrancarle el arma a su víctima. La probabilidad NO mira nada de la víctima — ni su nivel, ni su
        // fuerza, ni si el arma es buena—, cosa que sorprende y por eso queda fijada acá.
        Assert.True(Combat.ProbabilidadDesarmar(0, 1) < 5);       // recién empezado: casi nunca
        Assert.True(Combat.ProbabilidadDesarmar(100, 50) > 50);   // maestro de nivel alto: más de la mitad
    }

    // Los dos dados del original. Se leen de `ServerIni` y no se escriben a mano: asi, si alguien le cambia el
    // default a la clave del Server.ini, estos casos se caen en vez de seguir midiendo un numero que ya no rige.
    private static readonly int DadoDelInstall = CoverAO.Server.Data.ServerIni.Cargar(null).GolpeParalizanteDado;
    private static readonly int DadoChicoDelInstall = CoverAO.Server.Data.ServerIni.Cargar(null).GolpeParalizanteDadoGladiadorBardo;

    [Theory]
    // El golpe que paraliza (VB6 GolpeParalizaUsuario:2280 / GolpeParalizaNPC:2226) reusa la probabilidad de
    // desarmar pero la tira contra un dado mucho más grande. Gladiador(8) y bardo(6) tienen el dado chico.
    [InlineData(6, 300)]
    [InlineData(8, 300)]
    [InlineData(1, 500)]
    [InlineData(5, 500)]   // el ladrón desarma bien, pero para paralizar es uno más del montón
    public void El_dado_del_golpe_paralizante_favorece_a_gladiador_y_bardo(int clase, int esperado)
        => Assert.Equal(esperado, Combat.TopeDadoGolpeParaliza(clase, DadoDelInstall, DadoChicoDelInstall));

    [Fact]
    public void Paralizar_de_un_golpe_es_mucho_mas_raro_que_desarmar()
    {
        // La misma probabilidad contra dados distintos: desarmar tira 1..100 y paralizar 1..500 (1..300 para
        // gladiador y bardo). Que las dos fórmulas compartan `ProbabilidadDesarmar` invita a igualarlas de más;
        // esto fija que el dado es lo que las separa.
        int prob = Combat.ProbabilidadDesarmar(100, 50);                                  // 53
        Assert.Equal(5.0, (prob / 100.0) / (prob / (double)Combat.TopeDadoGolpeParaliza(1, DadoDelInstall, DadoChicoDelInstall)), 6);
        Assert.Equal(3.0, (prob / 100.0) / (prob / (double)Combat.TopeDadoGolpeParaliza(8, DadoDelInstall, DadoChicoDelInstall)), 6);
    }

    [Fact]
    public void El_dano_a_una_criatura_usa_el_3_por_ciento_y_no_le_resta_resistencia()
    {
        // `HechizoPropNPC` (:1657) también va al 3% y suma el báculo, pero NO hay ResistenciaMagica que
        // descontar: a los NPCs no se les mira el equipo.
        Assert.Equal(52, Combat.EfectoHechizoArea3Pct(30, 25));
        Assert.Equal(59, Combat.EfectoHechizoArea3Pct(30, 25, 7));
    }
}
