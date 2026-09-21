using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// <c>/RELOADSINI</c> tiene que LEER EL DISCO DE NUEVO, no contestar que sí.
///
/// <para>El flow que ejercita el comando corre con el fixture, que arranca sin <c>Server.ini</c>: ahí lo
/// único que puede comprobar es que conteste "no hay de dónde releer". La relectura de verdad se prueba acá,
/// con un archivo real que se edita entre las dos lecturas.</para>
///
/// <para>Y se comprueba sobre LA MISMA INSTANCIA a propósito: el despachador guarda una referencia al
/// <see cref="ServerIni"/> y lo consulta en caliente (el gate de cadencia, el privilegio en cada login). Si
/// <c>Recargar</c> devolviera un objeto nuevo en vez de actualizar éste, el comando diría "recargado" y el
/// servidor seguiría con los valores viejos — la clase de verde falso que este repo ya pagó otras veces.</para>
/// </summary>
public class ServerIniRecargarTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), "coverao_ini_" + Guid.NewGuid().ToString("N") + ".ini");

    public void Dispose() { try { File.Delete(_ruta); } catch { } }

    /// <summary>OJO EL CONTADOR: <c>Dioses=N</c> va en <c>[INIT]</c>, no adentro de <c>[Dioses]</c> — ahí van
    /// sólo los NOMBRES (<c>Dios1</c>, <c>Dios2</c>…). Con el contador en la sección equivocada la lista sale
    /// vacía sin ningún error, que es exactamente como se ve un ini bien escrito y mal leído.</summary>
    private void Escribir(string exp, string dios) =>
        File.WriteAllText(_ruta,
            $"[INIT]\r\nExp={exp}\r\nStartPort=7666\r\nDioses=1\r\n\r\n[Dioses]\r\nDios1={dios}\r\n");

    [Fact]
    public void Recargar_lee_el_archivo_de_nuevo_sobre_la_misma_instancia()
    {
        Escribir("2", "Rugura");     // el multiplicador directo: 2 = ×2
        var ini = ServerIni.Cargar(_ruta);
        Assert.Equal(2, ini.Exp);
        Assert.True(ini.EsStaff("Rugura"));

        Escribir("5", "Merlin");
        Assert.True(ini.Recargar(), "con archivo, Recargar tiene que poder");

        Assert.Equal(5, ini.Exp);                                   // el valor nuevo
        Assert.True(ini.EsStaff("Merlin"), "el staff nuevo entra");
        // Y el viejo SALE. Sin limpiar la lista, quitarle el rango a alguien editando el ini no tendría
        // efecto hasta reiniciar — que es justo el caso en el que uno corre este comando con apuro.
        Assert.False(ini.EsStaff("Rugura"), "el staff que se saca del ini deja de ser staff");
    }

    [Fact]
    public void Sin_archivo_Recargar_avisa_que_no_puede()
    {
        var ini = ServerIni.Cargar(null);       // defaults, sin ruta
        Assert.False(ini.Recargar());
    }

    [Fact]
    public void Lo_que_pide_reiniciar_esta_declarado_y_no_vacio()
    {
        // La lista es la mitad del comando: sin ella el "recargado" sería mentira para el puerto, que ya
        // está escuchando. Que exista y nombre al puerto es lo que evita que alguien la vacíe sin pensarlo.
        Assert.NotEmpty(ServerIni.PidenReiniciar);
        Assert.Contains("StartPort", ServerIni.PidenReiniciar);
    }
}
