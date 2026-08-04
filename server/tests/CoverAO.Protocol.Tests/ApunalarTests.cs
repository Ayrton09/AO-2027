using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// La chance de APUÑALAR (VB6 <c>DoApuñalar</c>, <c>Trabajo.bas:1766-1780</c>). La mecánica entera no existía
/// en el port, con el campo <c>Apuñala</c> presente en 13 objetos del transcode y sin parsear.
///
/// Lo que estos tests protegen no es la aritmética: es que <b>la clase se note</b>. Son cuatro curvas
/// distintas y si alguien "simplifica" los coeficientes —o los aplana como hizo aoweb, que no tiene skills—
/// el asesino deja de ser asesino y nadie lo ve hasta jugarlo.
/// </summary>
public class ApunalarTests
{
    private const int Clerigo = 1, Asesino = 4, Ladron = 5, Bardo = 6, Guerrero = 3, Paladin = 9, Sastre = 16;

    [Fact]
    public void Con_skill_cero_todos_tienen_algo_de_chance()
    {
        // Las cuatro ramas arrancan entre 4 y 5: el original NUNCA da 0% de base.
        foreach (int clase in new[] { Asesino, Clerigo, Bardo, Guerrero })
        {
            int s = Combat.SuerteApunalar(clase, 0);
            Assert.InRange(s, 4, 5);
        }
    }

    [Fact]
    public void El_asesino_se_despega_del_resto_al_subir_la_skill()
    {
        // ESTA es la razón de ser de las cuatro curvas. Con la skill al máximo la brecha tiene que ser grande;
        // si alguien aplana los polinomios, este test cae.
        int asesino = Combat.SuerteApunalar(Asesino, 100);
        int guerrero = Combat.SuerteApunalar(Guerrero, 100);
        Assert.True(asesino > guerrero * 2,
            $"con skill 100 el asesino dio {asesino} y el guerrero {guerrero}: la clase dejó de importar");
    }

    [Fact]
    public void Clerigo_paladin_y_sastre_comparten_curva()
    {
        // El original los mete en el mismo Case. Separarlos sería inventar balance.
        for (int skill = 0; skill <= 100; skill += 25)
        {
            int c = Combat.SuerteApunalar(Clerigo, skill);
            Assert.Equal(c, Combat.SuerteApunalar(Paladin, skill));
            Assert.Equal(c, Combat.SuerteApunalar(Sastre, skill));
        }
    }

    [Fact]
    public void El_bardo_tiene_su_propia_curva()
    {
        // Ni la del asesino ni la del grupo clérigo: es un Case aparte.
        int bardo = Combat.SuerteApunalar(Bardo, 100);
        Assert.NotEqual(bardo, Combat.SuerteApunalar(Asesino, 100));
        Assert.NotEqual(bardo, Combat.SuerteApunalar(Clerigo, 100));
    }

    [Fact]
    public void Cualquier_otra_clase_cae_en_la_rama_lineal()
    {
        // El `Case Else` del original: una recta, no un cúbico. Ladrón y guerrero comparten ese destino.
        Assert.Equal(Combat.SuerteApunalar(Guerrero, 50), Combat.SuerteApunalar(Ladron, 50));
        // Y es lineal de verdad: el salto de 0→50 tiene que ser igual al de 50→100.
        int a = Combat.SuerteApunalar(Guerrero, 0);
        int b = Combat.SuerteApunalar(Guerrero, 50);
        int c = Combat.SuerteApunalar(Guerrero, 100);
        Assert.Equal(b - a, c - b);
    }

    [Fact]
    public void Mas_skill_nunca_baja_la_chance()
    {
        // Monotonía en las cuatro ramas. Un cúbico mal transcrito puede dar la vuelta y bajar: acá se caza.
        foreach (int clase in new[] { Asesino, Clerigo, Bardo, Guerrero })
        {
            int previo = -1;
            for (int skill = 0; skill <= 100; skill++)
            {
                int actual = Combat.SuerteApunalar(clase, skill);
                Assert.True(actual >= previo, $"clase {clase}: con skill {skill} bajó de {previo} a {actual}");
                previo = actual;
            }
        }
    }

    [Fact]
    public void Trunca_hacia_abajo_como_el_Int_de_VB6()
    {
        // `Int()` trunca, NO redondea — al revés que el Clng half-to-even del resto del combate. Con skill 0 el
        // asesino da 4,25 y tiene que quedar en 4; si alguien usa el Clng de al lado, daría 4 también, pero con
        // 4,93 del clérigo daría 5 en vez de 4. Ese es el caso que separa las dos conversiones.
        Assert.Equal(4, Combat.SuerteApunalar(Asesino, 0));    // 4,25 → 4
        Assert.Equal(4, Combat.SuerteApunalar(Clerigo, 0));    // 4,93 → 4 (redondear daría 5)
        Assert.Equal(4, Combat.SuerteApunalar(Bardo, 0));      // 4,81 → 4 (redondear daría 5)
        Assert.Equal(4, Combat.SuerteApunalar(Guerrero, 0));   // 4,39 → 4
    }
}
