using System.Text.RegularExpressions;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL MISMO DEFECTO DE LA BÓVEDA, EN EL INVENTARIO Y EN SEIS LUGARES.
///
/// <para><c>FindSlotFor(p, obj, cantidad)</c> apila sólo si el montón no se pasa de
/// <c>MaxInventoryObjs</c> (10.000, el <c>MAX_INVENTORY_OBJS</c> del original). El parámetro tiene default 1
/// para los que agregan una unidad — y seis llamadores lo usaban así para después sumar N: fundir lingotes,
/// el adjunto de una carta, fabricar, sacar un recurso de un yacimiento, robarle a alguien y canjear. El
/// buscador decía que sí porque entraba UNO, y la pila terminaba arriba de 10.000 y clampeada contra
/// <c>short.MaxValue</c> (32.767), que es el paso previo a que se pierda en silencio lo que no entra.</para>
///
/// <para>Cuatro de los once llamadores SÍ pasaban la cantidad. De a pares otra vez: uno bien y otro mal.</para>
/// </summary>
public class InventarioTopeDeRanuraTests
{
    private const short Flecha = 480;

    private static Player ConPila(int enLaPila)
    {
        var p = new Player { Name = "Probador", Account = "cuenta_test", CharIndex = 1 };
        if (enLaPila > 0) p.Inventory[1] = new InventorySlot { ObjIndex = Flecha, Amount = (short)enLaPila };
        return p;
    }

    /// <summary>Pidiendo lugar para N, la ranura elegida tiene que aguantar N sin pasarse del tope.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(5000)]
    public void La_ranura_elegida_aguanta_la_cantidad_pedida(int cuantos)
    {
        var p = ConPila(GameLimits.MaxInventoryObjs - 50);   // casi llena: no entra un montón grande
        int slot = PacketDispatcher.FindSlotFor(p, Flecha, cuantos);
        Assert.NotEqual(0, slot);
        Assert.True(p.Inventory[slot].Amount + cuantos <= GameLimits.MaxInventoryObjs,
            $"la ranura {slot} quedaría en {p.Inventory[slot].Amount + cuantos}, por encima de {GameLimits.MaxInventoryObjs}");
    }

    /// <summary>Y el default de 1 NO alcanza para un montón: es justo lo que hacían los seis llamadores.</summary>
    [Fact]
    public void Pedir_lugar_para_uno_y_meter_muchos_es_lo_que_estaba_mal()
    {
        var p = ConPila(GameLimits.MaxInventoryObjs - 1);
        int conElDefault = PacketDispatcher.FindSlotFor(p, Flecha);        // entra UNO: apila en la 1
        int conLaCantidad = PacketDispatcher.FindSlotFor(p, Flecha, 500);  // no entran 500: manda a una vacía
        Assert.Equal(1, conElDefault);
        Assert.NotEqual(1, conLaCantidad);
        Assert.True(p.Inventory[conLaCantidad].IsEmpty);
    }

    /// <summary>
    /// BARRIDO DEL FUENTE: ninguna suma de inventario puede volver a toparse contra <c>short.MaxValue</c>.
    ///
    /// <para>Ese tope es 32.767 y el del juego es 10.000. Toparse contra el grande significa que la pila ya se
    /// pasó del tope real, y que lo que sobre de 32.767 se pierde sin decir nada. Es el guardia que impide la
    /// séptima repetición: los valores de arriba fijan lo que ya se arregló, esto cubre lo que se escriba
    /// mañana.</para>
    /// </summary>
    [Fact]
    public void Ninguna_suma_de_inventario_se_topa_contra_short_MaxValue()
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        var culpables = new List<string>();
        var lineas = fuente.Split('\n');
        for (int i = 0; i < lineas.Length; i++)
        {
            string s = lineas[i].Trim();
            if (s.StartsWith("//") || s.StartsWith("///") || s.StartsWith("*")) continue;
            if (Regex.IsMatch(s, @"Math\.Min\s*\(\s*short\.MaxValue\s*,[^)]*\.Amount"))
                culpables.Add($"PacketDispatcher.cs:{i + 1}  {s}");
        }
        Assert.True(culpables.Count == 0,
            "hay sumas de inventario topadas contra short.MaxValue en vez de GameLimits.MaxInventoryObjs:\n  "
            + string.Join("\n  ", culpables));
    }

    private static string LeerFuente(string relativo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string rel = relativo.Replace('/', Path.DirectorySeparatorChar);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"no encontré '{relativo}' subiendo desde {AppContext.BaseDirectory}: sin el fuente el barrido no prueba nada.");
    }
}
