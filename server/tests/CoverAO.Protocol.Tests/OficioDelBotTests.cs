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
