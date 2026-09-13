using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT ABRE LAS PUERTAS. Era el hueco que le cerraba medio juego: 494 puertas en el mundo, 381 sin llave, y ni
/// una línea para abrirlas — así que todo lo que vive adentro de un edificio le era inalcanzable. Medido con el
/// navegante real: los ocho vendedores de pergaminos del mapa 20 dan "no hay camino" desde la entrada, y las once
/// puertas de ese mapa están cerradas y ninguna tiene llave.
/// </summary>
public class PuertasDelBotTests
{
    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    /// <summary>
    /// QUÉ PUERTA SE PUEDE ABRIR, contra la data de verdad: cerrada, sin llave y con versión abierta. Y la abierta
    /// NO se vuelve a abrir, que es lo que evita que el bot se quede haciendo doble clic para siempre.
    /// </summary>
    [Fact]
    public void La_puerta_cerrada_sin_llave_se_abre_y_la_abierta_no()
    {
        var datos = Datos();
        var puertas = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).Where(o => o is { IsDoor: true }).ToList();
        Assert.True(puertas.Count >= 10, $"la data trae {puertas.Count} puertas: el test no prueba nada");

        var cerradaSinLlave = puertas.FirstOrDefault(o => o!.Closed == 1 && o.Llave == 0 && o.IndexAbierta > 0);
        var abierta = puertas.FirstOrDefault(o => o!.Closed == 0);
        var conLlave = puertas.FirstOrDefault(o => o!.Llave != 0);

