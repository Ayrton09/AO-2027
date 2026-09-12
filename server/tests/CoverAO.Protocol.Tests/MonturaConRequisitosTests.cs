using System.Linq;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HUARGO, SÓLO PARA ORCOS. Reporte del tester del 2026-09-12 ("hay que poner que los orcos sólo puedan usar
/// huargos, o sea huargos y huargos reforzados") y decisión del autor.
///
/// <para><b>Esto NO es paridad, y el test lo fija a propósito.</b> En el original las monturas no pasan por ningún
/// chequeo de ficha: <c>CheckRazaUsaRopa</c> se llama desde un solo lugar —la armadura de cuerpo— y el huargo es
/// <c>otMonturas</c>, que se va por <c>DoEquita</c>, cuya única compuerta es la skill de Equitación. Allá un humano
/// puede montar un huargo. Lo que sí es del original es el DATO: los dos huargos traen <c>RazasProhibidas=1,2,3,4,5</c>
/// y nadie lo leía.</para>
///
/// <para>Y la otra mitad del reporte —"los orcos sólo huargos"— NO se implementa, porque no existe: de las 238
/// armaduras de cuerpo del catálogo, 150 no le prohíben nada al orco. Ese test está abajo y es el contrapeso.</para>
/// </summary>
public class MonturaConRequisitosTests
{
    private const int Huargo = 1344, HuargoReforzado = 1349;

    private static GameData Datos()
    {
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita la gamedata en disco");
        return data;
    }

    /// <summary>La regla sola: devuelve el mensaje del rechazo, o 0 si no le falta nada.</summary>
    [Fact]
    public void Devuelve_el_mensaje_del_primer_requisito_que_no_cumple()
    {
        var datos = Datos();
        var huargo = datos.Obj(Huargo);
        Assert.True(huargo is not null, "el Huargo (1344) tiene que existir en la data");

        byte orco = 0, otra = 0;
        for (byte r = 1; r <= 6; r++)
            if (huargo!.RaceForbidden(r)) { if (otra == 0) otra = r; }
            else if (orco == 0) orco = r;
        Assert.True(orco != 0 && otra != 0, "el Huargo dejó de tener razas prohibidas: el test no prueba nada");

        Assert.Equal(266, Equipment.QueLeFaltaParaUsarlo(huargo!, nivel: 50, clase: 1, raza: otra));   // raza prohibida
        Assert.Equal(0, Equipment.QueLeFaltaParaUsarlo(huargo!, nivel: 50, clase: 1, raza: orco));     // el orco sí
    }

    /// <summary>
    /// LOS DOS HUARGOS, CONTRA LA DATA: los dos dejan una sola raza en pie, y es la misma para los dos.
    /// </summary>
    [Fact]
    public void Los_dos_huargos_dejan_una_sola_raza_y_es_la_misma()
    {
        var datos = Datos();
        var permitidas = new[] { Huargo, HuargoReforzado }.Select(i =>
        {
            var o = datos.Obj((short)i);
            Assert.True(o is not null, $"la montura {i} tiene que existir");
            Assert.True(o!.IsMount, $"'{o.Name}' dejó de ser montura");
            return Enumerable.Range(1, 6).Where(r => !o.RaceForbidden((byte)r)).ToArray();
        }).ToList();

        Assert.All(permitidas, p => Assert.Single(p));
        Assert.Equal(permitidas[0][0], permitidas[1][0]);
    }

    /// <summary>
    /// Y EL NIVEL SIGUE VALIENDO para lo que lo pide, con su propio mensaje.
    /// </summary>
    [Fact]
    public void El_nivel_minimo_devuelve_su_propio_mensaje()
    {
        var datos = Datos();
        var conNivel = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i))
                                 .FirstOrDefault(o => o is { MinLevel: > 1 });
        if (conNivel is null) return;                      // sin data que lo pida no hay nada que medir

        Assert.Equal(268, Equipment.QueLeFaltaParaUsarlo(conNivel, nivel: 1, clase: 0, raza: 0));
        Assert.Equal(0, Equipment.QueLeFaltaParaUsarlo(conNivel, nivel: conNivel.MinLevel, clase: 0, raza: 0));
    }

    /// <summary>
    /// AL QUE YA ESTÁ MONTADO NO SE LE PREGUNTA NADA: si no, el que dejó de cumplir —cambió de raza con una poción, o
    /// cambió la data— se quedaría trabado arriba del animal para siempre. Y el GM pasa, como en el resto del equipo.
    /// </summary>
    [Fact]
    public void Al_que_ya_esta_montado_no_se_le_pregunta_y_el_gm_pasa()
    {
        var datos = Datos();
        var huargo = datos.Obj(Huargo)!;
        byte prohibida = (byte)Enumerable.Range(1, 6).First(r => huargo.RaceForbidden((byte)r));

        // subiéndose, sin ser GM y con la raza prohibida: rechazado
        Assert.Equal(266, Equipment.QueLeFaltaParaMontar(huargo, yaEstaMontado: false, esGm: false, 50, 1, prohibida));
        // ya arriba: se puede bajar igual
        Assert.Equal(0, Equipment.QueLeFaltaParaMontar(huargo, yaEstaMontado: true, esGm: false, 50, 1, prohibida));
        // GM: pasa
        Assert.Equal(0, Equipment.QueLeFaltaParaMontar(huargo, yaEstaMontado: false, esGm: true, 50, 1, prohibida));
    }

    /// <summary>
    /// EL CONTRAPESO: "los orcos sólo huargos" NO existe. Si alguna vez este test se pone rojo es porque alguien
    /// implementó esa mitad del reporte, y hay que revisar por qué: la mayoría de las armaduras del juego no le
    /// prohíben nada al orco.
    /// </summary>
    [Fact]
    public void El_orco_puede_usar_la_mayoria_de_las_armaduras()
    {
        var datos = Datos();
        var huargo = datos.Obj(Huargo);
        byte orco = (byte)Enumerable.Range(1, 6).First(r => !huargo!.RaceForbidden((byte)r));

        var armaduras = Enumerable.Range(1, 1700).Select(i => datos.Obj((short)i))
                                  .Where(o => o is { IsArmor: true }).ToList();
        int puede = armaduras.Count(o => !o!.RaceForbidden(orco));

        Assert.True(armaduras.Count > 100, $"aparecieron {armaduras.Count} armaduras: el test no prueba nada");
        Assert.True(puede > armaduras.Count / 2,
            $"el orco sólo puede usar {puede} de {armaduras.Count} armaduras: 'los orcos sólo huargos' no sale de esta data");
    }
}
