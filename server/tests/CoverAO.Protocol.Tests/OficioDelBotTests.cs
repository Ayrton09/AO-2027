using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// DE DÓNDE SACA UN BOT LOS DOSCIENTOS DE SU HERRAMIENTA.
///
/// <para>Un Pescador, un Leñador y un Minero no nacen con la herramienta de su oficio: hay que comprarla, y
/// hasta que la tengan no pescan, no talan y no minan. La cadena entera —viajar, abrir la tienda, comprar—
/// estaba escrita y aun así el oro cerraba en CERO corrida tras corrida, y diez minutos de mirar bots no
/// decían por qué.</para>
///
/// <para>Lo que hay acá es esa pregunta contestada SIN SERVIDOR: contra la data del juego, ¿existe el camino
/// del oro desde la baldosa donde nace todo personaje? Cada test fija un eslabón, y son eslabones que viven
/// en la DATA, o sea que la próxima edición del mundo los puede romper sin tocar una línea de código —justo
/// el tipo de rotura que no se ve leyendo—.</para>
/// </summary>
public class OficioDelBotTests
{
    private const short CanaDePesca = 881, HachaDeLenador = 127, PiqueteDeMinero = 187;

    /// <summary>
    /// DE UN PUEBLO SE PUEDE SALIR A CAZAR. Un paseo que pisa una puerta cambia de mapa, y el bot toma el
    /// mapa nuevo como su zona: si cae en un pueblo —donde no hay criaturas— se queda ahí para siempre.
    /// Medido: cuatro de ocho bots con la experiencia congelada una hora entera. La salida existe si desde un
    /// mapa sin presas el navegante encuentra uno con presas, que es justo lo que el bot le pregunta.
    /// </summary>
    [Fact]
    public void Desde_un_mapa_sin_presas_se_encuentra_uno_con_presas()
    {
        if (Mundo() is not { } m) return;

        bool HayPresa(int ficha) => m.Datos.Npc(ficha) is { Hostile: true, Attackable: true } n
                                    && SeLeAnimaUnRecienNacido(n);

        // Un mapa poblado donde un recién nacido no tiene NADA que cazar: es la situación que dejó bots
        // parados. Si la data cambiara y ese mapa tuviera presas, el test se buscaría otro antes de opinar.
        int pueblo = m.Datos.MapSpawns.Keys.FirstOrDefault(
            mapa => m.Datos.MapSpawns[mapa].Count > 5
                    && !m.Datos.MapSpawns[mapa].Any(s => HayPresa(s.NpcIndex)));
        if (pueblo == 0) return;

        Assert.NotNull(m.Mapas.DondeHay(pueblo, (50, 50), HayPresa));
    }

    /// <summary>
    /// LA SALIDA DEL QUE MURIÓ LEJOS. Un fantasma choca con las paredes igual que un vivo, así que "hay un
    /// templo a un salto" no garantiza que se pueda llegar: un bot murió en una punta del mapa 814 y la puerta
    /// hacia el 815 no tenía camino desde ahí. Le queda <c>RegresarHogar</c>, que lo deja en la baldosa de
    /// resurrección de su ciudad — y esa baldosa tiene que caer DENTRO de la ventana en la que el servidor
    /// atiende el pedido, o el bot sale del pozo para caer en otro igual.
    /// </summary>
    [Fact]
    public void La_baldosa_de_resurreccion_del_hogar_cae_al_lado_del_sacerdote()
    {
        if (Mundo() is not { } m) return;
        var casa = Cities.DeadTile(Cities.DungeonNewbie);
        if (!m.Datos.MapSpawns.TryGetValue(casa.Map, out var spawns)) return;

        var sacerdotes = spawns.Where(s => m.Datos.Npc(s.NpcIndex)?.NpcType == TipoRevividor).ToList();
        Assert.True(sacerdotes.Count > 0, $"el mapa {casa.Map} no tiene sacerdote: volver a casa no sirve de nada");
        // La ventana del servidor es 8 de ancho por 6 de alto, por eje. Se exige con margen, que es como
        // el bot mide su propio alcance.
        Assert.Contains(sacerdotes, s => Math.Abs(s.X - casa.X) <= 6 && Math.Abs(s.Y - casa.Y) <= 4);
    }

