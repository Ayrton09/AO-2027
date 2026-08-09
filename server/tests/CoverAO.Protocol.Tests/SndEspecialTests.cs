using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL SONIDO DEL AURA. Equipar un ítem que tiene aura suena en el área ("Sonido de auras" del original,
/// InvUsuario.bas:1014, 1077, 1181 y 1270 — los cuatro caminos de equipar, siempre adentro del
/// <c>If Obj.Aura &lt;&gt; 0</c>).
///
/// El port leía el campo <c>Aura</c> y mandaba el <c>AuraToChar</c>, pero el sonido no estaba: ponerse una
/// Espada Mata Dragones era mudo. Nada lo detectaba, porque por el protocolo la emisión del aura salía
/// perfecta — lo que faltaba era un paquete ENTERO que nadie extrañaba.
/// </summary>
public class SndEspecialTests
{
    /// <summary>Carga la data y EXIGE que esté. Acá había un `try/catch` que devolvía null, y cada test
    /// arrancaba con un `if (data is null) return;`: con la data ausente los tres pasaban sin comprobar
    /// nada. Se vio sacándole el campo al loader a propósito — el sabotaje tenía que dar rojo y dio verde.</summary>
    private static GameData Cargar()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        return d;
    }

    [Fact]
    public void El_obj_dat_trae_el_sonido_del_aura()
    {
        var data = Cargar();

        // Valores DUROS, no "hay alguno". El campo se agregó AL FINAL del record ObjInfo y el loader pasa los
        // argumentos por POSICIÓN: si algún día alguien lo mete en el medio, todo lo que viene detrás se corre
        // en silencio y el compilador no dice nada porque son todos int. Estos números anclan el alineamiento.
        Assert.Equal(254, data.Obj(402)!.SndEspecial);    // Espada Mata Dragones
        Assert.Equal(241, data.Obj(668)!.SndEspecial);    // Harbinger Kin
        Assert.Equal(255, data.Obj(1333)!.SndEspecial);   // Nudillos de Oro
        Assert.Equal(28, data.Obj(1180)!.SndEspecial);    // Escudo de Reflexión

        // Y el vecino de al lado tiene lo SUYO, no lo de éste: Snd1 y SndEspecial son campos distintos y
        // contiguos, así que un desfase de una posición los intercambiaría sin que el compilador chiste.
        // (Las armas traen Snd1 aunque el servidor sólo lo use para los instrumentos: ver GameData.)
        Assert.Equal(25, data.Obj(402)!.Snd1);
        Assert.NotEqual(data.Obj(402)!.Snd1, data.Obj(402)!.SndEspecial);
    }

    [Fact]
    public void Los_diez_que_suenan_son_exactamente_los_que_tienen_aura()
    {
        var data = Cargar();

        var conSonido = data.Objects.Values.Where(o => o.SndEspecial > 0).ToList();
        Assert.Equal(10, conSonido.Count);
        // La condición del original es `If Obj.Aura <> 0`: un ítem con sonido pero SIN aura no sonaría nunca.
        // Que los diez tengan aura es lo que hace que la regla portada alcance a todos.
        Assert.All(conSonido, o => Assert.True(o.Aura > 0,
            $"el objeto '{o.Name}' tiene SndEspecial pero no aura: con la regla del original no sonaría"));
    }

    [Fact]
    public void Los_instrumentos_siguen_teniendo_su_propio_sonido()
    {
        var data = Cargar();
        // Snd1 y SndEspecial son campos DISTINTOS y caminos distintos: el instrumento suena al USARLO, el
        // aura al EQUIPAR. Agregar el segundo no puede haberse comido el primero.
        var instrumentos = data.Objects.Values.Where(o => o.IsInstrument).ToList();
        Assert.NotEmpty(instrumentos);
        Assert.All(instrumentos, o => Assert.True(o.Snd1 > 0, $"el instrumento '{o.Name}' se quedó sin sonido"));
    }
}
