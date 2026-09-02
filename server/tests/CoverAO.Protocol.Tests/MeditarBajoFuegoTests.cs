using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// MEDITAR NO AGUANTA UN GOLPE — la parte de la regla que es aritmética pura.
///
/// <para>El original corta la meditación por dos caminos distintos y con reglas distintas, y el port no tenía
/// ninguno de los dos: la regeneración de maná del loop sigue mientras <c>Meditating</c> esté prendido, así
/// que meditar en medio de una pelea era maná gratis.</para>
///
/// <list type="bullet">
/// <item><b>PvP</b> (<c>UsuarioAtacadoPorUsuario</c>, SistemaCombate.bas:1457-1466): se corta SIEMPRE, sin
/// umbral y sin mirar si el golpe entró. Eso vive en el dispatcher y lo ejerce <c>meditar_bajo_fuego_flow</c>.</item>
/// <item><b>PvE</b> (<c>NpcDaño</c>, :593-601): sólo si el golpe pasa un umbral que sale del HP que te queda,
/// la Inteligencia y la skill Meditar. Eso es <see cref="Combat.UmbralRompeMeditacion"/>.</item>
/// </list>
///
/// <para>Y el 25% de evasión recortada (<c>UsuarioImpacto</c>, :1123-1128) es <see cref="Combat.HitChance"/>.</para>
/// </summary>
public class MeditarBajoFuegoTests
{
    // ---- El recorte del 25% de evasión ------------------------------------------------------------------

    [Fact]
    public void SinMeditar_la_chance_no_cambia()
    {
        // El tramo de siempre: clamp(50 + (ataque - evasión) * 0,4, 10, 90).
        Assert.Equal(50, Combat.HitChance(100, 100));
        Assert.Equal(90, Combat.HitChance(1000, 0));
        Assert.Equal(10, Combat.HitChance(0, 1000));
    }

    [Fact]
    public void Meditando_le_recorta_a_la_victima_un_cuarto_de_la_evasion()
    {
        // 50 de chance → evadir 50 → recortado 37,5 → al Long PAR: 38 → chance 62.
        Assert.Equal(62, Combat.HitChance(100, 100, victimaMeditando: true));
    }

    [Fact]
    public void El_recorte_no_tiene_el_piso_de_diez()
    {
        // ÉSTE es el caso que separa una lectura descuidada de la fiel: el original vuelve a aplicar el techo
        // de 90 pero NO el piso de 10 (`ProbExito = MinimoInt(90, 100 - ProbEvadir)`). Con la chance en el
        // piso, meditar la TRIPLICA.
        Assert.Equal(10, Combat.HitChance(0, 1000));
        Assert.Equal(32, Combat.HitChance(0, 1000, victimaMeditando: true));   // 90 * 0,75 = 67,5 → 68 → 32
    }

    [Fact]
    public void El_techo_de_noventa_sigue_valiendo()
    {
        // 90 → evadir 10 → 7,5 → al par: 8 → 92, y el MinimoInt lo baja a 90.
        Assert.Equal(90, Combat.HitChance(1000, 0, victimaMeditando: true));
    }

    [Theory]
    // El redondeo AL PAR de VB6, que es donde esto se hace mal: 0,5 exactos suben o bajan según la paridad.
    [InlineData(10, 32)]   // 67,5 → 68 (68 es par)
    [InlineData(30, 48)]   // 52,5 → 52 (52 es par, así que BAJA)
    [InlineData(50, 62)]   // 37,5 → 38
    [InlineData(70, 78)]   // 22,5 → 22
    public void El_cuarto_de_evasion_redondea_al_par(int chanceBase, int esperado)
    {
        // Se arma la chance base con la diferencia de poderes: 50 + (pa - pe) * 0,4 = chanceBase.
        long pa = (long)((chanceBase - 50) / 0.4);
        Assert.Equal(chanceBase, Combat.HitChance(pa, 0));
        Assert.Equal(esperado, Combat.HitChance(pa, 0, victimaMeditando: true));
    }

    // ---- El umbral del golpe de criatura ----------------------------------------------------------------

    [Fact]
    public void Con_la_skill_Meditar_en_cero_el_umbral_es_cero()
    {
        // Y como el original corta con `daño > umbral`, cualquier golpe de 1 o más la parte. Es el caso del
        // 99% de los personajes: la skill Meditar arranca en 0 y hay que ponerle puntos a mano.
        for (int dado = 0; dado <= 5; dado++)
            Assert.Equal(0, Combat.UmbralRompeMeditacion(hpDespuesDelGolpe: 500, inteligencia: 20, meditar: 0, dado));
    }

    [Fact]
    public void Con_inteligencia_y_skill_altas_aguanta_un_golpe_chico()
    {
        // 200 HP, Int 20, Meditar 100: 200/100=2 → *20=40 → *100=4000 → /100=40 → *12=480, dividido por 7..12.
        Assert.Equal(68, Combat.UmbralRompeMeditacion(200, 20, 100, dado: 0));   // 480/7  = 68,57 → Fix 68
        Assert.Equal(40, Combat.UmbralRompeMeditacion(200, 20, 100, dado: 5));   // 480/12 = 40
    }

    [Fact]
    public void El_umbral_cae_con_el_HP_que_queda()
    {
        // El original lee el MinHP de DESPUÉS de descontar el golpe, así que al que está por morir la
        // meditación se le rompe con cualquier cosa. Es lo contrario de lo que uno escribiría de memoria.
        long sano = Combat.UmbralRompeMeditacion(200, 20, 100, dado: 0);
        long herido = Combat.UmbralRompeMeditacion(20, 20, 100, dado: 0);
        Assert.True(herido < sano);
        Assert.Equal(6, herido);
    }

    [Fact]
    public void El_umbral_trunca_hacia_cero_y_no_redondea()
    {
        // `Fix`, no `CLng`: acá no hay asignación a un entero de por medio, así que la regla de la casa —el
        // redondeo al par— NO aplica. 480/9 = 53,33 y 480/11 = 43,63: los dos van para abajo.
        Assert.Equal(53, Combat.UmbralRompeMeditacion(200, 20, 100, dado: 2));
        Assert.Equal(43, Combat.UmbralRompeMeditacion(200, 20, 100, dado: 4));
    }
}
