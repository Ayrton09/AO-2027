using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;

namespace CoverAO.Protocol.Tests;

public sealed class EntregaDiferidaDeSubastaDelBotTests
{
    private const BindingFlags Privado = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Lazy<GameData> Datos = new(GameData.Load);
    private static readonly MethodInfo Retirar = typeof(Bot).GetMethod("RetirarLoDeLaSubastaAsync", Privado)!;
    private static readonly MethodInfo Anotar = typeof(Bot).GetMethod("AnotarLoQueDejoLaSubasta", Privado)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_compra_que_el_Centro_aun_no_pudo_entregar_no_se_olvida_por_no_estar_en_la_boveda(bool pergamino)
    {
        var d = Datos.Value;
        var s = new SentidosDelBot { MiNombre = "Bruma" };
        var t = new TransporteDeBot("Bruma");
        var banco = d.MapSpawns[20].First(n => d.Npc(n.NpcIndex)?.NpcType == 4);
        var yo = new Player
        {
            CharIndex = 500, Name = "Bruma", Account = "bruma_test", Pos = new WorldPos(20, (byte)banco.X, (byte)(banco.Y + 2)),
            Level = 23, Hp = 200, MaxHp = 200, Mana = 500, MaxMana = 500, Stamina = 300, MaxStamina = 300, Gold = 50_000,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500)); s.Recibir(ServerMessages.ChangeMap(20));
        s.Recibir(ServerMessages.CharacterCreate(yo)); s.Recibir(ServerMessages.UpdateUserStats(yo));
        yo.Skills[GameLimits.SkillMagia] = 100;
        s.Recibir(ServerMessages.SendSkills(yo.Skills));
        s.Recibir(ServerMessages.CharacterCreate(new Npc { CharIndex = 600, Template = d.Npc(banco.NpcIndex)!, Pos = new WorldPos(20, (byte)banco.X, (byte)banco.Y) }));
        var bot = new Bot("Bruma", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, d, new Navegante(d), null!), 1);
        var mejora = typeof(Bot).GetMethod("LoMejora", Privado)!;
        short objeto = d.Objects.Values.Where(o => o.IsPergamino == pergamino && o.Value is >= 1_000 and <= 20_000
            && !o.NoSeTira && !o.IsNewbie && (bool)mejora.Invoke(bot, new object[] { s, o.Index })!).First().Index;
        var pendientes = (HashSet<short>)typeof(Bot).GetField("_porRetirar", Privado)!.GetValue(bot)!;
        var comprados = (HashSet<short>)typeof(Bot).GetField("_comprados", Privado)!.GetValue(bot)!;

        // Estado real de una compra cobrada que el Centro mantiene pendiente porque la bóveda estaba llena.
        var centro = new CentroDeSubastas();
        var subasta = centro.Publicar("Anaqa", objeto, 1, 100, 200, 12, 1_000);
        centro.Comprar(subasta.Id, "Bruma");
        Assert.Equal(0, centro.Entregar("Bruma", _ => false));
        Assert.Contains(centro.Pendientes, e => e.Personaje == "Bruma" && e.Objeto == objeto);
        pendientes.Add(objeto); comprados.Add(objeto);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var visita = (Task)Retirar.Invoke(bot, new object[] { t, s, new TaskCompletionSource().Task, fin.Token })!;
        try
        {
            Assert.Equal((byte)ClientPacketId.DoubleClick, (await Leer(t, fin.Token))[0]);
            s.Recibir(ServerMessages.BankInit(true, 1_000));
            Assert.Equal((byte)ClientPacketId.BankStart, (await Leer(t, fin.Token))[0]);
            var relleno = d.Objects.Values.Where(o => o.Index != objeto && o.IsPickable).Take(GameLimits.MaxBankSlots).ToArray();
            Assert.Equal(GameLimits.MaxBankSlots, relleno.Length);
            for (byte slot = 1; slot <= GameLimits.MaxBankSlots; slot++)
                s.Recibir(ServerMessages.ChangeBankSlot(slot, relleno[slot - 1].Index, 1, 1));
            s.Recibir(ServerMessages.BankInit(false));
            Assert.Equal((byte)ClientPacketId.BankEnd, (await Leer(t, fin.Token))[0]);
            await visita.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Null(s.EnLaBoveda(objeto));
            Assert.Contains(objeto, pendientes); // La ausencia en la bóveda no cancela lo que todavía debe el Centro.

            Assert.Equal(1, centro.Entregar("Bruma", entrega =>
            {
                s.Recibir(ServerMessages.ConsoleMsg($"El Centro de Subastas dejó en tu bóveda: {entrega.Cantidad} {d.Obj(entrega.Objeto)!.Name}.", PacketDispatcher.FuenteInfo));
                return true;
            }));
            Anotar.Invoke(bot, new object[] { s });
            Assert.Contains(objeto, pendientes);
            Assert.DoesNotContain(objeto, comprados);
            Assert.DoesNotContain(objeto, (HashSet<short>)typeof(Bot).GetField("_noSeVendio", Privado)!.GetValue(bot)!);
        }
        finally
        {
            fin.Cancel(); t.Terminar();
            try { await visita; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<byte[]> Leer(TransporteDeBot t, CancellationToken ct)
    {
        var buffer = new byte[4096]; int n = await t.ReceiveAsync(buffer, ct);
        return buffer.Take(n).Select(b => (byte)(b ^ t.Clave)).ToArray();
    }
}
