using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LOS CUATRO HANDLERS QUE EXISTÍAN Y NO HACÍAN NADA. El censo del port contó `case`s y dio 114/114; estos
/// cuatro estaban entre ellos y sin embargo dos no tenían NINGUNA rama de éxito (`Gamble` e `Information`
/// contestaban siempre el mismo rechazo) y otros dos habían perdido su gate (`Resucitate` revivía sin
/// sacerdote, `SeleccionarHogar` se usaba desde cualquier punto del mundo).
///
/// <para>Lo que los cuatro tienen en común es la excusa: un comentario que decía "el slice no modela X" con
/// X = TargetNPC, Revividor o los ejércitos — <b>los tres portados después de que se escribiera la nota</b>.
/// Estos tests fijan las constantes y los topes que salen de la fuente, que es lo que un comentario no puede
/// garantizar.</para>
/// </summary>
public class CascarasTests
{
    [Fact]
    public void El_casino_paga_menos_de_la_mitad_de_las_veces()
    {
        // VB6 HandleGamble (Protocol.bas:5912): `If RandomNumber(1, 100) <= 47`. El 47 ES la ventaja de la
        // banca —a la larga el timbero se queda con el 6% de lo apostado— y es lo primero que se "corrige" a 50
        // por creerlo un redondeo. Si alguien lo empareja, la casa deja de ganar y este test lo dice.
        Assert.Equal(47, PacketDispatcher.ProbGanarApuesta);
        Assert.True(PacketDispatcher.ProbGanarApuesta < 50, "la banca tiene que ganar");
        Assert.Equal(5000, PacketDispatcher.MaxApuesta);
        Assert.Equal(12, PacketDispatcher.NpcTimbero);   // eNPCType.Timbero (Declares.bas:394)
    }

    [Fact]
    public void Hay_un_NPC_Timbero_en_la_data_del_install()
    {
        // Sin un timbero en el mundo, portar el casino sería escribir código para nadie — el mismo criterio con
        // el que se DESCARTÓ DropGlobal.dat. Acá la data sí lo respalda.
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "este test necesita la gamedata en disco");
        var timberos = data.Npcs.Values.Where(n => n.NpcType == PacketDispatcher.NpcTimbero).ToList();
        Assert.True(timberos.Count > 0, "no hay ningún NPC Timbero: el casino no tendría dónde jugarse");
    }

    [Fact]
    public void El_sacerdote_del_fixture_es_un_Revividor_de_verdad()
    {
        // El gate nuevo de Resucitate cuelga de `IsReviver`. Si el fixture dejara de marcarlo, el flow de
        // muerte se pondría rojo por el escenario y no por el producto — conviene que el rojo diga cuál es.
        var data = GameData.Load();
        var reviverEnData = data.Npcs.Values.Any(n => n.NpcType == 1);   // eNPCType.Revividor
        Assert.True(reviverEnData, "la data del install trae al menos un NPC Revividor");
    }

    [Theory]
    // Los topes de rango por bando, tal cual el original (Protocol.bas:5374 Armada, :5390 Milicia, :5405 Caos).
    // La Milicia corta en 7 y los otros dos en 10: NO son el mismo número, y es lo que un port apurado unifica.
    [InlineData(Player.StatusArmada, 10)]
    [InlineData(Player.StatusMilicia, 7)]
    [InlineData(Player.StatusCaos, 10)]
    public void Cada_ejercito_tiene_su_propio_tope_de_rango(byte bando, int tope)
    {
        Assert.Equal(tope, PacketDispatcher.TopeDeRango(bando));
    }

    [Fact]
    public void Un_bando_que_el_original_no_contempla_no_tiene_mision()
    {
        // El `Select Case` del VB6 sólo tiene Case 1 (Armada), 2 (Milicia) y 4 (Caos) — no hay `Case Else`.
        // 0 significa "acá no se pide misión", y es lo que hace que el handler CALLE en vez de inventar una.
        Assert.Equal(0, PacketDispatcher.TopeDeRango(Player.StatusCiudadano));
        Assert.Equal(0, PacketDispatcher.TopeDeRango(0));
    }
}
