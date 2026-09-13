using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL NAVEGANTE, CONTRA LOS MAPAS DE VERDAD.
///
/// <para>Es la pieza de la que dependen todas las cosas que un bot hace a propósito: ir a comprar, volver al
/// pueblo, buscar al sacerdote, llegar al agua. Y es la única del bot que se puede probar sin levantar un
/// servidor, porque no habla el protocolo: sólo lee los mapas.</para>
///
/// <para>Los tests se saltean solos si la carpeta de datos del juego no está — el navegante sin mapas no
/// tiene nada que decir, y un rojo por eso hablaría del entorno y no del código.</para>
/// </summary>
public class NaveganteTests
{
    private static (Navegante Mapas, GameData Datos)? Mundo()
    {
        var datos = GameData.Load();
        return datos.MapExits.Count == 0 ? null : (new Navegante(datos), datos);
    }

    [Fact]
    public void El_mundo_trae_salidas_entre_mapas()
    {
        if (Mundo() is not { } m) return;
        Assert.True(m.Mapas.MapasConSalida > 50,
            $"sólo {m.Mapas.MapasConSalida} mapas con salida: sin eso los bots no pueden viajar a ningún lado");
    }

    /// <summary>Un camino corto adentro de un mapa. Cada paso tiene que ser de UNA baldosa en cruz, que es
    /// como camina el juego: un camino con diagonales o con saltos no lo puede seguir nadie.</summary>
    [Fact]
    public void El_camino_avanza_de_a_una_baldosa_en_cruz()
    {
        if (Mundo() is not { } m) return;
        var camino = m.Mapas.Camino(1, (50, 50), (54, 53));
        if (camino is null) return;   // ese tramo puede estar tapiado en esta data

        var antes = ((byte X, byte Y))(50, 50);
        foreach (var b in camino)
        {
            int d = Math.Abs(b.X - antes.X) + Math.Abs(b.Y - antes.Y);
            Assert.True(d == 1, $"de ({antes.X},{antes.Y}) a ({b.X},{b.Y}) hay {d} baldosas, y el juego camina de a una");
            antes = b;
        }
        Assert.Equal(((byte)54, (byte)53), antes);
    }

    [Fact]
    public void Ir_a_donde_ya_se_esta_no_cuesta_ningun_paso()
    {
        if (Mundo() is not { } m) return;
        Assert.Empty(m.Mapas.Camino(1, (50, 50), (50, 50))!);
    }

    /// <summary>Una pared no tiene camino. Es lo que evita que el bot se quede empujándola.</summary>
    [Fact]
    public void A_una_baldosa_bloqueada_no_hay_camino()
    {
        if (Mundo() is not { } m) return;
        // Se busca una pared de verdad en el mapa 1; si no hubiera ninguna, no hay nada que comprobar.
        for (int x = 10; x < 90; x++)
            for (int y = 10; y < 90; y++)
                if (m.Datos.IsBlockedTile(1, x, y))
                {
                    Assert.Null(m.Mapas.Camino(1, (50, 50), ((byte)x, (byte)y)));
                    return;
                }
    }

    /// <summary>Del mapa de nacimiento se tiene que poder salir. Si esto fallara, los bots estarían encerrados
    /// donde nacen y ni el comercio ni los oficios podrían ocurrir nunca.</summary>
    [Fact]
    public void Desde_el_mapa_de_nacimiento_se_llega_a_otro()
    {
        if (Mundo() is not { } m) return;
        Assert.NotNull(m.Mapas.PuertaHacia(37, 34));
    }

    /// <summary>Y se tiene que poder ubicar a un mercader. Es el destino del que dependen la herramienta del
    /// oficio y la reposición de pociones.</summary>
    [Fact]
    public void Se_encuentra_donde_hay_un_mercader()
    {
        if (Mundo() is not { } m) return;
        var donde = m.Mapas.DondeHay(37, (50, 50), ficha => m.Datos.Npc(ficha)?.StockItems is { Count: > 0 });
        Assert.NotNull(donde);
    }