        Assert.True(cerradaSinLlave is not null, "no hay ninguna puerta cerrada sin llave: el test no prueba nada");
        Assert.True(Bot.PuertaQueSeAbre(cerradaSinLlave));
        if (abierta is not null) Assert.False(Bot.PuertaQueSeAbre(abierta), $"'{abierta.Name}' ya está abierta");
        if (conLlave is not null) Assert.False(Bot.PuertaQueSeAbre(conLlave), $"'{conLlave.Name}' pide llave");
        Assert.False(Bot.PuertaQueSeAbre(null));
        var noEsPuerta = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i)).First(o => o is { IsWeapon: true });
        Assert.False(Bot.PuertaQueSeAbre(noEsPuerta));
    }

    /// <summary>
    /// LA DISTANCIA ES LA EUCLÍDEA DEL ORIGINAL, no el Chebyshev de los gates de NPC: la diagonal de dos —2,83— NO
    /// alcanza, y el servidor contesta "Estás muy lejos". Si el bot usa la medida equivocada manda el doble clic
    /// desde donde no lo van a escuchar.
    /// </summary>
    [Theory]
    // dx | dy | ¿alcanza?
    [InlineData(0, 0, true)]
    [InlineData(2, 0, true)]      // justo, en línea recta
    [InlineData(0, -2, true)]
    [InlineData(1, 1, true)]      // la diagonal de uno: 1,41
    [InlineData(2, 2, false)]     // la diagonal de dos: 2,83 — NO alcanza
    [InlineData(3, 0, false)]
    [InlineData(2, 1, false)]     // 2,24
    public void Se_abre_a_distancia_euclidea_de_dos(int dx, int dy, bool alcanza)
    {
        Assert.Equal(alcanza, Bot.AlAlcanceDeLaPuerta(dx, dy));
    }

    /// <summary>
    /// Y EL CASO QUE LO MOTIVA, contra los mapas de verdad: el mapa 20 (Rinkel) tiene puertas cerradas sin llave, y
    /// detrás de ellas están los vendedores de pergaminos a los que el bot no llegaba.
    /// </summary>
    [Fact]
    public void En_rinkel_hay_puertas_cerradas_sin_llave_y_vendedores_detras()
    {
        var datos = Datos();
        if (!datos.MapObjects.TryGetValue(20, out var objetos)) return;   // sin la data de mapas no hay nada que mirar

        int puertasQueSeAbren = objetos.Count(o => Bot.PuertaQueSeAbre(datos.Obj((short)o.ObjIndex)));
        Assert.True(puertasQueSeAbren >= 5,
            $"el mapa 20 tiene {puertasQueSeAbren} puertas que el bot puede abrir; se esperaban varias");

        if (!datos.MapSpawns.TryGetValue(20, out var spawns)) return;
        int vendenPergaminos = spawns.Count(s => datos.Npc(s.NpcIndex) is { StockItems: { Count: > 0 } st }
                                                 && st.Any(o => datos.Obj((short)o.ObjIndex) is { IsPergamino: true }));
        Assert.True(vendenPergaminos > 0, "el mapa 20 dejó de tener vendedores de pergaminos: el caso ya no aplica");
    }

    /// <summary>
    /// SE VIAJA AL PUEBLO AUNQUE NO SE LLEGUE AL MERCADER. Buscar la puerta donde el bot está parado sólo sirve si
    /// ya llegó, y en el bosque no hay ninguna: medido en `puertas1`, once veces "NINGUNA a la vista" con los bots
    /// en m202 y m807. El mercader de ciudad está adentro de un edificio, así que "no se llega a ninguno" es lo
    /// normal desde afuera — pero el MAPA sí se encuentra, y una vez adentro la puerta está a la vista.
    /// </summary>
    [Theory]
    // ¿hay mercader al que se llegue? | ¿el mapa que lo tiene es otro? | ¿se viaja igual?
    [InlineData(false, true, true)]     // el caso: nadie alcanzable, pero el pueblo está a unos saltos
    [InlineData(false, false, false)]   // ya está en ese mapa: lo que falta es la puerta, no el viaje
    [InlineData(true, true, false)]     // hay uno alcanzable: se va por el camino de siempre
    [InlineData(true, false, false)]
    public void Se_viaja_al_pueblo_cuando_no_hay_mercader_alcanzable(bool hayDondeLlegar, bool otroMapa, bool viaja)
    {
        Assert.Equal(viaja, Bot.ValeViajarAlPueblo(hayDondeLlegar, otroMapa));
    }

    /// <summary>
    /// SE CAMINA HACIA EL MERCADER AUNQUE NO SE LLEGUE. El servidor sólo manda los objetos del suelo que entran en la
    /// vista del jugador (unas ocho baldosas) y la puerta del edificio está a veinte: el bot la estaba esperando en
    /// vez de ir a buscarla, y por eso la sonda decía "NINGUNA a la vista" incluso ya dentro del pueblo.
    /// </summary>
    [Theory]
    // ¿está en el mapa del mercader? | ¿lo alcanza? | ¿camina igual?
    [InlineData(true, false, true)]     // el caso: está en el pueblo y el mercader es inalcanzable
    [InlineData(true, true, false)]     // lo alcanza: va por el camino de siempre
    [InlineData(false, false, false)]   // está en otro mapa: primero el viaje
    [InlineData(false, true, false)]
    public void Se_camina_hacia_el_mercader_para_que_la_puerta_entre_en_vista(bool enSuMapa, bool loAlcanza, bool camina)
    {
        Assert.Equal(camina, Bot.ValeAcercarseAunqueNoLlegue(enSuMapa, loAlcanza));
    }

    /// <summary>
    /// ABRIR UNA PUERTA REINICIA LA BÚSQUEDA. El servidor desbloquea sus dos baldosas y el navegante las lee en vivo,
    /// así que un camino que no existía ahora sí — pero los tres caminos caros estaban atados al primer intento, y el
    /// bot abría la puerta y en la misma vuelta se daba por vencido. Medido en `puertas7`, dos líneas seguidas:
    /// `abierta=True` y acto seguido `viaje al pueblo: intento=1 | mapa con mercader=NINGUNO`.
    /// </summary>
    [Theory]
    // intento | ¿abrió una puerta? | ¿se prueban los caminos caros?
    [InlineData(0, false, true)]    // el primero, siempre
    [InlineData(1, false, false)]   // sin novedades: no se insiste
    [InlineData(2, false, false)]
    [InlineData(1, true, true)]     // el caso: acaba de abrir una puerta, el mundo cambió
    [InlineData(2, true, true)]
    public void Abrir_una_puerta_reinicia_la_busqueda(int intento, bool abrio, bool prueba)
    {
        Assert.Equal(prueba, Bot.TocaProbarLosCaminosCaros(intento, abrio));
    }

    /// <summary>
    /// LA PUERTA SE BUSCA DONDE ATIENDEN. Una puerta es un camino hacia un mercader de ESE mapa: sin mapa que
    /// atienda y sin mercader que le venda, no hay nada detrás de ninguna. Medido contra la data con el navegante
    /// replicado: de los ocho mapas donde el bot entró a este camino en `puertas7`, atienden CERO — y en el m34
    /// (once mercaderes, `battleMode=1`) la puerta que abrió sumó treinta baldosas y ningún mercader.
    /// </summary>
    [Theory]
    // ¿atiende el mapa? | ¿hay quien le venda acá? | ¿vale buscar la puerta?
    [InlineData(true, true, true)]      // el único caso que sirve
    [InlineData(false, true, false)]    // el m34: once mercaderes y battleMode 1, ninguno le vende
    [InlineData(true, false, false)]    // atiende pero no hay mercader: la puerta no lleva a nadie
    [InlineData(false, false, false)]   // el campo, que es donde estaba cinco de cada ocho veces
    public void La_puerta_se_busca_donde_atienden(bool atiende, bool hayQuienLeVenda, bool vale)
    {
        Assert.Equal(vale, Bot.ValeBuscarLaPuertaAca(atiende, hayQuienLeVenda));
    }

    /// <summary>
    /// LA PUERTA QUE CONVIENE ES LA QUE DESTRABA, no la más cercana. Medido contra la data: de los 105 mercaderes
    /// encerrados de los mapas que atienden, abrir una puerta libera 30 — y en el mapa 20 la correcta libera SEIS
    /// vendedores de pergaminos a seis pasos de la entrada, mientras la que el bot abrió en campo (mapa 34) no
    /// liberaba a ninguno.
    /// </summary>
    [Fact]
    public void Entre_las_puertas_gana_la_que_destraba_aunque_este_mas_lejos()
    {
        var candidatas = new ((byte X, byte Y) Donde, short Obj, int Pasos)[]
        {
            (((byte)43, (byte)78), (short)8, 6),      // la de al lado, que no lleva a nadie
            (((byte)50, (byte)86), (short)1404, 12),  // la que destraba
        };
        var elegida = Bot.LaPuertaQueConviene(candidatas, donde => donde == ((byte)50, (byte)86));
        Assert.Equal(((byte)50, (byte)86), elegida?.Donde);
    }

    /// <summary>Y entre dos que destraban gana la más cercana, que es la más barata: vienen ordenadas.</summary>
    [Fact]
    public void Entre_dos_que_destraban_gana_la_mas_cercana()
    {
        var candidatas = new ((byte X, byte Y) Donde, short Obj, int Pasos)[]
        {
            (((byte)10, (byte)10), (short)8, 4),
            (((byte)20, (byte)20), (short)8, 9),
        };
        var elegida = Bot.LaPuertaQueConviene(candidatas, _ => true);
        Assert.Equal(((byte)10, (byte)10), elegida?.Donde);
    }

    /// <summary>Si ninguna destraba no se abre ninguna: abrir por abrir gasta el viaje y la puerta no se vuelve a
    /// cerrar. Es el caso del mapa 34, con dieciséis puertas y ninguna que sirviera desde donde estaba.</summary>
    [Fact]
    public void Si_ninguna_destraba_no_se_abre_ninguna()
    {
        var candidatas = new ((byte X, byte Y) Donde, short Obj, int Pasos)[]
        {
            (((byte)43, (byte)78), (short)8, 6),
        };
        Assert.Null(Bot.LaPuertaQueConviene(candidatas, _ => false));
    }

    /// <summary>Sin puertas a la vista, nada.</summary>
    [Fact]
    public void Sin_puertas_a_la_vista_no_hay_nada_que_abrir()
    {
        Assert.Null(Bot.LaPuertaQueConviene(Array.Empty<((byte X, byte Y) Donde, short Obj, int Pasos)>(), _ => true));
    }

    /// <summary>
    /// UNA PUERTA DESTRABA SUS DOS BALDOSAS, <c>(X,Y)</c> y <c>(X-1,Y)</c>: es lo que hace `AccionParaPuerta` al
    /// abrirla (PacketDispatcher.cs:8163). Con una sola, un vano de dos de ancho seguía cortando el camino.
    /// </summary>
    [Fact]
    public void Una_puerta_destraba_sus_dos_baldosas()
    {
        var libres = Navegante.BaldosasQueDestraba(((byte)43, (byte)78));
        Assert.Equal(2, libres.Count);
        Assert.Contains(43 | (78 << 8), libres);
        Assert.Contains(42 | (78 << 8), libres);
    }

    /// <summary>
    /// LA MEJORA, CONTRA LA DATA. La corrida de campo verificó el mecanismo —ver dos puertas, evaluar las dos,
    /// elegir y abrir— pero ahí la que destrababa era ADEMÁS la más cercana, así que no distinguía la conducta
    /// nueva de la vieja. El mapa 20 tampoco sirve: desde su entrada la más cercana ya destraba.
    ///
    /// <para>El caso que SÍ distingue, buscado en los dieciséis mapas que atienden con mercader y puertas: el
    /// <b>mapa 248 desde la entrada (50,50)</b>, con cuatro puertas al alcance. La más cercana —(49,44), a cinco
    /// pasos— no libera a NADIE; la de (49,59), a ocho, libera CUATRO mercaderes. El bot que abre la más cercana
    /// abre la inútil y se queda afuera.</para>
    /// </summary>
    [Fact]
    public void Elige_la_que_destraba_y_no_la_mas_cercana()
    {
        var datos = Datos();
        if (!datos.MapObjects.TryGetValue(248, out _) || !datos.MapSpawns.TryGetValue(248, out _)) return;
        var navegante = new Navegante(datos);
        (byte X, byte Y) entrada = (50, 50);

        // Sirve cualquiera que comercie: en el 248 los cuatro que la puerta libera son mercaderes comunes.
        bool Sirve(int ficha) => datos.Npc((short)ficha) is { StockItems: { Count: > 0 } };

        // Las puertas del mapa que el bot puede abrir, con los pasos REALES desde la entrada.
        var candidatas = datos.MapObjects[248]
            .Where(o => Bot.PuertaQueSeAbre(datos.Obj((short)o.ObjIndex)))
            .Select(o => (Donde: ((byte)o.X, (byte)o.Y), Obj: (short)o.ObjIndex,
                          Pasos: navegante.HayComoLlegar(248, entrada, ((byte)o.X, (byte)o.Y), 2)?.Count ?? int.MaxValue))
            .Where(p => p.Pasos != int.MaxValue)
            .OrderBy(p => p.Pasos)
            .ToList();
        Assert.True(candidatas.Count >= 2,
            $"desde {entrada} se alcanzan {candidatas.Count} puertas del mapa 248: el caso necesita al menos dos");

        var destraban = candidatas.Where(p => navegante.AbrirlaDaCamino(248, entrada, p.Donde, Sirve)).ToList();
        Assert.NotEmpty(destraban);

        // EL CORAZÓN: la más cercana NO es de las que destraban, así que elegir por cercanía deja al bot afuera.
        Assert.False(navegante.AbrirlaDaCamino(248, entrada, candidatas[0].Donde, Sirve),
            $"la puerta más cercana {candidatas[0].Donde} ahora destraba: el caso dejó de distinguir las dos conductas");

        // Y la regla elige una de las que destraban, no la de al lado.
        var elegida = Bot.LaPuertaQueConviene(candidatas,
            donde => navegante.AbrirlaDaCamino(248, entrada, donde, Sirve));
        Assert.NotNull(elegida);
        Assert.Contains(elegida!.Value.Donde, destraban.Select(d => d.Donde));
        Assert.NotEqual(candidatas[0].Donde, elegida!.Value.Donde);
    }
}
