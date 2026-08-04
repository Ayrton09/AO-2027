using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// Los cuatro relojes de combate (VB6 <c>modNuevoTimer.bas</c>). El port no tenía NINGUNO de casteo, así que
/// un mago tiraba hechizos a velocidad de red.
///
/// Lo que estos tests fijan no es la aritmética —es trivial— sino <b>la semántica de los dos atajos</b>, que
/// leyendo las funciones sueltas se entiende al revés: no bloquean, habilitan. El call site del original los
/// usa con un OR contra la cadencia normal, y el guard temprano hace que el atajo valga una sola vez por
/// casteo (o por golpe). Un port que los tratara como gates bloqueantes rompería todo el melee.
/// </summary>
public class CombatTimingTests
{
    private const long Castear = 1000;   // IntervaloLanzaHechizo del Server.ini del install
    private const long Cruzado = 400;    // IntervaloMagiaGolpe / IntervaloGolpeMagia / IntervaloGolpeUsar
    private const long Golpe = 1000;     // IntervaloUserPuedeAtacar

    private static Player Nuevo() => new() { CharIndex = 1, Name = "Rugura", Account = "cuenta" };

    [Fact]
    public void El_casteo_respeta_su_cadencia()
    {
        var p = Nuevo();
        Assert.True(CombatTiming.PermiteLanzarSpell(p, 10_000, Castear));   // primer casteo: pasa
        Assert.False(CombatTiming.PermiteLanzarSpell(p, 10_500, Castear));  // 500 ms después: no
        Assert.True(CombatTiming.PermiteLanzarSpell(p, 11_000, Castear));   // justo al vencer: sí
    }

    [Fact]
    public void Un_intervalo_en_cero_no_gatea_nada()
    {
        // 0 = desactivado, igual que el resto de los intervalos del ini. Es la config con la que corren hoy
        // los flows, así que esta rama tiene que seguir dejando pasar todo.
        var p = Nuevo();
        Assert.True(CombatTiming.PermiteLanzarSpell(p, 0, 0));
        Assert.True(CombatTiming.PermiteLanzarSpell(p, 0, 0));
        Assert.True(CombatTiming.PermiteAtacar(p, 0, 0));
        Assert.True(CombatTiming.PermiteAtacar(p, 0, 0));
    }

    [Fact]
    public void El_atajo_deja_pegar_antes_de_que_venza_la_cadencia_de_golpe()
    {
        var p = Nuevo();
        CombatTiming.PermiteLanzarSpell(p, 10_000, Castear);                 // castea en t=10000
        // A los 400 ms el atajo ya habilita el golpe, aunque la cadencia normal pediría 1000.
        Assert.False(CombatTiming.PermiteMagiaGolpe(p, 10_300, Cruzado));     // 300: todavía no
        Assert.True(CombatTiming.PermiteMagiaGolpe(p, 10_400, Cruzado));      // 400: sí
    }

    [Fact]
    public void El_atajo_vale_UNA_vez_por_casteo()
    {
        var p = Nuevo();
        CombatTiming.PermiteLanzarSpell(p, 10_000, Castear);
        Assert.True(CombatTiming.PermiteMagiaGolpe(p, 10_400, Cruzado));      // se usa
        Assert.False(CombatTiming.PermiteMagiaGolpe(p, 20_000, Cruzado));     // y no vuelve, por más que pase tiempo
        CombatTiming.PermiteLanzarSpell(p, 20_000, Castear);                  // hasta castear de nuevo
        Assert.True(CombatTiming.PermiteMagiaGolpe(p, 20_400, Cruzado));
    }

    [Fact]
    public void Cuando_el_atajo_se_agota_la_cadencia_normal_sigue_dejando_pegar()
    {
        // ESTA es la propiedad que importa: el call site del original es un OR. Si el atajo dice que no, el
        // jugador NO queda trabado — cae al intervalo de ataque de siempre. Tratarlos como AND rompería el
        // melee entero del juego.
        var p = Nuevo();
        CombatTiming.PermiteLanzarSpell(p, 10_000, Castear);
        Assert.True(CombatTiming.PermiteMagiaGolpe(p, 10_400, Cruzado));      // el atajo mueve LastAttackMs
        Assert.False(CombatTiming.PermiteMagiaGolpe(p, 11_500, Cruzado));     // agotado
        Assert.True(CombatTiming.PermiteAtacar(p, 11_500, Golpe));            // pero la cadencia normal habilita
    }

    [Fact]
    public void El_atajo_de_castear_despues_de_pegar_es_simetrico()
    {
        var p = Nuevo();
        CombatTiming.PermiteAtacar(p, 10_000, Golpe);                         // pega en t=10000
        Assert.False(CombatTiming.PermiteGolpeMagia(p, 10_300, Cruzado));
        Assert.True(CombatTiming.PermiteGolpeMagia(p, 10_400, Cruzado));      // castea antes de los 1000
        Assert.False(CombatTiming.PermiteGolpeMagia(p, 12_000, Cruzado));     // una sola vez por golpe
    }

    [Fact]
    public void Pegar_arranca_la_espera_para_usar_un_objeto()
    {
        // El VB6 resetea TimerGolpeUsar dentro de PermiteAtacar: no se puede tomar una poción en el mismo
        // instante del golpe.
        var p = Nuevo();
        CombatTiming.PermiteAtacar(p, 10_000, Golpe);
        Assert.False(CombatTiming.PermiteGolpeUsar(p, 10_200, Cruzado));
        Assert.True(CombatTiming.PermiteGolpeUsar(p, 10_400, Cruzado));
    }

    [Fact]
    public void Consultar_si_se_puede_usar_no_mueve_el_reloj()
    {
        // El original consulta este gate con Actualizar:=False (InvUsuario.bas:1559): lo mueven el golpe y el
        // atajo, no el trago. Si consultar lo moviera, dos pociones seguidas se bloquearían entre sí.
        var p = Nuevo();
        CombatTiming.PermiteAtacar(p, 10_000, Golpe);
        Assert.True(CombatTiming.PermiteGolpeUsar(p, 10_400, Cruzado));
        Assert.True(CombatTiming.PermiteGolpeUsar(p, 10_401, Cruzado));   // sigue dejando: no se auto-bloqueó
    }

    [Fact]
    public void El_atajo_tambien_arranca_la_espera_de_usar()
    {
        // PermiteMagiaGolpe adelanta TRES relojes, no uno: la marca, el de ataque y el de usar. Así el golpe
        // que viene por el atajo cuenta como un golpe normal a todos los efectos.
        var p = Nuevo();
        CombatTiming.PermiteLanzarSpell(p, 10_000, Castear);
        Assert.True(CombatTiming.PermiteMagiaGolpe(p, 10_400, Cruzado));
        Assert.False(CombatTiming.PermiteGolpeUsar(p, 10_500, Cruzado));   // el atajo movió GolpeUsarMs
        Assert.True(CombatTiming.PermiteGolpeUsar(p, 10_800, Cruzado));
    }
}
