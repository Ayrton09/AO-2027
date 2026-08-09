using CoverAO.Server.Data;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Las pociones de SubTipo 5, que lanzan un hechizo sobre quien las toma. Hasta ahora caían en el
/// <c>default</c> del switch y el servidor contestaba, textualmente, "Esa poción todavía no tiene efecto".
///
/// El alcance no es una decisión nuestra: el original las rutea por <c>HechizoEstadoUsuario</c>
/// (InvUsuario.bas:1636), y ESE Sub lee Sanacion, Envenena, Incinera, CuraVeneno, Paraliza, Inmoviliza,
/// RemoverParalisis, Ceguera, Invisibilidad, SubeHP y los buffs de atributo — pero NO metamorfosis ni
/// desencantar. Si no aplica nada devuelve <c>Casteo = False</c> y hace <c>Exit Sub</c>: la poción ni siquiera
/// se consume. O sea que las tres que quedan inertes lo están TAMBIÉN en el original.
/// </summary>
public class PocionHechizoTests
{
    private static GameData Cargar()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "este test necesita la gamedata en disco");
        return d;
    }

    [Theory]
    // (objeto, hechizo que lanza) — los valores DUROS anclan el alineamiento: LanzaHechizo se agregó al final
    // del record ObjInfo y el loader pasa los argumentos por posición.
    [InlineData(166, 1)]     // Poción Violeta            -> Curar Veneno
    [InlineData(890, 1)]     // Poción Violeta (Alquimia) -> Curar Veneno
    [InlineData(893, 10)]    // Remover Parálisis         -> Remover paralisis
    [InlineData(1096, 14)]   // Invisibilidad             -> Invisibilidad
    [InlineData(1597, 32)]   // Poción de Sanar           -> Sanar
    [InlineData(507, 39)]    // Metamorfosis: Dragón      -> Metamorfosis (inerte, ver abajo)
    public void La_pocion_declara_su_hechizo(int objeto, int hechizo)
    {
        var data = Cargar();
        var o = data.Obj(objeto);
        Assert.NotNull(o);
        Assert.Equal(hechizo, o!.LanzaHechizo);
        Assert.Equal(11, o.ObjType);   // otPociones
        Assert.Equal(5, o.SubType);    // "lanzan hechizo"
    }

    [Fact]
    public void Las_ocho_pociones_estan_y_ninguna_otra_cosa_trae_el_campo()
    {
        var data = Cargar();
        var conHechizo = data.Objects.Values.Where(o => o.LanzaHechizo > 0).ToList();
        Assert.Equal(8, conHechizo.Count);
        // Todas son pociones de SubTipo 5: si alguna fuera de otro tipo, el camino que porté no la alcanzaría
        // y quedaría muda sin que nadie se entere.
        Assert.All(conHechizo, o => Assert.True(o.IsPotion && o.SubType == 5,
            $"'{o.Name}' trae LanzaHechizo pero no es una poción de SubTipo 5 (tipo={o.ObjType} sub={o.SubType})"));
    }

    [Fact]
    public void Las_tres_inertes_lo_son_porque_su_hechizo_no_tiene_efecto_modelado()
    {
        var data = Cargar();
        // Metamorfosis (39) y Desencantar (22) no los lee HechizoEstadoUsuario: la poción no aplica nada y no
        // se consume. Este test existe para que, si algún día se modelan esos hechizos, alguien vea acá que
        // las pociones se activan solas — y no lo descubra por un reporte de un jugador.
        foreach (var (objeto, hechizo) in new[] { (507, 39), (895, 22), (896, 22) })
        {
            var o = data.Obj(objeto);
            Assert.NotNull(o);
            Assert.Equal(hechizo, o!.LanzaHechizo);
            var h = data.Spell(hechizo);
            Assert.NotNull(h);
            // Ninguno de los campos que el Sub del original mira: por eso no pasa nada.
            Assert.True(h!.SubeHp == 0 && h.Invisibilidad == 0 && !h.IsSanacion && !h.IsCurePoison
                        && !h.IsRemoveParalysis && !h.IsParalyze && !h.IsPoison && !h.IsIncinerate && h.Ceguera == 0,
                $"el hechizo {hechizo} ahora SÍ tiene efecto modelado: la poción {objeto} pasó a funcionar sola");
        }
    }
}
