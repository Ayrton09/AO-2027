using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL BOT CAZADOR TIRA CON EL ARCO (30/09). Nace con el Arco Simple y 1500 flechas y ninguna arma de mano, y el bot, que no
/// disparaba, se sacaba el arco y peleaba a mano limpia hasta el 13: en `early5`, `early7` y `cadena1` llegó al 13 entre los
/// últimos, y en `early6` y `cadena1` murió en el nivel 6 sin poder sacarse de encima un Goblin y un Esqueleto.
/// Ver <see cref="Bot.TiraConArco"/>.
/// </summary>
public class ElCazadorTiraConArcoTests
{
    /// <summary>
    /// Tira con arco sólo el Cazador, y el Balance lo hace mejor tirando que pegando: el acierto por el daño de su clase con el arco
    /// supera al del arma. En el Guerrero es al revés, que es lo que el bot no tiene que copiar.
    /// </summary>
    [Fact]
    public void Tira_con_arco_solo_el_Cazador_y_es_el_que_mejor_tira()
    {
        Assert.Equal(new[] { (int)GameLimits.ClaseCazador }, Enumerable.Range(1, 20).Where(c => Bot.TiraConArco((byte)c)));
        var d = GameData.Load();
        if (d.ProjectileAttackMod.Count == 0) return;
        double ConArco(byte c) => d.ProjectileAttackMod[c] * d.ProjectileDamageMod[c];
        double ConArma(byte c) => d.AttackMod[c] * d.WeaponMod[c];
        Assert.True(ConArco(GameLimits.ClaseCazador) > ConArma(GameLimits.ClaseCazador));
        Assert.True(ConArco(GameLimits.ClaseGuerrero) < ConArma(GameLimits.ClaseGuerrero));
    }

    /// <summary>Y el Cazador nace con un arco y flechas que se puede poner, que es lo que lo deja tirar desde el nivel 1.</summary>
    [Fact]
    public void El_Cazador_nace_con_arco_y_flechas_que_se_puede_poner()
    {
        var d = GameData.Load();
        if (d.NewbieKit(GameLimits.ClaseCazador).Count == 0) return;
        var kit = d.NewbieKit(GameLimits.ClaseCazador).Select(i => d.Obj((short)i.ObjIndex)).ToList();
        var mochila = kit.Select((o, i) => ((byte)(i + 1), o!, false, Bot.PuedeUsarlo(o, GameLimits.ClaseCazador, 1, 1, 1)));
        Assert.NotNull(Bot.ArcoYFlechas(mochila));
    }

    private static readonly ObjInfo Arco = Obj(1, objType: 2, maxHit: 5, proyectil: 1);
    private static readonly ObjInfo ArcoMejor = Obj(2, objType: 2, maxHit: 9, proyectil: 1);
    private static readonly ObjInfo Flechas = Obj(3, objType: 32);
    private static readonly ObjInfo Espada = Obj(4, objType: 2, maxHit: 12);

    private static ObjInfo Obj(short indice, int objType, int maxHit = 0, int proyectil = 0) =>
        new(indice, $"obj {indice}", ObjType: objType, MaxModifier: 0, Value: 0, Anim: 0, MaxHit: maxHit, Proyectil: proyectil,
            Municiones: proyectil);

    [Fact]
    public void Sin_flechas_o_sin_arco_no_tira()
    {
        Assert.Null(Bot.ArcoYFlechas(new[] { ((byte)1, Arco, true, true), ((byte)2, Espada, false, true) }));
        Assert.Null(Bot.ArcoYFlechas(new[] { ((byte)1, Flechas, true, true), ((byte)2, Espada, false, true) }));
        Assert.Null(Bot.ArcoYFlechas(new[] { ((byte)1, Arco, false, false), ((byte)2, Flechas, false, true) }));
    }

    [Fact]
    public void Se_queda_con_lo_puesto_y_si_no_con_el_arco_que_mas_pega()
    {
        Assert.Equal(((byte)1, false, (byte)3, false),
                     Bot.ArcoYFlechas(new[] { ((byte)1, Arco, false, true), ((byte)2, Espada, false, true), ((byte)3, Flechas, false, true) }));
        Assert.Equal(((byte)2, false, (byte)3, true),
                     Bot.ArcoYFlechas(new[] { ((byte)1, Arco, false, true), ((byte)2, ArcoMejor, false, true), ((byte)3, Flechas, true, true) }));
        Assert.Equal(((byte)1, true, (byte)3, true),
                     Bot.ArcoYFlechas(new[] { ((byte)1, Arco, true, true), ((byte)2, ArcoMejor, false, true), ((byte)3, Flechas, true, true) }));
    }
}
