using System.Text.RegularExpressions;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// TODO COMANDO DE GM TIENE QUE CHEQUEAR PRIVILEGIO.
///
/// <para>Son setenta y cuatro handlers <c>Gm*</c> y basta que UNO se olvide del gate para que cualquier
/// jugador pueda tirarlo: <c>/MASSKILL</c> borra las criaturas de la pantalla, <c>/ECHAR</c> desconecta a
/// quien sea, <c>/DEST</c> destruye lo que hay en el piso. No hay un gate central antes del
/// <c>switch</c> — cada handler pone el suyo—, así que el que se agregue mañana sin él pasa desapercibido.</para>
///
/// <para>El chequeo vale por CUALQUIERA de las formas que el archivo ya usa: los helpers por rango o una
/// comparación directa contra <c>PrivUser</c>. Si aparece una forma nueva, va acá — con su nombre, no
/// aflojando el barrido.</para>
/// </summary>
public class ComandosGmGateadosTests
{
    /// <summary>Las maneras válidas de gatear, tal como están escritas en el archivo.</summary>
    private static readonly string[] Gates =
    {
        "RequireGm",            // el helper general: avisa y corta
        "IsAltoGm",             // Dios / Admin / RoleMaster
        "IsSemiDiosOrHigher",   // SemiDios para arriba
        "IsDios",
        "PrivilegioDe",         // el rango del Server.ini (lo usa /SETINIVAR, que exige Admin)
        "IsGm",                 // cualquiera de los cuatro tiers de staff (el `EsGm` del original)
        "Privileges <= PrivUser",   // gate silencioso: Consejero para arriba
        "Privileges &",             // máscara de bits directa
    };

    // Escribir el barrido costó TRES rondas y las tres fueron falsos positivos MIOS, no defectos del código:
    // primero faltaban `IsAltoGm`/`IsSemiDiosOrHigher`, después la comparación directa contra `PrivUser`, y
    // al final `IsGm`. Los 74 handlers estaban gateados desde el principio. Por eso la lista de arriba lleva
    // el nombre EXACTO de cada forma y un comentario: una forma nueva se agrega acá, no se afloja el barrido
    // con un patrón laxo que después no cace al que sí falte.

    [Fact]
    public void Todos_los_handlers_de_gm_chequean_privilegio()
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        var sinGate = new List<string>();
        int total = 0;

        foreach (Match m in Regex.Matches(fuente, @"private\s+(?:static\s+)?(?:async\s+)?[\w<>?\[\], ]+\s+(Gm\w+)\s*\("))
        {
            total++;
            string cuerpo = CuerpoDelMetodo(fuente, m.Index + m.Length);
            if (!Gates.Any(cuerpo.Contains))
                sinGate.Add($"{m.Groups[1].Value} (linea {fuente.Take(m.Index).Count(c => c == '\n') + 1})");
        }

        // Sin esto el test pasaria con la lista vacia si el regex dejara de enganchar.
        Assert.True(total >= 70, $"solo encontre {total} handlers Gm*; el parseo se rompio");
        Assert.True(sinGate.Count == 0,
            "hay comandos de GM sin chequeo de privilegio (los puede tirar cualquier jugador):\n  "
            + string.Join("\n  ", sinGate));
    }

    /// <summary>El cuerpo entre llaves del método que arranca en <paramref name="desde"/>, salteando strings y
    /// comentarios para que una llave adentro de un texto no corte el conteo.</summary>
    private static string CuerpoDelMetodo(string texto, int desde)
    {
        int i = texto.IndexOf('{', desde);
        if (i < 0) return "";
        int prof = 0;
        for (int j = i; j < texto.Length; j++)
        {
            char c = texto[j];
            if (c == '"')
            {
                j++;
                while (j < texto.Length && texto[j] != '"') j += texto[j] == '\\' ? 2 : 1;
            }
            else if (c == '/' && j + 1 < texto.Length && texto[j + 1] == '/')
            {
                while (j < texto.Length && texto[j] != '\n') j++;
            }
            else if (c == '{') prof++;
            else if (c == '}' && --prof == 0) return texto[i..j];
        }
        return texto[i..];
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
            $"no encontre '{relativo}' subiendo desde {AppContext.BaseDirectory}: sin el fuente el barrido no prueba nada.");
    }
    /// <summary>
    /// LOS QUE EL ORIGINAL GATEA POR TIER, no por "es GM".
    ///
    /// <para>El test de arriba exige que cada handler tenga ALGÚN gate; éste exige que tres de ellos tengan
    /// el que corresponde. La diferencia importa: <c>RequireGm</c> deja pasar a cualquiera de los cuatro
    /// tiers de staff, y estos tres comandos —crear items de la nada, y acuñar créditos de donador, que es
    /// la moneda equivalente a dinero real— el original los reserva a los tiers altos.</para>
    ///
    /// <para>Por qué no se ve solo: en el install del original <b>sólo la lista de Dios está poblada</b>
    /// (GAPS-CODIGO-MUERTO.md §1), así que ahí "es GM" y "es Dios" coinciden y aflojar el gate no cambia
    /// nada. Pero el <c>Server.ini</c> de este port acepta las cinco listas, así que un host que poblara
    /// <c>SemiDioses</c> le daría a un tier medio un poder que el VB6 le niega. Ya pasó con <c>/DONADOR</c> y
    /// <c>/DARPUN</c>, y lo destapó una revisión adversarial; <c>/CI</c> se corrigió el 2026-08-17.</para>
    /// </summary>
    public static TheoryData<string, string, string> GatesEstrictos => new()
    {
        { "GmCreateItem", "IsDios",   "Protocol.bas:9885 — If .flags.Privilegios And PlayerType.Dios" },
        { "GmDonador",    "IsDios",   "Protocol.bas:16116 — sólo Dios" },
        { "GmDarpun",     "IsAltoGm", "Protocol.bas:16564 — Dios/Admin/RoleMaster, bloquea SemiDios" },
    };

    [Theory]
    [MemberData(nameof(GatesEstrictos))]
    public void Los_gates_por_tier_no_se_aflojan_al_generico(string handler, string gate, string fuenteVb6)
    {
        string fuente = LeerFuente("server/src/CoverAO.Server/Net/PacketDispatcher.cs");
        var m = Regex.Match(fuente, @"private\s+(?:static\s+)?(?:async\s+)?[\w<>?\[\], ]+\s+" + handler + @"\s*\(");
        Assert.True(m.Success, $"no encontré el handler {handler}: el test dejó de medir");
        string cuerpo = CuerpoDelMetodo(fuente, m.Index + m.Length);

        Assert.True(cuerpo.Contains(gate),
            $"{handler} tiene que gatear con {gate} ({fuenteVb6}), y no lo hace");
        Assert.False(cuerpo.Contains("RequireGm"),
            $"{handler} usa el gate GENÉRICO: deja pasar a un SemiDios donde el original exige {gate} ({fuenteVb6})");
    }
}
