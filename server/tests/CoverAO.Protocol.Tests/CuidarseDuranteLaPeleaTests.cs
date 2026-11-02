using System.Reflection;
using CoverAO.Protocol;
using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>Tomar una poción permite seguir decidiendo durante una pelea, usando el estado posterior a la espera.</summary>
public sealed class CuidarseDuranteLaPeleaTests
{
    private static readonly Lazy<(GameData Datos, Navegante Mapas)> Mundo = new(() =>
    {
        var datos = GameData.Load();
        return (datos, new Navegante(datos));
    });
    private static readonly MethodInfo Cazar = typeof(Bot).GetMethod("CazarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task Con_la_vida_a_medias_toma_la_roja_y_devuelve_el_golpe_sin_repetir_solo_pociones()
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal(new byte[] { (byte)ClientPacketId.UseItem, 1 }, await Proximo(f));
            // La poción y el daño recibido pueden dejarlo al mismo 50 %: todavía debe decidir cómo pelear.
            f.Sentidos.Recibir(ServerMessages.UpdateHP(100));
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Si_la_roja_no_lo_saca_del_estado_critico_se_retira()
    {
        var f = Armar(hp: 40);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.UpdateHP(40));
            Assert.Equal((byte)ClientPacketId.Walk, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Si_el_rival_se_mueve_durante_la_cura_apunta_a_su_posicion_actual()
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.CharacterMove(501, (byte)(f.X + 1), f.Y));
            Assert.Equal(new byte[] { (byte)ClientPacketId.ChangeHeading, SentidosDelBot.RumboHacia(1, 0) }, await Proximo(f));
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Si_cambia_de_mapa_durante_la_cura_no_retoma_la_persecucion_de_la_pelea_anterior()
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.ChangeMap(37));
            f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
                Pos = new WorldPos(37, (byte)(f.X + 1), f.Y),
            }));
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Una_cancelacion_durante_la_pocion_no_deja_un_ataque_pendiente()
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
        await Cancelar(pelea, corte);
        f.Transporte.Terminar();
        Assert.Empty(await Proximo(f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deja_de_atacar_si_el_rival_pasa_a_ser_un_aliado_durante_una_espera(bool duranteCura)
    {
        var f = Armar(hp: duranteCura ? 100 : 200);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            if (duranteCura) Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            else
            {
                Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
                Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
            }
            f.Sentidos.Recibir(ServerMessages.CharStatus(501, Player.StatusCiudadano));
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_indice_reutilizado_durante_la_cura_no_convierte_a_otro_personaje_en_la_presa(bool ahoraEsNpc)
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.CharacterRemove(501, false));
            if (ahoraEsNpc)
            {
                var ficha = Mundo.Value.Datos.Npcs.Values.First(n => !n.Hostile && n.MaxHit == 0);
                f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Npc
                {
                    CharIndex = 501, Template = ficha, Hp = ficha.MaxHp,
                    Pos = new WorldPos(18, f.X, (byte)(f.Y - 1)),
                }));
            }
            else f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = 501, Name = "Nilo", Account = "nilo_test", FactionStatus = Player.StatusCriminal,
                Pos = new WorldPos(18, f.X, (byte)(f.Y - 1)),
            }));
            Assert.True(await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Fact]
    public async Task Un_cambio_en_el_clan_visible_no_convierte_al_rival_en_otro_personaje()
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            f.Sentidos.Recibir(ServerMessages.CharacterCreate(new Player
            {
                CharIndex = 501, Name = "Kael <Norte>", Account = "kael_test", FactionStatus = Player.StatusCriminal,
                Pos = new WorldPos(18, f.X, (byte)(f.Y - 1)),
            }));
            Assert.Equal((byte)ClientPacketId.ChangeHeading, (await Proximo(f))[0]);
            Assert.Equal((byte)ClientPacketId.attack, (await Proximo(f))[0]);
        }
        finally { await Cancelar(pelea, corte); }
    }

    [Theory]
    [InlineData("muerte", true)]
    [InlineData("cierre", false)]
    [InlineData("rival desaparece", true)]
    public async Task Una_urgencia_o_un_blanco_que_ya_no_esta_impiden_atacar_despues_de_la_pocion(string cambio, bool sigue)
    {
        var f = Armar(hp: 100);
        using var corte = new CancellationTokenSource();
        var pelea = Empezar(f, corte.Token);
        try
        {
            Assert.Equal((byte)ClientPacketId.UseItem, (await Proximo(f))[0]);
            switch (cambio)
            {
                case "muerte": f.Sentidos.Recibir(ServerMessages.UpdateHP(0)); break;
                case "cierre": f.Cerrada.SetResult(); break;
                case "rival desaparece": f.Sentidos.Recibir(ServerMessages.CharacterRemove(501, false)); break;
            }
            Assert.Equal(sigue, await pelea.WaitAsync(TimeSpan.FromSeconds(4)));
            f.Transporte.Terminar();
            Assert.Empty(await Proximo(f));
        }
        finally { await Cancelar(pelea, corte); }
    }

    private sealed record Fixture(Bot Bot, SentidosDelBot Sentidos, TransporteDeBot Transporte, TaskCompletionSource Cerrada, byte X, byte Y);

    private static Fixture Armar(int hp)
    {
        var (datos, mapas) = Mundo.Value;
        const short mapa = 18;
        var sitio = (from y in Enumerable.Range(20, 60)
                     from x in Enumerable.Range(20, 60)
                     where (from dy in Enumerable.Range(-1, 3)
                            from dx in Enumerable.Range(-1, 3)
                            select datos.IsBlockedTile(mapa, x + dx, y + dy)).All(b => !b)
                     select (X: (byte)x, Y: (byte)y)).First();
        var s = new SentidosDelBot { MiNombre = "Rufo" };
        var yo = new Player
        {
            CharIndex = 500, Name = "Rufo", Account = "rufo_test", Pos = new WorldPos(mapa, sitio.X, sitio.Y), Level = 24,
            FactionStatus = Player.StatusCiudadano, MaxHp = 200, Hp = hp, MaxStamina = 300, Stamina = 300,
        };
        s.Recibir(ServerMessages.UserIndexInServer(500));
        s.Recibir(ServerMessages.ChangeMap(mapa));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        s.Recibir(ServerMessages.ChangeInventorySlot(1, 38, 10, false, 0, 1));
        s.Recibir(ServerMessages.CharacterCreate(new Player
        {
            CharIndex = 501, Name = "Kael", Account = "kael_test", FactionStatus = Player.StatusCriminal,
            Pos = new WorldPos(mapa, sitio.X, (byte)(sitio.Y - 1)),
        }));
        var bot = new Bot("Rufo", null, new EntornoDeBots(null!, new ServerIni(), 0, 0, datos, mapas, null!), 1);
        return new Fixture(bot, s, new TransporteDeBot("Rufo"), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), sitio.X, sitio.Y);
    }

    private static Task<bool> Empezar(Fixture f, CancellationToken ct) =>
        (Task<bool>)Cazar.Invoke(f.Bot, new object[] { f.Transporte, f.Sentidos, (short)501, f.Cerrada.Task, ct, true })!;

    private static async Task<byte[]> Proximo(Fixture f)
    {
        using var corte = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[4_096];
        int n = await f.Transporte.ReceiveAsync(buffer, corte.Token);
        return buffer.Take(n).Select(b => (byte)(b ^ f.Transporte.Clave)).ToArray();
    }

    private static async Task Cancelar(Task tarea, CancellationTokenSource corte)
    {
        corte.Cancel();
        try { await tarea.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (corte.IsCancellationRequested) { }
    }
}
