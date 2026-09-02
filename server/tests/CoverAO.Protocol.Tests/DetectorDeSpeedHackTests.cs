using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL DETECTOR DE SPEEDHACK del original (Protocol.bas:1597-1625), que es la ÚNICA traba de movimiento que
/// tiene el VB6 — no hay cadencia por paso allá.
///
/// <para>Lo que hay que fijar acá no es sólo que eche al que corre: es que <b>no le pegue a nadie honesto</b>.
/// Una baldosa del cliente dura 209 ms, y 174 a caballo o de muerto, así que las dos pruebas que valen son la
/// del que camina a 174 (no lo toca) y la del que camina a 130 —que es lo más rápido que la otra traba de este
/// port deja pasar— que sí cae.</para>
/// </summary>
public class DetectorDeSpeedHackTests
{
    /// <summary>Camina <paramref name="pasos"/> pasos cada <paramref name="cadaMs"/> ms y devuelve el peor
    /// veredicto que salió. El reloj arranca alto a propósito: el del original es <c>GetTickCount</c>.</summary>
    private static DetectorDeSpeedHack.Veredicto Caminar(DetectorDeSpeedHack d, int pasos, long cadaMs, ref long reloj)
    {
        var peor = DetectorDeSpeedHack.Veredicto.Nada;
        for (int i = 0; i < pasos; i++)
        {
            reloj += cadaMs;
            var v = d.Paso(reloj);
            if (v > peor) peor = v;
        }
        return peor;
    }

    /// <summary>Deja el detector con su primera ventana ya cerrada. Hace falta en casi todas las pruebas: la
    /// ventana inicial NUNCA se mide, ver <see cref="La_primerisima_ventana_no_se_mide"/>.</summary>
    private static void Arrancar(DetectorDeSpeedHack d, ref long reloj) => Caminar(d, 34, 209, ref reloj);

    [Fact]
    public void La_primerisima_ventana_no_se_mide()
    {
        // El original arranca con `StartWalk = 0` (TCP.bas:1461) y compara `GetTickCount - StartWalk`, así que
        // la primera resta da el reloj entero y jamás entra en los 5200 ms. O sea que los primeros 34 pasos de
        // una sesión son gratis por construcción — no es un olvido nuestro, es lo que hace el VB6.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void La_primera_ventana_medida_nunca_echa()
    {
        // Aunque venga a fondo: el original sólo deja una marca (`.flags.CountSH = TempTick`) y sigue. Es a
        // propósito — un tirón de red no le corta la sesión a nadie.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void La_segunda_ventana_rapida_seguida_echa()
    {
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Caminar(d, 34, 10, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Echar, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void Si_la_segunda_llega_tarde_la_marca_ya_se_olvido()
    {
        // Los 30 s de :1606. Pasado ese rato vuelve a ser la "primera" ventana, o sea Sospecha y no Echar.
        //
        // El rato se deja pasar CAMINANDO despacio y no adelantando el reloj a mano: la ventana se mide entre
        // el cierre de una y el de la siguiente, así que un salto de reloj a secas se contaría como parte de la
        // ventana siguiente y la haría lenta — el detector diría Nada y el verde no probaría nada. 34 pasos de
        // un segundo son 34 s, que es lo que hace falta.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, 34, 1000, ref reloj));   // 34 s de calma
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void El_que_camina_como_el_cliente_no_se_toca()
    {
        // 209 ms por baldosa, que es lo que dura un paso en el motor del original. Cinco ventanas seguidas.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, 34 * 5, 209, ref reloj));
    }

    [Fact]
    public void El_que_va_a_caballo_tampoco()
    {
        // 174 ms: el paso más rápido que un cliente honesto puede pedir (montado o muerto, factor 1,2).
        // 34 * 174 = 5916 ms contra los 5200 de la ventana. Éste es el margen de todo el mecanismo.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, 34 * 5, 174, ref reloj));
        Assert.True(34 * 174 > DetectorDeSpeedHack.VentanaMs);
    }

    [Fact]
    public void El_que_va_al_tope_de_la_cadencia_por_paso_SI_cae()
    {
        // 130 ms es lo que la otra traba de este port —la de HandleWalk, que el original no tiene— deja pasar.
        // Sostenerlo son 4420 ms cada 34 pasos: por debajo de la ventana. Es exactamente el hueco que este
        // detector viene a tapar, y por eso el veredicto tiene que ser Echar y no Nada.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Caminar(d, 34, 130, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Echar, Caminar(d, 34, 130, ref reloj));
    }

    [Fact]
    public void Una_ventana_lenta_en_el_medio_no_borra_la_marca()
    {
        // La marca la borra el TIEMPO (30 s), no una ventana tranquila: el original nunca resetea `CountSH`
        // por haber caminado despacio, sólo por antigüedad. Tres ventanas de 34 a 209 ms son 21,3 s, así que
        // la marca sigue viva y la cuarta ventana rápida echa.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, 34 * 3, 209, ref reloj));
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Echar, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void Estar_paralizado_perdona_la_marca()
    {
        // `.flags.CountSH = 0` de la rama de paralizado (:1640): mientras no te podés mover los tiempos entre
        // paquetes no significan nada, así que lo anotado se borra y la próxima ventana rápida vuelve a ser la
        // primera.
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Arrancar(d, ref reloj);
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
        d.Olvidar();
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Sospecha, Caminar(d, 34, 10, ref reloj));
    }

    [Fact]
    public void Con_menos_de_una_ventana_no_dice_nada()
    {
        var d = new DetectorDeSpeedHack();
        long reloj = 1_000_000;
        Assert.Equal(DetectorDeSpeedHack.Veredicto.Nada, Caminar(d, DetectorDeSpeedHack.PasosPorVentana - 1, 1, ref reloj));
    }
}
