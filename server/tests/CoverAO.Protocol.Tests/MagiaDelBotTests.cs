using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// POR QUÉ UN BOT QUE CASTEA TIENE QUE PONER SUS PRIMEROS PUNTOS EN MAGIA.
///
/// <para>Una clase que castea nace con UN hechizo —el original le da <c>UserHechizos(1) = 2</c> en el alta
/// (TCP.bas:541)— y con CERO en todas las habilidades, porque los diez puntos iniciales se reparten después.
/// Si esos puntos no van a Magia, el único hechizo que tiene no se puede tirar: el servidor lo rechaza contra
/// el <c>MinSkill</c> del hechizo. Y un mago pegando con el báculo no mata nada.</para>
///
/// <para>Medido en el campo: el Mago, el Bardo y la Clériga con el maná LLENO y la experiencia congelada
/// mientras los guerreros llegaban a nivel cinco. Lo que se fija acá es la premisa que hace necesario el
/// reparto dirigido — y son todos hechos de la DATA, o sea que la próxima edición del mundo los puede
/// cambiar sin tocar una línea de código.</para>
/// </summary>
public class MagiaDelBotTests
{
    /// <summary>VB6 <c>eClass</c>: las clases que el alta arranca con maná y un hechizo.</summary>
    private static readonly byte[] Casters = { 1 /*Clerigo*/, 2 /*Mago*/, 6 /*Bardo*/, 7 /*Druida*/ };

    /// <summary>Los puntos de habilidad con los que arranca un personaje (VB6 <c>SkillPtsLibres</c>).</summary>
    private const int PuntosIniciales = 10;

    private sealed record Mundo(GameData Datos, Leveling Niveles, Persistence Db);

    private static Mundo? Cargar(string raiz)
    {
        var datos = GameData.Load();
        if (datos.ItemCount == 0 || datos.LevelExp.Count == 0) return null;
        Directory.CreateDirectory(raiz);
        var niveles = new Leveling(datos.LevelExp, datos.LifeMod);
        return new Mundo(datos, niveles, new Persistence(AlmacenSqlite.EnMemoria(), niveles));
    }

    /// <summary>
    /// EL QUE CASTEA NACE CON UN HECHIZO Y SIN HABILIDAD PARA TIRARLO. Las dos mitades importan: si naciera
    /// sin hechizo no habría nada que habilitar, y si naciera con la habilidad puesta el reparto dirigido
    /// sobraría.
    /// </summary>
    [Fact]
    public void Nace_con_un_hechizo_que_todavia_no_puede_tirar()
    {
        string raiz = Path.Combine(Path.GetTempPath(), "coverao_magia_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Cargar(raiz) is not { } m) return;

            foreach (byte clase in Casters)
            {
                var acc = m.Db.CreateAccount("cuenta_magia_" + clase, "clave_de_prueba");
                var p = m.Db.CreateCharacter(acc, "Caster" + clase, 1, 1, 1, clase, 1, 1,
                                             m.Datos.NewbieKit(clase), m.Datos.ModRaza);

                short hechizo = p.Spells[1];
                Assert.True(hechizo > 0, $"la clase {clase} nace sin hechizo: no hay nada que habilitar");

                var h = m.Datos.Spell(hechizo);
                Assert.True(h is not null, $"el hechizo {hechizo} no está en la data");
                Assert.True(p.Skills[GameLimits.SkillMagia] < h!.MinSkill,
                    $"la clase {clase} ya nace con Magia suficiente ({p.Skills[GameLimits.SkillMagia]} contra " +
                    $"MinSkill {h.MinSkill}): el reparto dirigido a Magia dejaría de hacer falta");
            }
        }
        finally { try { Directory.Delete(raiz, recursive: true); } catch { /* el temp queda */ } }
    }

