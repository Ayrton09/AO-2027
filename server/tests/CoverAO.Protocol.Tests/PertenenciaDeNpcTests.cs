using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// LA PERTENENCIA DE UNA CRIATURA — de quién es un bicho salvaje por haberle pegado primero (VB6
/// <c>ApropioNpc</c> / <c>PerdioNpc</c> / <c>IntervaloPerdioNpc</c>).
///
/// <para>Lo que se fija acá es la bolsa de reglas que el original tiene repartida en tres archivos, y sobre
/// todo <b>las tres salidas por las que NO se apropia</b>: un GM, una zona segura, y el
/// <c>RoboNpcsPermitido</c> —que no se porta porque está muerto en la fuente—.</para>
///
/// <para>No confundir con la MASCOTA: eso es <c>MaestroUser</c> / <see cref="Npc.OwnerCharIndex"/> y es otra
/// cosa. Ver el comentario de <see cref="PertenenciaDeNpc"/>.</para>
/// </summary>
public class PertenenciaDeNpcTests
{
    private static NpcInfo Ficha(string nombre = "Lobo") =>
        new(Index: 1, Name: nombre, Body: 1, Head: 1, Heading: 3, MaxHp: 100, MinHit: 1, MaxHit: 1,
            Def: 0, GiveExp: 0, GiveGld: 0, PoderEvasion: 0, PoderAtaque: 1);

    private static Player Jugador(short idx = 1) =>
        new() { CharIndex = idx, Name = "Probador" + idx, Account = "cuenta_test" };

    private static Npc Bicho(short idx = 100) => new() { CharIndex = idx, Template = Ficha(), Hp = 100 };

    /// <summary>Apropiar sin nada raro: no hay zona segura y no hay criatura anterior que soltar.</summary>
    private static void Apropiar(Player p, Npc npc, long ahora) =>
        PertenenciaDeNpc.Apropiar(p, npc, ahora, enZonaSegura: false, _ => { });

    [Fact]
    public void Pegarle_a_una_criatura_libre_te_la_apropia()
    {
        var p = Jugador(); var npc = Bicho();
        Apropiar(p, npc, 1_000);
        Assert.Equal(p.CharIndex, npc.ApropiadoPor);
        Assert.Equal(npc.CharIndex, p.NpcApropiado);
        Assert.Equal(1_000, p.TimerNpcApropiadoMs);
    }

    [Fact]
    public void Un_GM_no_se_apropia_de_nada()
    {
        // `If EsGm(UserIndex) Then Exit Sub` (Modulo_UsUaRiOs.bas:2089). Es lo primero de la rutina.
        var p = Jugador(); p.Privileges = 8;   // Dios
        var npc = Bicho();
        Apropiar(p, npc, 1_000);
        Assert.Equal(0, npc.ApropiadoPor);
        Assert.Equal(0, p.NpcApropiado);
    }

    [Fact]
    public void En_zona_segura_tampoco()
    {
        // `If MapData(...).Trigger = eTrigger.ZONASEGURA Then Exit Sub` (:2092).
        var p = Jugador(); var npc = Bicho();
        PertenenciaDeNpc.Apropiar(p, npc, 1_000, enZonaSegura: true, _ => { });
        Assert.Equal(0, npc.ApropiadoPor);
        Assert.Equal(0, p.NpcApropiado);
    }

    [Fact]
    public void Apropiarse_de_otra_suelta_la_anterior()
    {
        // El jugador tiene UNA sola: `If .flags.OwnedNpc > 0 Then Npclist(.flags.OwnedNpc).Owner = 0` (:2098).
        var p = Jugador();
        var primera = Bicho(100);
        var segunda = Bicho(200);
        Apropiar(p, primera, 1_000);
        PertenenciaDeNpc.Apropiar(p, segunda, 2_000, enZonaSegura: false,
            idx => { if (idx == primera.CharIndex) primera.ApropiadoPor = 0; });
        Assert.Equal(0, primera.ApropiadoPor);
        Assert.Equal(p.CharIndex, segunda.ApropiadoPor);
        Assert.Equal(segunda.CharIndex, p.NpcApropiado);
    }

