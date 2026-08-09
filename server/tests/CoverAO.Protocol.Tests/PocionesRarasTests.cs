using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS CUATRO POCIONES QUE EL SWITCH NO ATENDÍA. El <c>Select Case TipoPocion</c> del original
/// (InvUsuario.bas:1567) tiene los casos 1‑9 y 13; el port tenía 1‑5 y 13, así que once objetos del install
/// caían en el <c>default</c> y contestaban "no tiene efecto".
///
/// <para>Estos tests miran la DATA, no el handler: el veredicto de "¿esto es un gap o es un objeto que el
/// original tampoco sabía usar?" lo decide qué SubTipos existen en el <c>obj.dat</c> y cuáles tienen un
/// <c>Case</c> allá. Es la misma lección que dejó la auditoría de combate — el código de los dos lados puede
/// mirarse igual y la data decide cuál de las dos ramas está viva.</para>
/// </summary>
public class PocionesRarasTests
{
    private static GameData Cargar()
    {
        var d = GameData.Load();
        Assert.False(d.SourceDir is null, "estos tests necesitan la gamedata en disco");
        return d;
    }

    [Theory]
    [InlineData(498, 6)]    // Scroll a Intermundia
    [InlineData(504, 7)]    // Poción mágica de cambio de cara
    [InlineData(505, 8)]    // Poción mágica de cambio de sexo
    [InlineData(506, 9)]    // Poción mágica de Nhareth
    public void Los_cuatro_objetos_que_motivaron_el_porte_existen_con_su_SubTipo(int indice, int subtipo)
    {
        var o = Cargar().Obj((short)indice);
        Assert.NotNull(o);
        Assert.True(o!.IsPotion, $"el objeto {indice} tiene que ser otPociones (11) para entrar al switch");
        Assert.Equal(subtipo, o.SubType);
    }

    [Fact]
    public void El_cofre_de_creditos_se_lee_aunque_el_dat_escriba_la_clave_con_otra_grafia()
    {
        // Los tres cofres traen `Subtipo` con t minúscula, no `SubTipo`. El lector del VB6 normaliza con UCase$
        // y no se entera; el nuestro tiene que listar las dos grafías a mano. Sin esto el cofre llega con
        // SubType=0, no entra al case 13 y los créditos de donador no se acreditan nunca — en silencio.
        foreach (short cofre in new short[] { 1604, 1605, 1606 })
        {
            var o = Cargar().Obj(cofre);
            Assert.NotNull(o);
            Assert.Equal(13, o!.SubType);
            Assert.True(o.IsCreditChest, $"el cofre {cofre} tiene que otorgar créditos");
        }
    }

    [Fact]
    public void Los_SubTipo_10_11_y_12_siguen_sin_case_porque_alla_tampoco_lo_tienen()
    {
        // Scroll x2 Experiencia (10), x2 Oro (11) y del Trueno (12): el Select Case del original salta del 9 al
        // 13. Son data sin código en el server de origen, así que el port no los inventa. Este test fija el
        // VEREDICTO: si algún día se los implementa, que sea a sabiendas y no por creer que faltaban.
        var data = Cargar();
        foreach (short scroll in new short[] { 509, 516, 517 })
        {
            var o = data.Obj(scroll);
            Assert.NotNull(o);
            Assert.InRange(o!.SubType, 10, 12);
        }
    }

    [Fact]
    public void Ninguna_pocion_del_install_quedo_con_un_SubTipo_sin_atender()
    {
        // El barrido al revés: en vez de listar los que sé, pregunto qué SubTipos de poción trae la data. Los
        // atendidos son 1‑9 y 13; el 0 es de los cuatro objetos cuya clave el .dat escribe `TipoPocion` —una
        // clave que NADIE lee, tampoco el VB6 (FileIO.bas:816 sólo lee SubTipo), así que allá también valen 0.
        var atendidos = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        var raros = Cargar().Objects.Values
            .Where(o => o.IsPotion && !atendidos.Contains(o.SubType))
            .Select(o => $"{o.Index} ({o.Name}) SubTipo={o.SubType}")
            .ToList();
        Assert.True(raros.Count == 0, "pociones con un SubTipo que nadie mira: " + string.Join(", ", raros));
    }

    [Theory]
    [InlineData(1, 1, 1, 30)]        // Humano   varón
    [InlineData(2, 1, 101, 120)]     // Elfo     varón — ValidarCabeza dice 101-121
    [InlineData(3, 1, 201, 213)]     // Drow     varón — ValidarCabeza dice 202-212
    [InlineData(4, 1, 401, 410)]     // gnomo    varón — ValidarCabeza dice 401-409
    [InlineData(5, 1, 301, 313)]     // enano    varón — ValidarCabeza dice 301-315
    [InlineData(6, 1, 501, 514)]     // Orco     varón
    [InlineData(1, 2, 70, 80)]       // Humana
    [InlineData(2, 2, 170, 189)]     // Elfa
    [InlineData(3, 2, 270, 278)]     // Drow
    [InlineData(4, 2, 470, 481)]     // gnoma
    [InlineData(5, 2, 370, 373)]     // enana
    [InlineData(6, 2, 570, 573)]     // Orca
    public void La_cabeza_de_la_pocion_sale_del_rango_de_ChangeHead_no_del_de_crear_personaje(
        byte raza, byte genero, int min, int max)
    {
        // Primero: la tabla que USA la poción dice lo que dice ChangeHead. Esto es lo que se pone rojo si
        // alguien "simplifica" haciendo que HeadRangeDePocion delegue en HeadRange.
        var pocion = Equipment.HeadRangeDePocion(raza, genero);
        Assert.Equal(min, pocion.Min);
        Assert.Equal(max, pocion.Max);

        // Y segundo, el porqué de que sean dos tablas: en las cuatro razas masculinas comentadas arriba los dos
        // bloques del original NO coinciden. Si algún día coincidieran, este assert avisa que ya se pueden
        // unificar — hoy afirma la discrepancia, que es un hecho de la fuente y no una preferencia nuestra.
        var creacion = Equipment.HeadRange(raza, genero);
        Assert.NotNull(creacion);
        bool discrepa = creacion!.Value.Min != pocion.Min || creacion.Value.Max != pocion.Max;
        Assert.Equal(genero == 1 && raza is 2 or 3 or 4 or 5, discrepa);
    }
}