    /// <summary>
    /// Y CON LOS PUNTOS INICIALES ALCANZA. Si el hechizo de arranque pidiera más de lo que se reparte al nacer,
    /// ningún reparto lo habilitaría y el arreglo sería otro — mudar el hechizo, o esperar a subir de nivel.
    /// </summary>
    [Fact]
    public void Los_puntos_iniciales_alcanzan_para_habilitarlo()
    {
        string raiz = Path.Combine(Path.GetTempPath(), "coverao_magia2_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Cargar(raiz) is not { } m) return;

            var acc = m.Db.CreateAccount("cuenta_magia_tope", "clave_de_prueba");
            var p = m.Db.CreateCharacter(acc, "CasterTope", 1, 1, 1, Casters[0], 1, 1,
                                         m.Datos.NewbieKit(Casters[0]), m.Datos.ModRaza);
            var h = m.Datos.Spell(p.Spells[1]);
            if (h is null) return;

            // El bot le dedica la MITAD de lo que tiene libre, que es lo que hace un jugador que no quiere
            // quedarse manco en todo lo demás.
            Assert.True(h.MinSkill <= PuntosIniciales / 2,
                $"el hechizo de arranque pide {h.MinSkill} de Magia y al nacer se reparten {PuntosIniciales / 2}: " +
                "con el reparto de a mitad no se habilita nunca");
        }
        finally { try { Directory.Delete(raiz, recursive: true); } catch { /* el temp queda */ } }
    }

    /// <summary>
    /// QUÉ PERGAMINO VALE LA PENA: el de un hechizo que hace daño O QUE CURA y que todavía no sabe. Un caster nace con
    /// uno solo, así que sin esto se queda con ése para siempre — y sin la cura se le van las pociones.
    /// </summary>
    [Fact]
    public void Vale_la_pena_el_pergamino_de_un_hechizo_de_dano_que_no_sabe()
    {
        var sabe = new List<(byte Ranura, short Hechizo)> { ((byte)1, (short)2) };
        var dano = new SpellInfo(7, "Descarga", SubeHp: 2, MinHp: 20, MaxHp: 40, ManaCost: 30, Target: 1);
        var yaLoSabe = new SpellInfo(2, "Proyectil Mágico", SubeHp: 2, MinHp: 5, MaxHp: 10, ManaCost: 5, Target: 1);
        var cura = new SpellInfo(9, "Curar Heridas", SubeHp: 1, MinHp: 10, MaxHp: 20, ManaCost: 10, Target: 1);

        Assert.True(Bot.ValeLaPenaElPergamino(dano, sabe));
        Assert.False(Bot.ValeLaPenaElPergamino(yaLoSabe, sabe));
        Assert.True(Bot.ValeLaPenaElPergamino(cura, sabe));       // la cura TAMBIÉN: ver `CurarseConMagia`
        Assert.False(Bot.ValeLaPenaElPergamino(null, sabe));
    }

    /// <summary>
    /// EL QUE SABE CURARSE SE CURA CON MAGIA, y con la más fuerte que le dé el maná: entre Curar Heridas Leves y
    /// Graves, la Grave mientras pueda pagarla.
    /// </summary>
    [Fact]
    public void Se_cura_con_el_hechizo_mas_fuerte_que_puede_pagar()
    {
        var curas = new (byte Ranura, int Cura, int Mana)[]
        {
            (1, 15, 6),     // Curar Heridas Leves
            (2, 45, 25),    // Curar Heridas Graves
        };

        Assert.Equal((byte)2, Bot.MejorCuraQuePuedePagar(curas, mana: 30)!.Value.Ranura);
        Assert.Equal((byte)1, Bot.MejorCuraQuePuedePagar(curas, mana: 10)!.Value.Ranura);   // no le alcanza la grave
        Assert.Null(Bot.MejorCuraQuePuedePagar(curas, mana: 3));                            // no le alcanza ninguna
        Assert.Null(Bot.MejorCuraQuePuedePagar(System.Array.Empty<(byte, int, int)>(), mana: 500));   // no sabe curarse
    }

