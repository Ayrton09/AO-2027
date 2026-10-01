using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL HOGAR DEL BOT, DONDE LE VENDEN (29/09). En `tardeM1` doce de los dieciocho bots de nivel 16 a 28 no encontraban dónde comprar
/// pociones, con decenas de miles de monedas: cazaban del lado de Nix y de Ullathorpe, donde nadie le vende a un ciudadano, y la
/// runa —que es la vuelta a casa— sólo se usaba en un mapa seguro, y para cuatro de ellos la casa era Nix. El bot pone el hogar en
/// una ciudad donde le venden, no se gradúa adentro del dungeon (que lo manda a Nix) y, para ir a comprar, camina hasta un mapa
/// seguro y usa la runa.
/// </summary>
public class ElHogarDelBotTests
{
    [Fact]
    public void Rinkel_puede_ser_el_hogar_de_cualquiera_y_las_ciudades_de_un_bando_solo_de_los_suyos()
    {
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusCiudadano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(20, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusCiudadano), "Nix, del imperio");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(34, Player.StatusRepublicano));
        Assert.True(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusRepublicano), "Illiandor, de la república");
        Assert.False(PacketDispatcher.PuedeSerHogarDe(194, Player.StatusCiudadano));
        Assert.False(PacketDispatcher.PuedeSerHogarDe(37, Player.StatusCiudadano), "el dungeon de novatos no es una ciudad");
    }

    /// <summary>Una vez, y otra al dejar de ser novato: graduarse adentro del dungeon le cambia el hogar a Nix.</summary>
    [Theory]
    // nivel con el que lo puso (0: nunca) | nivel ahora | ¿lo vuelve a pedir?
    [InlineData(0, 5, true)]
    [InlineData(5, 10, false)]
    [InlineData(13, 15, false)]
    [InlineData(13, 16, true)]
    [InlineData(16, 20, false)]
    public void El_hogar_se_pide_una_vez_y_otra_al_dejar_de_ser_novato(int nivelDelHogar, int nivel, bool loPide)
    {
        Assert.Equal(loPide, Bot.VuelveAPonerElHogar(nivelDelHogar, nivel));
    }

    [Fact]
    public void En_el_ultimo_nivel_de_novato_no_se_caza_en_el_dungeon()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(37)), "la cuna, en el último nivel de novato");
        Assert.True(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(208)), "el otro mapa de novatos");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel - 1, d.MapZone(37)), "antes, sí");
        Assert.False(Bot.NoSeGraduaAhi(GameLimits.NewbieMaxLevel, d.MapZone(20)), "afuera del dungeon, sí");
    }

    /// <summary>
    /// NI ENTRE LOS GUARDIAS QUE VAN A IR POR ÉL AL DEJAR DE SER NOVATO: en el 814 nacen un Paladín, dos Magos y un Bardo
    /// republicanos; al ciudadano de nivel 15 todavía no lo atacan, en el 16 sí, y al republicano nunca. Es Elmo en `alto1`. Del
    /// lado imperial, al revés.
    /// </summary>
    [Fact]
    public void En_el_ultimo_nivel_de_novato_no_se_caza_entre_los_guardias_que_van_a_ir_por_el()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        const int ultimo = GameLimits.NewbieMaxLevel;
        byte ciudadano = Player.StatusCiudadano, republicano = Player.StatusRepublicano;
        Assert.False(Bot.HayGuardiaQueVaPor(814, ciudadano, ultimo, d, ini), "premisa: al novato no lo atacan");
        Assert.True(Bot.HayGuardiaQueVaPor(814, ciudadano, ultimo + 1, d, ini), "premisa: graduado, sí");
        Assert.True(Bot.NoSeGraduaEntreGuardias(814, ciudadano, ultimo, d, ini));
        Assert.False(Bot.NoSeGraduaEntreGuardias(814, ciudadano, ultimo - 1, d, ini), "antes del último nivel, sí");
        Assert.False(Bot.NoSeGraduaEntreGuardias(814, republicano, ultimo, d, ini), "son los suyos");
        Assert.False(Bot.NoSeGraduaEntreGuardias(746, ciudadano, ultimo, d, ini), "el 746, adonde iba, no tiene guardias");
        Assert.True(Bot.NoSeGraduaEntreGuardias(761, republicano, ultimo, d, ini));
        Assert.False(Bot.NoSeGraduaEntreGuardias(761, ciudadano, ultimo, d, ini));
    }

    /// <summary>
    /// A COMPRAR, PASADO EL NOVATO Y CON LA RUNA, NO SE CRUZA EL DESIERTO: desde el 13 Rinkel queda a cinco mapas por el 21 y el 16,
    /// donde nacen Arañas Gigantes que pegan hasta 115. Medido en `paso16b`: tres bots de nivel 20 y 21 muertos ahí yendo de
    /// compras. Buscando el pueblo sólo por donde no lo matan, desde ahí no hay: le toca la runa.
    /// </summary>
    [Fact]
    public void A_comprar_no_se_cruza_el_desierto()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var nav = new Navegante(d);
        const int nivel = 20, vida = 190;
        Func<int, bool> pisable = m => Bot.PuedeEntrar(nivel, d.MapZone(m));
        Func<int, bool> peligroso = m => d.MapSpawns.TryGetValue(m, out var sp)
            && sp.Any(q => Bot.EsPeligrosoPara(d.Npc((short)q.NpcIndex), vida, nivel, conPociones: true, curaVeneno: false));
        Func<int, bool> sinPeligro = m => pisable(m) && !peligroso(m);
        Func<int, bool> vendePociones = m => Bot.ElMercaderLeVende(null, d.MapBattleMode(m), Player.StatusCiudadano)
            && d.MapSpawns.TryGetValue(m, out var sp) && sp.Any(q => d.Npc((short)q.NpcIndex)?.StockItems is { Count: > 0 } st
                   && st.Any(s => d.Obj((short)s.ObjIndex) is { IsPotion: true, SubType: 3 }));
        var desde = ((byte)10, (byte)30);
        Assert.True(peligroso(16), "premisa: en el 16 nace algo que lo mata");
        Assert.Equal(20, Bot.PuebloConMercader(nav, 13, desde, vendePociones, pisable, sinPeligro)?.Mapa);
        Assert.Null(Bot.PuebloConMercader(nav, 13, desde, vendePociones, sinPeligro, sinPeligro));
    }

    /// <summary>
    /// EL REPUBLICANO DEL LADO DE NIX TIENE SU MAPA SEGURO MÁS LEJOS: con los guardias del ini que se distribuye, Ullathorpe, Nix
    /// y Banderbill tienen guardias imperiales que van por él, y desde el 19 el primero que le sirve es Rinkel, a seis mapas —más
    /// que los cinco de siempre—. Al ciudadano, Ullathorpe le sirve a cuatro. Medido en `tardeL1`: Milo pasó la hora sin comida.
    /// </summary>
    [Fact]
    public void El_republicano_del_lado_de_Nix_tiene_su_mapa_seguro_a_seis()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return;
        var ini = ServerIni.Cargar(ruta);
        Assert.True(ini.GuardiasAtacan, "premisa: el ini que se distribuye trae los guardias que atacan");
        var nav = new Navegante(d);
        Func<int, bool> pisable = m => Bot.PuedeEntrar(23, d.MapZone(m));
        Func<int, bool> seguroRepublicano = m => Bot.MapaSeguroPara(m, Player.StatusRepublicano, 23, d, ini);
        Func<int, bool> seguroCiudadano = m => Bot.MapaSeguroPara(m, Player.StatusCiudadano, 23, d, ini);
        Assert.False(seguroRepublicano(1));
        Assert.True(seguroRepublicano(20), "en Rinkel los guardias no pelean");
        var desde = ((byte)57, (byte)69);
        Assert.Equal(1, nav.MapaMasCercano(19, desde, seguroCiudadano, pisable)?.Mapa);
        Assert.Null(nav.MapaMasCercano(19, desde, seguroRepublicano, pisable));
        Assert.Equal(20, nav.MapaMasCercano(19, desde, seguroRepublicano, pisable, Bot.SaltosHastaElPuebloLejano)?.Mapa);
    }

    /// <summary>Y en Rinkel le venden a un ciudadano, y al sacerdote se llega desde la entrada de la cuna —por donde el bot entra a
    /// comprar— abriendo una puerta del templo: sin abrirla, a cinco baldosas no se llega, y el /HOGAR pide cinco o menos.</summary>
    [Fact]
    public void En_Rinkel_le_venden_y_al_sacerdote_se_llega_abriendo_el_templo()
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return;
        Assert.True(Bot.ElMercaderLeVende(null, d.MapBattleMode(20), Player.StatusCiudadano));
        Assert.False(Bot.ElMercaderLeVende(null, d.MapBattleMode(34), Player.StatusCiudadano), "premisa: en Nix no le venden");
        var nav = new Navegante(d);
        var templo = d.MapSpawns[20].First(s => d.Npc((short)s.NpcIndex)?.NpcType == 1);
        var llegada = d.MapExits.First(e => e.Key.Map == 37 && e.Value.DestMap == 20).Value;
        var desde = ((byte)llegada.DestX, (byte)llegada.DestY);
        Assert.Null(nav.HayComoLlegar(20, desde, ((byte)templo.X, (byte)templo.Y), 5));
        Assert.True(d.MapObjects[20].Any(o => d.Obj((short)o.ObjIndex) is { IsDoor: true }
                                          && nav.AbrirlaDaCamino(20, desde, ((byte)o.X, (byte)o.Y), f => d.Npc((short)f)?.NpcType == 1)),
            "ninguna puerta de Rinkel da camino al sacerdote");
    }
}
