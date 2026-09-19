using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DÓNDE CAZAR, CONTRA LOS MAPAS DE VERDAD. Lo que paga va primero sólo si hay suficiente: en `sano3` siete de ocho bots recién
/// creados se mudaron de la cuna al mapa 62, donde cuatro Águilas pagan para todos, y cazaron cuatro criaturas en media hora.
/// </summary>
public class CotoDelBotTests
{
    /// <summary>
    /// NO VER NINGUNA PRESA NO ES LO MISMO QUE VER UNA QUE NO PAGA, y estaban con la misma paciencia de tres
    /// minutos. Con presas a la vista el bot está cazando aunque no le rindan; sin ninguna, lo único que hace
    /// mientras espera es pasear al azar.
    ///
    /// <para>Medido con una sonda que cuenta, en cada vuelta del ciclo, el primer motivo por el que no pelea:
    /// en 1355 vueltas de ocho bots, <b>63 % fueron sin una sola presa a la vista</b> —y la cuna tiene 53
    /// hostiles— contra 9 % peleando.</para>
    /// </summary>
    [Theory]
    // ¿ve alguna presa? | ms desde la última que pagó | ¿espera antes de buscar coto?
    [InlineData(true, 10_000, true)]      // ve presas y hace poco que una pagó: sigue donde está
    [InlineData(true, 100_000, true)]     // ve presas: aguanta los tres minutos
    [InlineData(true, 200_000, false)]    // ya pasaron: mira si le conviene mudarse
    [InlineData(false, 10_000, true)]     // sin presas, pero recién buscó: no lo hace en cada vuelta
    [InlineData(false, 40_000, false)]    // EL CASO: no ve nada hace rato, deja de pasear y va a buscar
    [InlineData(false, 200_000, false)]
    public void Sin_ninguna_presa_a_la_vista_se_busca_coto_antes(bool veAlguna, long desde, bool espera)
    {
        Assert.Equal(espera, Bot.EsperaParaBuscarCoto(veAlguna, desde));
    }

    /// <summary>Y la espera sin ninguna presa es MÁS CORTA que la otra, que es de lo que se trata.</summary>
    [Fact]
    public void La_espera_sin_presas_es_mas_corta_que_la_otra()
    {
        // El punto donde una ya dejó de esperar y la otra todavía espera.
        Assert.False(Bot.EsperaParaBuscarCoto(false, 60_000));
        Assert.True(Bot.EsperaParaBuscarCoto(true, 60_000));
    }

    private static (Navegante Mapas, GameData Datos)? Mundo()
    {
        var datos = GameData.Load();
        return datos.MapExits.Count == 0 || datos.Npc(500) is null ? null : (new Navegante(datos), datos);
    }

