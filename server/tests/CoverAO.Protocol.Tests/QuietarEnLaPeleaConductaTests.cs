using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LO QUE MANDA EL BOT al quietar a la persona con la que pelea (<c>QuietarA</c>) y al sacarse la parálisis
/// (<c>RemoverseLaParalisis</c>), con los hechizos en el libro y el maná de verdad. Los de <see cref="QuietarEnLaPeleaTests"/> prueban
/// las cuentas; éstos, que la conducta sale por el cable.
/// </summary>
public sealed class QuietarEnLaPeleaConductaTests
{
    private static readonly GameData Datos = GameData.Load();
    private static readonly SpellInfo Inmovilizar = Datos.Spell(24)!, Remover = Datos.Spell(10)!;
    private static readonly SpellInfo Cura = Datos.Spells.Values.Where(h => h is { IsHeal: true, MaxHp: > 0, ManaCost: >= 10 }).MinBy(h => h.ManaCost)!;

    private static readonly MethodInfo QuietarM = typeof(Bot).GetMethod("QuietarA", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RemoverM = typeof(Bot).GetMethod("RemoverseLaParalisis", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task Quieta_a_la_persona_con_Inmovilizar_sobre_su_baldosa()
    {
        var (bot, s, t) = Armar(mana: 1_000);
        Assert.True((bool)QuietarM.Invoke(bot, new object[] { t, s, s.PersonaLlamada("Kael")! })!);
        var lanza = await Proximo(t);
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 1 }, lanza);
        var clic = (await Proximo(t))!;
        Assert.Equal((byte)ClientPacketId.WorkLeftClick, clic[0]);
        Assert.Equal(KaelX, clic[1]);
    }

    /// <summary>Sin el maná de su cura más barata después de quietar, no quieta.</summary>
    [Fact]
    public async Task No_quieta_si_se_queda_sin_el_mana_de_la_cura()
    {
        var (bot, s, t) = Armar(mana: Inmovilizar.ManaCost + Cura.ManaCost - 1);
        Assert.False((bool)QuietarM.Invoke(bot, new object[] { t, s, s.PersonaLlamada("Kael")! })!);
        Assert.Null(await Proximo(t, 500, tolerante: true));
    }

    [Fact]
    public async Task Paralizado_se_saca_la_paralisis_sobre_si_mismo_y_espera_para_la_otra()
    {
        var (bot, s, t) = Armar(mana: 1_000);
        Assert.False((bool)RemoverM.Invoke(bot, new object[] { t, s })!);   // sin parálisis, nada
        s.Recibir(ServerMessages.ParalizeOK());
        Assert.True((bool)RemoverM.Invoke(bot, new object[] { t, s })!);
        Assert.Equal(new byte[] { (byte)ClientPacketId.CastSpell, 2 }, await Proximo(t));
        var clic = (await Proximo(t))!;
        Assert.Equal(YoX, clic[1]);
        Assert.False((bool)RemoverM.Invoke(bot, new object[] { t, s })!);   // la segunda, recién después de EntreRemocionesMs
    }

    private const short Yo = 500, Kael = 501;
    private const byte YoX = 50, KaelX = 50;
    private const int Mapa = 18;

    /// <summary>Un caster de nivel 22 con Inmovilizar (ranura 1), Remover parálisis (2) y su cura (3), y Kael a tres pasos.</summary>
    private static (Bot, SentidosDelBot, TransporteDeBot) Armar(int mana)
    {
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = Yo, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(Mapa, YoX, 50), Level = 22,
            MaxHp = 200, Hp = 200, MaxMana = 2_000, Mana = mana, MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(Yo));
        s.Recibir(ServerMessages.ChangeMap(Mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeSpellSlot(1, Inmovilizar.Index));
        s.Recibir(ServerMessages.ChangeSpellSlot(2, Remover.Index));
        s.Recibir(ServerMessages.ChangeSpellSlot(3, Cura.Index));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = Kael, Name = "Kael", Account = "kael_test", Pos = new WorldPos(Mapa, KaelX, 47),
        }));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, Datos, new Navegante(Datos), null!), 1);
        return (bot, s, new TransporteDeBot("Rufo"));
    }

    private static async Task<byte[]?> Proximo(TransporteDeBot t, int ms = 2_000, bool tolerante = false)
    {
        using var cts = new CancellationTokenSource(ms);
        var buffer = new byte[4096];
        try
        {
            int n = await t.ReceiveAsync(buffer, cts.Token);
            var paquete = new byte[n];
            for (int i = 0; i < n; i++) paquete[i] = (byte)(buffer[i] ^ t.Clave);
            return paquete;
        }
        catch (OperationCanceledException) when (tolerante) { return null; }
    }
}
