using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// CUÁNTO DURA LA POCIÓN DE ATRIBUTO, con el número de la data y no con uno escrito a mano. El `DuracionEfecto` del obj.dat está
/// en VUELTAS del loop de 40 ms —el original le resta una por pasada— y acá se leía como segundos y se multiplicaba por 25: la
/// Amarilla y la Verde duraban 13 h 53 min en vez de 33 min 20 s. El tester lo reportó como que el efecto no se le iba más.
/// </summary>
public class DuracionDePocionTests
{
    private const short Amarilla = 36, Verde = 39, AmarillaAlquimia = 889, VerdeAlquimia = 892;
    private const int VueltasDelInstall = 50_000, VueltasAlquimia = 150_000;

    /// <summary>LAS DOS POCIONES DEL INSTALL DURAN LO QUE DICE LA DATA, en vueltas, y eso son 33 min 20 s.</summary>
    [Fact]
    public void La_pocion_dura_las_vueltas_que_trae_la_data()
    {
        var d = GameData.Load();
        if (d.Obj(Amarilla) is null || d.Obj(Verde) is null) return;
        Assert.Equal(VueltasDelInstall, d.Obj(Amarilla)!.DuracionEfectoEnVueltas);   // premisa: la data trae el campo
        Assert.Equal(VueltasDelInstall, d.Obj(Verde)!.DuracionEfectoEnVueltas);

        Assert.Equal(VueltasDelInstall, Potions.VueltasDeEfecto(d.Obj(Amarilla)!));
        Assert.Equal(VueltasDelInstall, Potions.VueltasDeEfecto(d.Obj(Verde)!));
        // 50.000 vueltas x 40 ms = 2.000 s = 33 min 20 s, que es lo que dura en el original.
        Assert.Equal(2_000, Potions.VueltasDeEfecto(d.Obj(Amarilla)!) * GameLoop.TickMs / 1_000);
    }

    /// <summary>Y LAS DE ALQUIMIA, 100 MINUTOS.</summary>
    [Fact]
    public void Las_de_alquimia_duran_cien_minutos()
    {
        var d = GameData.Load();
        if (d.Obj(AmarillaAlquimia) is null || d.Obj(VerdeAlquimia) is null) return;
        Assert.Equal(VueltasAlquimia, d.Obj(AmarillaAlquimia)!.DuracionEfectoEnVueltas);
        Assert.Equal(6_000, Potions.VueltasDeEfecto(d.Obj(VerdeAlquimia)!) * GameLoop.TickMs / 1_000);
    }

    /// <summary>UNA POCIÓN SIN DURACIÓN EN LA DATA DURA UNA VUELTA, no cero: un efecto que no dura nada no se sentiría.</summary>
    [Fact]
    public void Sin_duracion_en_la_data_dura_una_vuelta()
    {
        var sinDuracion = new ObjInfo(1, "de prueba", ObjType: 11, MaxModifier: 0, Value: 0, Anim: 0);
        Assert.Equal(1, Potions.VueltasDeEfecto(sinDuracion));
    }
}
