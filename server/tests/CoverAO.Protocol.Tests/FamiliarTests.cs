using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CURVA Y LAS TABLAS DEL FAMILIAR, fijadas sin server.
///
/// <para>Existen por lo que una auditoría dejó a la vista: los flows del familiar sólo llegan al <b>nivel
/// 15</b> —más arriba habría que matar el dummy de experiencia varias veces y el caso se vuelve lento y
/// frágil—, así que las habilidades que se aprenden a los niveles <b>20 y 30</b> no las miraba nadie, y
/// tampoco los bordes de la curva. <see cref="Familiar"/> es una clase pura: no necesita socket ni mundo, y
/// acá se la puede llevar al techo en un bucle.</para>
///
/// <para>La trampa que estos tests custodian, y que casi se come el subsistema al escribirlo: los números de
/// las habilidades de GOLPE y los de las MÁGICAS <b>se pisan</b>. La 11 como bandera es "entorpece" y como
/// hechizo es <b>Resucitar</b>; la 10 es "paraliza al golpear" y <b>Remover parálisis</b>. Si alguna vez
/// <c>HechizosQueTira()</c> deja de ser una lista explícita, el Ent revive a su víctima de un mordisco.</para>
/// </summary>
public class FamiliarTests
{
    private static Familiar Nacido(byte tipo, long vida = 15, long golpeMin = 5, long golpeMax = 20)
    {
        var f = new Familiar();
        f.Nacer(tipo, "Bicho", vida, golpeMin, golpeMax);
        return f;
    }

    /// <summary>Le da experiencia de a poco hasta llegar al nivel pedido. De a poco a propósito: dársela toda
    /// junta es el otro caso y se prueba aparte.</summary>
    private static Familiar SubidoHasta(byte tipo, int nivel)
    {
        var f = Nacido(tipo);
        for (int i = 0; i < 20000 && f.Nivel < nivel; i++) f.GanarExperiencia(500);
        return f;
    }

    [Fact]
    public void Nace_en_nivel_1_con_las_barras_de_su_ficha()
    {
        var f = Nacido(Familiar.Ely);
        Assert.Equal(1, f.Nivel);
        Assert.Equal(Familiar.EluInicial, f.Elu);
        Assert.Equal(15, f.VidaMaxima);
        Assert.Equal(15, f.Vida);
        Assert.All(f.Habilidades, h => Assert.Equal(0, h));
    }

    [Theory]
    [InlineData(Familiar.Ely, 10, Familiar.HabCurar)]
    [InlineData(Familiar.FuegoFatuo, 10, Familiar.HabMisil)]
    [InlineData(Familiar.Ent, 15, Familiar.HabGolpeEnvenena)]
    [InlineData(Familiar.Tigre, 15, Familiar.HabGolpeEntorpece)]
    [InlineData(Familiar.Ely, 20, Familiar.HabDescarga)]
    [InlineData(Familiar.Fuego, 20, Familiar.HabTormenta)]
    [InlineData(Familiar.Agua, 20, Familiar.HabParaliza)]
    [InlineData(Familiar.Tierra, 20, Familiar.HabInmoviliza)]
    [InlineData(Familiar.Ent, 20, Familiar.HabGolpeParaliza)]
    [InlineData(Familiar.Oso, 30, Familiar.HabGolpeDesarma)]
    [InlineData(Familiar.Lobo, 30, Familiar.HabGolpeCiega)]
    [InlineData(Familiar.Ely, 30, Familiar.HabDesencantar)]
    [InlineData(Familiar.FuegoFatuo, 30, Familiar.HabDetectar)]
    public void Aprende_su_habilidad_al_llegar_al_nivel(byte tipo, int nivel, int habilidad)
    {
        var f = SubidoHasta(tipo, nivel);
        Assert.True(f.Nivel >= nivel, $"no llegó al nivel {nivel} (quedó en {f.Nivel})");
        Assert.True(f.Sabe(habilidad),
            $"el tipo {tipo} llegó al nivel {f.Nivel} y no aprendió {Familiar.NombreDeHabilidad(habilidad)}");
    }

    /// <summary>Subir VARIOS niveles de un saque no puede saltear habilidades: el bucle de
    /// <c>GanarExperiencia</c> las aprende de a una por vuelta. Un solo dummy del fixture da 100.000, que son
    /// bastantes niveles juntos, así que el caso no es teórico.</summary>
    [Fact]
    public void Una_experiencia_enorme_de_una_no_le_saltea_habilidades()
    {
        var f = Nacido(Familiar.Ely);
        f.GanarExperiencia(100_000);
        Assert.True(f.Nivel >= 15, $"con 100.000 tendría que pasar el nivel 15, quedó en {f.Nivel}");
        Assert.True(f.Sabe(Familiar.HabCurar), "se salteó la del nivel 10");
        Assert.True(f.Sabe(Familiar.HabInmoviliza), "se salteó la del nivel 15");
    }

