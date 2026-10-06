using System.Text.RegularExpressions;
using CoverAO.Protocol;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Byte-parity guard against the ORIGINAL VB6 source of CoverAO-2021 (the ground truth).
/// The wire ID of every packet is the numeric value of its member in the VB6
/// <c>ServerPacketID</c> / <c>ClientPacketID</c> enums (positional from 0, since the VB6 enums
/// carry no explicit <c>= N</c>). A single insertion/removal/reordering in either enum would shift
/// every subsequent ID and silently break a real VB6 client. These tests re-derive the VB6 values
/// straight from <c>Protocol.bas</c> and assert the C# enums match name-by-name, case-insensitively.
/// </summary>
public class ProtocolParityTests
{
    // ---- Locate the VB6 source relative to the repo root (walk up from the test assembly). ----
    private static string? FindVb6Protocol()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CoverAO-2021", "Servidor", "Codigo", "Protocol.bas");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Parses a VB6 <c>Enum</c> body into name→value, mirroring VB6 semantics:
    /// members number positionally from 0; an explicit <c>= N</c> resets the running counter.</summary>
    private static Dictionary<string, int> ParseVb6Enum(string text, string enumName)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        bool inside = false;
        int next = 0;
        var header = new Regex($@"^\s*(?:Public|Private)\s+Enum\s+{Regex.Escape(enumName)}\b", RegexOptions.IgnoreCase);
        var member = new Regex(@"^\s*([A-Za-z_]\w*)\s*(?:=\s*(-?\w+))?\s*(?:'.*)?$");
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inside)
            {
                if (header.IsMatch(line)) inside = true;
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*End\s+Enum\b", RegexOptions.IgnoreCase)) break;
            var stripped = line.Split('\'')[0].Trim(); // drop VB6 comment
            if (stripped.Length == 0) continue;
            var m = member.Match(line);
            if (!m.Success) continue;
            int val = next;
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var assigned))
                val = assigned;
            result[m.Groups[1].Value] = val;
            next = val + 1;
        }
        return result;
    }

    private static Dictionary<int, string> CsEnumByValue<TEnum>() where TEnum : struct, Enum
        => Enum.GetValues<TEnum>().ToDictionary(v => Convert.ToInt32(v), v => v.ToString());

    /// <summary>LOS IDS QUE SON NUESTROS Y NO DEL VB6. El protocolo del original termina en el 113; lo que
    /// esté por encima es una extensión de este port y tiene que estar declarada acá, con su motivo. La lista
    /// existe para que agregar un paquete sea una DECISIÓN visible y no un descuido: el guard de paridad sigue
    /// exigiendo que todo lo que esté en el rango del VB6 coincida nombre por nombre y valor por valor.</summary>
    private static readonly Dictionary<int, string> ExtensionesPropias = new()
    {
        // Borrar un personaje (1b del informe del tester). El original NO tiene borrado: su `Tipo = 2` de
        // ProcesosLogin es una rama muerta que contesta `EjecutarAccion(5, "0")` y no toca nada
        // (Protocol.bas:15742), así que no había de dónde portarlo.
        [114] = "BorrarPersonaje",
        // Elegir el familiar (tipo + nombre). El subsistema entero es un agregado nuestro: el original trae la
        // data —las 9 fichas 'FAMILIAR, los hechizos 59 y 79, la Veterinaria— y le arrancó el código. Va como
        // paquete aparte y no como campos nuevos de `LoginNewChar` para no sacar de la paridad de bytes al
        // paquete de creación, que sí es del original.
        [115] = "ElegirFamiliar",
        // El Centro de Subastas (pedido del tester, 06/10). El original dejó al Subastador, los textos de la ventana y los
        // mensajes 318-321, sin código ni paquetes.
        [116] = "SubastasPedir",
        [117] = "SubastaPublicar",
        [118] = "SubastaOfertar",
        [119] = "SubastaComprar",
        [120] = "SubastaCancelar",
    };

    /// <summary>Lo mismo, del lado del SERVIDOR. Va aparte porque el techo del VB6 no es el mismo en las dos
    /// direcciones: el protocolo del original llega al 113 en los paquetes del cliente y al 106 en los del
    /// servidor.</summary>
    private static readonly Dictionary<int, string> ExtensionesPropiasDelServidor = new()
    {
        // La ficha del familiar. El original tenía el panel dibujado en `frmEstadisticas` —con las etiquetas
        // de nivel, vida y experiencia ya puestas, todas en Visible = 0— y ningún paquete que lo alimentara.
        [107] = "FamiliarInfo",
        // La vida de un jefe, para la barra de arriba de la pantalla (pedido del tester, 2026-09-27). El original no
        // muestra la vida de ninguna criatura.
        [108] = "VidaDelJefe",
        // La lista del Centro de Subastas (pedido del tester, 06/10).
        [109] = "SubastasLista",
    };

    /// <summary>El último id que usa el protocolo del VB6 en cada dirección. Todo lo nuestro va por encima.</summary>
    private const int UltimoIdDelVb6 = 113;
    private const int UltimoIdDelVb6Servidor = 106;

    [Fact]
    public void Las_extensiones_propias_estan_declaradas_y_fuera_del_rango_del_vb6()
    {
        foreach (var (id, nombre) in ExtensionesPropias)
        {
            Assert.True(id > UltimoIdDelVb6, $"el id {id} ({nombre}) pisa el rango del VB6 (0..{UltimoIdDelVb6})");
            Assert.True(Enum.IsDefined(typeof(ClientPacketId), (byte)id), $"el id {id} está declarado como extensión y no existe en el enum");
            Assert.Equal(nombre, ((ClientPacketId)(byte)id).ToString());
        }
        // Y AL REVÉS, que es la mitad que atrapa el descuido: cualquier miembro del enum por encima del rango
        // del VB6 tiene que estar en la lista. Agregar un paquete sin declararlo acá pone este test en rojo.
        foreach (var v in Enum.GetValues<ClientPacketId>())
        {
            int id = (int)v;
            if (id > UltimoIdDelVb6)
                Assert.True(ExtensionesPropias.ContainsKey(id),
                    $"{v} ({id}) está por encima del protocolo del VB6 y no figura en ExtensionesPropias: " +
                    "si es un paquete nuestro, declaralo ahí con el motivo.");
        }

        // Y LAS DEL SERVIDOR, con la misma regla en las dos direcciones. Esta mitad faltaba: la lista existía
        // sólo para los paquetes del cliente, así que un paquete SC nuestro entraba sin declararse.
        foreach (var (id, nombre) in ExtensionesPropiasDelServidor)
        {
            Assert.True(id > UltimoIdDelVb6Servidor, $"el id {id} ({nombre}) pisa el rango del VB6 (0..{UltimoIdDelVb6Servidor})");
            Assert.True(Enum.IsDefined(typeof(ServerPacketId), (byte)id), $"el id {id} está declarado como extensión y no existe en el enum");
            Assert.Equal(nombre, ((ServerPacketId)(byte)id).ToString());
        }
        foreach (var v in Enum.GetValues<ServerPacketId>())
        {
            int id = (int)v;
            if (id > UltimoIdDelVb6Servidor)
                Assert.True(ExtensionesPropiasDelServidor.ContainsKey(id),
                    $"{v} ({id}) está por encima del protocolo del VB6 y no figura en ExtensionesPropiasDelServidor.");
        }
    }

    private static void AssertEnumParity(string enumName, Dictionary<int, string> cs)
    {
        var path = FindVb6Protocol();
        // Si la fuente VB6 (src/CoverAO-2021) no está (p.ej. tras el retiro de Fase 4), esta comparación
        // no puede correr — pero el guard permanente NO desaparece: los snapshots de abajo
        // (*_snapshot_pins_ids) fijan los valores esperados sin depender del árbol VB6.
        if (path is null) return;
        var vb6 = ParseVb6Enum(File.ReadAllText(path), enumName);
        // VB6 value -> name
        var vb6ByValue = vb6.ToDictionary(kv => kv.Value, kv => kv.Key);
        // Las extensiones NUESTRAS se sacan de la comparación: por definición no están en el VB6. Que estén
        // declaradas, y que no pisen el rango del original, lo verifica el test de arriba.
        if (enumName == "ClientPacketID")
            cs = cs.Where(kv => !ExtensionesPropias.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (enumName == "ServerPacketID")
            cs = cs.Where(kv => !ExtensionesPropiasDelServidor.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        // Same number of members on both sides.
        Assert.True(vb6ByValue.Count == cs.Count,
            $"{enumName}: VB6 tiene {vb6ByValue.Count} miembros, C# {cs.Count}.");

        // Every C# id must map to the same-named VB6 member (case-insensitive).
        foreach (var (value, csName) in cs)
        {
            Assert.True(vb6ByValue.TryGetValue(value, out var vb6Name),
                $"{enumName}: el valor {value} ({csName}) no existe en el enum VB6.");
            Assert.True(string.Equals(vb6Name, csName, StringComparison.OrdinalIgnoreCase),
                $"{enumName}[{value}]: VB6='{vb6Name}' vs C#='{csName}' (IDs desalineados).");
        }
    }

    [Fact]
    public void ServerPacketId_matches_vb6_source_byte_for_byte()
        => AssertEnumParity("ServerPacketID", CsEnumByValue<ServerPacketId>());

    [Fact]
    public void ClientPacketId_matches_vb6_source_byte_for_byte()
        => AssertEnumParity("ClientPacketID", CsEnumByValue<ClientPacketId>());

    // ---- Durable snapshot: pins the wire IDs WITHOUT the VB6 tree, so the guard survives Fase 4's
    // removal of src/CoverAO-2021. Member counts + spot-checks across the range catch any reorder
    // (a single insert/remove shifts every following id, which the spread of anchors detects). ----

    [Fact]
    public void ServerPacketId_snapshot_pins_ids()
    {
        // 107 del VB6 (0..106) + las extensiones nuestras.
        Assert.Equal(107 + ExtensionesPropiasDelServidor.Count, Enum.GetValues<ServerPacketId>().Length);
        Assert.Equal(107, (int)ServerPacketId.FamiliarInfo);   // el primero que es nuestro
        Assert.Equal(108, (int)ServerPacketId.VidaDelJefe);
        Assert.Equal(109, (int)ServerPacketId.SubastasLista);
        Assert.Equal(0, (int)ServerPacketId.LoggedSuccessful);
        Assert.Equal(9, (int)ServerPacketId.CommerceInit);
        Assert.Equal(10, (int)ServerPacketId.BankInit);
        Assert.Equal(14, (int)ServerPacketId.UpdateGold);
        Assert.Equal(19, (int)ServerPacketId.ChatOverHeadLocale);
        Assert.Equal(20, (int)ServerPacketId.ConsoleMsg);
        Assert.Equal(25, (int)ServerPacketId.CharacterCreate);
        Assert.Equal(48, (int)ServerPacketId.BlacksmithWeapons);
        Assert.Equal(53, (int)ServerPacketId.SastreObjects);
        Assert.Equal(68, (int)ServerPacketId.TrainerCreatureList);
        Assert.Equal(76, (int)ServerPacketId.GuildDetails);
        Assert.Equal(82, (int)ServerPacketId.UpdateTagAndStatus);
        Assert.Equal(106, (int)ServerPacketId.EjecutarAccion);
    }

    [Fact]
    public void ClientPacketId_snapshot_pins_ids()
    {
        // 114 del VB6 (0..113) + las extensiones nuestras.
        Assert.Equal(114 + ExtensionesPropias.Count, Enum.GetValues<ClientPacketId>().Length);
        Assert.Equal(114, (int)ClientPacketId.BorrarPersonaje);   // el primero que es nuestro
        Assert.Equal(115, (int)ClientPacketId.ElegirFamiliar);
        Assert.Equal(116, (int)ClientPacketId.SubastasPedir);
        Assert.Equal(120, (int)ClientPacketId.SubastaCancelar);
        Assert.Equal(0, (int)ClientPacketId.Walk);
        Assert.Equal(17, (int)ClientPacketId.DropDestroy);
        Assert.Equal(20, (int)ClientPacketId.DoubleClick);
        Assert.Equal(26, (int)ClientPacketId.CraftSastre);
        Assert.Equal(30, (int)ClientPacketId.EquiparSkin);
        Assert.Equal(39, (int)ClientPacketId.MoveBank);
        Assert.Equal(60, (int)ClientPacketId.GuildRequestMembership);
        Assert.Equal(92, (int)ClientPacketId.GMCommands);
        Assert.Equal(102, (int)ClientPacketId.TransferGOLD);
        Assert.Equal(113, (int)ClientPacketId.DesconectarCuenta);
    }
}