    private static Func<int, bool> Presa(GameData d, int vida, int nivel) =>
        f => Bot.EsPresaPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);

    /// <summary>
    /// UN NIVEL 1 NO SE VA A LAS CUATRO ÁGUILAS DEL 62, el coto que paga más cercano, y caza en la cuna, que tiene 38 presas y ninguna
    /// paga: desde la cuna, y desde la llegada al 208 y al mapa 1, que tienen 8 y 1. Desde el 62 no se prueba: a pie es un pozo, la
    /// salida de vuelta a la cuna no se alcanza desde las Águilas.
    /// </summary>
    [Fact]
    public void Un_nivel_uno_caza_en_la_cuna_y_no_en_las_cuatro_aguilas()
    {
        if (Mundo() is not { } m) return;
        var presa = Presa(m.Datos, 20, 1);
        Assert.True(m.Mapas.CuantasSeAlcanzan(62, (50, 30), f => presa(f) && m.Datos.Npc(f)?.GiveGld > 0) is > 0 and <= 4,
            "el 62 dejó de tener pocas Águilas que pagan: el test no prueba nada");
        foreach (var (mapa, desde) in new[] { (37, ((byte)57, (byte)74)), (208, ((byte)56, (byte)72)), (1, ((byte)58, (byte)45)) })
        {
            var coto = Bot.ElegirCoto(m.Mapas, mapa, desde, presa, f => m.Datos.Npc(f)?.GiveGld > 0,
                                      f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0, null, out bool porElOro, out _);
            Assert.True(coto is not null, $"desde el mapa {mapa} no eligió ningún coto");
            Assert.NotEqual(62, coto!.Value.Mapa);
            Assert.False(porElOro);
            Assert.True(m.Mapas.CuantasSeAlcanzan(coto.Value.Mapa, (coto.Value.X, coto.Value.Y), presa) >= 38,
                $"desde el mapa {mapa} eligió el {coto.Value.Mapa}, con menos presas que la cuna");
        }
    }

    /// <summary>
    /// EN LA CUNA NO SE REPARTE A LA VISTA DE DONDE NACEN LOS LOBOS: para un nivel 1 con pociones, 6 de sus 38 spawns de presa tienen un
    /// Lobo naciendo a la vista, y quedan afuera.
    /// </summary>
    [Fact]
    public void En_la_cuna_no_se_caza_a_la_vista_de_donde_nacen_los_lobos()
    {
        if (Mundo() is not { } m) return;
        var spawns = m.Datos.MapSpawns[37];
        var presas = spawns.Where(sp => Bot.EsPresaPara(m.Datos.Npc(sp.NpcIndex), 20, 1, conPociones: true, curaVeneno: false))
                           .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
        var peligros = spawns.Where(sp => Bot.EsPeligrosoPara(m.Datos.Npc(sp.NpcIndex), 20, 1, conPociones: true, curaVeneno: false))
                             .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
        Assert.Equal(38, presas.Count);
        var lejos = Bot.LejosDelPeligro(presas, peligros);
        Assert.Equal(32, lejos.Count);
        Assert.DoesNotContain(lejos, t => peligros.Any(o => Bot.AlAlcanceDeSuVista(t, o)));
    }

    /// <summary>LO QUE PAGA VALE SI ALCANZA: una que paga por cada cuatro presas del coto más poblado.</summary>
    [Fact]
    public void Lo_que_paga_vale_si_hay_una_por_cada_cuatro_presas()
    {
        Assert.False(Bot.ValeMudarsePorElOro(4, 38));
        Assert.True(Bot.ValeMudarsePorElOro(9, 36));
        Assert.True(Bot.ValeMudarsePorElOro(1, 1));
    }

    /// <summary>
    /// LO QUE RINDE SE MIDE CONTRA LO QUE TIENE, no contra un número fijo. Los números son los de la data, medidos con
    /// `mejor_presa_por_mapa.py`: lo más gordo que un bot se anima a cazar en la cuna y lo más gordo a dos mapas.
    /// </summary>
    [Theory]
    // lo mejor de acá | lo mejor de allá | ¿se muda?
    [InlineData(38, 25, false)]     // nivel 1: Escorpión en la cuna contra Serpiente al lado — se queda
    [InlineData(115, 115, false)]   // nivel 10: el mismo Lobo de los dos lados
    [InlineData(115, 270, true)]    // nivel 13: Lobo contra Esqueleto Guerrero
    [InlineData(115, 505, true)]    // nivel 20: Lobo contra Orco
    [InlineData(0, 1, true)]        // donde no rinde NADA, cualquier cosa es mejor
    [InlineData(0, 0, false)]       // pero si allá tampoco hay nada, no hay por qué moverse
    public void Se_muda_cuando_lo_de_alla_rinde_varias_veces_lo_de_aca(int aca, int alla, bool seMuda)
    {
        Assert.Equal(seMuda, Bot.ValeMudarsePorLoQueRinde(aca, alla));
    }

    /// <summary>
    /// Y SE REVISA AL SUBIR DE NIVEL, aunque tenga presa a la vista: es el otro medio arreglo, porque con la cuna
    /// siempre llena de bichos la búsqueda de coto no arrancaba nunca. Medido en `seguir1`: Ciro de nivel 13 con la
    /// experiencia clavada y 31 criaturas muertas, cazando Lobos en la cuna.
    /// </summary>
    [Theory]
    // nivel con el que eligió el coto | nivel de ahora | ¿vuelve a mirar?
    [InlineData(0, 1, true)]      // recién entrado: todavía no eligió nada
    [InlineData(13, 13, false)]   // sigue en el mismo nivel: se queda donde está
    [InlineData(13, 14, true)]    // subió: mira alrededor
    public void Al_subir_de_nivel_vuelve_a_mirar_donde_esta_cazando(int nivelDelCoto, int nivelAhora, bool revisa)
    {
        Assert.Equal(revisa, Bot.HayQueRevisarElCoto(nivelDelCoto, nivelAhora));
    }

    /// <summary>
    /// Y CONTRA LOS MAPAS DE VERDAD: en la cuna, un bot de nivel 13 tiene que irse a un coto que rinda más, y uno de
    /// nivel 1 tiene que quedarse. Es el óptimo local que dejaba a los bots matando Lobos para siempre: medido en
    /// `seguir1`, Ciro de nivel 13 con 31 criaturas muertas y la experiencia clavada.
    /// </summary>
    [Fact]
    public void Desde_la_cuna_el_de_nivel_trece_se_muda_y_el_de_nivel_uno_no()
    {
        if (Mundo() is not { } m) return;
        var cuna = ((byte)57, (byte)74);
        int Rinde(Func<int, bool> presa) => m.Mapas.LoQueMasRinde(37, cuna, Da(presa))?.Rinde ?? 0;
        Func<int, int> Da(Func<int, bool> presa) => f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0;

        var novato = Presa(m.Datos, 20, 1);
        var crecido = Presa(m.Datos, 141, 13);
        Assert.True(Rinde(crecido) > Rinde(novato), "en la cuna un nivel 13 no se anima a nada mejor que un nivel 1: el test no prueba nada");

        var mejorCerca = m.Mapas.CotoQueMasRinde(37, cuna, Da(crecido), 2);
        Assert.True(mejorCerca is not null, "a dos mapas de la cuna no hay ningún coto");
        Assert.True(Bot.ValeMudarsePorLoQueRinde(Rinde(crecido), mejorCerca!.Value.Rinde),
            $"el de nivel 13 se queda: la cuna rinde {Rinde(crecido)} y lo mejor cerca rinde {mejorCerca.Value.Rinde}");

        var cercaDelNovato = m.Mapas.CotoQueMasRinde(37, cuna, Da(novato), 2);
        Assert.False(cercaDelNovato is { } n && n.Mapa != 37 && Bot.ValeMudarsePorLoQueRinde(Rinde(novato), n.Rinde),
            "el de nivel 1 se va de la cuna, que es el mejor lugar donde puede estar");
    }

    /// <summary>
    /// EL QUE SE MUDA PORQUE ACÁ RINDE POCO NO FRENA EN EL CAMINO POR LO QUE RINDE POCO. Los números son los de
    /// `seguir3`: Ciro eligió irse de m195 (Lobo, 115) a m202 (Esqueleto Guerrero, 270) cuatro veces y no llegó nunca,
    /// porque en m195 siempre tiene un lobo a la vista y el viaje se cortaba en el primer paso.
    /// </summary>
    [Theory]
    // lo que rinde lo del camino | lo que rinde el destino | ¿frena?
    [InlineData(115, 270, false)]   // el lobo del camino contra el esqueleto del destino: sigue de largo
    [InlineData(270, 270, true)]    // un esqueleto en el camino: ése sí
    [InlineData(140, 270, true)]    // la mitad ya alcanza: no se trata de despreciar todo
    [InlineData(115, 0, true)]      // el viaje no es por lo que rinde: vale cualquier presa, como siempre
    [InlineData(0, 0, true)]
    public void Yendo_a_algo_que_rinde_mas_no_frena_por_lo_que_rinde_menos(int camino, int destino, bool frena)
    {
        Assert.Equal(frena, Bot.ValeFrenarElViaje(camino, destino));
    }

    /// <summary>
    /// EL TOPE DE SALTOS CORTA LA EXPLORACIÓN, no la lista del final. Buscar coto enumeraba hasta cinco mapas de
    /// distancia y recién después descartaba los de más de dos, con lo cual el trabajo de los saltos 3, 4 y 5 se
    /// hacía y se tiraba — y cada mapa enumerado cuesta una búsqueda en anchura de hasta 7392 baldosas.
    ///
    /// <para>Medido sobre el grafo de salidas, desde los 322 mapas con criaturas: a dos saltos se llega a 12
    /// mapas (mediana) y a cinco, a 74. Siete veces y media más de los que se usaban.</para>
    /// </summary>
    [Fact]
    public void El_tope_de_saltos_corta_la_exploracion()
    {
        if (Mundo() is not { } m) return;
        var cuna = ((byte)57, (byte)74);
        var lejos = m.Mapas.MapasAlcanzables(37, cuna, 5).ToList();
        var cerca = m.Mapas.MapasAlcanzables(37, cuna, 2).ToList();

        // Sin mapas a más de dos saltos en el mundo de verdad, el test no probaría nada.
        Assert.Contains(lejos, x => x.Saltos > 2);
        Assert.All(cerca, x => Assert.True(x.Saltos <= 2, $"el mapa {x.Mapa} vino a {x.Saltos} saltos con el tope en 2"));
        Assert.True(cerca.Count < lejos.Count, "con el tope en 2 se enumeran los mismos mapas que con 5");
    }

    /// <summary>
    /// NO SE CAZA EN UN MAPA INSEGURO DONDE NACE ALGO QUE LO MATA. En las nueve corridas de campo del 15 al 17/09 hubo quince
    /// muertes y trece fueron en dos mapas inseguros con un bicho así: diez del Basilisco del 202, dos del Señor de las sombras
    /// del 4, una de un Lobo en el mismo 202. En un mapa seguro la runa lo saca, y ahí no se descarta nada.
    /// </summary>
    [Theory]
    // ¿mapa inseguro? | ¿nace algo peligroso para él? | ¿se caza ahí?
    [InlineData(true, true, false)]     // el 202 para un nivel 13: el Basilisco
    [InlineData(true, false, true)]     // inseguro pero sin nada que lo mate
    [InlineData(false, true, true)]     // la cuna para un nivel 1, con sus Lobos: la runa lo saca
    [InlineData(false, false, true)]
    public void Se_caza_ahi_salvo_en_un_mapa_inseguro_con_algo_que_lo_mata(bool inseguro, bool conPeligro, bool seCaza)
    {
        var nacen = new[] { 500, 573 };   // un Murciélago y un Basilisco
        Assert.Equal(seCaza, Bot.SeCazaAhi(inseguro, nacen, f => conPeligro && f == 573));
    }

    private static Func<int, bool> SeCaza(GameData d, int vida, int nivel) =>
        m => Bot.SeCazaAhi(d.IsPkMap(m),
                           d.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>(),
                           f => Bot.EsPeligrosoPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false));

    /// <summary>
    /// Y CONTRA LOS MAPAS DE VERDAD: alrededor del 202, un nivel 13 lo elegía de coto por sus trece Lobos, que pagan; con el
    /// filtro no lo elige nadie, y lo que elige en su lugar es un coto donde se caza.
    /// </summary>
    [Fact]
    public void Alrededor_del_202_ninguno_va_a_cazar_donde_vive_el_Basilisco()
    {
        if (Mundo() is not { } m) return;
        const int vida = 140, nivel = 13;
        var presa = Presa(m.Datos, vida, nivel);
        Func<int, int> rinde = f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0;
        Func<int, bool> paga = f => m.Datos.Npc(f)?.GiveGld > 0;
        var seCaza = SeCaza(m.Datos, vida, nivel);
        Assert.False(seCaza(202), "el 202 no es un mapa inseguro con algo que mata a un nivel 13: el test no prueba nada");

        var entrada202 = m.Datos.MapExits.Values.Where(e => e.DestMap == 202)
                                         .Select(e => ((byte)e.DestX, (byte)e.DestY)).First();
        int loElegian = 0;
        foreach (var (mapa, _, entrada, _) in m.Mapas.MapasAlcanzables(202, entrada202, 2))
        {
            var antes = Bot.ElegirCoto(m.Mapas, mapa, entrada, presa, paga, rinde, null, out _, out _);
            if (antes?.Mapa == 202) loElegian++;
            var ahora = Bot.ElegirCoto(m.Mapas, mapa, entrada, presa, paga, rinde, null, out _, out _, seCaza);
            Assert.True(ahora is not { } a || seCaza(a.Mapa), $"desde el {mapa} eligió el {ahora?.Mapa}, donde no se caza");
        }
        Assert.True(loElegian > 0, "sin el filtro ningún mapa vecino elegía el 202: el test no prueba nada");
    }

    private static readonly IReadOnlySet<int> PielDeLobo = new HashSet<int> { CoverAO.Server.Game.GameLimits.SkinWolfObj };

    /// <summary>
    /// EL SASTRE SIN PIELES VA A DONDE ESTÁN LOS LOBOS, y al que tiene más a dos mapas o menos. Medido en `oficios2`: cazando donde
    /// cazaba cualquiera juntó UNA piel en tres cuartos de hora. Un nivel 12 sale de la cuna, con sus seis Lobos repartidos
    /// entre todos los recién creados; un nivel 16 ya no entra a la zona NEWBIE.
    /// </summary>
    [Theory]
    // nivel | vida | mapa y baldosa de donde sale
    [InlineData(12, 120, 37, 57, 74)]    // la cuna
    [InlineData(16, 160, 1, 58, 45)]     // la llegada al mapa 1
    public void El_sastre_sin_pieles_va_a_donde_estan_los_lobos(int nivel, int vida, int mapa, int x, int y)
    {
        if (Mundo() is not { } m) return;
        var d = m.Datos;
        var presa = Presa(d, vida, nivel);
        Func<int, bool> sirve = f => presa(f) && Bot.SueltaAlgoDe(d.Npc(f), PielDeLobo);
        Func<int, bool> pisable = z => Bot.PuedeEntrar(nivel, d.MapZone(z));
        var seCaza = SeCaza(d, vida, nivel);
        var desde = ((byte)x, (byte)y);
        var coto = Bot.CotoParaSuOficio(m.Mapas, mapa, desde, sirve, pisable, seCaza);
        Assert.NotNull(coto);
        var c = coto.Value;
        Assert.True(seCaza(c.Mapa) && pisable(c.Mapa), $"eligió el {c.Mapa}, donde no se caza o no entra");
        int lobosAlla = m.Mapas.CuantasSeAlcanzan(c.Mapa, (c.X, c.Y), sirve);
        Assert.True(lobosAlla > 0, $"en el {c.Mapa} no hay ningún Lobo que sirva");
        // Ninguno de los que se alcanzan a dos mapas, y donde entra y se caza, tiene más.
        foreach (var (otro, _, entrada, _) in m.Mapas.MapasAlcanzables(mapa, desde, 2, pisable))
            if (seCaza(otro))
                Assert.True(m.Mapas.CuantasSeAlcanzan(otro, entrada, sirve) <= lobosAlla,
                    $"el {otro} tiene más Lobos que el {c.Mapa}, que es el que eligió");
        // Y SIN LA REGLA NO IBA: el coto de siempre tiene menos Lobos, o el test no prueba nada.
        var deSiempre = Bot.ElegirCoto(m.Mapas, mapa, desde, presa, f => d.Npc(f)?.GiveGld > 0,
                                       f => presa(f) ? (int)(d.Npc(f)?.GiveExp ?? 0) : 0, pisable, out _, out _, seCaza);
        Assert.True(deSiempre is not { } s || m.Mapas.CuantasSeAlcanzan(s.Mapa, (s.X, s.Y), sirve) < lobosAlla,
            $"el coto de siempre ({deSiempre?.Mapa}) ya tenía los Lobos: el test no prueba nada");
    }

    /// <summary>
    /// Y POR UNA PIEL NO SE VA A DONDE LO MATAN: alrededor del 202 el que tiene más Lobos es el 202 mismo, donde vive el Basilisco,
    /// y un Sastre de nivel 13 no lo elige desde ninguno de los mapas vecinos.
    /// </summary>
    [Fact]
    public void Por_una_piel_no_se_va_a_donde_vive_el_Basilisco()
    {
        if (Mundo() is not { } m) return;
        const int vida = 140, nivel = 13;
        var d = m.Datos;
        var presa = Presa(d, vida, nivel);
        Func<int, bool> sirve = f => presa(f) && Bot.SueltaAlgoDe(d.Npc(f), PielDeLobo);
        var seCaza = SeCaza(d, vida, nivel);
        var entrada202 = d.MapExits.Values.Where(e => e.DestMap == 202).Select(e => ((byte)e.DestX, (byte)e.DestY)).First();
        int loElegian = 0;
        foreach (var (mapa, _, entrada, _) in m.Mapas.MapasAlcanzables(202, entrada202, 2))
        {
            if (Bot.CotoParaSuOficio(m.Mapas, mapa, entrada, sirve, null, _ => true)?.Mapa == 202) loElegian++;
            var ahora = Bot.CotoParaSuOficio(m.Mapas, mapa, entrada, sirve, null, seCaza);
            Assert.True(ahora is not { } a || seCaza(a.Mapa), $"desde el {mapa} eligió el {ahora?.Mapa}, donde no se caza");
        }
        Assert.True(loElegian > 0, "sin el filtro ningún vecino elegía el 202: el test no prueba nada");
    }

    /// <summary>
    /// DÓNDE SE SIENTA A FABRICAR EL QUE ESTÁ EN UN MAPA INSEGURO: el mapa seguro más cercano. Medido en `oficios3`: el Sastre juntó
    /// 49 pieles cazando Lobos en el 19 y el 24, los dos pk, y no fabricó nada. Desde las baldosas donde estuvo hay dónde, a cinco
    /// saltos o menos, también con el nivel que ya no entra a la cuna.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    public void Desde_donde_caza_lobos_hay_un_mapa_seguro_donde_sentarse_a_fabricar(int nivel)
    {
        if (Mundo() is not { } m) return;
        var d = m.Datos;
        Func<int, bool> seguro = z => !d.IsPkMap(z);
        Func<int, bool> pisable = z => Bot.PuedeEntrar(nivel, d.MapZone(z));
        // Las baldosas donde estuvo el Sastre en `oficios3`, y la llegada al 8 desde el mapa 1.
        foreach (var (mapa, x, y) in new (int, byte, byte)[] { (19, 75, 55), (19, 68, 46), (24, 60, 65), (24, 32, 11), (8, 91, 50) })
        {
            Assert.True(d.IsPkMap(mapa), $"el {mapa} no es inseguro: el test no prueba nada");
            var lugar = Bot.DondeSentarseAFabricar(m.Mapas, mapa, (x, y), seguro, pisable);
            Assert.True(lugar is not null, $"desde el {mapa} ({x},{y}) no hay dónde sentarse a fabricar");
            Assert.True(seguro(lugar.Value.Mapa) && pisable(lugar.Value.Mapa), $"desde el {mapa} eligió el {lugar.Value.Mapa}");
        }
    }

    /// <summary>
    /// EL LUGAR LLENO: con nada que cazar a la vista y tres personas o más, el mapa sale de la elección del coto. Medido en
    /// `oficios3` y `oficios4`: tres cuartos del tiempo de 18 bots en la cuna, casi siempre con diez a dieciocho juntos.
    /// </summary>
    [Theory]
    // personas a la vista | ¿está lleno?
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(8, true)]
    public void Con_tres_personas_a_la_vista_el_lugar_esta_lleno(int personas, bool lleno)
    {
        Assert.Equal(lleno, Bot.EstaLleno(personas));
    }

    /// <summary>Y SE DEJA POR UN RATO, no para siempre: pasado el plazo vuelve a contar como cualquiera.</summary>
    [Fact]
    public void El_lugar_lleno_se_deja_por_un_rato()
    {
        var llenos = new Dictionary<int, long> { [37] = 10_000 };
        Assert.True(Bot.SigueLleno(llenos, 37, 9_999));
        Assert.False(Bot.SigueLleno(llenos, 37, 10_000));
        Assert.False(Bot.SigueLleno(llenos, 208, 0));
    }

    /// <summary>
    /// Y CONTRA LOS MAPAS DE VERDAD: con la cuna llena, de nivel 3 a 11, el otro coto nunca es un mapa donde nace algo peligroso
    /// para él —el 208 del Señor de las sombras—; si no hay ninguno así, ninguno, y se queda. Medido en `oficios6`, con la regla
    /// del lugar lleno pasando sólo por el filtro de siempre: nueve de catorce muertes en el 208, del Señor de las sombras.
    /// </summary>
    [Fact]
    public void Por_un_lugar_lleno_no_se_va_a_donde_vive_el_senor_de_las_sombras()
    {
        if (Mundo() is not { } m) return;
        var d = m.Datos;
        var cuna = ((byte)57, (byte)74);
        var llenos = new Dictionary<int, long> { [37] = long.MaxValue };
        IEnumerable<int> Nacen(int x) => d.MapSpawns.TryGetValue(x, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>();
        int alDoscientosOcho = 0, seVan = 0;
        foreach (var (nivel, vida) in new[] { (3, 30), (5, 50), (8, 80), (11, 110) })
        {
            var presa = Presa(d, vida, nivel);
            Func<int, bool> peligroso = f => Bot.EsPeligrosoPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);
            Func<int, int> rinde = f => presa(f) ? (int)(d.Npc(f)?.GiveExp ?? 0) : 0;
            Func<int, bool> paga = f => d.Npc(f)?.GiveGld > 0;
            var seCaza = SeCaza(d, vida, nivel);
            // Lo de antes: el lugar lleno pasando sólo por el filtro de siempre.
            var antes = Bot.ElegirCoto(m.Mapas, 37, cuna, presa, paga, rinde, null, out _, out _,
                                       x => !Bot.SigueLleno(llenos, x, 0) && seCaza(x));
            if (antes?.Mapa == 208) alDoscientosOcho++;
            var ahora = Bot.OtroCotoSinPeligro(m.Mapas, 37, cuna, presa, paga, rinde, null,
                                               x => Bot.SirveParaIrseDelLleno(Bot.SigueLleno(llenos, x, 0), Nacen(x), peligroso),
                                               null, out _, out _);
            if (ahora is not { } a) continue;
            seVan++;
            Assert.True(a.Mapa != 37 && !Nacen(a.Mapa).Any(peligroso), $"nivel {nivel}: eligió el {a.Mapa}, donde nace algo que lo mata");
            Assert.True(m.Mapas.CuantasSeAlcanzan(a.Mapa, (a.X, a.Y), presa) > 0, $"nivel {nivel}: el {a.Mapa} no tiene presas");
        }
        Assert.True(alDoscientosOcho > 0, "con la regla de antes ninguno iba al 208: el test no prueba nada");
        Assert.True(seVan > 0, "ningún nivel encuentra dónde irse: la regla no mueve a nadie");
    }

    /// <summary>
    /// EL DESTINO EN OTRO MAPA, LEJOS DEL PELIGRO: el coto del 208 que da el navegante es el (45,40), a la vista de donde nace el Señor
    /// de las sombras (47,37); para un nivel 4 se cambia por otro spawn de presa del 208 sin nada peligroso a la vista y al que se
    /// llega desde ahí. Medido en `oficios6`: nueve muertes del Señor de las sombras ahí.
    /// </summary>
    [Fact]
    public void En_el_208_no_se_va_al_lado_del_senor_de_las_sombras()
    {
        if (Mundo() is not { } m) return;
        var d = m.Datos;
        const int vida = 45, nivel = 4;
        var presa = Presa(d, vida, nivel);
        var spawns = d.MapSpawns[208];
        var peligros = spawns.Where(sp => Bot.EsPeligrosoPara(d.Npc(sp.NpcIndex), vida, nivel, conPociones: true, curaVeneno: false))
                             .Select(sp => ((byte)sp.X, (byte)sp.Y)).ToList();
        var presas = spawns.Where(sp => presa(sp.NpcIndex)).Select(sp => ((byte)sp.X, (byte)sp.Y)).Distinct().ToList();
        var elegido = ((byte)45, (byte)40);
        Assert.True(peligros.Any(o => Bot.AlAlcanceDeSuVista(elegido, o)), "el (45,40) no tiene nada peligroso a la vista: el test no prueba nada");
        var destino = Bot.DestinoLejosDelPeligro(m.Mapas, 208, elegido, presas, peligros);
        Assert.NotEqual(elegido, destino);
        Assert.DoesNotContain(peligros, o => Bot.AlAlcanceDeSuVista(destino, o));
        Assert.Contains(destino, presas);
        Assert.NotNull(m.Mapas.HayComoLlegar(208, elegido, destino, 3));
        // Y el que ya está lejos del peligro se queda como está.
        Assert.Equal(destino, Bot.DestinoLejosDelPeligro(m.Mapas, 208, destino, presas, peligros));
    }

    /// <summary>
    /// UN PASO SUELTO NO SACA DEL MAPA: desde (10,82) del 187, un paso al oeste pisa la salida al 186 (toda la columna x = 9), en
    /// cuya entrada vive el Ent; al norte o al sur, no, y una baldosa más adentro, tampoco. Medido en `oficios6`: dos bots de nivel
    /// 12 muertos así, sin haber elegido nunca ir al 186.
    /// </summary>
    [Fact]
    public void Un_paso_al_borde_del_mapa_se_ve_antes_de_darlo()
    {
        if (Mundo() is not { } m) return;
        var salidas = m.Datos.MapExits;
        Assert.Equal(186, salidas[(187, 9, 82)].DestMap);
        Assert.True(Bot.PasoSaleDelMapa(salidas, 187, (10, 82), 4));    // al oeste: la salida al 186
        Assert.False(Bot.PasoSaleDelMapa(salidas, 187, (10, 82), 1));   // al norte
        Assert.False(Bot.PasoSaleDelMapa(salidas, 187, (10, 82), 3));   // al sur
        Assert.False(Bot.PasoSaleDelMapa(salidas, 187, (11, 82), 4));   // una más adentro
    }

    /// <summary>Los mapas por los que pasa un viaje, siguiendo las puertas que da el navegante como las sigue el bot.</summary>
    private static List<int> PorDondePasa(Navegante mapas, GameData d, int mapa, (byte X, byte Y) desde, int destino,
                                          (byte X, byte Y) meta, Func<int, bool> filtro)
    {
        var ruta = new List<int>();
        for (int i = 0; i < 8 && mapa != destino; i++)
        {
            if (mapas.PuertaHacia(mapa, destino, desde, meta, filtro) is not { } p) return new List<int>();
            var llegada = d.MapExits[(mapa, p.Puerta.X, p.Puerta.Y)];
            mapa = llegada.DestMap;
            desde = ((byte)llegada.DestX, (byte)llegada.DestY);
            ruta.Add(mapa);
        }
        return ruta;
    }

    /// <summary>
    /// POR DONDE NO LO MATAN, SI HAY POR DONDE: yendo del 32 al 23, una Cazadora de nivel 7 cruzaba el 4, donde nacen tres Señores de
    /// las sombras, y murió ahí (`oficios7`); con <see cref="Bot.PasaPorAhi"/> el camino va por el 29. Y del 9 al 242 no hay otro que
    /// cruzar el 10 de la Aparición: ahí el filtro no da camino y el bot va por el de siempre.
    /// </summary>
    [Fact]
    public void El_viaje_no_cruza_donde_nace_lo_que_lo_mata_si_hay_otro_camino()
    {
        if (Mundo() is not { } m) return;
        var d = m.Datos;
        const int vida = 70, nivel = 7;
        Func<int, bool> peligroso = f => Bot.EsPeligrosoPara(d.Npc(f), vida, nivel, conPociones: true, curaVeneno: false);
        bool NaceAlgo(int x) => d.MapSpawns.TryGetValue(x, out var sp) && sp.Any(s => peligroso(s.NpcIndex));
        Func<int, bool> pisable = x => Bot.PuedeEntrar(nivel, d.MapZone(x));
        var desde = ((byte)14, (byte)11);
        var meta = ((byte)30, (byte)59);
        var deSiempre = PorDondePasa(m.Mapas, d, 32, desde, 23, meta, pisable);
        Assert.True(deSiempre.Contains(4) && NaceAlgo(4), $"el camino de siempre ({string.Join(" > ", deSiempre)}) no cruza el 4: el test no prueba nada");
        var sinPeligro = PorDondePasa(m.Mapas, d, 32, desde, 23, meta, x => Bot.PasaPorAhi(x, 23, pisable(x), NaceAlgo(x)));
        Assert.True(sinPeligro.Count > 0 && sinPeligro[^1] == 23, "sin peligro no llega al 23");
        Assert.DoesNotContain(sinPeligro.Take(sinPeligro.Count - 1), NaceAlgo);
    }

    /// <summary>
    /// NI EL PASEO NI EL VIAJE SE ARRIMAN A LO QUE LO MATA: un paso que lo deja a tres baldosas o menos de algo peligroso a la vista,
    /// sin alejarlo, no se da; alejarse vale siempre. Con el Señor de las sombras en (47,37), como en el 208, donde en `oficios8` un
    /// Carpintero murió tres veces paseando hacia las presas que tenía a la vista.
    /// </summary>
    [Theory]
    // desde | hasta | ¿lo arrima?
    [InlineData(47, 41, 47, 40, true)]    // de cuatro a tres: entra adonde tendría que huir
    [InlineData(47, 40, 47, 41, false)]   // de tres a cuatro: se aleja
    [InlineData(47, 39, 48, 39, false)]   // de dos a tres, alejándose: vale
    [InlineData(47, 39, 47, 38, true)]    // de dos a uno: al lado
    [InlineData(50, 40, 49, 40, false)]   // de seis a cinco: todavía lejos
    public void Un_paso_no_lo_arrima_a_lo_que_lo_mata(int dx, int dy, int hx, int hy, bool loArrima)
    {
        var senor = new List<(byte X, byte Y)> { (47, 37) };
        Assert.Equal(loArrima, Bot.LoArrimaAlPeligro(((byte)dx, (byte)dy), ((byte)hx, (byte)hy), senor));
        Assert.False(Bot.LoArrimaAlPeligro(((byte)dx, (byte)dy), ((byte)hx, (byte)hy), new List<(byte X, byte Y)>()));
    }

    /// <summary>SI NINGÚN COTO PASA EL FILTRO, SE ELIGE COMO SIEMPRE: mejor cazar con cuidado que quedarse sin dónde.</summary>
    [Fact]
    public void Si_ningun_coto_pasa_el_filtro_se_elige_como_siempre()
    {
        if (Mundo() is not { } m) return;
        var presa = Presa(m.Datos, 140, 13);
        Func<int, int> rinde = f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0;
        Func<int, bool> paga = f => m.Datos.Npc(f)?.GiveGld > 0;
        var cuna = ((byte)57, (byte)74);
        var sinFiltro = Bot.ElegirCoto(m.Mapas, 37, cuna, presa, paga, rinde, null, out _, out _);
        var nadaSirve = Bot.ElegirCoto(m.Mapas, 37, cuna, presa, paga, rinde, null, out _, out _, _ => false);
        Assert.NotNull(sinFiltro);
        Assert.Equal(sinFiltro, nadaSirve);
    }

    /// <summary>LA CUNA SIGUE SIENDO EL COTO DEL NIVEL 1 con el filtro puesto: es segura, y sus Lobos no la descartan.</summary>
    [Fact]
    public void La_cuna_sigue_siendo_el_coto_del_nivel_uno()
    {
        if (Mundo() is not { } m) return;
        var presa = Presa(m.Datos, 20, 1);
        var seCaza = SeCaza(m.Datos, 20, 1);
        Assert.Contains(m.Datos.MapSpawns[37], sp => Bot.EsPeligrosoPara(m.Datos.Npc(sp.NpcIndex), 20, 1, true, false));
        var coto = Bot.ElegirCoto(m.Mapas, 37, ((byte)57, (byte)74), presa, f => m.Datos.Npc(f)?.GiveGld > 0,
                                  f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0, null, out _, out _, seCaza);
        Assert.Equal(37, coto?.Mapa);
    }

    private static readonly string[] LosOcho = { "Aldo", "Bruma", "Ciro", "Dalia", "Elmo", "Fabia", "Gonzo", "Hilda" };

    /// <summary>CADA BOT TIENE SU COTO ENTRE LOS QUE RINDEN IGUAL: el mismo siempre para el mismo bot, y no el mismo para todos.</summary>
    [Fact]
    public void Cuanto_le_gusta_un_mapa_es_fijo_por_bot_y_distinto_entre_bots()
    {
        var mapas = new[] { 807, 814, 823, 824 };
        int Favorito(string nombre) => mapas.OrderByDescending(x => Bot.CuantoLeGusta(Bot.SemillaDelNombre(nombre), x)).First();
        Assert.All(LosOcho, n => Assert.Equal(Favorito(n), Favorito(n)));
        Assert.True(LosOcho.Select(Favorito).Distinct().Count() >= 3,
            "los ocho bots prefieren casi todos el mismo mapa: el desempate no reparte");
    }

    /// <summary>
    /// Y CONTRA LOS MAPAS DE VERDAD, LO QUE SE VIO EN `tarde2`: los bots de nivel 14 a 19 salían de comprar en el 815 y se
    /// iban todos al 807, el más cercano entre los que empatan con la Rata Gigante; cinco en veintidós presas. Con el
    /// desempate se reparten, y ninguno elige un coto que rinda menos que el que elegían.
    /// </summary>
    [Fact]
    public void Los_que_salen_de_comprar_del_815_no_van_todos_al_mismo_coto()
    {
        if (Mundo() is not { } m) return;
        const int vida = 180, nivel = 18;
        var presa = Presa(m.Datos, vida, nivel);
        Func<int, int> rinde = f => presa(f) ? (int)(m.Datos.Npc(f)?.GiveExp ?? 0) : 0;
        Func<int, bool> paga = f => m.Datos.Npc(f)?.GiveGld > 0;
        var seCaza = SeCaza(m.Datos, vida, nivel);
        var tienda = ((byte)60, (byte)73);
        var todos = Bot.ElegirCoto(m.Mapas, 815, tienda, presa, paga, rinde, null, out _, out int rindeAntes, seCaza);
        Assert.True(todos is not null && rindeAntes > 0, "desde el 815 no se elige coto por lo que rinde: el test no prueba nada");

        var elegidos = new List<int>();
        foreach (var nombre in LosOcho)
        {
            int propio = Bot.SemillaDelNombre(nombre);
            var suyo = Bot.ElegirCoto(m.Mapas, 815, tienda, presa, paga, rinde, null, out _, out int rindeSuyo, seCaza,
                                      x => Bot.CuantoLeGusta(propio, x));
            Assert.NotNull(suyo);
            Assert.Equal(rindeAntes, rindeSuyo);
            elegidos.Add(suyo!.Value.Mapa);
        }
        Assert.True(elegidos.Distinct().Count() >= 3, $"los ocho eligen {string.Join(", ", elegidos)}: no se reparten");
    }

    /// <summary>
    /// EN UN PUEBLO NO NACE NADA, ASI QUE NO HAY NADA QUE ESPERAR. La paciencia de treinta segundos se aplicaba en
    /// cualquier mapa, incluido aquel al que el bot va a comprar. Contra la data de verdad: Ullathorpe (1) y las otras
    /// ciudades Nix y Banderbill no tienen una sola criatura hostil; la cuna (37) y Ullathorpe, si.
    /// </summary>
    [Fact]
    public void En_un_mapa_donde_no_nace_nada_no_se_espera()
    {
        var d = GameData.Load();
        bool EsHostil(int f) => d.Npc((short)f) is { Hostile: true };
        List<MapNpcSpawn> DeEse(int m) => d.MapSpawns.TryGetValue(m, out var s) ? s.ToList() : new List<MapNpcSpawn>();

        // La cuna y el mapa 1 existen en la data de verdad, o el test no probaria nada.
        Assert.NotEmpty(DeEse(37));

        // Donde nace algo hostil, la regla dice que si.
        Assert.True(Bot.AlgunoEsHostil(DeEse(37), EsHostil));

        // Y en las ciudades, que es donde el bot cae al ir de compras, no nace una sola.
        foreach (int ciudad in new[] { 34, 59 })   // Nix y Banderbill Central
            Assert.False(Bot.AlgunoEsHostil(DeEse(ciudad), EsHostil),
                         $"el mapa {ciudad} tiene criaturas hostiles y el test lo toma por ciudad");

        // Ullathorpe es el borde y por eso va: tiene UNA sola hostil —la serpiente—, asi que si puede aparecer
        // algo y la paciencia corre. La regla no mira cuantas: eso lo decide el coto.
        Assert.True(Bot.AlgunoEsHostil(DeEse(1), EsHostil));

        // Una lista vacia tampoco: no hay nada que esperar donde no hay nada.
        Assert.False(Bot.AlgunoEsHostil(new List<MapNpcSpawn>(), EsHostil));
    }

    /// <summary>
    /// UNA PRESA A LA QUE NUNCA VA NO CUENTA COMO "ACA HAY ALGO". La paciencia larga —tres minutos— es para cuando el bot
    /// tiene algo para cazar donde esta; una presa mas lejos que lo que su temperamento camina no se pelea nunca, asi que
    /// contarla lo dejaba paseando alrededor de algo que no iba a atacar.
    /// </summary>
    [Fact]
    public void La_presa_mas_lejos_de_lo_que_camina_no_cuenta()
    {
        // El tranquilo camina seis: la de nueve pasos no la va a buscar.
        Assert.False(Bot.VeUnaPresaQueIriaABuscar(9, 6));
        Assert.True(Bot.VeUnaPresaQueIriaABuscar(6, 6));
        Assert.True(Bot.VeUnaPresaQueIriaABuscar(1, 6));

        // Sin ninguna presa a la vista tampoco, que es el caso que ya existia.
        Assert.False(Bot.VeUnaPresaQueIriaABuscar(null, 12));

        // Y la misma presa, para el cazador que camina doce, si cuenta.
        Assert.True(Bot.VeUnaPresaQueIriaABuscar(9, 12));

        // La paciencia es la que cambia con eso: corta sin presa que valga, larga con una.
        Assert.True(Bot.EsperaParaBuscarCoto(true, 60_000));
        Assert.False(Bot.EsperaParaBuscarCoto(false, 60_000));
    }
}
