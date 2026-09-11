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

    /// <summary>Y dónde se puede pescar. El agua no viaja por la red —el servidor no manda el terreno— así que
    /// si esto no la encontrara, un Pescador no tendría forma de saber a dónde ir.</summary>
    [Fact]
    public void Se_encuentra_donde_hay_agua()
    {
        if (Mundo() is not { } m) return;
        Assert.NotNull(m.Mapas.DondeSeTrabaja(37, agua: true));
    }
}