    /// <summary>
    /// Y EL PERGAMINO DE CURA TAMBIÉN SE COMPRA. Antes sólo contaban los de daño, así que el bot mago nunca aprendía
    /// a curarse — que es lo primero que compra cualquiera que juega un caster.
    /// </summary>
    [Theory]
    [InlineData(2, true)]     // hace daño
    [InlineData(1, true)]     // cura
    [InlineData(0, false)]    // ni una cosa ni la otra: un hechizo de estado
    public void El_pergamino_que_vale_la_pena_hace_dano_o_cura(int subeHp, bool vale)
    {
        var hechizo = new SpellInfo(77, "el que sea", SubeHp: subeHp, MinHp: 10, MaxHp: 20, ManaCost: 10, Target: 1);

        Assert.Equal(vale, Bot.ValeLaPenaElPergamino(hechizo, System.Array.Empty<(byte Ranura, short Hechizo)>()));
        Assert.False(Bot.ValeLaPenaElPergamino(hechizo, new (byte Ranura, short Hechizo)[] { (1, 77) }),
            "compró un pergamino de un hechizo que ya sabía");
    }

    /// <summary>
    /// EL CASTER VA PRIMERO POR EL PERGAMINO, pero sólo si hay dónde comprarlo. Medido en `seguir4`: Fabia, maga con
    /// quince mil monedas, viajaba al Herrero Rinkel —que no vende pergaminos— porque estaba más cerca, y ningún
    /// caster de ninguna corrida pasó del hechizo con el que nace.
    /// </summary>
    [Theory]
    // ¿va por hechizo? | ¿tiene lo esencial? | ¿hay vendedor al alcance? | ¿el viaje es sólo por el pergamino?
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]    // sin vendedor cerca se afloja: comprar comida importa más
    [InlineData(true, false, true, false)]    // le falta comer, tomar o con qué curarse: eso va antes
    [InlineData(false, true, true, false)]    // no castea, o no le alcanza la plata
    [InlineData(false, false, false, false)]
    public void El_caster_va_primero_por_el_pergamino_si_hay_donde(bool vaPorHechizo, bool loEsencial, bool hayUno,
                                                                   bool soloElPergamino)
    {
        Assert.Equal(soloElPergamino, Bot.PrimeroElPergamino(vaPorHechizo, loEsencial, hayUno));
    }

    /// <summary>
    /// NO ELIGE UN HECHIZO QUE EL SERVIDOR LE VA A RECHAZAR. Al lanzar, el servidor pide maná Y ENERGÍA
    /// (<c>p.Stamina &lt; spell.StaRequerido</c>); el bot miraba sólo el maná. Y el rechazo no gasta maná, así que
    /// volvía a elegir el mismo en la vuelta siguiente.
    /// </summary>
    [Theory]
    // maná que tiene | energía que tiene | maná que pide | energía que pide | ¿lo puede lanzar?
    [InlineData(100, 100, 50, 50, true)]
    [InlineData(100, 100, 50, 200, false)]   // le sobra maná y le falta energía: el caso que no se miraba
    [InlineData(10, 100, 50, 50, false)]     // le falta maná
    [InlineData(100, 0, 50, 0, true)]        // el que no pide energía se lanza sin ella
    public void No_elige_un_hechizo_que_el_servidor_le_rechaza(int mana, int energia, int pideMana, int pideSta, bool puede)
    {
        var h = Hechizo(pideMana, pideSta);
        Assert.Equal(puede, Bot.LoPuedeLanzarAhora(h, mana, energia));
    }

    /// <summary>
    /// Y NO COMPRA EL PERGAMINO DE UN HECHIZO QUE NADIE PUEDE LANZAR. Dos de los ciento trece piden mil de energía
    /// y el techo del juego son 999 (<c>MaxStaCap</c>), así que son incasteables para cualquier personaje de
    /// cualquier clase y nivel — y son justo los dos que encabezan el ranking del bot, que ordena por daño.
    /// </summary>
    [Theory]
    [InlineData(1000, false)]   // los dos de la data: energía 1000 contra un techo de 999
    [InlineData(999, true)]     // el borde exacto
    [InlineData(5, true)]
    [InlineData(0, true)]
    public void No_compra_el_pergamino_de_un_hechizo_incasteable(int pideSta, bool algunaVez)
    {
        Assert.Equal(algunaVez, Bot.AlgunaVezLoVaAPoderLanzar(Hechizo(1, pideSta)));
    }

    /// <summary>Y contra la data de verdad: los hechizos que piden más energía que el techo quedan afuera.</summary>
    [Fact]
    public void Los_hechizos_que_piden_mas_energia_que_el_techo_quedan_afuera()
    {
        var datos = GameData.Load();
        if (datos.Spell(30) is null) return;                 // sin la data del juego no hay nada que mirar
        int incasteables = 0;
        for (short i = 1; i <= 113; i++)
            if (datos.Spell(i) is { } h && !Bot.AlgunaVezLoVaAPoderLanzar(h))
            {
                incasteables++;
                Assert.True(h.StaRequerido > Leveling.MaxStaCap);
            }
        Assert.True(incasteables > 0, "la data dejó de tener hechizos incasteables: el caso ya no aplica");
    }

    /// <summary>Un hechizo de prueba con lo que pide de maná y de energía.</summary>
    private static SpellInfo Hechizo(int mana, int sta) => new(
        Index: 1, Name: "prueba", SubeHp: 2, MinHp: 1, MaxHp: 10, ManaCost: mana, Target: 1,
        StaRequerido: sta);

    /// <summary>
    /// LO QUE LE RECHAZARON SE VUELVE A PROBAR AL SUBIR MAGIA. Anotar un hechizo para siempre lo dejaría muerto
    /// aunque el bot suba la skill que le faltaba — y la sube: <c>RepartirPuntos</c> le mete ahí la mitad de los
    /// puntos libres cuando el personaje castea. Cualquier otra skill no cambia nada, así que no borra la lista.
    /// </summary>
    [Theory]
    [InlineData((byte)GameLimits.SkillMagia, true)]
    [InlineData((byte)GameLimits.SkillTalar, false)]
    [InlineData((byte)0, false)]
    public void Lo_que_le_rechazaron_se_vuelve_a_probar_al_subir_magia(byte skill, bool vuelveAProbar)
    {
        Assert.Equal(vuelveAProbar, Bot.VuelveAProbarLosHechizos(skill));
    }

    /// <summary>
    /// EL HECHIZO QUE EL SERVIDOR RECHAZA POR MAGIA NO SE VUELVE A ELEGIR. <c>LanzarAlgo</c> elegía el de más
    /// daño entre los que podía pagar de maná y energía, sin mirar la skill de Magia que el servidor pide primero
    /// (<c>LocaleMsg 221</c>). El rechazo no cobra nada, así que volvía a elegir el mismo — y como
    /// <c>LanzarAlgo</c> devolvía true por haber mandado los paquetes, arriba el <c>continue</c> le comía también
    /// el golpe con el arma.
    ///
    /// <para>Medido: de los 23 pergaminos de daño que venden los mercaderes el bot elige el Misil Mágico
    /// (daño 35, maná 40), que pide 30 de Magia. Junta 5 al nacer y 2 por nivel, o sea nivel 16 — y el pergamino
    /// lo compra mucho antes. Desde que lo aprende hasta ahí no atacaba de ninguna forma.</para>
    /// </summary>
    [Theory]
    // cuándo lo pidió | último rechazo | ¿es de este pedido?
    [InlineData(1000L, 1200L, true)]    // el rechazo llegó después: es de este casteo
    [InlineData(1000L, 1000L, true)]    // en el mismo tick, que es lo normal
    [InlineData(1000L, 900L, false)]    // el rechazo es ANTERIOR: era de otro hechizo
    [InlineData(1000L, 0L, false)]      // nunca le rechazaron nada
    [InlineData(0L, 1200L, false)]      // no pidió nada, no hay a quién anotar
    public void Se_lo_rechazo_por_magia(long cuandoLoPidio, long ultimoRechazo, bool esDeEstePedido)
    {
        Assert.Equal(esDeEstePedido, Bot.SeLoRechazoPorMagia(cuandoLoPidio, ultimoRechazo));
    }

    /// <summary>
    /// EL HECHIZO DE DAÑO QUE CONVIENE es el de más daño entre los que puede pagar — y NO uno que el
    /// servidor ya le rechazó. Sin saltear los anotados vuelve a elegir el mismo para siempre, porque un casteo
    /// rechazado no gasta maná ni energía: no cambia nada que lo lleve a elegir otro.
    /// </summary>
    [Fact]
    public void El_hechizo_de_dano_que_conviene()
    {
        // ranura, número, daño, maná, energía
        var hechizos = new[]
        {
            ((byte)1, (short)2, 15, 12, 2),      // Proyectil Mágico: barato y flojo
            ((byte)2, (short)24, 35, 40, 20),    // Misil Mágico: el que más pega de los que puede pagar
            ((byte)3, (short)10, 80, 1400, 120)  // Implosión: no le alcanza el maná
        };
        var ninguno = new HashSet<short>();

        Assert.Equal((byte)2, Bot.MejorHechizoDeDano(hechizos, mana: 100, energia: 100, ninguno));
        // Con el Misil anotado baja al Proyectil en vez de insistir con el que le rechazan.
        Assert.Equal((byte)1, Bot.MejorHechizoDeDano(hechizos, 100, 100, new HashSet<short> { 24 }));
        // Y si le rechazaron los dos, no castea: le queda pegar con el arma.
        Assert.Null(Bot.MejorHechizoDeDano(hechizos, 100, 100, new HashSet<short> { 24, 2 }));
        // El maná y la energía siguen mandando: con poco de cada uno sólo llega al más barato.
        Assert.Equal((byte)1, Bot.MejorHechizoDeDano(hechizos, 20, 100, ninguno));
        Assert.Equal((byte)1, Bot.MejorHechizoDeDano(hechizos, 100, 10, ninguno));
    }

    /// <summary>
    /// HAY DÓNDE COMPRAR EL PERGAMINO aunque no se llegue caminando: los vendedores están adentro de edificios
    /// —los del mapa 20, detrás de sus once puertas— y preguntar sólo por los alcanzables a pie daba siempre que
    /// no, así que el bot nunca priorizaba el viaje.
    ///
    /// <para>Medido en campo, 45 minutos: el caster salió de compras 35 veces, 7 queriendo un hechizo, llegó a
    /// 10 mostradores y en ninguno había un pergamino que le sirviera — con 17.775 monedas encima y el único
    /// hechizo con el que nació.</para>
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]     // se llega a pie: siempre sirvió
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]    // detrás de una puerta, pero el mapa existe: ahora también
    [InlineData(false, false, false)]  // no hay ninguno en ningún lado
    public void Hay_donde_comprar_el_pergamino(bool aPie, bool elMapa, bool hayDonde)
    {
        Assert.Equal(hayDonde, Bot.HayDondeComprarElPergamino(aPie, elMapa));
    }

    /// <summary>
    /// PARA EL QUE CASTEA, EL HECHIZO VA ANTES QUE EL ARMA. Las dos compras salen de la misma bolsa, asi que el orden
    /// decide cual se queda sin plata: con el pergamino ultimo, un Clerigo con 3.550 monedas viajo ONCE veces por el
    /// pergamino en media hora y volvio sin ninguno, y un Mago de nivel 16 con 7.115 monedas tampoco aprendio uno.
    /// </summary>
    [Fact]
    public void El_que_castea_gasta_primero_en_el_pergamino()
    {
        Assert.True(Bot.ElHechizoAntesQueElArma(castea: true));

        // Al que no castea no le cambia nada: no compra pergaminos, asi que el equipo sigue yendo primero.
        Assert.False(Bot.ElHechizoAntesQueElArma(castea: false));
    }
}