    [Fact]
    public void Al_techo_de_nivel_se_le_corta_la_cuenta_y_no_se_cuelga()
    {
        var f = Nacido(Familiar.Oso);
        for (int i = 0; i < 5000 && f.Nivel < Familiar.NivelMaximo; i++) f.GanarExperiencia(1_000_000);
        Assert.Equal(Familiar.NivelMaximo, f.Nivel);
        f.GanarExperiencia(long.MaxValue / 4);          // no se cuelga ni desborda
        Assert.Equal(Familiar.NivelMaximo, f.Nivel);
    }

    /// <summary>
    /// Los dos techos, que en el original son hermanos (<c>STAT_MAXHP</c> y <c>STAT_MAXHIT_OVER36</c>).
    ///
    /// <para>ARRANCA CERCA DEL TECHO A PROPÓSITO, y ésa es la mitad importante del caso: con las barras de una
    /// ficha de verdad el familiar NUNCA llega — cincuenta niveles del que más crece son unos 300 de golpe
    /// contra un techo de 999—, así que un test que subiera un Oso normal daría verde con el clamp puesto
    /// <b>y también sin él</b>. Se comprobó: sacando el clamp, esa versión del caso seguía en verde. Empezando
    /// en 990 el clamp tiene que actuar, y si alguien lo saca, esto se pone rojo.</para>
    /// </summary>
    [Fact]
    public void La_vida_y_el_golpe_tienen_techo()
    {
        var f = Nacido(Familiar.Oso, vida: Familiar.VidaTope - 9, golpeMin: 985, golpeMax: 990);
        for (int i = 0; i < 5000 && f.Nivel < Familiar.NivelMaximo; i++) f.GanarExperiencia(1_000_000);
        Assert.True(f.VidaMaxima <= Familiar.VidaTope, $"vida {f.VidaMaxima} pasó el techo");
        Assert.True(f.GolpeMaximo <= Familiar.GolpeTope, $"golpe {f.GolpeMaximo} pasó el techo");
        Assert.True(f.GolpeMinimo <= f.GolpeMaximo, "el golpe mínimo quedó por encima del máximo");
        // PREMISA: que el techo se haya TOCADO. Sin esto el caso volvería a ser incapaz de fallar.
        Assert.Equal(Familiar.GolpeTope, f.GolpeMaximo);
        Assert.Equal(Familiar.VidaTope, f.VidaMaxima);
    }

    /// <summary>LA TRAMPA DE LOS NÚMEROS QUE SE PISAN. Las de golpe NO son hechizos: si alguna se colara en
    /// <c>HechizosQueTira()</c>, el familiar tiraría el hechizo que lleva ese número — y la 11 es Resucitar.</summary>
    [Fact]
    public void Las_habilidades_de_golpe_nunca_se_tiran_como_hechizo()
    {
        int[] deGolpe =
        {
            Familiar.HabGolpeParaliza, Familiar.HabGolpeEntorpece, Familiar.HabGolpeDesarma,
            Familiar.HabGolpeCiega, Familiar.HabGolpeEnvenena,
        };
        foreach (byte tipo in new byte[] { Familiar.Ent, Familiar.Oso, Familiar.Tigre, Familiar.Lobo })
        {
            var f = SubidoHasta(tipo, Familiar.NivelMaximo);
            var tira = f.HechizosQueTira();
            foreach (var g in deGolpe)
                Assert.False(tira.Contains(g),
                    $"el tipo {tipo} tiraría como HECHIZO la bandera de golpe {g} — el {g} como hechizo es otra cosa");
        }
    }

    /// <summary>Y las dos que el ORIGINAL tampoco resuelve no se tiran: Desencantar y Detectar invisibilidad
    /// cargan su bandera y no las consulta nadie, ni siquiera para un jugador. Se aprenden y se muestran.</summary>
    [Fact]
    public void Desencantar_y_detectar_se_aprenden_pero_no_se_tiran()
    {
        var ely = SubidoHasta(Familiar.Ely, 30);
        Assert.True(ely.Sabe(Familiar.HabDesencantar));
        Assert.DoesNotContain(Familiar.HabDesencantar, ely.HechizosQueTira());

        var fatuo = SubidoHasta(Familiar.FuegoFatuo, 30);
        Assert.True(fatuo.Sabe(Familiar.HabDetectar));
        Assert.DoesNotContain(Familiar.HabDetectar, fatuo.HechizosQueTira());
    }

