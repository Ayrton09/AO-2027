using System;
using System.IO;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA CADENCIA DEL ARCO — la única compuerta de tiempo del original que el port no tenía.
///
/// <para>El servidor VB6 trae diez relojes vivos en <c>modNuevoTimer.bas</c>. El port tenía cinco, y el que
/// faltaba era el más fuerte: <c>IntervaloPermiteUsarArcos</c> (<c>IntervaloFlechasCazadores = 1100</c>).
/// Resultado: <b>el camino del disparo no tenía ninguna cadencia</b>. No es que tuviera una más floja — los
/// otros dos chequeos de ese call site van con <c>Actualizar = False</c>, así que el único que sella el reloj
/// es éste. Sin él, un cliente modificado disparaba tan rápido como se lo permitiera el techo de paquetes.</para>
///
/// <para>Y es TRANSVERSAL, que es lo que la separa del resto: disparar sella, y mientras esté caliente el
/// original tampoco deja pegar (<c>SistemaCombate.bas:975</c>) ni castear (<c>Protocol.bas:2936</c>).</para>
/// </summary>
public class CadenciaDelArcoTests : IDisposable
{
    private readonly string _ini = Path.Combine(Path.GetTempPath(), "coverao_arco_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ini); } catch { } }

    private ServerIni Cargar(string cuerpo)
    {
        File.WriteAllText(_ini, "[INIT]\r\nStartPort=7666\r\n" + cuerpo);
        return ServerIni.Cargar(_ini);
    }

    /// <summary>El número sale del <c>Server.ini</c> del install, no de la nada: la clave existe, tiene 1100
    /// y el VB6 la lee (<c>FileIO.bas:1514</c>). Portarla no es agregar una regla, es dejar de ignorar una.</summary>
    [Fact]
    public void El_intervalo_sale_del_install()
    {
        Assert.Equal(1100, new ServerIni().IntervaloArco);
        Assert.Equal(1100, Cargar("").IntervaloArco);
        Assert.Equal(2500, Cargar("[INTERVALOS]\r\nIntervaloFlechasCazadores=2500\r\n").IntervaloArco);
        Assert.Equal(0, Cargar("[INTERVALOS]\r\nIntervaloFlechasCazadores=0\r\n").IntervaloArco);
    }

    /// <summary>Dos disparos seguidos: el segundo se rechaza hasta que pasa el intervalo. Es el caso que no
    /// existía — antes los dos pasaban, y también el número 200 del mismo segundo.</summary>
    [Fact]
    public void No_se_puede_disparar_dos_veces_seguidas()
    {
        var p = new Player { Name = "Arquero", Account = "cuenta_test", CharIndex = 1 };
        Assert.True(CombatTiming.PermiteUsarArcos(p, 10_000, 1100));    // el primero, siempre
        Assert.False(CombatTiming.PermiteUsarArcos(p, 10_001, 1100));   // 1 ms después: no
        Assert.False(CombatTiming.PermiteUsarArcos(p, 11_099, 1100));   // un pelo antes: tampoco
        Assert.True(CombatTiming.PermiteUsarArcos(p, 11_100, 1100));    // justo: sí (`>=`, como el VB6)
    }

    /// <summary>
    /// LO TRANSVERSAL: disparar bloquea PEGAR y CASTEAR.
    ///
    /// <para>Es la mitad que se pierde si uno lee la función suelta y la pone sólo en el disparo. El original
    /// la consulta en <c>UsuarioAtaca</c> y en el clic del hechizo, las dos veces <b>sin</b> actualizar: el
    /// arquero no puede disparar y rematar a mano en el mismo instante.</para>
    /// </summary>
    [Fact]
    public void Despues_de_disparar_no_se_puede_ni_pegar_ni_castear()
    {
        var p = new Player { Name = "Arquero", Account = "cuenta_test", CharIndex = 1 };
        Assert.True(CombatTiming.PermiteUsarArcos(p, 10_000, 1100));    // dispara: SELLA

        // Los dos que consultan, dentro de la ventana: rechazan.
        Assert.False(CombatTiming.PermiteUsarArcos(p, 10_500, 1100, actualizar: false));
        // Y no la corren: consultar no mueve el reloj, así que el vencimiento sigue siendo el mismo.
        Assert.True(CombatTiming.PermiteUsarArcos(p, 11_100, 1100, actualizar: false));
    }

    /// <summary>Consultar NO sella. Si sellara, cada golpe correría la ventana del arco hacia adelante y el
    /// arquero que pelea a mano no podría volver a disparar nunca — el gate pasaría de proteger a romper.</summary>
    [Fact]
    public void Consultar_no_sella_el_reloj()
    {
        var p = new Player { Name = "Arquero", Account = "cuenta_test", CharIndex = 1 };
        CombatTiming.PermiteUsarArcos(p, 10_000, 1100);                       // dispara
        for (long t = 10_100; t < 11_100; t += 100)
            CombatTiming.PermiteUsarArcos(p, t, 1100, actualizar: false);     // mil consultas en el medio
        Assert.True(CombatTiming.PermiteUsarArcos(p, 11_100, 1100));          // vence cuando tenía que vencer
    }

    /// <summary>Con el intervalo en 0 no hay espera, pero el reloj SE SIGUE SELLANDO. Es la misma semántica
    /// que los otros cuatro: `0` es "sin espera" —lo que da la aritmética del original— y no "apagado".
    /// Importa porque de esos sellos dependen las consultas del golpe y del casteo.</summary>
    [Fact]
    public void En_cero_no_hay_espera_pero_el_reloj_se_sella()
    {
        var p = new Player { Name = "Arquero", Account = "cuenta_test", CharIndex = 1 };
        Assert.True(CombatTiming.PermiteUsarArcos(p, 10_000, 0));
        Assert.True(CombatTiming.PermiteUsarArcos(p, 10_000, 0));   // sin espera: pasa igual
        Assert.Equal(10_000, p.ArcoMs);                              // pero quedó sellado
    }
}
