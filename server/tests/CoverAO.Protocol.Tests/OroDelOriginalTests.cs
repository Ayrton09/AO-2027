using System.Text.RegularExpressions;
using CoverAO.Server.Data;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LAS DOS REGLAS DEL ORO QUE EL PORT NO TENÍA.
///
/// <para><b>1. Se le puede transferir a alguien DESCONECTADO.</b> El <c>TransferGOLD</c> del original (VB6
/// Protocol.bas:15821) mueve la <b>bóveda</b>, no la billetera, y si el destinatario no está en el mundo le
/// escribe el saldo directo al <c>.chr</c> con <c>GetVar</c>/<c>WriteVar</c> sobre <c>[STATS] BANCO</c>. Es
/// para lo que existe la función —el juego la llama "tu cuenta Goliath"— y acá contestaba "Destinatario
/// inválido" a media función: el handler exigía que el otro estuviera conectado.</para>
///
/// <para><b>2. El oro en mano tiene techo.</b> <c>MAXORO</c> son noventa millones (Declares.bas:344) y el
/// original lo aplica sobre el TOTAL después de sumar, en los dos lugares donde entra de a montones: vender
/// (Comercio.bas:169) y robar (Trabajo.bas:1591). Acá el robo tenía anotado "el VB6 capea a MAXORO, el robo es
/// de a cientos, nunca llega", que razona sobre lo que ENTRA en vez de sobre lo que queda.</para>
/// </summary>
public class OroDelOriginalTests
{
    /// <summary>La tabla de niveles no interviene acá; alcanza con una que no esté vacía.</summary>
    private static CoverAO.Server.Game.Leveling Leveling() =>
        new(new Dictionary<int, long> { [1] = 100, [2] = 200 });

    private static string Raiz()
    {
        string d = Path.Combine(Path.GetTempPath(), "coverao-oro-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>El <c>WriteVar</c> del original: suma sobre lo que ya había y deja el resto del archivo igual.</summary>
    [Fact]
    public void Acreditar_la_boveda_de_un_desconectado_suma_sobre_lo_que_habia()
    {
        string raiz = Raiz();
        try
        {
            var store = new FileStore(raiz);
            store.WriteCharacter("Ausente", "[STATS]\r\nGLD=7\r\nBANCO=100\r\n[FLAGS]\r\nMuerto=0\r\n");
            var db = new Persistence(store, Leveling());

            Assert.Equal(150, db.AcreditarBovedaOffline("Ausente", 50));
            Assert.Equal(200, db.AcreditarBovedaOffline("Ausente", 50));   // sobre lo ya acreditado, no sobre 100

            // Y NO puede pisar el resto del personaje: el original toca UNA clave.
            string ini = store.ReadCharacter("Ausente")!;
            Assert.Contains("GLD=7", ini);
            Assert.Contains("Muerto=0", ini);
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>Sin <c>.chr</c> no hay a quién acreditarle: el handler necesita distinguirlo para contestar
    /// "El personaje no existe." en vez de tragarse el oro.</summary>
    [Fact]
    public void Acreditar_a_alguien_que_no_existe_devuelve_nulo()
    {
        string raiz = Raiz();
        try
        {
            var db = new Persistence(new FileStore(raiz), Leveling());
            Assert.Null(db.AcreditarBovedaOffline("NadieAqui", 50));
        }
        finally { Directory.Delete(raiz, recursive: true); }
    }

    /// <summary>
    /// BARRIDO DEL FUENTE: donde entra oro de a montones, el tope es <c>MaxOro</c> y no <c>int.MaxValue</c>.
    ///
    /// <para>Toparse contra <c>int.MaxValue</c> son 2.147 millones: veinticuatro veces el techo del juego, o
    /// sea que en la práctica no topa nada. Este barrido fija lo que se acaba de corregir en la venta y en el
    /// robo, y sobre todo cubre al que se escriba mañana — que es donde esta familia ya reapareció seis veces.</para>
    /// </summary>
    [Fact]
    public void Ninguna_suma_de_oro_se_topa_contra_int_MaxValue()
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        var culpables = new List<string>();
        var lineas = fuente.Split('\n');
        for (int i = 0; i < lineas.Length; i++)
        {
            string s = lineas[i].Trim();
            if (s.StartsWith("//") || s.StartsWith("///") || s.StartsWith("*")) continue;
            if (Regex.IsMatch(s, @"\.Gold\s*=.*int\.MaxValue"))
                culpables.Add($"PacketDispatcher.cs:{i + 1}  {s}");
        }
        Assert.True(culpables.Count == 0,
            "hay sumas de oro topadas contra int.MaxValue en vez de MaxOro (90.000.000):\n  "
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