    /// <summary>Un familiar mágico subido SÍ tira algo: sin esto el test de arriba pasaría con una lista
    /// siempre vacía, que es el verde que sale de no hacer nada.</summary>
    [Fact]
    public void Un_familiar_magico_subido_tira_al_menos_un_hechizo()
    {
        var fatuo = SubidoHasta(Familiar.FuegoFatuo, 20);
        Assert.NotEmpty(fatuo.HechizosQueTira());
        Assert.Contains(Familiar.HabMisil, fatuo.HechizosQueTira());
    }

    [Theory]
    [InlineData(GameLimits.ClaseMago, 5)]
    [InlineData(GameLimits.ClaseDruida, 4)]
    [InlineData(GameLimits.ClaseCazador, 4)]
    [InlineData(GameLimits.ClaseGuerrero, 0)]
    [InlineData(GameLimits.ClaseClerigo, 0)]
    [InlineData(GameLimits.ClaseLadron, 0)]
    public void Solo_tres_clases_llevan_familiar(byte clase, int cuantos)
        => Assert.Equal(cuantos, Familiar.TiposDeLaClase(clase).Length);

    [Fact]
    public void Ninguna_clase_puede_llevar_un_tipo_que_no_es_suyo()
    {
        Assert.False(Familiar.ClasePuedeLlevar(GameLimits.ClaseMago, Familiar.Ent), "el Mago no lleva bestias");
        Assert.False(Familiar.ClasePuedeLlevar(GameLimits.ClaseDruida, Familiar.Ely), "el Druida no lleva Ely");
        Assert.False(Familiar.ClasePuedeLlevar(GameLimits.ClaseGuerrero, Familiar.Lobo), "el Guerrero no lleva ninguno");
        Assert.False(Familiar.ClasePuedeLlevar(GameLimits.ClaseMago, 0), "el tipo 0 no es un familiar");
        Assert.False(Familiar.ClasePuedeLlevar(GameLimits.ClaseMago, 200), "un tipo fuera de rango no existe");
    }

    /// <summary>Las nueve fichas son las nueve que <c>NPCs.dat</c> rotula <c>'FAMILIAR</c>, y ninguna se repite:
    /// dos tipos apuntando a la misma criatura sería un error de tabla que nadie notaría jugando.</summary>
    [Fact]
    public void Los_nueve_tipos_apuntan_a_nueve_fichas_distintas()
    {
        var fichas = new System.Collections.Generic.List<short>();
        for (byte tipo = 1; tipo <= Familiar.TipoMaximo; tipo++)
        {
            short f = Familiar.FichaDe(tipo);
            Assert.True(f > 0, $"el tipo {tipo} no tiene ficha");
            fichas.Add(f);
        }
        Assert.Equal(fichas.Count, new System.Collections.Generic.HashSet<short>(fichas).Count);
        Assert.Equal(0, Familiar.FichaDe(0));
        Assert.Equal(0, Familiar.FichaDe(200));
    }

    [Fact]
    public void Limpiar_lo_deja_como_si_nunca_hubiera_existido()
    {
        var f = SubidoHasta(Familiar.Lobo, 20);
        f.Muerto = true;
        f.Limpiar();
        Assert.False(f.Existe);
        Assert.False(f.Invocado);
        Assert.False(f.Muerto);
        Assert.All(f.Habilidades, h => Assert.Equal(0, h));
    }

    [Fact]
    public void En_el_tope_de_nivel_la_experiencia_deja_de_acumularse()
    {
        // Al llegar al techo, GanarExperiencia pone Exp y Elu en 0. Pero la suma ocurre ANTES del bucle y el
        // bucle exige `Elu > 0`, asi que a partir de ahi cada muerte le suma experiencia que ya no sirve para
        // nada y que nadie vuelve a poner en cero. El `.chr` la guarda con un cast a int.
        var f = new Familiar();
        f.Nacer(Familiar.Oso, "Bruno", 100, 5, 10);
        f.GanarExperiencia(long.MaxValue / 4);          // lo lleva al tope de una
        Assert.Equal(Familiar.NivelMaximo, f.Nivel);
        Assert.Equal(0, f.Elu);
        Assert.Equal(0, f.Exp);

        // Y ahora, una muerte mas cualquiera:
        f.GanarExperiencia(1000);
        Assert.Equal(0, f.Exp);   // no deberia acumular nada: ya no hay nivel que comprar
    }
}