    [Fact]
    public void El_reloj_no_vence_antes_del_intervalo()
    {
        var p = Jugador(); var npc = Bicho();
        Apropiar(p, npc, 1_000);
        Assert.False(PertenenciaDeNpc.SeAgoto(p, 1_000 + PertenenciaDeNpc.IntervaloPorDefectoMs - 1,
                                              PertenenciaDeNpc.IntervaloPorDefectoMs));
    }

    [Fact]
    public void Y_vence_justo_al_cumplirse()
    {
        // El original compara con `>=`, no con `>`.
        var p = Jugador(); var npc = Bicho();
        Apropiar(p, npc, 1_000);
        Assert.True(PertenenciaDeNpc.SeAgoto(p, 1_000 + PertenenciaDeNpc.IntervaloPorDefectoMs,
                                             PertenenciaDeNpc.IntervaloPorDefectoMs));
    }

    [Fact]
    public void Volver_a_golpearla_corre_el_reloj()
    {
        // Es lo que hace que la criatura siga siendo tuya mientras la peleás: cada golpe del dueño renueva.
        var p = Jugador(); var npc = Bicho();
        Apropiar(p, npc, 1_000);
        PertenenciaDeNpc.Renovar(p, 10_000);
        Assert.False(PertenenciaDeNpc.SeAgoto(p, 20_000, PertenenciaDeNpc.IntervaloPorDefectoMs));
        Assert.True(PertenenciaDeNpc.SeAgoto(p, 28_000, PertenenciaDeNpc.IntervaloPorDefectoMs));
    }

    [Fact]
    public void Perderla_limpia_los_dos_lados_y_llama_a_las_mascotas()
    {
        var p = Jugador(); var npc = Bicho();
        Apropiar(p, npc, 1_000);
        bool volvieron = false;
        PertenenciaDeNpc.Perder(p, idx => { if (idx == npc.CharIndex) npc.ApropiadoPor = 0; }, () => volvieron = true);
        Assert.Equal(0, npc.ApropiadoPor);
        Assert.Equal(0, p.NpcApropiado);
        Assert.True(volvieron);   // `FollowAmo` de PerdioNpc: dejan de pelearla y vuelven con el dueño
    }

    [Fact]
    public void Perder_sin_tener_nada_no_hace_ni_llama_a_nadie()
    {
        var p = Jugador();
        bool volvieron = false;
        PertenenciaDeNpc.Perder(p, _ => Assert.Fail("no había nada que soltar"), () => volvieron = true);
        Assert.False(volvieron);
    }

    [Fact]
    public void El_intervalo_sale_de_la_seccion_TIMERS_del_ini()
    {
        // La clave vive en [TIMERS], no en [INTERVALOS] — es la unica de esa seccion que leemos. Si la lectura
        // fallara y devolviera 0, `SeAgoto` daria SIEMPRE true y la marca se soltaria en el tick siguiente al
        // golpe: la pertenencia entera quedaria muerta sin que nada mas se rompiera.
        var ruta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coverao_timers_" + System.Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            System.IO.File.WriteAllText(ruta, "[INIT]\r\nStartPort=7666\r\n[TIMERS]\r\nIntervaloOwnedNpc=8000\r\n");
            Assert.Equal(8_000, CoverAO.Server.Data.ServerIni.Cargar(ruta).IntervaloNpcApropiado);
        }
        finally { try { System.IO.File.Delete(ruta); } catch { } }
    }

    [Fact]
    public void Sin_la_clave_vale_el_del_install()
    {
        var ruta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coverao_timers_" + System.Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            System.IO.File.WriteAllText(ruta, "[INIT]\r\nStartPort=7666\r\n");
            Assert.Equal(18_000, CoverAO.Server.Data.ServerIni.Cargar(ruta).IntervaloNpcApropiado);
        }
        finally { try { System.IO.File.Delete(ruta); } catch { } }
    }

    [Fact]
    public void El_intervalo_por_defecto_es_el_del_install()
    {
        // `[TIMERS] IntervaloOwnedNpc=18000` del Server.ini del install. No es un número inventado.
        Assert.Equal(18_000, PertenenciaDeNpc.IntervaloPorDefectoMs);
    }
}
