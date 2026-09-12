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
}