    /// <summary>
    /// LO QUE ELIGE <c>DondeHay</c> EN OTRO MAPA TIENE UNA PUERTA A LA QUE SE LLEGA. Si el destino se eligiera por
    /// una salida sin camino, <c>PuertaHacia</c> la descartaría, el viaje moriría en el primer tramo y la búsqueda
    /// siguiente elegiría lo mismo: un Minero en el mapa 62 mandado al vendedor de piquetes del 194.
    /// </summary>
    [Fact]
    public void Lo_que_elige_DondeHay_en_otro_mapa_tiene_una_puerta_a_la_que_se_llega()
    {
        if (Mundo() is not { } m) return;
        bool Atiende(int mapa) => m.Datos.MapBattleMode(mapa) is 2 or 3;
        int miradas = 0;
        foreach (var (mapa, desde) in new (int, (byte X, byte Y))[] { (62, (70, 72)), (37, (57, 74)), (37, (80, 88)), (208, (48, 51)) })
            foreach (short herramienta in new short[] { 881, 127, 187 })
            {
                bool Vende(int ficha) => m.Datos.Npc(ficha)?.StockItems?.Any(s => s.ObjIndex == herramienta) == true;
                if (m.Mapas.DondeHay(mapa, desde, Vende, Atiende) is not { Saltos: > 0 } lugar) continue;
                miradas++;
                Assert.True(m.Mapas.PuertaHacia(mapa, lugar.Mapa, desde) is not null,
                    $"DondeHay mandó de m{mapa} {desde} al m{lugar.Mapa} y no hay puerta a la que se llegue");
            }
        Assert.True(miradas > 0, "ninguna búsqueda eligió otro mapa: el test no miró nada");
    }

    /// <summary>
    /// LO QUE ELIGE <c>DondeHay</c> EN OTRO MAPA SE ALCANZA DESDE LA BALDOSA POR LA QUE SE ENTRA, y la puerta que da
    /// <c>PuertaHacia</c> con esa meta lleva a esa entrada. Si no, el bot llega y no encuentra camino: una
    /// Pescadora del 62 mandada al sacerdote del 64, que desde la entrada (10,36) no se alcanza.
    /// </summary>
    [Fact]
    public void Lo_que_elige_DondeHay_en_otro_mapa_se_alcanza_desde_la_entrada()
    {
        if (Mundo() is not { } m) return;
        bool EsSacerdote(int ficha) => m.Datos.Npc(ficha)?.NpcType == 1;
        bool VendeCana(int ficha) => m.Datos.Npc(ficha)?.StockItems?.Any(s => s.ObjIndex == 881) == true;
        int miradas = 0;
        foreach (var (mapa, desde, sirve) in new (int, (byte X, byte Y), Func<int, bool>)[]
                 { (62, (44, 32), EsSacerdote), (62, (54, 80), EsSacerdote), (37, (57, 74), VendeCana), (37, (57, 74), EsSacerdote) })
        {
            if (m.Mapas.DondeHay(mapa, desde, sirve) is not { Saltos: 1 } lugar) continue;
            var puerta = m.Mapas.PuertaHacia(mapa, lugar.Mapa, desde, (lugar.X, lugar.Y));
            Assert.True(puerta is not null, $"DondeHay mandó de m{mapa} al m{lugar.Mapa} y no hay puerta hacia allá");
            var llegada = m.Datos.MapExits[(mapa, puerta!.Value.Puerta.X, puerta.Value.Puerta.Y)];
            miradas++;
            Assert.True(m.Mapas.HayComoLlegar(lugar.Mapa, ((byte)llegada.DestX, (byte)llegada.DestY), (lugar.X, lugar.Y), 3) is not null,
                $"de m{mapa} a m{lugar.Mapa} ({lugar.X},{lugar.Y}): desde la entrada ({llegada.DestX},{llegada.DestY}) no se llega");
        }
        Assert.True(miradas > 0, "ninguna búsqueda eligió un mapa vecino: el test no miró nada");
    }

    /// <summary>
    /// DESDE LA CUNA SE ENCUENTRA DÓNDE MINAR. Los yacimientos están en pocos mapas y en los más cercanos el centro es
    /// pared: buscando desde (50,50), el Minero no encontraba nunca dónde trabajar.
    /// </summary>
    [Fact]
    public void Desde_la_cuna_se_encuentra_donde_minar()
    {
        if (Mundo() is not { } m) return;
        bool EsYacimiento(int obj) => m.Datos.Obj(obj) is { IsDeposit: true };
        var lugar = m.Mapas.DondeSeTrabaja(37, false, EsYacimiento, (57, 74));
        Assert.True(lugar is not null, "desde la cuna no se encuentra ningún yacimiento al que se llegue");
    }

