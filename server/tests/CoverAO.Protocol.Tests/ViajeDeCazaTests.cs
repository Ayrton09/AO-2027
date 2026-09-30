using CoverAO.Server.Data;
using CoverAO.Server.Game;
using CoverAO.Server.Net;
using Xunit;

namespace CoverAO.Protocol.Tests;

/// <summary>
/// EL VIAJE A CAZAR SE CORTA CUANDO APARECE UNA PRESA. Con la reaparición fiel las criaturas quedan desparramadas por el mapa, así
/// que lo que se cruza en el camino vale tanto como lo que hay en el destino. Medido en `verdad2`: ocho bots caminando entre veinte
/// y treinta y cuatro baldosas por minuto y setenta y dos criaturas entre todos en quince minutos, dos de ellos sin matar ninguna.
///
/// <para>La decisión se prueba con los SENTIDOS de verdad —los paquetes que manda el servidor— y las fichas del install, no con una
/// tabla de booleanos: lo que importa es a quién ve el bot y con qué radio, no que la fórmula compile.</para>
/// </summary>
public class ViajeDeCazaTests
{
    private const short Yo = 500, Murcielago = 500, Lobo = 501;
    private const int RadioCazador = 12, RadioPaseante = 4;   // Bot.RadioDeCaza y RadioDeCaza / 3
    private const int VidaMaxima = 20;

    private static Player Personaje(byte x, byte y, int stamina = 40)
    {
        var p = new Player { CharIndex = Yo, Name = "Aldo", Account = "cuenta_test" };
        p.Pos = new WorldPos(37, x, y);
        p.MaxHp = VidaMaxima; p.Hp = VidaMaxima;
        p.MaxStamina = 40; p.Stamina = stamina;
        p.Level = 1;
        p.Hunger = 50; p.Thirst = 50;   // sin hambre ni sed: así la energía se mide por lo que hay y no por la regla del que no regenera
        return p;
    }

    private static Npc Criatura(GameData d, short charIndex, short ficha, byte x, byte y) =>
        new() { CharIndex = charIndex, Template = d.Npc(ficha)!, Pos = new WorldPos(37, x, y) };

    private static SentidosDelBot Sentidos(Player yo, params Npc[] criaturas)
    {
        var s = new SentidosDelBot();
        s.Recibir(ServerMessages.UserCharIndexInServer(Yo));
        s.Recibir(ServerMessages.CharacterCreate(yo));
        s.Recibir(ServerMessages.UpdateUserStats(yo));
        foreach (var c in criaturas) s.Recibir(ServerMessages.CharacterCreate(c));
        return s;
    }

    private static bool Corta(SentidosDelBot s, GameData d, int radio) =>
        Bot.HayAlgoQueCazarCerca(s, d, VidaMaxima, nivel: 1, conPociones: true, curaVeneno: false, radioSuyo: radio);

    /// <summary>CON UNA PRESA AL ALCANCE, FRENA; y la misma presa fuera de su radio no lo mueve.</summary>
    [Fact]
    public void Frena_por_la_presa_que_tiene_al_alcance()
    {
        var d = GameData.Load();
        if (d.Npc(Murcielago) is null) return;
        Assert.True(Bot.EsPresaPara(d.Npc(Murcielago), VidaMaxima, 1, conPociones: true, curaVeneno: false),
                    "premisa: el Murciélago tiene que ser presa para un nivel 1");

        Assert.True(Corta(Sentidos(Personaje(50, 50), Criatura(d, 600, Murcielago, 50, 47)), d, RadioCazador));    // a tres
        Assert.False(Corta(Sentidos(Personaje(50, 50), Criatura(d, 600, Murcielago, 50, 37)), d, RadioCazador));   // a trece
    }

    /// <summary>EL PASEANTE NO FRENA POR LO MISMO QUE EL CAZADOR: la misma escena con el radio chico.</summary>
    [Fact]
    public void El_paseante_frena_menos_que_el_cazador()
    {
        var d = GameData.Load();
        if (d.Npc(Murcielago) is null) return;
        var escena = Sentidos(Personaje(50, 50), Criatura(d, 600, Murcielago, 50, 44));   // a seis
        Assert.True(Corta(escena, d, RadioCazador));
        Assert.False(Corta(escena, d, RadioPaseante));
    }

    /// <summary>CON UN LOBO A LA VISTA SE SIGUE DE LARGO, aunque tenga un Murciélago al lado.</summary>
    [Fact]
    public void Con_algo_peligroso_a_la_vista_no_frena()
    {
        var d = GameData.Load();
        if (d.Npc(Murcielago) is null || d.Npc(Lobo) is null) return;
        Assert.True(Bot.EsPeligrosoPara(d.Npc(Lobo), VidaMaxima, 1, conPociones: true, curaVeneno: false),
                    "premisa: el Lobo tiene que ser peligroso para un nivel 1");

        var conLobo = Sentidos(Personaje(50, 50), Criatura(d, 600, Murcielago, 50, 47), Criatura(d, 601, Lobo, 50, 45));
        Assert.False(Corta(conLobo, d, RadioCazador));
    }

    /// <summary>SIN ENERGÍA NO FRENA: llegar y no poder pegar es peor que seguir viaje.</summary>
    [Fact]
    public void Sin_energia_no_frena()
    {
        var d = GameData.Load();
        if (d.Npc(Murcielago) is null) return;
        Assert.False(Corta(Sentidos(Personaje(50, 50, stamina: 0), Criatura(d, 600, Murcielago, 50, 47)), d, RadioCazador));
    }

    /// <summary>Y SIN NADA A LA VISTA, el viaje sigue.</summary>
    [Fact]
    public void Sin_presa_no_frena()
    {
        var d = GameData.Load();
        Assert.False(Corta(Sentidos(Personaje(50, 50)), d, RadioCazador));
    }

    /// <summary>
    /// NI POR LO QUE EL CICLO NO VA A CAZAR: lo que no puede lastimar y lo que tiene anotado que no se alcanza. Medido en `early3`:
    /// un Guerrero de nivel 1 pasó doce minutos en el mismo rincón del 5 cortando en el primer paso cada viaje a la cuna.
    /// </summary>
    [Fact]
    public void No_frena_por_lo_que_no_puede_lastimar_ni_por_lo_que_no_alcanza()
    {
        var d = GameData.Load();
        if (d.Npc(Murcielago) is null) return;
        var escena = Sentidos(Personaje(50, 50), Criatura(d, 600, Murcielago, 50, 47));
        Assert.True(Corta(escena, d, RadioCazador), "premisa: por el Murciélago a tres, frena");
        Assert.False(Bot.HayAlgoQueCazarCerca(escena, d, VidaMaxima, nivel: 1, conPociones: true, curaVeneno: false,
                                              radioSuyo: RadioCazador, noLaLastima: f => f == Murcielago));
        Assert.False(Bot.HayAlgoQueCazarCerca(escena, d, VidaMaxima, nivel: 1, conPociones: true, curaVeneno: false,
                                              radioSuyo: RadioCazador, sinCamino: new HashSet<short> { 600 }));
    }
}
