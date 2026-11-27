using CoverAO.Server.Data;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL EQUIPO Y LA BÓVEDA (el tester, 27/11, sobre la 1.1.1): el bot guardaba lo que acababa de comprar antes de ponérselo, el que
/// revivía desnudo no sacaba nada de la bóveda llena de armas, y el rico con la mochila llena no pasaba nunca por el pueblo.
/// </summary>
public sealed class EquipoDeLaBovedaTests
{
    private static readonly Lazy<GameData> Datos = new(GameData.Load);

    private static ObjInfo Llamado(string nombre) => Datos.Value.Objects.Values.Where(o => o.Name == nombre).OrderBy(o => o.Index).First();

    [Fact]
    public void De_lo_mejor_deja_uno_y_guarda_los_repetidos()
    {
        var d = Datos.Value;
        short spatha = Llamado("Spatha").Index, escudo = Llamado("Escudo de Hierro +2").Index;
        bool LoMejor(short o) => o == spatha || o == escudo;
        // Recién comprada y sin poner: queda.
        Assert.Empty(Bot.LoQueGuarda(d, [(1, spatha, 1, false)], false, false, _ => false, loMejor: LoMejor));
        // Tres sin poner: guarda dos.
        Assert.Equal((1, spatha, 2), Assert.Single(Bot.LoQueGuarda(d, [(1, spatha, 3, false)], false, false, _ => false, loMejor: LoMejor)));
        // Apilados en el puesto (Ciro, 66): guarda 65.
        Assert.Equal((1, escudo, 65), Assert.Single(Bot.LoQueGuarda(d, [(1, escudo, 66, true)], false, false, _ => false, loMejor: LoMejor)));
        // Uno puesto y otro igual en otra ranura: guarda el de la otra.
        Assert.Equal((2, spatha, 1), Assert.Single(Bot.LoQueGuarda(d, [(1, spatha, 1, true), (2, spatha, 1, false)], false, false, _ => false,
                                                                    loMejor: LoMejor)));
        // Lo que no es lo mejor, entero.
        Assert.Equal((1, spatha, 3), Assert.Single(Bot.LoQueGuarda(d, [(1, spatha, 3, false)], false, false, _ => false, loMejor: _ => false)));
    }

    [Fact]
    public void Saca_de_la_boveda_lo_mejor_de_cada_clase_que_le_falta()
    {
        var d = Datos.Value;
        var spatha = Llamado("Spatha");
        var daga = Llamado("Daga");
        var cuero = Llamado("Armadura de Cuero");
        Assert.True(Bot.ValorDeCombate(spatha) > Bot.ValorDeCombate(daga));
        var boveda = new List<(byte, short, short)> { (1, daga.Index, 1), (2, spatha.Index, 23), (3, cuero.Index, 7) };
        Func<ObjInfo, bool>[] clases = [Bot.EsArma, o => o.IsArmor];
        // Desnudo: la Spatha (no la Daga) y la armadura, de a una ranura.
        var retira = Bot.EquipoParaRetirar(d, boveda, clases, _ => true, _ => 0);
        Assert.Equal(new[] { ((byte)2, spatha.Index), ((byte)3, cuero.Index) }, retira.ToArray());
        // Con un arma mejor encima, sólo la armadura.
        Assert.Equal(new[] { ((byte)3, cuero.Index) },
                     Bot.EquipoParaRetirar(d, boveda, clases, _ => true, es => es == clases[0] ? 1_000 : 0).ToArray());
        // Lo que no se puede poner, no.
        Assert.Empty(Bot.EquipoParaRetirar(d, boveda, clases, _ => false, _ => 0));
    }

    [Fact]
    public void Con_la_mochila_llena_va_al_pueblo()
    {
        Assert.True(Bot.ConLaMochilaLlena(25));
        Assert.False(Bot.ConLaMochilaLlena(24));
    }
}
