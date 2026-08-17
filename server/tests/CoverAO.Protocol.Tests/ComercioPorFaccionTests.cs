using CoverAO.Server.Data;
using CoverAO.Server.Game;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL MERCADER QUE NO TE ATIENDE (VB6 Protocol.bas:5203-5220 y Acciones.bas:180-197): el gate que decide si
/// se puede comerciar, y que hasta el 2026-08-19 no estaba portado — acá se le vendía a cualquiera en
/// cualquier mapa.
///
/// <para>Lo que fija este test es una conclusión <b>incómoda y medida</b>: como todo personaje nace con status
/// de facción (ciudadano, o republicano si nace en Illiandor), la regla del original deja el comercio
/// funcionando <b>sólo en los mapas battle_mode 2 y 3</b>. En Ullathorpe —battle_mode 1— el mercader te echa.
/// Se verificó contra el oráculo VB6 VIVO: un ciudadano recién creado que camina del Dungeon de Newbies
/// (battle_mode 3, donde compra sin problema) a Ullathorpe recibe del Carpintero un
/// <c>ChatOverHeadLocale(592, 4)</c> en vez de la tienda.</para>
///
/// <para>Por eso el test tiene DOS mitades. La regla, que es código nuestro, y el <b>dato</b> del que depende
/// que la regla signifique lo que dice: si mañana alguien re-transcodifica los mapas y el battle_mode se
/// pierde, la regla seguiría siendo correcta y el juego quedaría sin comercio en ningún lado. Eso lo caza la
/// segunda mitad.</para>
/// </summary>
public class ComercioPorFaccionTests
{
    private static Player Nuevo() => new() { CharIndex = 1, Name = "Prueba", Account = "cuenta" };
    private static Player Con(byte status) { var p = Nuevo(); p.FactionStatus = status; return p; }

    [Fact]
    public void El_comercio_solo_existe_en_los_mapas_battle_mode_2_y_3()
    {
        // Los dos primeros `If` del original: imperiales y republicanos, mismo umbral.
        foreach (byte status in new[] { Player.StatusCiudadano, Player.StatusArmada,
                                        Player.StatusRepublicano, Player.StatusMilicia })
        {
            Assert.True(Con(status).MercaderMeAtiende(2), $"status {status} en battle_mode 2");
            Assert.True(Con(status).MercaderMeAtiende(3), $"status {status} en battle_mode 3");
            foreach (int modo in new[] { 0, 1, 4 })
                Assert.False(Con(status).MercaderMeAtiende(modo), $"status {status} en battle_mode {modo}");
        }
    }

    [Fact]
    public void Un_renegado_o_un_caos_solo_comercia_en_battle_mode_3()
    {
        // El TERCER `If` no es igual a los otros dos: al criminal le falta el 2 (Lindos), o sea que un renegado
        // no compra donde sí compra un ciudadano. Copiar el umbral de arriba sería el error fácil.
        foreach (byte status in new[] { Player.StatusCriminal, Player.StatusCaos })
        {
            Assert.True(Con(status).MercaderMeAtiende(3), $"status {status} en battle_mode 3");
            foreach (int modo in new[] { 0, 1, 2, 4 })
                Assert.False(Con(status).MercaderMeAtiende(modo), $"status {status} en battle_mode {modo}");
        }
    }

    [Fact]
    public void Un_personaje_recien_creado_no_puede_comprar_en_Ullathorpe()
    {
        // El caso concreto que se midió contra el oráculo, escrito como test: el default de un char nuevo
        // (Persistence lo crea ciudadano, igual que TCP.bas:497) más el battle_mode real de Ullathorpe.
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");

        var nuevo = Nuevo();                                       // sin tocar la facción: como nace
        Assert.Equal(Player.StatusCiudadano, nuevo.FactionStatus);

        Assert.Equal(1, data.MapBattleMode(1));                    // Ullathorpe
        Assert.False(nuevo.MercaderMeAtiende(data.MapBattleMode(1)));

        Assert.Equal(3, data.MapBattleMode(37));                   // Dungeon de Newbies
        Assert.True(nuevo.MercaderMeAtiende(data.MapBattleMode(37)));
    }

    [Fact]
    public void El_battle_mode_llega_desde_MapInfo_y_no_es_el_flag_Pk()
    {
        // El battle_mode ya estaba en MapInfo.json y NADIE lo leía: se usaba sólo su derivado `pk`, que es
        // `battle_mode == 0`. Desde Pk los valores 2 y 3 son indistinguibles del 1 y del 4 (todos "no Pk"), así
        // que este gate no se podía escribir sin cargar el número crudo. Se fija con un mapa de cada clase.
        var data = GameData.Load();
        Assert.False(data.SourceDir is null, "el test necesita gamedata en disco");

        Assert.Equal(0, data.MapBattleMode(2));      // Sendero del Sur — el campo
        Assert.Equal(2, data.MapBattleMode(62));     // Lindos
        Assert.Equal(3, data.MapBattleMode(20));     // Rinkel

        // Y la trampa: los tres de arriba que NO son 0 dan todos `pk = false`, o sea que Pk no los distingue.
        Assert.True(data.IsPkMap(2));
        Assert.False(data.IsPkMap(62));
        Assert.False(data.IsPkMap(20));
    }
}