    /// <summary>
    /// SIN ZONAS PK, LA CUNA TIENE DÓNDE PESCAR Y TALAR PERO NO DÓNDE MINAR. Es la premisa de la regla de los bots: no se
    /// viaja a trabajar a un mapa pk, y todas las minas del mundo lo son; el agua y los árboles de la cuna, no.
    /// </summary>
    [Fact]
    public void Sin_zonas_pk_la_cuna_tiene_donde_pescar_y_talar_pero_no_donde_minar()
    {
        if (Mundo() is not { } m) return;
        bool Seguro(int mapa) => !m.Datos.IsPkMap(mapa);
        bool EsYacimiento(int obj) => m.Datos.Obj(obj) is { IsDeposit: true };
        bool EsArbol(int obj) => m.Datos.Obj(obj) is { IsTree: true };
        Assert.True(m.Mapas.DondeSeTrabaja(37, true, null, (57, 74), Seguro) is not null, "sin pk no hay dónde pescar desde la cuna");
        Assert.True(m.Mapas.DondeSeTrabaja(37, false, EsArbol, (57, 74), Seguro) is not null, "sin pk no hay dónde talar desde la cuna");
        Assert.True(m.Mapas.DondeSeTrabaja(37, false, EsYacimiento, (57, 74), Seguro) is null, "hay una mina fuera de zona pk: la regla se puede aflojar");
    }

    /// <summary>
    /// DESDE UN MAPA PK SE ENCUENTRA UNO SEGURO AL QUE SE LLEGA. Un bot al que se le vence la sesión en un mapa pk no se
    /// va a dormir ahí (el <c>Quit</c> arranca una cuenta y lo pueden matar): camina hasta el mapa seguro más cercano.
    /// Se prueba desde cada baldosa por la que se entra al 196, donde se vio a un bot cortar su sesión: lo elegido
    /// tiene que ser seguro y tener una puerta a la que se llegue desde ahí.
    /// </summary>
    [Fact]
    public void Desde_un_mapa_pk_se_encuentra_uno_seguro_al_que_se_llega()
    {
        if (Mundo() is not { } m) return;
        bool Seguro(int mapa) => !m.Datos.IsPkMap(mapa);
        Assert.True(m.Datos.IsPkMap(196), "el 196 tenía que ser pk o esto no prueba nada");
        var entradas = m.Datos.MapExits.Values.Where(e => e.DestMap == 196)
            .Select(e => ((byte)e.DestX, (byte)e.DestY)).Distinct().ToList();
        Assert.NotEmpty(entradas);

        int conRefugio = 0;
        foreach (var desde in entradas)
        {
            if (m.Mapas.MapaMasCercano(196, desde, Seguro) is not { } refugio) continue;
            conRefugio++;
            Assert.True(Seguro(refugio.Mapa), $"el mapa {refugio.Mapa} no es seguro");
            Assert.True(m.Mapas.PuertaHacia(196, refugio.Mapa, desde, (refugio.X, refugio.Y)) is not null,
                $"desde {desde} se eligió el mapa {refugio.Mapa} sin una puerta a la que se llegue");
        }
        Assert.True(conRefugio > 0, "desde ninguna entrada al 196 se encuentra un mapa seguro");
    }

    /// <summary>
    /// EL CAMINO NO PISA OTRAS SALIDAS. Pisar una salida saca del mapa, así que un camino que pasa por encima de
    /// una lleva a otro lado: una Pescadora que iba de la cuna a la puerta del 62 terminó en el 185 y en el 208.
    /// Sólo la última baldosa puede ser una salida.
    /// </summary>
    [Fact]
    public void El_camino_no_pisa_salidas_que_no_son_el_destino()
    {
        if (Mundo() is not { } m) return;
        int mirados = 0;
        foreach (var (mapa, desde, hasta) in new (int, (byte X, byte Y), (byte X, byte Y))[]
                 { (37, (80, 88), (66, 78)), (37, (89, 48), (66, 78)), (208, (56, 72), (12, 48)), (37, (57, 74), (63, 78)) })
        {
            if (m.Mapas.Camino(mapa, desde, hasta) is not { Count: > 0 } camino) continue;
            mirados++;
            foreach (var b in camino.Take(camino.Count - 1))
                Assert.False(m.Datos.MapExits.ContainsKey((mapa, b.X, b.Y)),
                    $"el camino de m{mapa} {desde} a {hasta} pisa la salida {b}");
        }
        Assert.True(mirados > 0, "ningún camino encontrado: el test no miró nada");
    }

