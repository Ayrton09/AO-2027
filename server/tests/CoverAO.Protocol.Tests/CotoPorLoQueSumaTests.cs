using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// PASADO EL NOVATO, EL COTO POR LO QUE SUMA EL MAPA (30/09). Por la criatura más gorda a dos mapas, en el late ganaba un mapa con
/// dos Zombies y el resto de relleno: en `tardeL1` y `tardeL2` Elmo, de nivel 27, pasó las dos corridas en el 14 con el 18 a un
/// mapa, y Nadia, de nivel 24, en el 25 con el 18 a dos. Ver <see cref="Bot.EligePorLoQueSuma"/>.
/// </summary>
public class CotoPorLoQueSumaTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(26, true)]
    public void Se_elige_por_lo_que_suma_desde_el_ultimo_nivel_de_novato(int nivel, bool suma)
    {
        Assert.Equal(suma, Bot.EligePorLoQueSuma(nivel));
    }

    /// <summary>Lo mínimo para mudarse es lo mismo que pide <see cref="Bot.ValeMudarsePorLoQueRinde"/>: el doble, o algo donde no hay nada.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1495, 2990)]
    public void Lo_minimo_para_mudarse_es_el_doble_de_lo_de_aca(int aca, int minimo)
    {
        Assert.Equal(minimo, Bot.LoMinimoParaMudarse(aca));
        Assert.True(Bot.ValeMudarsePorLoQueRinde(aca, minimo));
        Assert.False(Bot.ValeMudarsePorLoQueRinde(aca, minimo - 1));
    }

    private sealed record Cazador(Navegante Mapas, GameData Datos, Func<int, bool> EsPresa, Func<int, int> Rinde,
                                  Func<int, bool> SeCazaAhi, Func<int, bool> Nunca, Func<int, bool> Cruza, Func<int, bool> Rescate);

    /// <summary>Un ciudadano (o del <paramref name="bando"/> que se pida) con pociones, con los mismos filtros que arma el bot para buscar coto y con el ini que se distribuye.</summary>
    private static Cazador? UnCazador(int nivel, int vida, byte bando = Player.StatusCiudadano)
    {
        var d = GameData.Load();
        if (d.MapExits.Count == 0) return null;
        var ruta = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "hosting", "Server.ini.ejemplo");
        if (!File.Exists(ruta)) return null;
        var ini = ServerIni.Cargar(ruta);
        Func<int, bool> esPresa = f => Bot.EsPresaPara(d.Npc((short)f), vida, nivel, conPociones: true, curaVeneno: false);
        Func<int, bool> esPeligroso = f => Bot.EsPeligrosoParaElBot(d.Npc((short)f), vida, nivel, true, false, bando, d, ini);
        Func<int, IEnumerable<int>> nace = m => d.MapSpawns.TryGetValue(m, out var sp) ? sp.Select(s => s.NpcIndex) : Enumerable.Empty<int>();
        Func<int, bool> nunca = m => Bot.HayGuardiaQueVaPor(m, bando, nivel, d, ini) || Bot.NoSeGraduaAhi(nivel, d.MapZone(m))
                                     || Bot.NoSeGraduaEntreGuardias(m, bando, nivel, d, ini);
        Func<int, bool> seCazaAhi = m => Bot.SeCazaAhi(d.IsPkMap(m), nace(m), esPeligroso) && !nunca(m);
        Func<int, int> rinde = f => esPresa(f) ? (int)(d.Npc((short)f)?.GiveExp ?? 0) : 0;
        return new Cazador(new Navegante(d), d, esPresa, rinde, seCazaAhi, nunca,
                           Bot.SeCruzaBuscando(d, ini, bando, nivel, vida, conPociones: true, curaVeneno: false),
                           Bot.PisableDelRescate(d, ini, bando, nivel));
    }

    private static (int Mapa, byte X, byte Y, int Saltos)? Coto(Cazador c, int mapa, (byte X, byte Y) desde, bool porLoQueSuma,
                                                                Func<int, int>? desempate = null) =>
        Bot.ElegirCoto(c.Mapas, mapa, desde, c.EsPresa, f => c.Datos.Npc((short)f)?.GiveGld > 0, c.Rinde, c.Cruza, out _, out _,
                       c.SeCazaAhi, desempate, c.Nunca, porLoQueSuma);

    /// <summary>
    /// DESDE EL 14 SE VA AL 18: los dos tienen Zombies, que es lo más gordo que caza un nivel 27, pero el 14 tiene dos y el 18 doce.
    /// Es Elmo en `tardeL1` y `tardeL2`, en la baldosa donde lo encontró la corrida.
    /// </summary>
    [Fact]
    public void Del_mapa_de_dos_Zombies_se_va_al_de_doce()
    {
        if (UnCazador(27, 182) is not { } c) return;
        var desde = ((byte)36, (byte)78);
        Assert.Equal(c.Mapas.LoQueMasRinde(14, desde, c.Rinde)?.Rinde,
                     c.Mapas.CotoQueMasRinde(14, desde, c.Rinde, 1, c.Cruza, m => m == 18)?.Rinde);
        Assert.NotEqual(18, Coto(c, 14, desde, porLoQueSuma: false)?.Mapa);
        Assert.Equal(18, Coto(c, 14, desde, porLoQueSuma: true)?.Mapa);
    }

    /// <summary>
    /// DESDE RINKEL, ADONDE VUELVE A COMPRAR, A LOS ZOMBIES Y NO A LOS LOBOS DEL 19: por la más gorda a dos mapas, un nivel 26 elegía
    /// los Lobos; por lo que suma, un mapa de los que suman tres cuartos del mejor o más, y no todos el mismo.
    /// </summary>
    [Fact]
    public void Desde_Rinkel_el_nivel_26_va_a_los_Zombies_y_los_bots_se_reparten()
    {
        if (UnCazador(26, 260) is not { } c) return;
        var llegada = c.Datos.MapExits.First(e => e.Key.Map == 37 && e.Value.DestMap == 20).Value;
        var desde = ((byte)llegada.DestX, (byte)llegada.DestY);
        Assert.Equal(19, Coto(c, 20, desde, porLoQueSuma: false)?.Mapa);
        var mejor = c.Mapas.CotoQueMasSuma(20, desde, c.Rinde, 4, c.Cruza, c.SeCazaAhi);
        Assert.Equal(18, mejor?.Mapa);
        Assert.Equal(18, Coto(c, 20, desde, porLoQueSuma: true)?.Mapa);
        var elegidos = new List<int>();
        foreach (int propio in Enumerable.Range(1, 24))
        {
            Func<int, int> gusto = m => Bot.CuantoLeGusta(propio, m);
            var suyo = c.Mapas.CotoQueMasSuma(20, desde, c.Rinde, 4, c.Cruza, c.SeCazaAhi, gusto)!.Value;
            Assert.True(suyo.Suma * 4 >= mejor!.Value.Suma * 3, $"eligió el {suyo.Mapa}, que suma {suyo.Suma} contra {mejor.Value.Suma}");
            Assert.Equal(suyo.Mapa, Coto(c, 20, desde, true, gusto)?.Mapa);
            elegidos.Add(suyo.Mapa);
        }
        Assert.True(elegidos.Distinct().Count() >= 2, "todos los bots que salen de Rinkel van al mismo coto");
    }

    /// <summary>
    /// EN EL 15, DEL 261 AL 262: diecisiete Esqueletos y tres Esqueletos Guerreros contra trece Esqueletos Guerreros y diez Zombies. Por
    /// la criatura más gorda se quedaba, y en `cadena2` Gonzo pasó ahí treinta y seis minutos sin subir.
    /// </summary>
    [Fact]
    public void En_el_15_del_261_se_va_al_262()
    {
        if (UnCazador(15, 157) is not { } c) return;
        var desde = ((byte)30, (byte)40);
        Assert.Equal(261, Coto(c, 261, desde, porLoQueSuma: false)?.Mapa);
        Assert.Equal(262, Coto(c, 261, desde, porLoQueSuma: Bot.EligePorLoQueSuma(15))?.Mapa);
    }

    /// <summary>Y EN EL MEJOR, SE QUEDA, sea cual sea su gusto: nada suma el doble que los doce Zombies del 18, aunque el 13 y el 24
    /// sumen parecido.</summary>
    [Fact]
    public void En_el_mapa_de_doce_Zombies_se_queda()
    {
        if (UnCazador(26, 285) is not { } c) return;
        var desde = ((byte)56, (byte)23);
        Assert.Equal(18, Coto(c, 18, desde, porLoQueSuma: true)?.Mapa);
        foreach (int propio in Enumerable.Range(1, 24))
            Assert.Equal(18, Coto(c, 18, desde, true, m => Bot.CuantoLeGusta(propio, m))?.Mapa);
    }

    /// <summary>
    /// DE LOS BOSQUES DE ILLIANDOR, CRUZANDO PELIGRO, A UN COTO QUE SUMA EL DOBLE (<see cref="Bot.CotoCruzandoPeligro"/>, decisión del
    /// autor del 04/10). Es Gonzo, nivel 22, en la baldosa donde lo encontró `tardeN21`: sin cruzar peligro lo mejor son los trece Lobos
    /// del 195 (es republicano: Illiandor no lo echa); el coto que elige está a cuatro mapas o menos y suma por lo menos el doble. Y en el mapa de doce Zombies se queda.
    /// </summary>
    [Fact]
    public void De_los_bosques_de_Illiandor_cruza_el_peligro_a_un_coto_que_suma_el_doble()
    {
        if (UnCazador(22, 238, Player.StatusRepublicano) is not { } c) return;
        var desde = ((byte)10, (byte)8);
        var sinPeligro = c.Mapas.CotoQueMasSuma(195, desde, c.Rinde, 4, c.Cruza, c.SeCazaAhi);
        Assert.Equal(1495, sinPeligro?.Suma);
        var coto = Bot.CotoCruzandoPeligro(c.Mapas, 195, desde, c.Rinde, c.Cruza, c.Rescate, c.SeCazaAhi);
        Assert.NotNull(coto);
        var alla = c.Mapas.CotoQueMasSuma(195, desde, c.Rinde, 4, c.Rescate, m => m == coto!.Value.Mapa);
        Assert.True(alla?.Suma >= 2 * 1495, $"elige el {coto!.Value.Mapa}, que suma {alla?.Suma}");

        if (UnCazador(26, 285) is not { } enElMejor) return;
        Assert.Null(Bot.CotoCruzandoPeligro(enElMejor.Mapas, 18, ((byte)56, (byte)23), enElMejor.Rinde, enElMejor.Cruza,
                                            enElMejor.Rescate, enElMejor.SeCazaAhi));
    }

    /// <summary>
    /// Y EN EL ÚLTIMO NIVEL DE NOVATO SE VA DE ENTRE LOS GUARDIAS QUE VAN A IR POR ÉL (<see cref="Bot.NoSeGraduaEntreGuardias"/>):
    /// desde el 814 un ciudadano de nivel 14 se queda —para él es el mejor coto— y uno de 15 se va al 746, sin guardias. Es Elmo en
    /// `alto1`, en la baldosa donde subió al 16; el Paladín lo mató ahí.
    /// </summary>
    [Fact]
    public void En_el_ultimo_nivel_de_novato_se_va_de_entre_los_guardias()
    {
        const int ultimo = GameLimits.NewbieMaxLevel;
        if (UnCazador(ultimo - 1, 112) is not { } antes || UnCazador(ultimo, 119) is not { } ultimoNivel) return;
        var desde = ((byte)58, (byte)37);
        Assert.Equal(814, Coto(antes, 814, desde, porLoQueSuma: Bot.EligePorLoQueSuma(ultimo - 1))?.Mapa);
        Assert.Equal(746, Coto(ultimoNivel, 814, desde, porLoQueSuma: Bot.EligePorLoQueSuma(ultimo))?.Mapa);
    }
}
