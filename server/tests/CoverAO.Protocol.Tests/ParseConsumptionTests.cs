using System.Linq;
using System.Text;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Per-handler PARSE-CONSUMPTION guard — the regression net the audit (#78) found missing. It feeds the
/// dispatcher the exact bytes a real VB6 client sends for a packet, then asserts the handler consumed
/// EXACTLY those bytes: an under-read (e.g. reverting DropDestroy's ReadLong back to ReadInteger) leaves
/// stray bytes that desync every following packet; an over-read throws. Either regression fails here.
/// Parsing is synchronous (the Read* calls run before <c>_loop.Enqueue</c>), so we can check consumption
/// without a logged-in player or a running game loop.
/// </summary>
public class ParseConsumptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverao_parse_" + Guid.NewGuid().ToString("N"));
    private readonly PacketDispatcher _dispatcher;
    private readonly ClientSession _session;

    private sealed class FakeTransport : ITransport
    {
        public string RemoteEndpoint => "test";
        public ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct) => new(0);
        public void Send(byte[] data) { }
        public void Close(string reason) { }
    }

    public ParseConsumptionTests()
    {
        var world = new World();
        var leveling = new Leveling(new Dictionary<int, long>());
        var db = new Persistence(new FileStore(_root), leveling);
        var data = GameData.Load();                 // empty if data dir absent — parsing is data-independent
        var loop = new GameLoop();
        _dispatcher = new PacketDispatcher(world, loop, db, data, leveling);
        _session = new ClientSession(new FakeTransport(), _dispatcher);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Feeds [packet + 4 sentinel bytes]; the handler must consume exactly the packet.</summary>
    private void AssertConsumesExactly(byte[] packet)
    {
        var buf = new byte[packet.Length + 4];
        Array.Copy(packet, buf, packet.Length);
        var reader = new ByteQueueReader(buf);
        _dispatcher.HandleOne(_session, reader);
        Assert.Equal(packet.Length, reader.Consumed);
    }

    private static byte[] P(ClientPacketId id, params byte[][] parts) =>
        new[] { new[] { (byte)id } }.Concat(parts).SelectMany(x => x).ToArray();
    private static byte[] Byte(int v) => new[] { (byte)v };
    private static byte[] Bool(bool v) => new[] { v ? (byte)1 : (byte)0 };
    private static byte[] Int(short v) => new[] { (byte)(v & 0xff), (byte)((v >> 8) & 0xff) };
    private static byte[] Long(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    private static byte[] Str(string s) => Int((short)s.Length).Concat(Encoding.GetEncoding(1252).GetBytes(s)).ToArray();

    [Fact] public void DropDestroy_reads_byte_plus_long()          // the ReadInteger→ReadLong fix
        => AssertConsumesExactly(P(ClientPacketId.DropDestroy, Byte(5), Long(100)));

    [Fact] public void MoveBank_reads_boolean_plus_byte()          // the (slotA,slotB)→(upwards,slot) fix
        => AssertConsumesExactly(P(ClientPacketId.MoveBank, Bool(true), Byte(3)));

    [Fact] public void EquiparSkin_reads_two_bytes()               // the Integer→(Equipo,BackOrNext) fix
        => AssertConsumesExactly(P(ClientPacketId.EquiparSkin, Byte(1), Byte(1)));

    [Fact] public void TransferGold_reads_string_then_long()       // the order-swap fix (name, then Long)
        => AssertConsumesExactly(P(ClientPacketId.TransferGOLD, Str("Bob"), Long(500)));

    [Fact] public void AddFriend_reads_string_plus_byte()          // the missing trailing Byte fix
        => AssertConsumesExactly(P(ClientPacketId.AddAmigos, Str("Bob"), Byte(0)));

    [Fact] public void Talk_reads_string_plus_mode_byte()          // VB6 WriteTalk: ASCIIString(chat) + Byte(mode)
        => AssertConsumesExactly(P(ClientPacketId.Talk, Str("hola mundo"), Byte(1)));

    [Fact] public void CastSpell_reads_one_byte()
        => AssertConsumesExactly(P(ClientPacketId.CastSpell, Byte(1)));

    [Fact] public void DoubleClick_reads_two_bytes()
        => AssertConsumesExactly(P(ClientPacketId.DoubleClick, Byte(50), Byte(52)));

    // --- Movement / basic actions ---
    [Fact] public void Walk_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.Walk, Byte(3)));
    [Fact] public void Attack_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.attack));
    [Fact] public void ChangeHeading_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.ChangeHeading, Byte(2)));
    [Fact] public void UseItem_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.UseItem, Byte(1)));
    [Fact] public void Whisper_reads_two_strings() => AssertConsumesExactly(P(ClientPacketId.Whisper, Str("Bob"), Str("hola")));
    [Fact] public void WorkLeftClick_reads_three_bytes() => AssertConsumesExactly(P(ClientPacketId.WorkLeftClick, Byte(50), Byte(52), Byte(20)));

    // --- Commerce ---
    [Fact] public void CommerceBuy_reads_byte_plus_int() => AssertConsumesExactly(P(ClientPacketId.CommerceBuy, Byte(1), Int(5)));
    [Fact] public void CommerceSell_reads_byte_plus_int() => AssertConsumesExactly(P(ClientPacketId.CommerceSell, Byte(1), Int(5)));

    // --- The rest of the 17 previously-fixed parse bugs (mixed String/Byte/Int/Long layouts) ---
    [Fact] public void Casamiento_reads_string_plus_byte() => AssertConsumesExactly(P(ClientPacketId.Casamiento, Str("Bob"), Byte(1)));
    [Fact] public void Train_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.Train, Byte(2)));
    [Fact] public void GMRequest_reads_byte_plus_string() => AssertConsumesExactly(P(ClientPacketId.GMRequest, Byte(1), Str("ayuda")));
    [Fact] public void InitCrafting_reads_long_plus_int() => AssertConsumesExactly(P(ClientPacketId.InitCrafting, Long(100), Int(10)));
    [Fact] public void AbrirForms_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.AbrirForms, Byte(1)));
    [Fact] public void ParticulaUsuario_reads_string_plus_int() => AssertConsumesExactly(P(ClientPacketId.ParticulaUsuario, Str("Bob"), Int(5)));
    [Fact] public void CentinelReport_reads_one_int() => AssertConsumesExactly(P(ClientPacketId.CentinelReport, Int(1234)));
    [Fact] public void ClanCodexUpdate_reads_two_strings() => AssertConsumesExactly(P(ClientPacketId.ClanCodexUpdate, Str("desc"), Str("codex")));
    [Fact] public void GuildNewWebsite_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildNewWebsite, Str("http://x")));
    [Fact] public void GuildMemberInfo_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildMemberInfo, Str("Bob")));
    [Fact] public void GuildRequestDetails_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildRequestDetails, Str("ClanWeb")));
    [Fact] public void GuildOfferPeace_reads_two_strings() => AssertConsumesExactly(P(ClientPacketId.GuildOfferPeace, Str("ClanRival"), Str("propuesta")));
    [Fact] public void ProcesosLogin_reads_3_strings_plus_4_bytes()   // the 3×String + 4×Byte fix
        => AssertConsumesExactly(P(ClientPacketId.ProcesosLogin, Str("cuenta"), Str("code"), Str("pass"), Byte(1), Byte(4), Byte(5), Byte(0)));
    // DesconectarCuenta era uno de los 17 parseos arreglados (antes no leía nada → desincronizaba el stream).
    // Sin este test, sacar el ReadAsciiString deja el nombre colgando y rompe cualquier paquete que venga pegado.
    [Fact] public void DesconectarCuenta_reads_one_string()
        => AssertConsumesExactly(P(ClientPacketId.DesconectarCuenta, Str("Rugura")));

    // --- Banco ---
    [Fact] public void BankDeposit_reads_byte_plus_int() => AssertConsumesExactly(P(ClientPacketId.BankDeposit, Byte(1), Int(5)));
    [Fact] public void BankExtractItem_reads_byte_plus_int() => AssertConsumesExactly(P(ClientPacketId.BankExtractItem, Byte(1), Int(5)));
    [Fact] public void BankDepositGold_reads_one_long() => AssertConsumesExactly(P(ClientPacketId.BankDepositGold, Long(100)));
    [Fact] public void BankExtractGold_reads_one_long() => AssertConsumesExactly(P(ClientPacketId.BankExtractGold, Long(100)));

    // --- Cuentas / login (layouts largos: version + mac + serial) ---
    [Fact] public void ConnectAccount_reads_str_str_3bytes_str_long()
        => AssertConsumesExactly(P(ClientPacketId.ConnectAccount, Str("cuenta"), Str("pass"), Byte(1), Byte(4), Byte(5), Str("mac"), Long(123)));
    [Fact] public void CreateNewAccount_reads_3_strings_plus_3_bytes()
        => AssertConsumesExactly(P(ClientPacketId.CreateNewAccount, Str("cuenta"), Str("pass"), Str("code"), Byte(1), Byte(4), Byte(5)));

    // --- Skills / clan / correo / misc ---
    [Fact] public void ModifySkills_reads_27_bytes() => AssertConsumesExactly(P(ClientPacketId.ModifySkills, new byte[GameLimits.NumSkills]));
    [Fact] public void CreateNewGuild_reads_four_strings()
        => AssertConsumesExactly(P(ClientPacketId.CreateNewGuild, Str("desc"), Str("ClanWeb"), Str("site"), Str("codex")));
    [Fact] public void GuildDeclareWar_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildDeclareWar, Str("ClanRival")));
    [Fact] public void GuildVote_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildVote, Str("Merlin")));
    [Fact] public void GuildLeave_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.GuildLeave));
    [Fact] public void EnviarCorreo_reads_two_strings_plus_two_ints()
        => AssertConsumesExactly(P(ClientPacketId.EnviarCorreo, Str("Bob"), Str("hola"), Int(38), Int(3)));
    [Fact] public void PacketsCorreo_reads_two_bytes() => AssertConsumesExactly(P(ClientPacketId.Packets_Correo, Byte(0), Byte(1)));
    [Fact] public void SeleccionarHogar_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.SeleccionarHogar, Byte(3)));
    [Fact] public void Denounce_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.Denounce, Str("denuncia")));
    [Fact] public void Gamble_reads_one_int() => AssertConsumesExactly(P(ClientPacketId.Gamble, Int(100)));

    // --- Hechizos / objetos: mover ranuras ---
    [Fact] public void MoveSpell_reads_two_bytes() => AssertConsumesExactly(P(ClientPacketId.MoveSpell, Byte(1), Byte(3)));
    [Fact] public void SwapObjects_reads_two_bytes() => AssertConsumesExactly(P(ClientPacketId.SwapObjects, Byte(1), Byte(2)));

    // --- Premios / facción / hogar (sin args) ---
    [Fact] public void RPremios_reads_one_int() => AssertConsumesExactly(P(ClientPacketId.RPremios, Int(5)));
    [Fact] public void PidePremios_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.PidePremios));
    [Fact] public void RetirarFaccion_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.RetirarFaccion));
    [Fact] public void RegresarHogar_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.RegresarHogar));

    // --- Operaciones de clan (alianzas / paz / gestión / cola de aspirantes) ---
    [Fact] public void GuildOpenElections_reads_nothing() => AssertConsumesExactly(P(ClientPacketId.GuildOpenElections));
    [Fact] public void GuildOfferAlliance_reads_two_strings() => AssertConsumesExactly(P(ClientPacketId.GuildOfferAlliance, Str("ClanAmigo"), Str("propuesta")));
    [Fact] public void GuildAcceptAlliance_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildAcceptAlliance, Str("ClanAmigo")));
    [Fact] public void GuildRejectAlliance_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildRejectAlliance, Str("ClanAmigo")));
    [Fact] public void GuildAcceptPeace_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildAcceptPeace, Str("ClanRival")));
    [Fact] public void GuildRejectPeace_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildRejectPeace, Str("ClanRival")));
    [Fact] public void GuildUpdateNews_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildUpdateNews, Str("novedades del clan")));
    [Fact] public void GuildKickMember_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildKickMember, Str("Merlin")));
    [Fact] public void GuildAcceptNewMember_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildAcceptNewMember, Str("Merlin")));
    [Fact] public void GuildRejectNewMember_reads_two_strings() => AssertConsumesExactly(P(ClientPacketId.GuildRejectNewMember, Str("Merlin"), Str("motivo")));
    [Fact] public void GuildRequestJoinerInfo_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildRequestJoinerInfo, Str("Merlin")));

    // --- Login / inventario / fabricación / clics (los con-args restantes: donde un bug de parseo se esconde) ---
    [Fact] public void LoginExistingChar_reads_str_str_3bytes_str_str_long()
        => AssertConsumesExactly(P(ClientPacketId.LoginExistingChar, Str("cuenta"), Str("pass"), Byte(1), Byte(4), Byte(5), Str("Rugura"), Str("mac"), Long(123)));
    [Fact] public void Drop_reads_byte_plus_long() => AssertConsumesExactly(P(ClientPacketId.Drop, Byte(1), Long(1)));
    [Fact] public void EquipItem_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.EquipItem, Byte(2)));
    [Fact] public void Work_reads_one_byte() => AssertConsumesExactly(P(ClientPacketId.Work, Byte(20)));
    [Fact] public void LeftClick_reads_two_bytes() => AssertConsumesExactly(P(ClientPacketId.LeftClick, Byte(50), Byte(52)));
    [Fact] public void CraftBlacksmith_reads_two_ints() => AssertConsumesExactly(P(ClientPacketId.CraftBlacksmith, Int(1261), Int(1)));
    [Fact] public void CraftCarpenter_reads_two_ints() => AssertConsumesExactly(P(ClientPacketId.CraftCarpenter, Int(58), Int(1)));
    [Fact] public void CraftSastre_reads_two_ints() => AssertConsumesExactly(P(ClientPacketId.CraftSastre, Int(200), Int(1)));
    [Fact] public void CraftAlquimia_reads_two_ints() => AssertConsumesExactly(P(ClientPacketId.Craftalquimia, Int(300), Int(1)));
    [Fact] public void GuildMessage_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.GuildMessage, Str("hola clan")));
    [Fact] public void ChangeDescription_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.ChangeDescription, Str("mi descripcion")));
    [Fact] public void MsgAmigos_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.MsgAmigos, Str("hola amigos")));
    [Fact] public void DelAmigos_reads_one_string() => AssertConsumesExactly(P(ClientPacketId.DelAmigos, Str("Bob")));
    [Fact] public void GMCommands_reads_cmd_byte()   // sub-comando sin args (5 = ServerTime)
        => AssertConsumesExactly(P(ClientPacketId.GMCommands, Byte(5)));
}