    /// <summary>
    /// EL CAMINO RODEA LO QUE SE LE PIDE EVITAR. Una criatura quieta en un pasillo no está en el mapa: el bot
    /// anota la baldosa donde rebotó y pide otro camino. Si el navegante la ignorara, devolvería el mismo camino y
    /// el bot chocaría contra lo mismo — que es lo que le pasó a uno en la cuna, doce veces contra (66,24).
    /// </summary>
    [Fact]
    public void El_camino_rodea_la_baldosa_que_se_pide_evitar()
    {
        if (Mundo() is not { } m) return;
        (byte X, byte Y) desde = (67, 24), hasta = (52, 65);
        // Igual que el viaje: el mercader ocupa su baldosa, así que se llega a una de alrededor.
        var directo = m.Mapas.HayComoLlegar(37, desde, hasta, 3);
        Assert.True(directo is { Count: > 0 }, "sin camino directo en la cuna: la premisa del test no vale");
        var tapada = directo![0];
        var rodeo = m.Mapas.HayComoLlegar(37, desde, hasta, 3, new HashSet<(byte X, byte Y)> { tapada });
        Assert.True(rodeo is { Count: > 0 }, $"tapando {tapada} no queda camino: esa baldosa no es un buen caso");
        Assert.DoesNotContain(tapada, rodeo!);
    }

    /// <summary>
    /// LA PUERTA QUE ELIGE EL NAVEGANTE SE ALCANZA DESDE DONDE SE ESTÁ. Si devolviera una baldosa de salida sin
    /// camino, el viaje fallaría en el primer tramo aunque la misma salida tuviera otra baldosa alcanzable — que
    /// es lo que le pasaba a un Minero en el mapa 62 con la puerta (51,73).
    /// </summary>
    [Fact]
    public void La_puerta_que_elige_el_navegante_se_alcanza_desde_donde_se_esta()
    {
        if (Mundo() is not { } m) return;
        int miradas = 0;
        foreach (var (mapa, desde, destino) in new (int, (byte, byte), int)[]
                 { (62, (58, 72), 194), (37, (57, 74), 62), (37, (83, 66), 62), (20, (80, 12), 62) })
        {
            if (m.Mapas.PuertaHacia(mapa, destino, desde) is not { } p) continue;
            miradas++;
            Assert.True(m.Mapas.Camino(mapa, desde, p.Puerta) is not null,
                $"el navegante eligió la puerta ({p.Puerta.X},{p.Puerta.Y}) del mapa {mapa} y desde {desde} no se llega");
        }
        Assert.True(miradas > 0, "ninguna consulta devolvió puerta: el test no miró nada");
    }

    /// <summary>
    /// DESDE DONDE QUEDÓ TRABADA DALIA SE LLEGA A UN SACERDOTE. Es la salida del pozo sin pociones: una Guerrera en el 194 con
    /// 5 de 58 de vida y sin con qué regenerar, a la que el sacerdote cura del todo si llega.
    /// </summary>
    [Fact]
    public void Desde_donde_quedo_trabada_Dalia_se_llega_a_un_sacerdote()
    {
        if (Mundo() is not { } m) return;
        var templo = m.Mapas.DondeHay(194, (54, 82), ficha => m.Datos.Npc(ficha)?.NpcType == 1);
        Assert.True(templo is not null, "desde (54,82) del 194 no se llega a ningún sacerdote");
        Assert.True(templo!.Value.Saltos <= 2, $"el sacerdote más cercano queda a {templo.Value.Saltos} mapas");
    }