    /// <summary>
    /// EN LA CUNA HAY LO QUE PAGA, PERO ES MINORÍA. Es la premisa que obliga al bot a PREFERIR la presa que
    /// suelta oro: si fuera mayoría, elegir lo más cercano ya alcanzaría. Medido con la regla de "lo más
    /// cercano": diez minutos, ocho bots subiendo de nivel y cero monedas entre todos.
    /// </summary>
    [Fact]
    public void En_la_cuna_lo_que_paga_se_deja_cazar_pero_es_minoria()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;

        // Un nivel 3 con la vida de un nivel 3: ya se anima al escorpión.
        const int Nivel = 3, Vida = 35;
        bool Presa(int f) => m.Datos.Npc(f) is { Hostile: true, Attackable: true } n
                             && n.GiveExp <= 30 + Nivel * 40 && n.MaxHit * 5 <= Vida;

        int pagan = spawns.Count(s => Presa(s.NpcIndex) && m.Datos.Npc(s.NpcIndex)!.GiveGld > 0);
        int noPagan = spawns.Count(s => Presa(s.NpcIndex) && m.Datos.Npc(s.NpcIndex)!.GiveGld == 0);

        Assert.True(pagan > 0, "en la cuna no hay nada que un nivel 3 pueda cazar y que suelte oro");
        Assert.True(noPagan > pagan,
            $"lo que paga ({pagan}) ya no es minoría frente a lo que no ({noPagan}): la preferencia por el oro " +
            "dejaría de hacer falta");
    }

    /// <summary>
    /// LO QUE PAGA ESTÁ REPARTIDO, ASÍ QUE LOS BOTS TIENEN QUE REPARTIRSE. Las criaturas que sueltan oro en la
    /// cuna no caminan y están en varios lugares del mapa; si hubiera uno solo, elegir al azar entre spawns no
    /// cambiaría nada y todos volverían a amontonarse. Se exigen al menos tres lugares distintos a los que se
    /// llega caminando desde la baldosa de nacimiento.
    /// </summary>
    [Fact]
    public void En_la_cuna_hay_varios_lugares_distintos_donde_cazar_lo_que_paga()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;

        const int Vida = 71;   // un nivel 7: ya se anima al escorpión, todavía no al lobo
        var desde = ((byte)m.Cuna.X, (byte)m.Cuna.Y);
        var lugares = spawns
            .Where(s => m.Datos.Npc(s.NpcIndex) is { Hostile: true, Attackable: true } n
                        && n.GiveGld > 0 && n.MaxHit * 5 <= Vida)
            .Select(s => ((byte)s.X, (byte)s.Y)).Distinct()
            .Where(lugar => m.Mapas.HayComoLlegar(m.Cuna.Map, desde, lugar, 3) is not null)
            .ToList();

        Assert.True(lugares.Count >= 3,
            $"sólo {lugares.Count} lugares alcanzables con presas que pagan: repartirse no cambiaría nada");
    }

    /// <summary>
    /// EL QUE QUEDA ENCERRADO TIENE CON QUÉ SALIR. Un bot vivo sin camino a ninguna salida y sin nada que cazar
    /// vuelve a casa con la Runa de teletransportación, así que la premisa tiene dos mitades: que el kit de
    /// novato la traiga, y que la casa no sea un mapa PK — estando vivo, en un mapa PK la Runa no viaja
    /// (VB6 InvUsuario.bas:1356-1372), y un bot que vuelve a un lugar donde tampoco sirve no ganó nada.
    /// </summary>
    [Fact]
    public void El_kit_de_novato_trae_la_runa_y_la_cuna_no_es_pk()
    {
        if (Mundo() is not { } m) return;
        const int ObjTypeRuna = 38;

        int conKit = 0;
        for (int clase = 1; clase <= 18; clase++)
        {
            var kit = m.Datos.NewbieKit(clase);
            if (kit.Count == 0) continue;
            conKit++;
            Assert.True(kit.Any(it => m.Datos.Obj(it.ObjIndex)?.ObjType == ObjTypeRuna),
                $"el kit de novato de la clase {clase} no trae Runa: un bot encerrado de esa clase no tendría con qué salir");
        }
        Assert.True(conKit > 0, "ninguna clase tiene kit de novato: el test no miró nada");

        var casa = Cities.SpawnTile(Cities.DungeonNewbie);
        Assert.False(m.Datos.IsPkMap(casa.Map), $"la cuna (mapa {casa.Map}) es PK: la Runa no sacaría a un vivo de ahí");
    }

    /// <summary>
    /// EL QUE SE QUEDÓ SIN COMIDA PUEDE REPONERLA CON CASI NADA. Un bot sin comer ni tomar deja de regenerar
    /// energía y ya no puede pelear, así que no junta oro: la salida es comprar con lo poco que le quede. Medido:
    /// un Gladiador de nivel 7 sin una manzana ni un agua y con doce monedas. La premisa es que en la cuna —un
    /// mapa que atiende— haya comida y bebida a un precio que se paga con la mitad de eso.
    /// </summary>
    [Fact]
    public void En_la_cuna_se_venden_comida_y_bebida_que_paga_un_hambriento()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;
        Assert.True(m.Datos.MapBattleMode(m.Cuna.Map) is 2 or 3, "la cuna no atiende: no hay dónde reponer");

        int MasBarato(Func<ObjInfo, bool> es) => spawns
            .SelectMany(sp => m.Datos.Npc(sp.NpcIndex)?.StockItems
                              ?? (IReadOnlyList<(int ObjIndex, int Amount)>)Array.Empty<(int ObjIndex, int Amount)>())
            .Select(it => m.Datos.Obj(it.ObjIndex))
            .Where(o => o is not null && es(o))
            .Select(o => o!.Value)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        const int PlataDeUnHambriento = 12;
        Assert.True(MasBarato(o => o.IsFood) <= PlataDeUnHambriento / 2,
            $"la comida más barata de la cuna vale {MasBarato(o => o.IsFood)}: un bot casi sin plata no repone");
        Assert.True(MasBarato(o => o.IsDrink) <= PlataDeUnHambriento / 2,
            $"la bebida más barata de la cuna vale {MasBarato(o => o.IsDrink)}: un bot casi sin plata no repone");
    }

    /// <summary>VB6 <c>otCualquiera</c> (Declares.bas:498): el mercader que compra de todo.</summary>
    private const int TipoItemsCualquiera = 1000;

    /// <summary>La vida de un recién nacido: la peor de las clases arranca en 16 y la mejor en 21.</summary>
    private const int VidaDeUnRecienNacido = 20;

    private static (GameData Datos, Navegante Mapas, WorldPos Cuna)? Mundo()
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0 || datos.MapExits.Count == 0) return null;
        return (datos, new Navegante(datos), Cities.SpawnTile(Cities.DungeonNewbie));
    }

    /// <summary>Lo que suelta SIEMPRE una criatura de ese mapa a la que un nivel 1 se anima, y que además
    /// tiene precio de venta.</summary>
    private static List<ObjInfo> BotinVendibleDeUnRecienNacido(GameData datos, int mapa)
    {
        var botin = new List<ObjInfo>();
        if (!datos.MapSpawns.TryGetValue(mapa, out var spawns)) return botin;
        foreach (int ficha in spawns.Select(s => s.NpcIndex).Distinct())
        {
            var n = datos.Npc(ficha);
            if (n is not { Hostile: true, Attackable: true, Drops: { } drops }) continue;
            if (!SeLeAnimaUnRecienNacido(n)) continue;
            foreach (var d in drops)
                if (d.ProbTirar >= 100 && datos.Obj(d.ObjIndex) is { } o && Comercio.PrecioDeVenta(o, 1) > 0)
                    botin.Add(o);
        }
        return botin;
    }

    /// <summary>La misma cuenta con la que el bot elige presa, puesta en nivel 1.</summary>
    private static bool SeLeAnimaUnRecienNacido(NpcInfo n) =>
        n.GiveExp <= 30 + 1 * 40 && n.MaxHit * 5 <= VidaDeUnRecienNacido;

    /// <summary>
    /// LA HERRAMIENTA CUESTA, y el precio lo tiene que leer de la data. Estuvo escrito —en un comentario, y en
    /// el porqué de un arreglo— que la caña valía 0 y que por eso un Pescador sin una moneda podía ir a
    /// buscarla. Vale 200, igual que el hacha y que el piquete: los tres oficios arrancan juntando plata.
    /// </summary>
    [Fact]
    public void Las_tres_herramientas_cuestan_y_el_precio_sale_de_la_data()
    {
        if (Mundo() is not { } m) return;
        foreach (short h in new[] { CanaDePesca, HachaDeLenador, PiqueteDeMinero })
        {
            var o = m.Datos.Obj(h);
            Assert.True(o is not null, $"el objeto {h} tendría que existir en la data");
            Assert.True(o!.Value > 0, $"{o.Name} vale {o.Value}: si fuera gratis, medio bot no tendría que juntar nada");
        }
    }

    /// <summary>
    /// EN LA CUNA NO HAY ORO QUE MATAR. De las criaturas del Dungeon Newbie, las que un recién nacido puede
    /// pelear dan CERO monedas; el escorpión da 3 y recién se le anima con el doble de vida, y el lobo da 50
    /// pero pega 18 y hay que llegar a nivel diez. Por eso el oro tiene que salir del BOTÍN, y por eso importa
    /// el test de abajo.
    /// </summary>
    [Fact]
    public void Lo_que_un_recien_nacido_puede_matar_en_la_cuna_no_deja_una_moneda()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;

        int miradas = 0;
        foreach (int ficha in spawns.Select(s => s.NpcIndex).Distinct())
        {
            var n = m.Datos.Npc(ficha);
            if (n is not { Hostile: true, Attackable: true } || !SeLeAnimaUnRecienNacido(n)) continue;
            miradas++;
            Assert.True(n.GiveGld == 0,
                $"{n.Name} da {n.GiveGld} de oro y se le anima un nivel 1: si esto cambió, el bot ya no depende del botín");
        }
        // Un recorrido que no miró nada pasa igual, y diría lo contrario de lo que quiere decir.
        Assert.True(miradas > 0, "no hay NINGUNA criatura que un nivel 1 pueda pelear en la cuna");
    }

    /// <summary>
    /// PERO EL BOTÍN SÍ SE PAGA. Las gallinas y los gallos de la cuna sueltan un Pollo con probabilidad 100 y
    /// el Pollo tiene precio de venta — o sea que el oro de la herramienta está ahí, en el piso, atado a las
    /// criaturas más mansas del juego.
    ///
    /// <para>Es el eslabón que el bot no usaba: su regla de "qué vale la pena vender" descartaba la comida y
    /// la bebida enteras, y lo único vendible que un recién nacido junta en la cuna es justamente comida.</para>
    /// </summary>
    [Fact]
    public void Lo_que_sueltan_las_criaturas_mansas_de_la_cuna_tiene_precio()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.ContainsKey(m.Cuna.Map)) return;

        var pagan = BotinVendibleDeUnRecienNacido(m.Datos, m.Cuna.Map);
        Assert.True(pagan.Count > 0,
            "ninguna criatura mansa de la cuna suelta algo que se pueda vender: sin eso un bot de oficio no junta la herramienta nunca");
    }

    /// <summary>
    /// Y HAY A QUIÉN VENDÉRSELO, A PASOS DE LA CUNA. No alcanza con que el botín valga: el mercader tiene que
    /// aceptar ESE tipo de objeto (<c>TipoItems</c>), el mapa tiene que atender —un mercader no le vende a un
    /// ciudadano donde el <c>battleMode</c> no es 2 ni 3— y se tiene que poder llegar caminando.
    /// </summary>
    [Fact]
    public void Hay_un_mercader_que_lo_compra_y_se_llega_caminando()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;

        var botin = BotinVendibleDeUnRecienNacido(m.Datos, m.Cuna.Map);
        Assert.True(botin.Count > 0, "sin botín vendible este test pasaría sin mirar nada");

        Assert.True(m.Datos.MapBattleMode(m.Cuna.Map) is 2 or 3,
            $"el mapa {m.Cuna.Map} tiene battleMode {m.Datos.MapBattleMode(m.Cuna.Map)}: ahí ningún mercader atiende a un ciudadano");

        var desde = ((byte)m.Cuna.X, (byte)m.Cuna.Y);
        var comprador = spawns
            .Select(s => (s, n: m.Datos.Npc(s.NpcIndex)))
            .FirstOrDefault(t => t.n?.StockItems is { Count: > 0 }
                                 && botin.Any(o => t.n.TipoItems == o.ObjType || t.n.TipoItems == TipoItemsCualquiera)
                                 && m.Mapas.HayComoLlegar(m.Cuna.Map, desde, ((byte)t.s.X, (byte)t.s.Y), 3) is not null);

        Assert.True(comprador.n is not null,
            $"nadie compra el botín de la cuna a pie desde ({desde.Item1},{desde.Item2}): el bot lo levanta y no tiene dónde cambiarlo");
    }

    /// <summary>
    /// DEL CEMENTERIO SE VUELVE. Un bot muerto no puede hacer NADA salvo caminar hasta un sacerdote, así que
    /// si desde la cuna no se llega a uno, el bot queda tirado para siempre — y eso fue exactamente lo que
    /// pasó: tres de siete bots muertos y quietos cuarenta y cinco minutos.
    ///
    /// <para>Se exige que el sacerdote esté en el mapa donde nace todo personaje, no en otro: ahí es donde
    /// van a morir los primeros, y el que muere de nivel uno no tiene con qué cruzar medio mundo.</para>
    /// </summary>
    [Fact]
    public void En_la_cuna_hay_un_sacerdote_al_que_se_llega_caminando()
    {
        if (Mundo() is not { } m) return;
        if (!m.Datos.MapSpawns.TryGetValue(m.Cuna.Map, out var spawns)) return;

        var desde = ((byte)m.Cuna.X, (byte)m.Cuna.Y);
        var sacerdotes = spawns.Where(s => m.Datos.Npc(s.NpcIndex)?.NpcType == TipoRevividor).ToList();
        Assert.True(sacerdotes.Count > 0, $"el mapa {m.Cuna.Map} no tiene ningún sacerdote: un muerto no vuelve nunca");
        Assert.Contains(sacerdotes, s => m.Mapas.HayComoLlegar(m.Cuna.Map, desde, ((byte)s.X, (byte)s.Y), 3) is not null);
    }

    /// <summary>Y el navegante tiene que encontrarlo solo, que es lo que el bot le pregunta de verdad cuando
    /// está tirado.</summary>
    [Fact]
    public void El_navegante_encuentra_al_sacerdote_desde_la_cuna()
    {
        if (Mundo() is not { } m) return;
        var desde = ((byte)m.Cuna.X, (byte)m.Cuna.Y);
        Assert.NotNull(m.Mapas.DondeHay(m.Cuna.Map, desde, f => m.Datos.Npc(f)?.NpcType == TipoRevividor));
    }

    /// <summary>VB6 <c>eNPCType.Revividor</c>.</summary>
    private const int TipoRevividor = 1;

    /// <summary>
    /// Y LA HERRAMIENTA SE PUEDE IR A BUSCAR. Con el oro juntado, el bot le pregunta al navegante dónde hay
    /// quien la venda EN UN MAPA QUE ATIENDA — y tiene que haber respuesta desde la cuna. De los ocho que
    /// venden la caña, cinco están en mapas donde nadie le vendería a un ciudadano.
    /// </summary>
    [Fact]
    public void Desde_la_cuna_se_encuentra_quien_venda_cada_herramienta()
    {
        if (Mundo() is not { } m) return;
        var desde = ((byte)m.Cuna.X, (byte)m.Cuna.Y);
        bool Atiende(int mapa) => m.Datos.MapBattleMode(mapa) is 2 or 3;

        foreach (short h in new[] { CanaDePesca, HachaDeLenador, PiqueteDeMinero })
        {
            short herramienta = h;
            bool Vende(int ficha) => m.Datos.Npc(ficha)?.StockItems is { Count: > 0 } stock
                                     && stock.Any(s => s.ObjIndex == herramienta);
            Assert.True(m.Mapas.DondeHay(m.Cuna.Map, desde, Vende, Atiende) is not null,
                $"desde la cuna no se llega a nadie que venda {m.Datos.Obj(h)?.Name}: ese oficio no arranca");
        }
    }
}