    /// <summary>
    /// A PARTIR DEL NIVEL 16 LA CUNA NO ES CAMINO NI DESTINO: el servidor rebota al que pisa la puerta de la zona NEWBIE. Con el mapa
    /// vedado, lo que se busca no puede quedar en la cuna y no hay puerta hacia ella.
    /// </summary>
    [Fact]
    public void Una_zona_vedada_no_es_camino_ni_destino()
    {
        if (Mundo() is not { } m) return;
        bool Pisable(int mapa) => Bot.PuedeEntrar(16, m.Datos.MapZone(mapa));
        Assert.False(Pisable(37), "la cuna tenía que ser zona NEWBIE o esto no prueba nada");
        bool Tabernero(int ficha) => m.Datos.Npc(ficha)?.StockItems?.Any(s => m.Datos.Obj((short)s.ObjIndex) is { IsDrink: true }) == true;
        bool Atiende(int mapa) => m.Datos.MapBattleMode(mapa) is 2 or 3;
        var libre = m.Mapas.DondeHay(20, (75, 55), Tabernero, Atiende);
        Assert.True(libre is { Mapa: 37 }, $"sin vedar tenía que elegir la cuna y eligió el mapa {libre?.Mapa}: el test no prueba nada");
        var vedado = m.Mapas.DondeHay(20, (75, 55), Tabernero, Atiende, Pisable);
        Assert.True(vedado is null || vedado.Value.Mapa != 37, "con la cuna vedada igual la eligió");
        Assert.Null(m.Mapas.PuertaHacia(20, 37, (75, 55), pisable: Pisable));
    }

    /// <summary>
    /// ALEJARSE NO SE TRABA EN UN RINCÓN: en (47,48) de la cuna hay pared al norte y al oeste, y con la criatura al este la huida a
    /// ciegas rebotaba entre las dos paredes. El camino sale por el piso, en cruz, y termina lejos. Con el sur ocupado no queda
    /// adónde ir.
    /// </summary>
    [Fact]
    public void Alejarse_de_un_rincon_sale_por_donde_hay_piso()
    {
        if (Mundo() is not { } m) return;
        Assert.True(m.Datos.IsBlockedTile(37, 47, 47) && m.Datos.IsBlockedTile(37, 46, 48) && !m.Datos.IsBlockedTile(37, 47, 49),
            "el rincón de la cuna cambió: el test no prueba nada");
        var camino = m.Mapas.LejosDe(37, (47, 48), (48, 48), 8);
        Assert.NotNull(camino);
        var antes = ((byte X, byte Y))(47, 48);
        foreach (var b in camino!)
        {
            Assert.Equal(1, Math.Abs(b.X - antes.X) + Math.Abs(b.Y - antes.Y));
            Assert.False(m.Datos.IsBlockedTile(37, b.X, b.Y), $"el camino pisa la pared en {b}");
            antes = b;
        }
        Assert.True(Math.Abs(antes.X - 48) + Math.Abs(antes.Y - 48) >= 5, $"terminó en {antes}, casi pegado a la criatura");
        Assert.Null(m.Mapas.LejosDe(37, (47, 48), (48, 48), 8, new HashSet<(byte X, byte Y)> { (47, 49) }));
    }

    /// <summary>Y dónde se puede pescar. El agua no viaja por la red —el servidor no manda el terreno— así que
    /// si esto no la encontrara, un Pescador no tendría forma de saber a dónde ir.</summary>
    [Fact]
    public void Se_encuentra_donde_hay_agua()
    {
        if (Mundo() is not { } m) return;
        Assert.NotNull(m.Mapas.DondeSeTrabaja(37, agua: true));
    }

    /// <summary>
    /// LA RUTA SE VALIDA A PIE EN TODOS LOS SALTOS, no sólo en el primero. Contra los mapas de verdad: desde el
    /// 746, parado en (45,16), las dos salidas se alcanzan caminando —la (19,17) hacia el 770 y la (80,69) hacia el
    /// 815— pero por el 770 el camino se corta del otro lado, así que al 839 se llega por el 815.
    ///
    /// <para>El BFS que había validaba el primer salto y de ahí en más recorría el grafo de mapas a secas: mandaba
    /// por la (19,17). Medido sobre 1242 destinos alcanzables desde 30 mapas, 68 salían por la puerta equivocada, y
    /// desde este mismo 746 eran ocho destinos por la misma puerta mala.</para>
    /// </summary>
    [Fact]
    public void La_ruta_se_valida_a_pie_en_todos_los_saltos()
    {
        if (Mundo() is not { } m) return;
        var salidas = new[] { ((byte)19, (byte)17), ((byte)80, (byte)69) };
        var elegida = m.Mapas.PuertaHacia(746, 839, ((byte)45, (byte)16));
        Assert.True(elegida is not null, "del 746 al 839 dejó de haber camino: el test no prueba nada");
        Assert.Contains(elegida!.Value.Puerta, salidas);
        Assert.Equal(((byte)80, (byte)69), elegida.Value.Puerta);
    }
}
